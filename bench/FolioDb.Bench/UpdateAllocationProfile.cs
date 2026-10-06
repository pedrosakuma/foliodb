using System.Diagnostics;
using FolioDb;

/// <summary>Bounded update workload using reusable filter/update documents and one explicit transaction.</summary>
public static class UpdateProfile
{
    private const int DocumentCount = 50_000;

    public static void Run(string[] args)
    {
        if (args.Length != 1 || !int.TryParse(args[0], out int seconds) || seconds is < 5 or > 300)
            throw new ArgumentException("Usage: --profile-update seconds:5..300");
        string path = Workload.TempFile(".folio");
        try
        {
            using var db = FolioDatabase.Open(path, new FolioOptions { Synchronous = SynchronousMode.Normal });
            using (var setup = db.BeginTransaction())
            {
                var collection = setup.GetCollection("docs");
                collection.CreateIndex("city");
                for (int i = 0; i < DocumentCount; i++)
                    collection.Insert(new Document
                    {
                        ["_id"] = i,
                        ["name"] = "user " + i,
                        ["city"] = Workload.Cities[i % Workload.Cities.Length],
                        ["age"] = i % 90,
                        ["score"] = i * 0.5,
                    });
                setup.Commit();
            }
            if (!db.Checkpoint()) throw new InvalidOperationException("Setup checkpoint blocked.");

            using var tx = db.BeginTransaction();
            var docs = tx.GetCollection("docs");
            var filter = new Document { ["_id"] = 0 };
            var set = new Document { ["$set"] = new Document { ["score"] = 0.0 } };
            for (int i = 0; i < 10; i++)
            {
                filter["_id"] = i;
                set["$set"].AsDocument["score"] = i * 0.25;
                if (docs.UpdateOne(filter, set).MatchedCount != 1)
                    throw new InvalidOperationException("Update profile warmup failed.");
            }

            var warmup = Stopwatch.StartNew();
            int next = 10, pass = 0;
            void Batch()
            {
                for (int end = 0; end < 256; end++)
                {
                    if (next == DocumentCount)
                    {
                        next = 0;
                        pass++;
                    }
                    int id = next++;
                    filter["_id"] = id;
                    set["$set"].AsDocument["score"] = (pass & 1) == 0 ? id * 0.25 : id * 0.5;
                    docs.UpdateOne(filter, set);
                }
            }
            while (warmup.Elapsed.TotalSeconds < 3) Batch();

            Console.WriteLine($"# PROFILE pid={Environment.ProcessId} update reused-inputs docs={DocumentCount} seconds={seconds}");
            Console.WriteLine($"# LOAD_START pid={Environment.ProcessId} utc={DateTime.UtcNow:O}");
            long updates = 0;
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                Batch();
                updates += 256;
            }
            double total = elapsed.Elapsed.TotalSeconds;
            Console.WriteLine($"# LOAD_END pid={Environment.ProcessId} utc={DateTime.UtcNow:O}");
            Console.WriteLine("seconds,updates,updates_per_s,ns_per_update");
            Console.WriteLine(FormattableString.Invariant($"{total:F1},{updates},{updates / total:F0},{total * 1e9 / updates:F1}"));
            tx.Commit();
        }
        finally
        {
            Workload.Delete(path);
        }
    }
}
