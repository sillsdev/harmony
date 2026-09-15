using CsCheck;

namespace SIL.Harmony.Tests.PropertyBased;

/// <summary>
/// CsCheck generators that force the rollback bug surface: stragglers, ties, cascades,
/// duplicates, and batching. Adapted from the handoff doc's §7 recipe to Harmony's commit
/// model. Author time is drawn from a SMALL range and is INDEPENDENT of arrival order (so
/// stragglers are the norm and rollback is exercised); entities come from a SMALL pool (so
/// commits contend and canonical order decides the value); duplicates and random batching are
/// always present. Commit ids and times are deterministic functions of the generated integers,
/// so a failing case reproduces exactly.
///
/// <para><b>Tier 1</b> uses only <c>SetWordTextChange</c> (create-or-edit), so every schedule
/// is valid in any order. <b>Tier 2</b> adds creates, deletes, notes, definitions,
/// antonym/word→definition reference cascades, and fractional ordering; it keeps schedules
/// valid by time-banding (word creates &lt; definition creates &lt; all edits), so every
/// entity is canonically created before it is edited or deleted (an edit/delete of a
/// not-yet-created entity throws by design — see SnapshotWorker.ApplyCommitChanges).</para>
/// </summary>
internal static class Generators
{
    private static readonly DateTimeOffset BaseDate = new(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);

    private static HybridDateTime Time(int hour) => new(BaseDate.AddHours(hour), 0);

    /// <summary>Deterministic Guid from a tag + index, so the same schedule reproduces identical ids.</summary>
    private static Guid Det(byte tag, int i)
    {
        Span<byte> b = stackalloc byte[16];
        b[0] = tag;
        BitConverter.TryWriteBytes(b[1..], i);
        return new Guid(b);
    }

    private static Guid WordId(int i) => Det(0x02, i);
    private static Guid DefId(int i) => Det(0x03, i);
    private static Guid CommitId(int i) => Det(0x01, i);

    // ---- Tier 1: create-or-edit text only ------------------------------------------------

    public static readonly Gen<Schedule> Tier1 =
        from n in Gen.Int[1, 30]
        from hours in Gen.Int[0, 8].Array[n]            // small range => ties + varied-depth stragglers
        from deep in Gen.Int[0, 9].Array[n]             // ~10% chance a commit is a deep straggler
        from entityIdx in Gen.Int[0, 3].Array[n]        // small entity pool => contention
        from textIdx in Gen.Int[0, 5].Array[n]
        let specs = BuildTier1Specs(n, hours, deep, entityIdx, textIdx)
        let deps = NoDependencies(specs.Length)
        from arrivalsA in ArrivalPlan(specs, deps)
        from arrivalsB in ArrivalPlan(specs, deps)
        select new Schedule(specs, arrivalsA, arrivalsB);

    private static CommitSpec[] BuildTier1Specs(int n, int[] hours, int[] deep, int[] entityIdx, int[] textIdx)
    {
        var specs = new CommitSpec[n];
        for (var i = 0; i < n; i++)
        {
            var hour = deep[i] == 0 ? -5 : hours[i];
            ChangeSpec change = new SetWordTextSpec(WordId(entityIdx[i]), "t" + textIdx[i]);
            specs[i] = new CommitSpec(CommitId(i), Time(hour), new[] { change });
        }
        return specs;
    }

    // ---- Tier 2: creates, deletes, notes, definitions, references, ordering --------------

    // Time bands guarantee canonical validity regardless of arrival order:
    //   word creates in [0,3]  <  definition creates in [4,7]  <  all edits/deletes in [8,14].
    private const int EditLow = 8, EditHigh = 14;

    public static readonly Gen<Schedule> Tier2 =
        from wCount in Gen.Int[1, 3]
        from dCount in Gen.Int[0, 3]
        from wCreateHour in Gen.Int[0, 3].Array[wCount]
        from dCreateHour in Gen.Int[4, 7].Array[Math.Max(dCount, 1)]
        from dWord in Gen.Int[0, 999].Array[Math.Max(dCount, 1)]
        from editCount in Gen.Int[0, 12]
        from editKind in Gen.Int[0, 1].Array[Math.Max(editCount, 1)]     // 0 = word edit, 1 = definition edit
        from editTarget in Gen.Int[0, 999].Array[Math.Max(editCount, 1)]
        from editOp in Gen.Int[0, 2].Array[Math.Max(editCount, 1)]
        from editHour in Gen.Int[EditLow, EditHigh].Array[Math.Max(editCount, 1)]
        from editVal in Gen.Int[0, 4].Array[Math.Max(editCount, 1)]
        from antCount in Gen.Int[0, 2]
        from antFrom in Gen.Int[0, 999].Array[Math.Max(antCount, 1)]
        from antTo in Gen.Int[0, 999].Array[Math.Max(antCount, 1)]
        from antHour in Gen.Int[EditLow, EditHigh].Array[Math.Max(antCount, 1)]
        let specSet = BuildTier2Specs(wCount, dCount, wCreateHour, dCreateHour, dWord,
            editCount, editKind, editTarget, editOp, editHour, editVal,
            antCount, antFrom, antTo, antHour)
        from arrivalsA in ArrivalPlan(specSet.Specs, specSet.Deps)
        from arrivalsB in ArrivalPlan(specSet.Specs, specSet.Deps)
        select new Schedule(specSet.Specs, arrivalsA, arrivalsB);

    /// <summary>Generated commits plus, for each commit, the indices of commits that must ARRIVE before it.</summary>
    private sealed record SpecSet(CommitSpec[] Specs, int[][] Deps);

    private static SpecSet BuildTier2Specs(
        int wCount, int dCount, int[] wCreateHour, int[] dCreateHour, int[] dWord,
        int editCount, int[] editKind, int[] editTarget, int[] editOp, int[] editHour, int[] editVal,
        int antCount, int[] antFrom, int[] antTo, int[] antHour)
    {
        var specs = new List<CommitSpec>();
        // For each commit, the single create it depends on arriving first (-1 = no dependency).
        var depDependsOnEntity = new List<Guid?>();
        var createIndexByEntity = new Dictionary<Guid, int>();
        var commitIndex = 0;

        // A create registers its entity's create index; an edit records the entity it needs present.
        void Add(int hour, ChangeSpec change, Guid? createdEntity, Guid? dependsOnEntity)
        {
            if (createdEntity is { } created) createIndexByEntity[created] = commitIndex;
            specs.Add(new CommitSpec(CommitId(commitIndex), Time(hour), new[] { change }));
            depDependsOnEntity.Add(dependsOnEntity);
            commitIndex++;
        }

        // Word creates (band [0,3]) — depend on nothing.
        for (var w = 0; w < wCount; w++)
            Add(wCreateHour[w], new NewWordSpec(WordId(w), "w" + w), createdEntity: WordId(w), dependsOnEntity: null);

        // Definition creates (band [4,7]) — need their word to have arrived (projected-table FK).
        for (var d = 0; d < dCount; d++)
        {
            var word = dWord[d] % wCount;
            Add(dCreateHour[d], new NewDefinitionSpec(DefId(d), WordId(word), "d" + d, "pos" + d, d),
                createdEntity: DefId(d), dependsOnEntity: WordId(word));
        }

        // Edits / deletes (band [8,14]) — need their subject entity to have arrived.
        for (var e = 0; e < editCount; e++)
        {
            var editsDefinition = dCount > 0 && editKind[e] == 1;
            if (!editsDefinition)
            {
                var w = editTarget[e] % wCount;
                ChangeSpec change = (editOp[e] % 3) switch
                {
                    0 => new SetWordTextSpec(WordId(w), "wt" + editVal[e]),
                    1 => new SetWordNoteSpec(WordId(w), "nn" + editVal[e]),
                    _ => new DeleteWordSpec(WordId(w)),
                };
                Add(editHour[e], change, createdEntity: null, dependsOnEntity: WordId(w));
            }
            else
            {
                var d = editTarget[e] % dCount;
                ChangeSpec change = (editOp[e] % 2) == 0
                    ? new SetDefinitionPartOfSpeechSpec(DefId(d), "pp" + editVal[e])
                    : new DeleteDefinitionSpec(DefId(d));
                Add(editHour[e], change, createdEntity: null, dependsOnEntity: DefId(d));
            }
        }

        // Antonym references between distinct words (band [8,14]) — need the SUBJECT word present.
        // The target word need not have arrived: SetAntonymReferenceChange no-ops on a missing target.
        if (wCount >= 2)
        {
            for (var a = 0; a < antCount; a++)
            {
                var from = antFrom[a] % wCount;
                var to = antTo[a] % wCount;
                if (from == to) continue;
                Add(antHour[a], new SetAntonymSpec(WordId(from), WordId(to)), createdEntity: null, dependsOnEntity: WordId(from));
            }
        }

        // Resolve each dependency-entity to the index of that entity's create commit.
        var deps = new int[specs.Count][];
        for (var i = 0; i < specs.Count; i++)
        {
            deps[i] = depDependsOnEntity[i] is { } entity && createIndexByEntity.TryGetValue(entity, out var createIdx)
                ? new[] { createIdx }
                : Array.Empty<int>();
        }

        return new SpecSet(specs.ToArray(), deps);
    }

    private static int[][] NoDependencies(int count)
    {
        var deps = new int[count][];
        for (var i = 0; i < count; i++) deps[i] = Array.Empty<int>();
        return deps;
    }

    // ---- Shared arrival planning ---------------------------------------------------------

    /// <summary>
    /// A delivery plan: the full commit set delivered in a random order that RESPECTS
    /// dependencies (each commit arrives no earlier than the commits in <paramref name="deps"/>),
    /// split into consecutive batches, followed by duplicate re-send batches. Arrival order is
    /// otherwise independent of author time, so stragglers, cascades and genesis-depth rollbacks
    /// abound — a low-keyed create arriving after other entities' high-keyed edits is still a
    /// straggler. Respecting dependencies mirrors real Harmony sync, which never delivers an
    /// edit before the create it needs. Duplicates always land in their own batches (a re-send of
    /// already-present commits), keeping each batch free of within-batch id collisions.
    /// </summary>
    private static Gen<IReadOnlyList<Arrival>> ArrivalPlan(CommitSpec[] specs, int[][] deps) =>
        from order in RandomLinearExtension(specs, deps)
        from originalBatches in SplitIntoBatches(order)
        from dupCount in Gen.Int[0, specs.Length / 3]
        from dupPick in Gen.Shuffle(specs)
        let dups = dupPick.Take(dupCount).ToArray()
        from dupBatches in dupCount == 0
            ? Gen.Const(Array.Empty<CommitSpec[]>())
            : SplitIntoBatches(dups)
        select (IReadOnlyList<Arrival>)originalBatches
            .Concat(dupBatches)
            .Select(b => new Arrival(b))
            .ToList();

    /// <summary>
    /// A uniformly-random-ish topological order (linear extension of the dependency DAG). Kahn's
    /// algorithm, breaking ties among ready commits by a generated priority — so different
    /// priority draws yield different valid arrival orders, while a dependency never precedes the
    /// commit that requires it.
    /// </summary>
    private static Gen<CommitSpec[]> RandomLinearExtension(CommitSpec[] specs, int[][] deps)
    {
        var n = specs.Length;
        if (n <= 1) return Gen.Const(specs);
        return Gen.Int[0, int.MaxValue].Array[n].Select(priorities =>
        {
            var indegree = new int[n];
            var dependents = new List<int>[n];
            for (var i = 0; i < n; i++) dependents[i] = new List<int>();
            for (var i = 0; i < n; i++)
            {
                foreach (var dep in deps[i])
                {
                    indegree[i]++;
                    dependents[dep].Add(i);
                }
            }

            // Ready set, always popping the smallest (priority, index) — a deterministic linear extension.
            var ready = new List<int>();
            for (var i = 0; i < n; i++)
                if (indegree[i] == 0) ready.Add(i);

            var order = new CommitSpec[n];
            var emitted = 0;
            while (ready.Count > 0)
            {
                var bestPos = 0;
                for (var k = 1; k < ready.Count; k++)
                {
                    var a = ready[k];
                    var b = ready[bestPos];
                    if (priorities[a] < priorities[b] || (priorities[a] == priorities[b] && a < b)) bestPos = k;
                }
                var next = ready[bestPos];
                ready.RemoveAt(bestPos);
                order[emitted++] = specs[next];
                foreach (var dependent in dependents[next])
                    if (--indegree[dependent] == 0) ready.Add(dependent);
            }

            // Cycles are impossible by construction; guard anyway so a bug surfaces loudly.
            if (emitted != n) throw new InvalidOperationException("dependency cycle in generated schedule");
            return order;
        });
    }

    private static Gen<CommitSpec[][]> SplitIntoBatches(CommitSpec[] stream)
    {
        if (stream.Length <= 1) return Gen.Const(new[] { stream });
        return Gen.Bool.Array[stream.Length - 1].Select(cuts => PartitionByCuts(stream, cuts));
    }

    private static CommitSpec[][] PartitionByCuts(CommitSpec[] stream, bool[] cuts)
    {
        var batches = new List<CommitSpec[]>();
        var current = new List<CommitSpec> { stream[0] };
        for (var i = 1; i < stream.Length; i++)
        {
            if (cuts[i - 1])
            {
                batches.Add(current.ToArray());
                current = new List<CommitSpec>();
            }
            current.Add(stream[i]);
        }
        batches.Add(current.ToArray());
        return batches.ToArray();
    }
}
