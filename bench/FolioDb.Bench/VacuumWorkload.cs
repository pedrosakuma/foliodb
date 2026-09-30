using System.Diagnostics;
using System.Globalization;
using FolioDb;

/// <summary>Paired seeded churn measurement for explicit compact-copy vacuuming.</summary>
public static class VacuumWorkload
{
    public static void Run(string[] args)
    {
        int documents = args.Length > 0 ? int.Parse(args[0], CultureInfo.InvariantCulture) : 3000;
        int repeats = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 3;
        if (args.Length > 2 || documents is < 500 or > 20000 || repeats is < 1 or > 10)
            throw new ArgumentException("Usage: --vacuum [documents:500..20000] [repeats:1..10]");

        Console.WriteLine("# Paired seeded churn; page=4096, synchronous=Normal source, vacuum output is Full and checkpointed.");
        Console.WriteLine("# process_working_set is sampled before/after only, not a peak-memory claim.");
        Console.WriteLine("trial,kind,documents,page_count,free_pages,db_bytes,wal_bytes,elapsed_ms,point_us_op,index_us_op,checksum,working_set_bytes");
        for (int trial = 1; trial <= repeats; trial++) RunTrial(documents, trial);
    }

    private static void RunTrial(int documents, int trial)
    {
        string sourcePath = Workload.TempFile(".folio");
        string outputPath = sourcePath + ".vacuum";
        try
        {
            using (var source = FolioDatabase.Open(sourcePath, new FolioOptions
            {
                PageSize = 4096,
                AutoCheckpointFrames = 0,
                Synchronous = SynchronousMode.Normal,
            }))
            {
            var c = source.GetCollection("items");
            c.CreateIndex(Document.Parse("{bucket:1,rank:-1}"));
            c.CreateIndex("tags");
            for (int id = 0; id < documents; id++)
                c.Insert(new Document
                {
                    ["_id"] = id,
                    ["bucket"] = id % 64,
                    ["rank"] = id * 17 % 10000,
                    ["tags"] = new DocArray { id % 23, id % 41 },
                    ["payload"] = new string((char)('a' + id % 20), id % 8 == 0 ? 9000 + id % 257 : 300 + id % 101),
                });
            for (int id = 0; id < documents; id += 3) c.DeleteById(id);
            source.Checkpoint();
            Capture(source, "source", documents, trial, TimeSpan.Zero);

            long wsBefore = Process.GetCurrentProcess().WorkingSet64;
            long started = Stopwatch.GetTimestamp();
            source.VacuumInto(outputPath);
            TimeSpan elapsed = Stopwatch.GetElapsedTime(started);
            using (var output = FolioDatabase.Open(outputPath, new FolioOptions { AutoCheckpointFrames = 0 }))
            {
                output.CheckIntegrity();
                if (output.GetCollection("items").Count() != c.Count()) throw new InvalidOperationException("Vacuum changed document count.");
                Capture(output, "vacuum", documents, trial, elapsed, wsBefore);
            }
            }
        }
        finally
        {
            Workload.Delete(sourcePath);
            Workload.Delete(outputPath);
        }
    }

    private static void Capture(FolioDatabase db, string kind, int documents, int trial, TimeSpan elapsed, long? workingSet = null)
    {
        var stats = db.GetStats();
        var c = db.GetCollection("items");
        long checksum = 0;
        long start = Stopwatch.GetTimestamp();
        for (int i = 1; i < 1001; i++)
            checksum += c.FindById((i * 7919) % documents)?.Count ?? 0;
        double point = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 1000;
        start = Stopwatch.GetTimestamp();
        for (int i = 0; i < 300; i++)
            checksum += c.Count(new Document { ["bucket"] = i % 64, ["rank"] = new Document { ["$gte"] = 0 } });
        double indexed = Stopwatch.GetElapsedTime(start).TotalMicroseconds / 300;
        Console.WriteLine(string.Join(',', new[]
        {
            trial.ToString(CultureInfo.InvariantCulture), kind, documents.ToString(CultureInfo.InvariantCulture),
            stats.PageCount.ToString(CultureInfo.InvariantCulture), stats.FreePages.ToString(CultureInfo.InvariantCulture),
            stats.DatabaseFileBytes.ToString(CultureInfo.InvariantCulture), stats.WalFileBytes.ToString(CultureInfo.InvariantCulture),
            elapsed.TotalMilliseconds.ToString("F3", CultureInfo.InvariantCulture), point.ToString("F3", CultureInfo.InvariantCulture),
            indexed.ToString("F3", CultureInfo.InvariantCulture), checksum.ToString(CultureInfo.InvariantCulture),
            (workingSet ?? Process.GetCurrentProcess().WorkingSet64).ToString(CultureInfo.InvariantCulture),
        }));
    }
}
