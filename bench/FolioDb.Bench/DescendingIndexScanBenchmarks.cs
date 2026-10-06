using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>Measures compound equality-prefix/range scans with an ascending vs. descending range component.</summary>
[MemoryDiagnoser]
public class DescendingIndexScanBenchmarks
{
    private const int N = 100_000;
    private string _path = "";
    private FolioDatabase _db = null!;
    private Collection _collection = null!;

    [Params(false, true)]
    public bool Descending { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("docs");
            c.CreateIndex(new Document { ["group"] = 1, ["value"] = Descending ? -1 : 1 });
            for (int i = 0; i < N; i++)
                c.Insert(new Document { ["_id"] = i, ["group"] = i % 10, ["value"] = i % 100 });
            tx.Commit();
        }
        _collection = _db.GetCollection("docs");
        var filter = Filter();
        if (_collection.Count(filter) != 6_000)
            throw new InvalidOperationException("Unexpected compound range result.");
    }

    private static Document Filter() => new()
    {
        ["group"] = 7,
        ["value"] = new Document { ["$gte"] = 20, ["$lt"] = 80 },
    };

    [Benchmark]
    public long Count() => _collection.Count(Filter());

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }
}
