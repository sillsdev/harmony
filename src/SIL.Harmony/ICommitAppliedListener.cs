namespace SIL.Harmony;

/// <summary>
/// Optional hook invoked after commits are applied — for both local authoring and sync — once the
/// apply transaction has committed. The method is asynchronous so implementations can do database
/// work (e.g. rolling a tag checkout forward). It is not invoked from <see cref="DataModel.RegenerateSnapshots"/>
/// or scoped read paths; this entrypoint boundary is what prevents a listener that rematerializes
/// from re-entering.
/// </summary>
public interface ICommitAppliedListener
{
    Task OnCommitsAppliedAsync(IReadOnlyCollection<Commit> commits);
}
