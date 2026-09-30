using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>Equal numeric sums over indexed matches, without consuming the 1 KiB payload.</summary>
[MemoryDiagnoser]
public class VisitBenchmarks
{
    private string _path = "";
    private FolioDatabase _db = null!;
    private Collection _items = null!;
    private Document _filter = null!;
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
        _visitor = d =>
        {
            if (!d.TryGetValue("n", out var n)) throw new InvalidOperationException("Missing n.");
            _sum += n.AsInt64;
            return true;
        };
        long expected = (long)Matches * (Matches - 1) / 2;
        if (Materialized() != expected || Projected() != expected || Borrowed() != expected)
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

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }
}
