using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using FolioDb;

/// <summary>How a price-like value is stored: native double, native Decimal, or a decimal written as a string.</summary>
public enum NumericRepr { Double, Decimal, DecimalAsString }

/// <summary>
/// Cost of the numeric representations in the hot paths: document (de)serialization, index-key encoding
/// (used for indexes, filters and sort) and an indexed range query end to end.
/// Note: DecimalAsString is measured for speed only; its range queries are semantically wrong ("10" &lt; "9").
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class NumericBenchmarks
{
    private const int N = 1000;

    [Params(NumericRepr.Double, NumericRepr.Decimal, NumericRepr.DecimalAsString)]
    public NumericRepr Repr { get; set; }

    private DocValue[] _values = [];
    private FolioDb.Document[] _docs = [];
    private byte[][] _bytes = [];
    private string _path = "";
    private FolioDatabase _db = null!;
    private FolioDb.Collection _prices = null!;
    private FolioDb.Document _rangeFilter = null!;

    private DocValue Make(decimal price) => Repr switch
    {
        NumericRepr.Double => (double)price,
        NumericRepr.Decimal => price,
        _ => price.ToString(System.Globalization.CultureInfo.InvariantCulture),
    };

    [GlobalSetup]
    public void Setup()
    {
        var rng = new Random(1);
        var prices = Enumerable.Range(0, N).Select(_ => Math.Round((decimal)rng.NextDouble() * 10_000m, 2)).ToArray();
        _values = prices.Select(Make).ToArray();
        _docs = _values.Select((v, i) => new FolioDb.Document { ["_id"] = i, ["sku"] = "SKU-" + i, ["price"] = v, ["qty"] = i % 50 }).ToArray();
        _bytes = _docs.Select(DocumentSerializer.Serialize).ToArray();

        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("prices");
            c.CreateIndex("price");
            for (int i = 0; i < 10; i++)
                foreach (var d in _docs)
                {
                    var copy = d.Clone();
                    copy["_id"] = i * N + d["_id"].AsInt32;
                    c.Insert(copy);
                }
            tx.Commit();
        }
        _db.Checkpoint();
        _prices = _db.GetCollection("prices");
        _rangeFilter = new FolioDb.Document { ["price"] = new FolioDb.Document { ["$gte"] = Make(1000m), ["$lt"] = Make(1100m) } };
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }

    [Benchmark(OperationsPerInvoke = N), BenchmarkCategory("Serialize")]
    public int Serialize()
    {
        int n = 0;
        foreach (var d in _docs) n += DocumentSerializer.Serialize(d).Length;
        return n;
    }

    [Benchmark(OperationsPerInvoke = N), BenchmarkCategory("Deserialize")]
    public int Deserialize()
    {
        int n = 0;
        foreach (var b in _bytes) n += DocumentSerializer.Deserialize(b).Count;
        return n;
    }

    [Benchmark(OperationsPerInvoke = N), BenchmarkCategory("KeyEncode")]
    public int KeyEncode()
    {
        int n = 0;
        foreach (var v in _values) n += KeyEncoder.Encode(v).Length;
        return n;
    }

    /// <summary>~1% of 10k documents through the index on "price" (all are matched for Double/Decimal).</summary>
    [Benchmark, BenchmarkCategory("IndexedRange")]
    public long IndexedRange() => _prices.Count(_rangeFilter);
}

/// <summary>
/// Cost of rewriting the whole document on update: one <c>$inc</c> on a counter of documents of increasing size
/// (the largest one lives in overflow pages). 100 updates per transaction.
/// </summary>
[MemoryDiagnoser]
public class UpdateRewriteBenchmarks
{
    private const int Docs = 200;
    private const int UpdatesPerTx = 100;

    [Params(200, 4 * 1024, 64 * 1024)]
    public int DocBytes { get; set; }

    private string _path = "";
    private FolioDatabase _db = null!;
    private readonly FolioDb.Document _inc = FolioDb.Document.Parse("{ $inc: { n: 1 } }");
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        string payload = new('x', Math.Max(0, DocBytes - 40));
        using (var tx = _db.BeginTransaction())
        {
            var c = tx.GetCollection("docs");
            for (int i = 0; i < Docs; i++) c.Insert(new FolioDb.Document { ["_id"] = i, ["n"] = 0, ["payload"] = payload });
            tx.Commit();
        }
        _db.Checkpoint();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }

    [Benchmark(OperationsPerInvoke = UpdatesPerTx)]
    public long IncCounter()
    {
        long modified = 0;
        using var tx = _db.BeginTransaction();
        var c = tx.GetCollection("docs");
        for (int i = 0; i < UpdatesPerTx; i++)
        {
            var filter = new FolioDb.Document { ["_id"] = _next++ % Docs };
            modified += c.UpdateOne(filter, _inc).ModifiedCount;
        }
        tx.Commit();
        return modified;
    }
}
