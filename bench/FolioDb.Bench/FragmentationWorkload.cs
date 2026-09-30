using System.Diagnostics;
using System.Globalization;
using FolioDb;
using FolioDb.Storage;

/// <summary>Deterministic occupancy and churn diagnostic; timings are secondary shared-host observations.</summary>
public static class FragmentationWorkload
{
    private const int PageSize = 4096;
    private const int CachePages = 1024;
    private const int PointOperations = 2000;
    private const int RangeOperations = 300;
    private const int WriteOperations = 200;

    public static void Run(string[] args)
    {
        int documents = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3000;
        int cycles = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 4;
        int repeats = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : 3;
        if (args.Length > 3 || documents is < 500 or > 20000 || cycles is < 1 or > 10 || repeats is < 1 or > 10)
            throw new ArgumentException("Usage: --fragmentation [documents:500..20000] [cycles:1..10] [repeats:1..10]");

        Console.WriteLine("# Deterministic churn diagnostic; metrics and integrity checks are outside timed sections.");
        Console.WriteLine($"# page={PageSize}; cache={CachePages}; synchronous=Normal; auto-checkpoint=0; documents={documents}; cycles={cycles}; repeats={repeats}");
        Console.WriteLine("# Policies are paired on identical seeds; execution order alternates by trial. leaf-bytes is internal-only: trigger <30%, merge <=75%, otherwise redistribute.");
        Console.WriteLine("# Initial documents: 1/8 overflow (~9KB payload), otherwise inline; simple city, compound bucket/score, multikey tags indexes.");
        Console.WriteLine("# Each cycle interleaves 10% concentrated and 10% seeded-random deletes, inserts the same count, then swaps equal inline/overflow populations while changing index keys.");
        Console.WriteLine("METRIC,policy,trial,stage,tree,kind,multikey,height,entries,leaf_pages,interior_pages,overflow_pages,leaf_live,leaf_cells,leaf_fragmented,leaf_free,interior_live,interior_cells,interior_fragmented,interior_free,overflow_live,overflow_payload,overflow_free,page_runs,page_span,max_page_gap,total_pages,free_pages,db_bytes,wal_bytes");
        Console.WriteLine("DELETE,policy,trial,cycle,deletes,delete_total_us,delete_p50_us,delete_p95_us,delete_p99_us,commit_us,page_writes,wal_bytes,free_pages_added");
        Console.WriteLine("PERF,policy,trial,stage,point_ops,point_us_op,range_ops,range_us_op,write_ops,write_us_op,checksum");

        for (int trial = 1; trial <= repeats; trial++)
        {
            var policies = trial % 2 == 0
                ? new[] { BTreeDeleteRebalanceMode.LeafByteOccupancy, BTreeDeleteRebalanceMode.None }
                : new[] { BTreeDeleteRebalanceMode.None, BTreeDeleteRebalanceMode.LeafByteOccupancy };
            foreach (var policy in policies) RunTrial(documents, cycles, trial, policy);
        }
    }

    private static void RunTrial(int documents, int cycles, int trial, BTreeDeleteRebalanceMode policy)
    {
        string path = Workload.TempFile(".folio");
        string policyName = policy == BTreeDeleteRebalanceMode.None ? "none" : "leaf-bytes";
        try
        {
            using var db = FolioDatabase.Open(path, new FolioOptions
            {
                PageSize = PageSize,
                CacheSizePages = CachePages,
                Synchronous = SynchronousMode.Normal,
                AutoCheckpointFrames = 0,
                DeleteRebalance = policy,
            });
            var alive = new HashSet<int>();
            var overflow = new HashSet<int>();
            using (var tx = db.BeginTransaction())
            {
                var collection = tx.GetCollection("items");
                collection.CreateIndex("city");
                collection.CreateIndex(Document.Parse("{ bucket: 1, score: -1 }"));
                collection.CreateIndex("tags");
                for (int id = 0; id < documents; id++)
                {
                    collection.Insert(CreateDocument(id, 0));
                    alive.Add(id);
                    if (IsInitialOverflow(id)) overflow.Add(id);
                }
                tx.Commit();
            }

            WarmDeletePath(db, alive);
            Capture(db, policyName, trial, "baseline");
            Measure(db, alive, policyName, trial, "baseline");
            int nextId = documents;
            var random = new Random(0x5F0110 + trial);
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                int target = Math.Max(1, documents / 10);
                var ordered = alive.Order().ToArray();
                int start = ordered.Length == target ? 0 : (cycle * 997) % (ordered.Length - target);
                var concentrated = ordered.Skip(start).Take(target).ToArray();
                var afterConcentrated = ordered.Except(concentrated).ToArray();
                Shuffle(afterConcentrated, random);
                var scattered = afterConcentrated.Take(Math.Min(target, afterConcentrated.Length)).ToArray();

                var deleteOrder = Interleave(concentrated, scattered);
                long freePagesBeforeDelete = db.GetStats().FreePages;
                var deleteMicros = new double[deleteOrder.Length];
                double commitMicros;
                using (var tx = db.BeginTransaction())
                {
                    var collection = tx.GetCollection("items");
                    for (int i = 0; i < deleteOrder.Length; i++)
                    {
                        long started = Stopwatch.GetTimestamp();
                        AssertDeleted(collection, deleteOrder[i]);
                        deleteMicros[i] = Stopwatch.GetElapsedTime(started).TotalMicroseconds;
                    }
                    long commitStarted = Stopwatch.GetTimestamp();
                    tx.Commit();
                    commitMicros = Stopwatch.GetElapsedTime(commitStarted).TotalMicroseconds;
                }
                alive.ExceptWith(concentrated);
                alive.ExceptWith(scattered);
                overflow.ExceptWith(concentrated);
                overflow.ExceptWith(scattered);
                var deleteStats = db.GetStats();
                Array.Sort(deleteMicros);
                Console.WriteLine(string.Join(',', new[]
                {
                    "DELETE",
                    policyName,
                    trial.ToString(CultureInfo.InvariantCulture),
                    cycle.ToString(CultureInfo.InvariantCulture),
                    deleteMicros.Length.ToString(CultureInfo.InvariantCulture),
                    deleteMicros.Sum().ToString("F3", CultureInfo.InvariantCulture),
                    Percentile(deleteMicros, 0.50).ToString("F3", CultureInfo.InvariantCulture),
                    Percentile(deleteMicros, 0.95).ToString("F3", CultureInfo.InvariantCulture),
                    Percentile(deleteMicros, 0.99).ToString("F3", CultureInfo.InvariantCulture),
                    commitMicros.ToString("F3", CultureInfo.InvariantCulture),
                    deleteStats.WalFrames.ToString(CultureInfo.InvariantCulture),
                    deleteStats.WalFileBytes.ToString(CultureInfo.InvariantCulture),
                    (deleteStats.FreePages - freePagesBeforeDelete).ToString(CultureInfo.InvariantCulture),
                }));
                Capture(db, policyName, trial, $"cycle{cycle}-delete");

                int insertCount = concentrated.Length + scattered.Length;
                using (var tx = db.BeginTransaction())
                {
                    var collection = tx.GetCollection("items");
                    for (int i = 0; i < insertCount; i++)
                    {
                        int id = nextId++;
                        collection.Insert(CreateDocument(id, cycle));
                        alive.Add(id);
                        if (IsInitialOverflow(id)) overflow.Add(id);
                    }
                    tx.Commit();
                }
                Capture(db, policyName, trial, $"cycle{cycle}-insert");

                var shrinkIds = overflow.ToArray();
                var growIds = alive.Where(id => !overflow.Contains(id)).ToArray();
                Shuffle(shrinkIds, random);
                Shuffle(growIds, random);
                int swapCount = Math.Min(Math.Min(shrinkIds.Length, growIds.Length), Math.Max(1, documents / 10));
                using (var tx = db.BeginTransaction())
                {
                    var collection = tx.GetCollection("items");
                    for (int i = 0; i < swapCount * 2; i++)
                    {
                        bool grow = i >= swapCount;
                        int id = grow ? growIds[i - swapCount] : shrinkIds[i];
                        var update = new Document
                        {
                            ["$set"] = new Document
                            {
                                ["payload"] = new string((char)('a' + cycle % 20), grow ? 9000 + i % 257 : 180 + i % 101),
                                ["city"] = $"city-{(id + cycle * 17) % 64:D2}",
                                ["bucket"] = (id * 3 + cycle) % 32,
                                ["score"] = (id * 29 + cycle * 101) % 10000,
                                ["tags"] = Tags(id + cycle * 11, cycle),
                            },
                        };
                        var result = collection.UpdateOne(new Document { ["_id"] = id }, update);
                        if (result.ModifiedCount != 1) throw new InvalidOperationException($"Update failed for {id}.");
                    }
                    tx.Commit();
                }
                overflow.ExceptWith(shrinkIds.Take(swapCount));
                overflow.UnionWith(growIds.Take(swapCount));
                Capture(db, policyName, trial, $"cycle{cycle}-update");
            }

            Capture(db, policyName, trial, "final");
            Measure(db, alive, policyName, trial, "final");
            if (db.GetCollection("items").Count() != documents) throw new InvalidOperationException("Document count changed.");
            db.CheckIntegrity();
        }
        finally
        {
            Workload.Delete(path);
        }
    }

    private static Document CreateDocument(int id, int generation) => new()
    {
        ["_id"] = id,
        ["city"] = $"city-{(id + generation * 7) % 64:D2}",
        ["bucket"] = (id + generation * 5) % 32,
        ["score"] = (id * 37 + generation * 97) % 10000,
        ["tags"] = Tags(id, generation),
        ["probe"] = 0L,
        ["payload"] = new string((char)('a' + generation % 20), IsInitialOverflow(id) ? 9000 + id % 311 : 180 + id % 281),
    };

    private static bool IsInitialOverflow(int id) => id % 8 == 0;

    private static DocArray Tags(int id, int generation) =>
        new()
        {
            $"tag-{(id + generation) % 48:D2}",
            $"tag-{(id * 5 + generation * 3) % 48:D2}",
            $"tag-{(id * 11 + generation * 7) % 48:D2}",
        };

    private static void AssertDeleted(Collection collection, int id)
    {
        if (!collection.DeleteById(id)) throw new InvalidOperationException($"Delete failed for {id}.");
    }

    private static void WarmDeletePath(FolioDatabase db, HashSet<int> alive)
    {
        using var tx = db.BeginTransaction();
        var collection = tx.GetCollection("items");
        foreach (int id in alive.Order().Where((_, index) => index % 5 == 0))
            AssertDeleted(collection, id);
    }

    private static void Capture(FolioDatabase db, string policy, int trial, string stage)
    {
        if (!db.Checkpoint()) throw new InvalidOperationException("Checkpoint was blocked.");
        db.CheckIntegrity();
        var diagnostics = db.GetStorageDiagnostics();
        foreach (var item in diagnostics.Trees)
        {
            var tree = item.Tree;
            Console.WriteLine(string.Join(',', new[]
            {
                "METRIC",
                policy,
                trial.ToString(CultureInfo.InvariantCulture),
                stage,
                item.Name,
                item.Kind.ToString(),
                item.MultiKey ? "1" : "0",
                tree.Height.ToString(CultureInfo.InvariantCulture),
                tree.EntryCount.ToString(CultureInfo.InvariantCulture),
                tree.LeafPages.ToString(CultureInfo.InvariantCulture),
                tree.InteriorPages.ToString(CultureInfo.InvariantCulture),
                tree.OverflowPages.ToString(CultureInfo.InvariantCulture),
                tree.LeafLiveBytes.ToString(CultureInfo.InvariantCulture),
                tree.LeafCellBytes.ToString(CultureInfo.InvariantCulture),
                tree.LeafFragmentedBytes.ToString(CultureInfo.InvariantCulture),
                tree.LeafFreeBytes.ToString(CultureInfo.InvariantCulture),
                tree.InteriorLiveBytes.ToString(CultureInfo.InvariantCulture),
                tree.InteriorCellBytes.ToString(CultureInfo.InvariantCulture),
                tree.InteriorFragmentedBytes.ToString(CultureInfo.InvariantCulture),
                tree.InteriorFreeBytes.ToString(CultureInfo.InvariantCulture),
                tree.OverflowLiveBytes.ToString(CultureInfo.InvariantCulture),
                tree.OverflowPayloadBytes.ToString(CultureInfo.InvariantCulture),
                tree.OverflowFreeBytes.ToString(CultureInfo.InvariantCulture),
                tree.AllocatedPageRuns.ToString(CultureInfo.InvariantCulture),
                tree.AllocatedPageSpan.ToString(CultureInfo.InvariantCulture),
                tree.MaximumPageGap.ToString(CultureInfo.InvariantCulture),
                diagnostics.Database.PageCount.ToString(CultureInfo.InvariantCulture),
                diagnostics.Database.FreePages.ToString(CultureInfo.InvariantCulture),
                diagnostics.Database.DatabaseFileBytes.ToString(CultureInfo.InvariantCulture),
                diagnostics.Database.WalFileBytes.ToString(CultureInfo.InvariantCulture),
            }));
        }
    }

    private static void Measure(FolioDatabase db, HashSet<int> alive, string policy, int trial, string stage)
    {
        if (!db.Checkpoint()) throw new InvalidOperationException("Checkpoint was blocked.");
        var collection = db.GetCollection("items");
        int[] ids = alive.Order().ToArray();
        int[] probes = Enumerable.Range(0, PointOperations).Select(i => ids[(i * 7919 + trial * 101) % ids.Length]).ToArray();
        var ranges = Enumerable.Range(0, RangeOperations).Select(i =>
            new Document
            {
                ["bucket"] = i % 32,
                ["score"] = new Document { ["$gte"] = (i * 31) % 9000, ["$lt"] = (i * 31) % 9000 + 1000 },
            }).ToArray();
        int[] writeIds = Enumerable.Range(0, WriteOperations).Select(i => ids[(i * 3571 + trial * 53) % ids.Length]).ToArray();
        var writeFilters = writeIds.Select(id => new Document { ["_id"] = id }).ToArray();
        var increment = new Document { ["$inc"] = new Document { ["probe"] = 1L } };

        for (int i = 0; i < Math.Min(512, probes.Length); i++) _ = collection.FindById(probes[i]);
        for (int i = 0; i < Math.Min(64, ranges.Length); i++) _ = collection.Count(ranges[i]);
        if (collection.UpdateOne(new Document { ["_id"] = ids[0] }, increment).ModifiedCount != 1)
            throw new InvalidOperationException("Warmup update failed.");
        if (!db.Checkpoint()) throw new InvalidOperationException("Checkpoint after warmup was blocked.");

        long checksum = 0;
        var stopwatch = Stopwatch.StartNew();
        foreach (int id in probes) checksum += collection.FindById(id)!["_id"].AsInt32;
        stopwatch.Stop();
        double pointUs = stopwatch.Elapsed.TotalMicroseconds / probes.Length;

        stopwatch.Restart();
        foreach (var range in ranges) checksum += collection.Count(range);
        stopwatch.Stop();
        double rangeUs = stopwatch.Elapsed.TotalMicroseconds / ranges.Length;

        stopwatch.Restart();
        foreach (var filter in writeFilters)
        {
            var result = collection.UpdateOne(filter, increment);
            if (result.ModifiedCount != 1) throw new InvalidOperationException("Timed update failed.");
        }
        stopwatch.Stop();
        double writeUs = stopwatch.Elapsed.TotalMicroseconds / writeFilters.Length;

        Console.WriteLine(string.Join(',', new[]
        {
            "PERF",
            policy,
            trial.ToString(CultureInfo.InvariantCulture),
            stage,
            probes.Length.ToString(CultureInfo.InvariantCulture),
            pointUs.ToString("F3", CultureInfo.InvariantCulture),
            ranges.Length.ToString(CultureInfo.InvariantCulture),
            rangeUs.ToString("F3", CultureInfo.InvariantCulture),
            writeFilters.Length.ToString(CultureInfo.InvariantCulture),
            writeUs.ToString("F3", CultureInfo.InvariantCulture),
            checksum.ToString(CultureInfo.InvariantCulture),
        }));
        if (!db.Checkpoint()) throw new InvalidOperationException("Checkpoint after timed writes was blocked.");
    }

    private static void Shuffle<T>(T[] values, Random random)
    {
        for (int i = values.Length - 1; i > 0; i--)
        {
            int j = random.Next(i + 1);
            (values[i], values[j]) = (values[j], values[i]);
        }
    }

    private static int[] Interleave(int[] first, int[] second)
    {
        var result = new int[first.Length + second.Length];
        int write = 0;
        for (int i = 0; i < Math.Max(first.Length, second.Length); i++)
        {
            if (i < first.Length) result[write++] = first[i];
            if (i < second.Length) result[write++] = second[i];
        }
        return result;
    }

    private static double Percentile(double[] sorted, double percentile)
    {
        if (sorted.Length == 0) return 0;
        int index = (int)Math.Ceiling(percentile * sorted.Length) - 1;
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
