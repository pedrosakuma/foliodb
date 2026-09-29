using BenchmarkDotNet.Attributes;
using FolioDb;

[MemoryDiagnoser]
public class CompoundIndexBenchmarks
{
    private string _path = "";
    private FolioDatabase _db = null!;
    private Collection _collection = null!;
    private readonly Document _filter = Document.Parse("{city:42,age:{$gte:30,$lt:40}}");
    private readonly FindOptions _projection = new() { Projection = Document.Parse("{city:1,age:1,_id:0}") };

    [Params(false, true)]
    public bool Compound { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = _db.BeginTransaction();
        var c = tx.GetCollection("c");
        if (Compound) c.CreateIndex(Document.Parse("{city:1,age:-1}"));
        else c.CreateIndex("city");
        for (int i = 0; i < 10000; i++)
            c.Insert(new Document { ["_id"] = i, ["city"] = i % 100, ["age"] = i / 100, ["payload"] = new string('x', 512) });
        tx.Commit();
        _collection = _db.GetCollection("c");
        if (_collection.Count(_filter) != 10) throw new InvalidOperationException("Unexpected benchmark result.");
    }

    [Benchmark]
    public long Count() => _collection.Count(_filter);

    [Benchmark]
    public int Project() => _collection.Find(_filter, _projection).Count;

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }
}
