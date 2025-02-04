namespace SIL.Harmony.Core;

public interface IObjectSnapshot
{
    Guid Id { get; }
    string TypeName { get; }
    IObjectBase Entity { get; }
    Guid[] References { get; }
    Guid EntityId { get; }
    bool EntityIsDeleted { get; }
    Guid CommitId { get; }
    CommitBase Commit { get; }
    bool IsRoot { get; }
}
