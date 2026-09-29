using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using FolioDb;
using LiteDB;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "--profile-writers")
    ConcurrentWorkload.Profile(args[1..]);
else if (args.Length > 0 && args[0] == "--concurrency")
    ConcurrentWorkload.Run(args[1..]);
else if (args.Length > 0 && args[0] == "--sharding")
    ConcurrentWorkload.Run(args[1..], sharding: true);
else if (args.Length > 0 && args[0] == "--batching")
    ConcurrentWorkload.Run(args[1..], batching: true);
else if (args.Length > 0 && args[0] == "--fairness")
    ConcurrentWorkload.Run(args[1..], fairness: true);
else
    BenchmarkSwitcher.FromAssembly(typeof(Workload).Assembly).Run(args);

/// <summary>Shared data set: documents with an indexed "city" field (100 distinct values).</summary>
public static class Workload
{
    public const int Documents = 10_000;
    public static readonly string[] Cities = Enumerable.Range(0, 100).Select(i => "city-" + i).ToArray();

    public static string TempFile(string ext) => Path.Combine(Path.GetTempPath(), "folio-bench-" + Guid.NewGuid().ToString("N") + ext);

    public static void Delete(string path)
    {
        foreach (var suffix in new[] { "", "-wal", "-shm", "-journal", "-log" })
            if (File.Exists(path + suffix)) File.Delete(path + suffix);
        var log = Path.ChangeExtension(path, null) + "-log" + Path.GetExtension(path);
        if (File.Exists(log)) File.Delete(log);
    }

    public static string Json(int i) =>
        $$"""{"_id":{{i}},"name":"user {{i}}","city":"{{Cities[i % Cities.Length]}}","age":{{i % 90}},"tags":["a","b"],"score":{{i * 0.5}}}""";
}

/// <summary>Bulk insert of 1,000 documents in a single transaction into a fresh database.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class InsertBenchmarks
{
    private const int N = 1000;
    private string _path = "";

    [IterationSetup]
    public void Setup() => _path = Workload.TempFile(".db");

    [IterationCleanup]
    public void Cleanup() => Workload.Delete(_path);

    [Benchmark(Baseline = true)]
    public void FolioDb()
    {
        using var db = FolioDatabase.Open(_path, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using var tx = db.BeginTransaction();
        var col = tx.GetCollection("users");
        col.CreateIndex("city");
        for (int i = 0; i < N; i++)
            col.Insert(new FolioDb.Document { ["_id"] = i, ["name"] = "user " + i, ["city"] = Workload.Cities[i % 100], ["age"] = i % 90, ["tags"] = new DocArray { "a", "b" }, ["score"] = i * 0.5 });
        tx.Commit();
    }

    [Benchmark]
    public void Sqlite_Json()
    {
        using var conn = new SqliteConnection($"Data Source={_path};Pooling=False");
        conn.Open();
        Exec(conn, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE users(id INTEGER PRIMARY KEY, data TEXT NOT NULL); CREATE INDEX users_city ON users(json_extract(data, '$.city'));");
        using var tx = conn.BeginTransaction();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "INSERT INTO users(id, data) VALUES ($id, $data)";
        var pid = cmd.Parameters.Add("$id", SqliteType.Integer);
        var pdata = cmd.Parameters.Add("$data", SqliteType.Text);
        for (int i = 0; i < N; i++)
        {
            pid.Value = i;
            pdata.Value = Workload.Json(i);
            cmd.ExecuteNonQuery();
        }
        tx.Commit();
    }

    [Benchmark]
    public void LiteDb()
    {
        using var db = new LiteDatabase($"Filename={_path};Connection=direct");
        var col = db.GetCollection("users");
        col.EnsureIndex("city");
        db.BeginTrans();
        for (int i = 0; i < N; i++)
            col.Insert(new BsonDocument { ["_id"] = i, ["name"] = "user " + i, ["city"] = Workload.Cities[i % 100], ["age"] = i % 90, ["tags"] = new BsonArray { "a", "b" }, ["score"] = i * 0.5 });
        db.Commit();
    }

    internal static void Exec(SqliteConnection c, string sql)
    {
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}

/// <summary>Point lookups by primary key and equality queries on a secondary index over 10,000 documents.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
[CategoriesColumn]
public class QueryBenchmarks
{
    private string _folioPath = "", _sqlitePath = "", _litePath = "";
    private FolioDatabase _folio = null!;
    private FolioDb.Collection _folioUsers = null!;
    private SqliteConnection _sqlite = null!;
    private SqliteCommand _sqliteById = null!, _sqliteByCity = null!;
    private LiteDatabase _lite = null!;
    private ILiteCollection<BsonDocument> _liteUsers = null!;
    private int _next;

    [GlobalSetup]
    public void Setup()
    {
        _folioPath = Workload.TempFile(".folio");
        _folio = FolioDatabase.Open(_folioPath, new FolioOptions { Synchronous = SynchronousMode.Normal });
        using (var tx = _folio.BeginTransaction())
        {
            var col = tx.GetCollection("users");
            col.CreateIndex("city");
            for (int i = 0; i < Workload.Documents; i++) col.Insert(FolioDb.Document.Parse(Workload.Json(i)));
            tx.Commit();
        }
        _folio.Checkpoint();
        _folioUsers = _folio.GetCollection("users");

        _sqlitePath = Workload.TempFile(".db");
        _sqlite = new SqliteConnection($"Data Source={_sqlitePath}");
        _sqlite.Open();
        InsertBenchmarks.Exec(_sqlite, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE users(id INTEGER PRIMARY KEY, data TEXT NOT NULL); CREATE INDEX users_city ON users(json_extract(data, '$.city'));");
        using (var tx = _sqlite.BeginTransaction())
        {
            using var ins = _sqlite.CreateCommand();
            ins.CommandText = "INSERT INTO users(id, data) VALUES ($id, $data)";
            var pid = ins.Parameters.Add("$id", SqliteType.Integer);
            var pdata = ins.Parameters.Add("$data", SqliteType.Text);
            for (int i = 0; i < Workload.Documents; i++)
            {
                pid.Value = i;
                pdata.Value = Workload.Json(i);
                ins.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _sqliteById = _sqlite.CreateCommand();
        _sqliteById.CommandText = "SELECT data FROM users WHERE id = $id";
        _sqliteById.Parameters.Add("$id", SqliteType.Integer);
        _sqliteByCity = _sqlite.CreateCommand();
        _sqliteByCity.CommandText = "SELECT data FROM users WHERE json_extract(data, '$.city') = $city";
        _sqliteByCity.Parameters.Add("$city", SqliteType.Text);

        _litePath = Workload.TempFile(".db");
        _lite = new LiteDatabase($"Filename={_litePath};Connection=direct");
        _liteUsers = _lite.GetCollection("users");
        _liteUsers.EnsureIndex("city");
        _lite.BeginTrans();
        for (int i = 0; i < Workload.Documents; i++) _liteUsers.Insert(LiteDB.JsonSerializer.Deserialize(Workload.Json(i)).AsDocument);
        _lite.Commit();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _folio.Dispose();
        _sqliteById.Dispose();
        _sqliteByCity.Dispose();
        _sqlite.Dispose();
        SqliteConnection.ClearAllPools();
        _lite.Dispose();
        Workload.Delete(_folioPath);
        Workload.Delete(_sqlitePath);
        Workload.Delete(_litePath);
    }

    private int NextId() => _next = (_next + 7919) % Workload.Documents;

    [Benchmark(Baseline = true), BenchmarkCategory("FindById")]
    public object? FolioDb_FindById() => _folioUsers.FindById(NextId());

    [Benchmark, BenchmarkCategory("FindById")]
    public object? Sqlite_FindById()
    {
        _sqliteById.Parameters[0].Value = NextId();
        return _sqliteById.ExecuteScalar();
    }

    [Benchmark, BenchmarkCategory("FindById")]
    public object? LiteDb_FindById() => _liteUsers.FindById(NextId());

    [Benchmark(Baseline = true), BenchmarkCategory("IndexedEq")]
    public int FolioDb_IndexedEq() => _folioUsers.Find(new FolioDb.Document { ["city"] = Workload.Cities[NextId() % 100] }).Count;

    [Benchmark, BenchmarkCategory("IndexedEq")]
    public int Sqlite_IndexedEq()
    {
        _sqliteByCity.Parameters[0].Value = Workload.Cities[NextId() % 100];
        using var r = _sqliteByCity.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            _ = r.GetString(0);
            n++;
        }
        return n;
    }

    [Benchmark, BenchmarkCategory("IndexedEq")]
    public int LiteDb_IndexedEq() => _liteUsers.Find(LiteDB.Query.EQ("city", Workload.Cities[NextId() % 100])).Count();
}
