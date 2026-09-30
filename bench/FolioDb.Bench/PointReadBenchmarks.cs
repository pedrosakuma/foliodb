using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>Materialized point reads with inline, single-page overflow and multi-page overflow payloads.</summary>
[MemoryDiagnoser]
public class PointReadBenchmarks
{
    private string _path = "";
    private FolioDatabase _db = null!;
    private Snapshot _snapshot = null!;
    private Collection _collection = null!, _snapshotCollection = null!;
    private int _next;

    [Params(128, 1024, 8192)]
    public int PayloadBytes { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("items");
            for (int i = 0; i < Workload.Documents; i++)
                c.Insert(new Document { ["_id"] = i, ["n"] = 0L, ["payload"] = new string('x', PayloadBytes) });
            tx.Commit();
        }
        _db.Checkpoint();
        _collection = _db.GetCollection("items");
        _snapshot = _db.BeginSnapshot();
        _snapshotCollection = _snapshot.GetCollection("items");
        for (int i = 0; i < 256; i++)
        {
            _ = _collection.FindById(i);
            _ = _snapshotCollection.FindById(i);
        }
    }

    private int NextId() => _next = (_next + 79) % 256;

    [Benchmark]
    public Document? AutoTransaction() => _collection.FindById(NextId());

    [Benchmark]
    public Document? ExistingSnapshot() => _snapshotCollection.FindById(NextId());

    [GlobalCleanup]
    public void Cleanup()
    {
        _snapshot.Dispose();
        _db.Dispose();
        Workload.Delete(_path);
    }
}
