using System.Diagnostics.CodeAnalysis;

namespace SIL.Harmony.Helpers;

public static class LinqHelpers
{
    internal static IEnumerable<TResult> FullOuterJoin<TA, TB, TKey, TResult>(
        this IEnumerable<TA> a,
        IEnumerable<TB> b,
        Func<TA, TKey> selectKeyA,
        Func<TB, TKey> selectKeyB,
        Func<TA, TB, TKey, TResult> projection,
        TA? defaultA = default(TA),
        TB? defaultB = default(TB),
        IEqualityComparer<TKey>? cmp = null)
    {
        cmp ??= EqualityComparer<TKey>.Default;
        var alookup = a.ToLookup(selectKeyA, cmp);
        var blookup = b.ToLookup(selectKeyB, cmp);

        var keys = new HashSet<TKey>(alookup.Select(p => p.Key), cmp);
        keys.UnionWith(blookup.Select(p => p.Key));

        var join = from key in keys
            from xa in alookup[key].DefaultIfEmpty(defaultA)
            from xb in blookup[key].DefaultIfEmpty(defaultB)
            select projection(xa, xb, key);

        return join;
    }
}