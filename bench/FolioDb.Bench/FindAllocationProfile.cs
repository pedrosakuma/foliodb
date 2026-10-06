using System.Diagnostics;
using FolioDb;

/// <summary>
/// Bounded, single-threaded profiling target: repeats one warm prepared-filter Find that materializes every match.
/// </summary>
public static class FindProfile
{
    public static void Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out int matches) || matches is not (1 or 100 or 1000)
            || !int.TryParse(args[1], out int seconds) || seconds is < 5 or > 300)
            throw new ArgumentException("Usage: --profile-find 1|100|1000 seconds:5..300");
        string path = Workload.TempFile(".folio");
        try
        {
            using var db = FolioDatabase.Open(path, new FolioOptions { Synchronous = SynchronousMode.Normal });
            using (var tx = db.BeginTransaction())
            {
                var c = tx.GetCollection("items");
                c.CreateIndex("bucket");
                for (int i = 0; i < Workload.Documents; i++)
                    c.Insert(new Document { ["_id"] = i, ["bucket"] = i / matches, ["n"] = (long)i, ["payload"] = new string('x', 1024) });
                tx.Commit();
            }
            if (!db.Checkpoint()) throw new InvalidOperationException("Setup checkpoint blocked.");
            var items = db.GetCollection("items");
            var prepared = PreparedFilter.FromDocument(new Document { ["bucket"] = 0 });
            long expected = (long)matches * (matches - 1) / 2;
            long Query()
            {
                var documents = items.Find(prepared);
                if (documents.Count != matches) throw new InvalidOperationException("Unexpected Find result count.");
                long sum = 0;
                foreach (var document in documents) sum += document["n"].AsInt64;
                if (sum != expected) throw new InvalidOperationException("Unexpected Find result values.");
                return sum;
            }

            var warmup = Stopwatch.StartNew();
            while (warmup.Elapsed.TotalSeconds < 3) Query();
            Console.WriteLine($"# PROFILE pid={Environment.ProcessId} find prepared matches={matches} seconds={seconds}");
            Console.WriteLine($"# LOAD_START pid={Environment.ProcessId} utc={DateTime.UtcNow:O}");
            long queries = 0;
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                for (int i = 0; i < 4; i++) Query();
                queries += 4;
            }
            double total = elapsed.Elapsed.TotalSeconds;
            Console.WriteLine($"# LOAD_END pid={Environment.ProcessId} utc={DateTime.UtcNow:O}");
            Console.WriteLine("matches,seconds,queries,queries_per_s,us_per_query");
            Console.WriteLine(FormattableString.Invariant($"{matches},{total:F1},{queries},{queries / total:F0},{total * 1e6 / queries:F3}"));
        }
        finally
        {
            Workload.Delete(path);
        }
    }
}
