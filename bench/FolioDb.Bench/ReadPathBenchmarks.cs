using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>
/// Read paths over 10,000 documents with indexes on "city" (100 distinct values) and "age" (90 distinct values),
/// plus updates that do not touch any indexed field.
/// </summary>
[MemoryDiagnoser]
public class ReadPathBenchmarks
{
    private const int UpdatesPerInvoke = 100;
    private string _path = "";
    private FolioDatabase _db = null!;
    private FolioDb.Collection _users = null!;
    private int _next;
    // Documents are revisited every 10,000 updates; 10,000 % 3 != 0, so each $set changes the value size (full rewrite).
    private long _tick;
    private readonly FolioDb.Document _cityProjection = new() { ["city"] = 1, ["_id"] = 0 };
    private readonly FolioDb.Document _idProjection = new() { ["_id"] = 1 };

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var col = tx.GetCollection("users");
            col.CreateIndex("city");
            col.CreateIndex("age");
            for (int i = 0; i < Workload.Documents; i++) col.Insert(FolioDb.Document.Parse(Workload.Json(i)));
            tx.Commit();
        }
        _db.Checkpoint();
        _users = _db.GetCollection("users");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }

    private int NextId() => _next = (_next + 7919) % Workload.Documents;
    private FolioDb.Document CityFilter() => new() { ["city"] = Workload.Cities[NextId() % 100] };

    [Benchmark]
    public int Find() => _users.Find(CityFilter()).Count;

    [Benchmark]
    public int FindReadField()
    {
        int n = 0;
        foreach (var d in _users.Find(CityFilter())) n += d["name"].AsString.Length;
        return n;
    }

    [Benchmark]
    public int FindProjectedIndexField() => _users.Find(CityFilter(), new FindOptions { Projection = _cityProjection }).Count;

    [Benchmark]
    public int FindProjectedId() => _users.Find(CityFilter(), new FindOptions { Projection = _idProjection }).Count;

    [Benchmark]
    public long CountEq() => _users.Count(CityFilter());

    [Benchmark]
    public long CountRange()
    {
        int lo = NextId() % 80;
        return _users.Count(new FolioDb.Document { ["age"] = new FolioDb.Document { ["$gte"] = lo, ["$lt"] = lo + 10 } });
    }

    [Benchmark(OperationsPerInvoke = UpdatesPerInvoke)]
    public void UpdatePatchNotIndexed()
    {
        using var tx = _db.BeginTransaction();
        var c = tx.GetCollection("users");
        for (int i = 0; i < UpdatesPerInvoke; i++)
            c.UpdateOne(new FolioDb.Document { ["_id"] = NextId() }, new FolioDb.Document { ["$inc"] = new FolioDb.Document { ["score"] = 1.0 } });
        tx.Commit();
    }

    [Benchmark(OperationsPerInvoke = UpdatesPerInvoke)]
    public void UpdateRewriteNotIndexed()
    {
        using var tx = _db.BeginTransaction();
        var c = tx.GetCollection("users");
        for (int i = 0; i < UpdatesPerInvoke; i++)
        {
            int id = NextId();
            c.UpdateOne(new FolioDb.Document { ["_id"] = id },
                new FolioDb.Document { ["$set"] = new FolioDb.Document { ["name"] = new string('n', 1 + (int)(_tick++ % 3)) } });
        }
        tx.Commit();
    }
}
