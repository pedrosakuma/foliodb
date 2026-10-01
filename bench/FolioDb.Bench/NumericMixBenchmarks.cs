using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using FolioDb;

/// <summary>
/// Cost of comparing numbers of the same type vs. different types. <c>Case</c> is "stored/literal": the type of the
/// stored field and of the filter constant. Stored values are integers 0..999 (Decimal with scale 2, e.g. 5.00m),
/// except DecimalFrac, which stores value + 0.25m. Every comparison encodes the stored value to its ordered key, so
/// cost follows the stored type, not whether the types match.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class NumericMixBenchmarks
{
    private const int N = 10_000;
    private const int Distinct = 1000;

    [Params("Int32/Int32", "Int32/Double", "Int32/Decimal", "Double/Double", "Double/Int32",
        "Decimal/Decimal", "Decimal/Int32", "DecimalFrac/DecimalFrac", "DecimalFrac/Int32")]
    public string Case { get; set; } = "";

    private string _path = "";
    private FolioDatabase _db = null!;
    private FolioDb.Collection _scan = null!;
    private FolioDb.Collection _indexed = null!;
    private FolioDb.Document _eq = null!;
    private FolioDb.Document _range = null!;
    private DocValue[] _stored = [];
    private DocValue _literal;

    private static DocValue Make(string type, int v) => type switch
    {
        "Int32" => v,
        "Double" => (double)v,
        "Decimal" => v + 0.00m,
        "DecimalFrac" => v + 0.25m,
        _ => throw new ArgumentException(type),
    };

    [GlobalSetup]
    public void Setup()
    {
        var parts = Case.Split('/');
        string stored = parts[0], literal = parts[1];
        _stored = Enumerable.Range(0, Distinct).Select(i => Make(stored, i)).ToArray();
        _literal = Make(literal, 500);
        _eq = new FolioDb.Document { ["v"] = _literal };
        _range = new FolioDb.Document { ["v"] = new FolioDb.Document { ["$gte"] = Make(literal, 500), ["$lt"] = Make(literal, 510) } };

        _path = Workload.TempFile(".folio");
        _db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _db.BeginTransaction())
        {
            var scan = tx.GetCollection("scan");
            var indexed = tx.GetCollection("indexed");
            indexed.CreateIndex("v");
            for (int i = 0; i < N; i++)
            {
                var doc = new FolioDb.Document { ["_id"] = i, ["name"] = "item " + i, ["v"] = _stored[i % Distinct] };
                scan.Insert(doc);
                indexed.Insert(doc.Clone());
            }
            tx.Commit();
        }
        _db.Checkpoint();
        _scan = _db.GetCollection("scan");
        _indexed = _db.GetCollection("indexed");

        long eq = stored == "DecimalFrac" && literal != "DecimalFrac" ? 0 : N / Distinct;
        if (ScanEq() != eq || IndexEq() != eq || ScanRange() != 100 || IndexRange() != 100)
            throw new InvalidOperationException($"Unexpected counts for {Case}.");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _db.Dispose();
        Workload.Delete(_path);
    }

    /// <summary>One value comparison (DocValue equality goes through the same ordered key).</summary>
    [Benchmark(OperationsPerInvoke = Distinct), BenchmarkCategory("Compare")]
    public int Compare()
    {
        int n = 0;
        foreach (var v in _stored) n += v.CompareTo(_literal);
        return n;
    }

    /// <summary>Full scan of 10k documents, equality (10 matches; none for DecimalFrac/Int32).</summary>
    [Benchmark, BenchmarkCategory("ScanEq")]
    public long ScanEq() => _scan.Count(_eq);

    /// <summary>Full scan of 10k documents, range (100 matches).</summary>
    [Benchmark, BenchmarkCategory("ScanRange")]
    public long ScanRange() => _scan.Count(_range);

    /// <summary>Index lookup, equality (covered count).</summary>
    [Benchmark, BenchmarkCategory("IndexEq")]
    public long IndexEq() => _indexed.Count(_eq);

    /// <summary>Index range (covered count, 100 entries).</summary>
    [Benchmark, BenchmarkCategory("IndexRange")]
    public long IndexRange() => _indexed.Count(_range);
}
