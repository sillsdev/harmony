namespace SIL.Harmony;

/// <summary>
/// Optional hook invoked once per locally-authored commit, before it is persisted.
/// Implementations may mutate <see cref="Commit.Metadata"/> and may throw to reject authoring.
/// It is not invoked for commits received via sync — those keep the assignment their author gave them.
/// </summary>
public interface ICommitInterceptor
{
    void OnCommitAuthored(Commit commit);
}
