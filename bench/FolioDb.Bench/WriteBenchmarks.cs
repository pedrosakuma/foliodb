using BenchmarkDotNet.Attributes;
using FolioDb;

/// <summary>Insert, replace and delete of 1,000 documents in a single transaction, with secondary indexes.</summary>
[MemoryDiagnoser]
public class WriteBenchmarks
{
    private const int N = 1000;
    private string _template = "", _path = "";

    /// <summary>Number of secondary indexes: 0 (primary only), 1 (simple) or 2 (simple + compound).</summary>
    [Params(0, 1, 2)]
    public int Indexes { get; set; }

    private static Document Doc(int i) => new()
    {
        ["_id"] = i,
        ["name"] = "user " + i,
        ["city"] = Workload.Cities[i % Workload.Cities.Length],
        ["age"] = i % 90,
        ["tags"] = new DocArray { "a", "b" },
        ["score"] = i * 0.5,
    };

    private void CreateIndexes(Transaction tx)
    {
        var c = tx.GetCollection("users");
        if (Indexes >= 1) c.CreateIndex("city");
        if (Indexes >= 2) c.CreateIndex(new Document { ["age"] = 1, ["name"] = -1 });
    }

    [GlobalSetup]
    public void Setup()
    {
        _template = Workload.TempFile(".folio");
        using (var db = FolioDatabase.Open(_template, new FolioOptions { Synchronous = SynchronousMode.Normal }))
        {
            using (var tx = db.BeginTransaction())
            {
                CreateIndexes(tx);
                var c = tx.GetCollection("users");
                for (int i = 0; i < N; i++) c.Insert(Doc(i));
                tx.Commit();
            }
            db.Checkpoint();
        }
    }

    [GlobalCleanup]
    public void Cleanup() => Workload.Delete(_template);

    [IterationSetup(Targets = [nameof(Insert), nameof(InsertNoIndexWork)])]
    public void SetupEmpty() => _path = Workload.TempFile(".folio");

    [IterationSetup(Targets = [nameof(Replace), nameof(Delete)])]
    public void SetupPopulated()
    {
        _path = Workload.TempFile(".folio");
        File.Copy(_template, _path);
    }

    [IterationCleanup]
    public void CleanupIteration() => Workload.Delete(_path);

    [Benchmark(Baseline = true)]
    public void Insert()
    {
        using var db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = db.BeginTransaction();
        CreateIndexes(tx);
        var c = tx.GetCollection("users");
        for (int i = 0; i < N; i++) c.Insert(Doc(i));
        tx.Commit();
    }

    /// <summary>Same inserts with the indexes created afterwards: isolates the per-document index key cost.</summary>
    [Benchmark]
    public void InsertNoIndexWork()
    {
        using var db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("users");
        for (int i = 0; i < N; i++) c.Insert(Doc(i));
        tx.Commit();
    }

    [Benchmark]
    public void Replace()
    {
        using var db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("users");
        for (int i = 0; i < N; i++)
        {
            var d = Doc(i);
            d["score"] = i * 0.25;
            c.ReplaceOne(new Document { ["_id"] = i }, d);
        }
        tx.Commit();
    }

    [Benchmark]
    public void Delete()
    {
        using var db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("users");
        for (int i = 0; i < N; i++) c.DeleteById(i);
        tx.Commit();
    }
}
