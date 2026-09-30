using BenchmarkDotNet.Attributes;
using System.Diagnostics;
using FolioDb;

/// <summary>Equal numeric sums over indexed matches, without consuming the 1 KiB payload.</summary>
[MemoryDiagnoser]
public class VisitBenchmarks
{
    private string _path = "";
    private FolioDatabase _db = null!;
    private Collection _items = null!;
    private Document _filter = null!;
    private PreparedFilter _prepared = null!;
    private readonly FindOptions _projection = new() { Projection = new Document { ["n"] = 1, ["_id"] = 0 } };
    private Func<DocumentView, bool> _visitor = null!;
    private long _sum;

    [Params(1, 100, 1000)]
    public int Matches { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("items");
            c.CreateIndex("bucket");
            for (int i = 0; i < Workload.Documents; i++)
                c.Insert(new Document { ["_id"] = i, ["bucket"] = i / Matches, ["n"] = (long)i, ["payload"] = new string('x', 1024) });
            tx.Commit();
        }
        _db.Checkpoint();
        _items = _db.GetCollection("items");
        _filter = new Document { ["bucket"] = 0 };
        _prepared = PreparedFilter.FromDocument(_filter);
        _visitor = d =>
        {
            if (!d.TryGetValue("n", out var n)) throw new InvalidOperationException("Missing n.");
            _sum += n.AsInt64;
            return true;
        };
        long expected = (long)Matches * (Matches - 1) / 2;
        if (Materialized() != expected || Projected() != expected || Borrowed() != expected ||
            MaterializedPrepared() != expected || ProjectedPrepared() != expected || BorrowedPrepared() != expected)
            throw new InvalidOperationException("Read results differ.");
    }

    [Benchmark(Baseline = true)]
    public long Materialized()
    {
        long sum = 0;
        foreach (var d in _items.Find(_filter)) sum += d["n"].AsInt64;
        return sum;
    }

    [Benchmark]
    public long Projected()
    {
        long sum = 0;
        foreach (var d in _items.Find(_filter, _projection)) sum += d["n"].AsInt64;
        return sum;
    }

    [Benchmark]
    public long Borrowed()
    {
        _sum = 0;
        if (_items.Visit(_filter, _visitor) != Matches) throw new InvalidOperationException("Unexpected visit count.");
        return _sum;
    }

    [Benchmark]
    public long MaterializedPrepared()
    {
        long sum = 0;
        foreach (var d in _items.Find(_prepared)) sum += d["n"].AsInt64;
        return sum;
    }

    [Benchmark]
    public long ProjectedPrepared()
    {
        long sum = 0;
        foreach (var d in _items.Find(_prepared, _projection)) sum += d["n"].AsInt64;
        return sum;
    }

    [Benchmark]
    public long BorrowedPrepared()
    {
        _sum = 0;
        if (_items.Visit(_prepared, _visitor) != Matches) throw new InvalidOperationException("Unexpected visit count.");
        return _sum;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }
}

/// <summary>
/// Bounded, single-threaded profiling target: repeats one warm prepared-filter Visit over the VisitBenchmarks data set
/// so an external collector can attach between the printed LOAD_START and LOAD_END markers.
/// </summary>
public static class VisitProfile
{
    public static void Run(string[] args)
    {
        if (args.Length != 2 || !int.TryParse(args[0], out int matches) || matches is not (1 or 100 or 1000)
            || !int.TryParse(args[1], out int seconds) || seconds is < 5 or > 300)
            throw new ArgumentException("Usage: --profile-visit 1|100|1000 seconds:5..300");
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
            long sum = 0;
            Func<DocumentView, bool> visitor = d =>
            {
                if (!d.TryGetValue("n", out var n)) throw new InvalidOperationException("Missing n.");
                sum += n.AsInt64;
                return true;
            };
            long expected = (long)matches * (matches - 1) / 2;
            void Query()
            {
                sum = 0;
                if (items.Visit(prepared, visitor) != matches || sum != expected)
                    throw new InvalidOperationException("Unexpected visit result.");
            }

            var warmup = Stopwatch.StartNew();
            while (warmup.Elapsed.TotalSeconds < 3) Query();
            Console.WriteLine($"# PROFILE pid={Environment.ProcessId} visit prepared matches={matches} seconds={seconds}");
            Console.WriteLine($"# LOAD_START pid={Environment.ProcessId} utc={DateTime.UtcNow:O}");
            long queries = 0;
            var elapsed = Stopwatch.StartNew();
            while (elapsed.Elapsed.TotalSeconds < seconds)
            {
                for (int i = 0; i < 64; i++) Query();
                queries += 64;
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
