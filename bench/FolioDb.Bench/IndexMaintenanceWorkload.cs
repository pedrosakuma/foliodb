using System.Diagnostics;
using System.Globalization;
using FolioDb;
using FolioDb.Engine;
using FolioDb.Storage;

/// <summary>Paired deterministic rebuild versus transactional drop/create, with diagnostics outside timing.</summary>
public static class IndexMaintenanceWorkload
{
    public static void Run(string[] args)
    {
        int documents = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3000;
        int repeats = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 2;
        if (args.Length > 2 || documents is < 500 or > 20000 || repeats is < 1 or > 10)
            throw new ArgumentException("Usage: --index-maintenance [documents:500..20000] [repeats:1..10]");
        Console.WriteLine("# Paired seeded churn; page=4096, synchronous=Normal, auto-checkpoint=0; timings are shared-host observations.");
        Console.WriteLine("# Scratch is peak bytes in live sorted-run files; sort holds <=8 MiB of entries plus <=128 merge heads, WAL pages held in memory by transaction.");
        Console.WriteLine("trial,method,documents,entries_before,leaf_before,fragmented_before,entries_after,height,leaf_pages,interior_pages,leaf_live,leaf_fragmented,leaf_free,elapsed_ms,allocated_bytes,working_set_before,working_set_after,scratch_bytes,runs,wal_frames_added,wal_bytes_added,free_pages_after");
        for (int trial = 1; trial <= repeats; trial++)
            foreach (bool rebuild in trial % 2 == 0 ? new[] { true, false } : new[] { false, true })
                Measure(documents, trial, rebuild);
    }

    private static void Measure(int documents, int trial, bool rebuild)
    {
        var path = Workload.TempFile(".folio");
        try
        {
            using var db = FolioDatabase.Open(path, new FolioOptions
            {
                PageSize = 4096,
                CacheSizePages = 1024,
                AutoCheckpointFrames = 0,
                Synchronous = SynchronousMode.Normal,
            });
            var pattern = Document.Parse("{bucket:1,rank:-1}");
            var c = db.GetCollection("items");
            c.CreateIndex(pattern);
            using (var tx = db.BeginTransaction())
            {
                var collection = tx.GetCollection("items");
                for (int id = 0; id < documents; id++)
                    collection.Insert(Doc(id, 0));
                tx.Commit();
            }
            using (var tx = db.BeginTransaction())
            {
                var collection = tx.GetCollection("items");
                for (int id = 0; id < documents; id++)
                    if (id % 3 == 0) collection.DeleteById(id);
                    else if (id % 5 == 0)
                        collection.UpdateOne(new Document { ["_id"] = id },
                            new Document { ["$set"] = new Document { ["bucket"] = (id + trial * 19) % 71 } });
                for (int id = 0; id < documents; id++)
                    if (id % 3 == 0) collection.Insert(Doc(documents + id, 1));
                tx.Commit();
            }
            var before = Tree(db);
            var stats = db.GetStats();
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            long workingBefore = process.WorkingSet64;
            long allocated = GC.GetAllocatedBytesForCurrentThread();
            var stopwatch = Stopwatch.StartNew();
            using (var tx = db.BeginTransaction())
            {
                var collection = tx.GetCollection("items");
                if (rebuild)
                {
                    if (!collection.RebuildIndex(pattern)) throw new InvalidOperationException("Index disappeared.");
                }
                else
                {
                    if (!collection.DropIndex(pattern)) throw new InvalidOperationException("Index disappeared.");
                    collection.CreateIndex(pattern);
                }
                tx.Commit();
            }
            stopwatch.Stop();
            allocated = GC.GetAllocatedBytesForCurrentThread() - allocated;
            process.Refresh();
            long workingAfter = process.WorkingSet64;
            var afterStats = db.GetStats();
            var after = Tree(db);
            db.CheckIntegrity();
            if (after.EntryCount != before.EntryCount) throw new InvalidOperationException("Index entry count changed.");
            var scratch = rebuild ? IndexSort.LastMetrics : default;
            Console.WriteLine(string.Join(',', new object[]
            {
                trial, rebuild ? "rebuild" : "drop-create", documents,
                before.EntryCount, before.LeafPages, before.LeafFragmentedBytes,
                after.EntryCount, after.Height, after.LeafPages, after.InteriorPages,
                after.LeafLiveBytes, after.LeafFragmentedBytes, after.LeafFreeBytes,
                stopwatch.Elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture),
                allocated, workingBefore, workingAfter, scratch.ScratchBytes, scratch.Runs,
                afterStats.WalFrames - stats.WalFrames,
                afterStats.WalFileBytes - stats.WalFileBytes,
                afterStats.FreePages,
            }.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture))));
        }
        finally { Workload.Delete(path); }
    }

    private static BTreeStorageDiagnostics Tree(FolioDatabase db) =>
        db.GetStorageDiagnostics().Trees.Single(t => t.Index == "bucket_1_rank_-1").Tree;

    private static Document Doc(int id, int generation) => new()
    {
        ["_id"] = id,
        ["bucket"] = (id * 73 + generation * 17) % 71,
        ["rank"] = id % 4 == 0 ? (DocValue)(decimal)(id % 137) : (long)(id % 137),
        ["payload"] = new string((char)('a' + id % 26), 64 + id % 41),
    };
}
