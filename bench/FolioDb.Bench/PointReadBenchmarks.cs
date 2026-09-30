using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>
/// Point reads with inline, single-page overflow and multi-page overflow payloads: full materialization, plus the
/// same scalar result (<c>n + payload length</c>) computed from a materialized document or a borrowed view.
/// </summary>
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
            if (Materialized(_collection, i) != Borrowed(_collection, i) || Borrowed(_snapshotCollection, i) != PayloadBytes)
                throw new InvalidOperationException("Materialized and borrowed scalar results differ.");
        }
    }

    private static long Materialized(Collection c, int id)
    {
        var d = c.FindById(id)!;
        return d["n"].AsInt64 + d["payload"].AsString.Length;
    }

    private static long Borrowed(Collection c, int id)
    {
        c.TryReadById(id, static d =>
        {
            d.TryGetValue("n", out var n);
            d.TryGetValue("payload", out var payload);
            return n.AsInt64 + payload.AsUtf8String.Length; // ASCII payload: bytes == chars
        }, out long result);
        return result;
    }

    private int NextId() => _next = (_next + 79) % 256;

    [Benchmark]
    public Document? AutoTransaction() => _collection.FindById(NextId());

    [Benchmark]
    public Document? ExistingSnapshot() => _snapshotCollection.FindById(NextId());

    [Benchmark]
    public long AutoMaterializedScalar() => Materialized(_collection, NextId());

    [Benchmark]
    public long AutoBorrowedScalar() => Borrowed(_collection, NextId());

    [Benchmark]
    public long SnapshotMaterializedScalar() => Materialized(_snapshotCollection, NextId());

    [Benchmark]
    public long SnapshotBorrowedScalar() => Borrowed(_snapshotCollection, NextId());

    [GlobalCleanup]
    public void Cleanup()
    {
        _snapshot.Dispose();
        _db.Dispose();
        Workload.Delete(_path);
    }
}
