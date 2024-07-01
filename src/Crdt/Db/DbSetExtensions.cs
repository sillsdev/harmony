namespace Crdt.Db;

//todo, I would like to move these extensions into QueryHelperTests but that's in Core and ObjectSnapshot is not part of core
public static class DbSetExtensions
{
    public static IQueryable<ObjectSnapshot> DefaultOrder(this IQueryable<ObjectSnapshot> queryable)
    {
        return queryable
            .OrderBy(c => c.Commit.HybridDateTime.DateTime)
            .ThenBy(c => c.Commit.HybridDateTime.Counter)
            .ThenBy(c => c.Commit.Id);
    }

    public static IQueryable<ObjectSnapshot> DefaultOrderDescending(this IQueryable<ObjectSnapshot> queryable)
    {
        return queryable
            .OrderByDescending(c => c.Commit.HybridDateTime.DateTime)
            .ThenByDescending(c => c.Commit.HybridDateTime.Counter)
            .ThenByDescending(c => c.Commit.Id);
    }

    public static IQueryable<ObjectSnapshot> WhereAfter(this IQueryable<ObjectSnapshot> queryable, Commit after)
    {
        return queryable.Where(
            s => after.HybridDateTime.DateTime < s.Commit.HybridDateTime.DateTime
                 || (after.HybridDateTime.DateTime == s.Commit.HybridDateTime.DateTime && after.HybridDateTime.Counter < s.Commit.HybridDateTime.Counter)
                 || (after.HybridDateTime.DateTime == s.Commit.HybridDateTime.DateTime && after.HybridDateTime.Counter == s.Commit.HybridDateTime.Counter && after.Id < s.Commit.Id));
    }
}