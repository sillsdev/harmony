using SIL.Harmony.Changes;
using SIL.Harmony.Db;

namespace SIL.Harmony.Maintenance;

/// <summary>
/// Maintenance operations on a <see cref="DataModel"/>'s commit chain that fall outside normal CRDT
/// mutation. Exposed as a static class (rather than methods on <see cref="DataModel"/>) so these
/// operations stay out of the everyday <c>dataModel.</c> autocomplete surface and a caller has to
/// deliberately reach for them.
/// </summary>
public static class DataModelMaintenance
{
    /// <summary>
    /// Mints a new <see cref="Commit.Id"/> for every commit in this DataModel's commit chain,
    /// sets every commit's <see cref="CommitBase.ClientId"/> to <paramref name="clientId"/>, and
    /// recomputes the chain's <see cref="Commit.Hash"/> / <see cref="Commit.ParentHash"/> against the
    /// new Ids. Commit content — <see cref="ChangeEntity{T}"/> rows and <see cref="ObjectSnapshot"/>
    /// rows — is preserved exactly, with their <c>CommitId</c> foreign keys updated to point at the
    /// new Commit Ids. Runs in a single transaction; the chain is left untouched if any step fails.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Intended for first-time bootstrap of a project from a pre-built commit chain (for example, one
    /// bulk-loaded from a SQL snapshot at project creation). NOT for syncing a chain in from another
    /// peer — for that, use the sync protocol (<see cref="DataModel.SyncWith"/>), which preserves
    /// Commit Ids so peer histories can reconcile.
    /// </para>
    /// <para>
    /// Refuses to run on a commit chain whose commits have multiple distinct
    /// <see cref="CommitBase.ClientId"/> values — that's the shape of an already-authored chain, not a
    /// pre-built one. This gate prevents corrupting a multi-author chain; it does NOT distinguish a
    /// pre-built template from an ordinary solo-authored project, so the caller is responsible for
    /// only invoking this on a freshly-bootstrapped chain.
    /// </para>
    /// <para>
    /// Also refuses if any two commits share an identical (DateTime, Counter): Harmony's canonical
    /// order breaks ties on <see cref="Commit.Id"/>, and minting fresh random Ids would silently
    /// reorder such commits. A single-author chain never produces this collision.
    /// </para>
    /// </remarks>
    /// <param name="model">The <see cref="DataModel"/> whose commit chain will be reseeded.</param>
    /// <param name="clientId">The ClientId to set on every commit in the chain.</param>
    /// <exception cref="InvalidOperationException">
    /// Thrown if the commit chain is empty, if its commits have more than one distinct ClientId, or if
    /// two commits share an identical (DateTime, Counter).
    /// </exception>
    public static Task ReseedProject(DataModel model, Guid clientId)
    {
        ArgumentNullException.ThrowIfNull(model);
        return model.ReseedProjectImpl(clientId);
    }
}
