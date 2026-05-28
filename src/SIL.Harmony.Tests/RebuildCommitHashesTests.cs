using Microsoft.EntityFrameworkCore;
using SIL.Harmony.Core;

namespace SIL.Harmony.Tests;

public class RebuildCommitHashesTests : DataModelTestBase
{
    [Fact]
    public async Task RebuildCommitHashes_RestoresChainAfterHashesAreCorrupted()
    {
        // Three commits in chain order. WriteNextChange persists them with correct hashes
        // computed against the live chain.
        var c1 = await WriteNextChange(SetWord(Guid.NewGuid(), "one"));
        var c2 = await WriteNextChange(SetWord(Guid.NewGuid(), "two"));
        var c3 = await WriteNextChange(SetWord(Guid.NewGuid(), "three"));

        // Corrupt c2's hash directly. With AlwaysValidateCommits on, any further AddChange
        // would throw — exactly the scenario RebuildCommitHashes exists to recover from
        // (in production, the corruption comes from substituting Commit.Ids in a SQL
        // template before the model has seen the chain).
        var tracked = await DbContext.Commits.SingleAsync(c => c.Id == c2.Id);
        // "BBAADD" is a valid hex string (3 bytes) — CommitBase.GenerateHash parses parentHash
        // via Convert.FromHexString, so the corruption value still needs to look like a hash.
        tracked.SetParentHash("BBAADD");
        await DbContext.SaveChangesAsync();

        // Sanity: validation now rejects further writes.
        Func<Task> beforeRebuild = async () => await WriteNextChange(SetWord(Guid.NewGuid(), "four"));
        await beforeRebuild.Should().ThrowAsync<CommitValidationException>();

        // Act
        await DataModel.RebuildCommitHashes();

        // Each commit's persisted Hash must equal the hash computed from its Id + the prior
        // commit's hash, walking from the chain root.
        var commits = await DbContext.Commits.AsNoTracking().DefaultOrder().ToListAsync();
        var parentHash = CommitBase.NullParentHash;
        foreach (var commit in commits)
        {
            commit.Hash.Should().Be(commit.GenerateHash(parentHash));
            commit.ParentHash.Should().Be(parentHash);
            parentHash = commit.Hash;
        }

        // And the chain is once again accepting writes.
        Func<Task> afterRebuild = async () => await WriteNextChange(SetWord(Guid.NewGuid(), "four"));
        await afterRebuild.Should().NotThrowAsync();
    }

    [Fact]
    public async Task RebuildCommitHashes_IsNoOpOnEmptyChain()
    {
        // Just don't throw / don't write anything. Guards against future regressions where
        // a callable shape (e.g. AsNoTracking + DefaultOrder().FirstAsync()) would crash on
        // an empty table.
        Func<Task> act = async () => await DataModel.RebuildCommitHashes();
        await act.Should().NotThrowAsync();
        (await DbContext.Commits.CountAsync()).Should().Be(0);
    }
}
