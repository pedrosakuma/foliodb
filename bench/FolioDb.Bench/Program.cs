using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Running;
using FolioDb;
using LiteDB;
using Microsoft.Data.Sqlite;

if (args.Length > 0 && args[0] == "--profile-writers")
    ConcurrentWorkload.Profile(args[1..]);
else if (args.Length > 0 && args[0] == "--profile-visit")
    VisitProfile.Run(args[1..]);
else if (args.Length > 0 && args[0] == "--fragmentation")
    FragmentationWorkload.Run(args[1..]);
else if (args.Length > 0 && args[0] == "--index-maintenance")
    IndexMaintenanceWorkload.Run(args[1..]);
else if (args.Length > 0 && args[0] == "--vacuum")
    VacuumWorkload.Run(args[1..]);
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

    /// <summary>The same document without the array, so it maps to one relational row.</summary>
    public static string FlatJson(int i) =>
        $$"""{"_id":{{i}},"name":"user {{i}}","city":"{{Cities[i % Cities.Length]}}","age":{{i % 90}},"score":{{i * 0.5}}}""";
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
    private SqliteCommand _sqliteById = null!, _sqliteByCity = null!, _sqliteNameByCity = null!, _sqliteFlatByCity = null!;
    private FolioDb.Collection _folioFlat = null!;
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
            var flat = tx.GetCollection("flat");
            flat.CreateIndex("city");
            for (int i = 0; i < Workload.Documents; i++) flat.Insert(FolioDb.Document.Parse(Workload.FlatJson(i)));
            tx.Commit();
        }
        _folio.Checkpoint();
        _folioUsers = _folio.GetCollection("users");
        _folioFlat = _folio.GetCollection("flat");

        _sqlitePath = Workload.TempFile(".db");
        _sqlite = new SqliteConnection($"Data Source={_sqlitePath}");
        _sqlite.Open();
        InsertBenchmarks.Exec(_sqlite, "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; CREATE TABLE users(id INTEGER PRIMARY KEY, data TEXT NOT NULL); CREATE INDEX users_city ON users(json_extract(data, '$.city')); CREATE TABLE flat(id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL, age INTEGER NOT NULL, score REAL NOT NULL); CREATE INDEX flat_city ON flat(city);");
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
            using var insFlat = _sqlite.CreateCommand();
            insFlat.CommandText = "INSERT INTO flat(id, name, city, age, score) VALUES ($id, $name, $city, $age, $score)";
            var fid = insFlat.Parameters.Add("$id", SqliteType.Integer);
            var fname = insFlat.Parameters.Add("$name", SqliteType.Text);
            var fcity = insFlat.Parameters.Add("$city", SqliteType.Text);
            var fage = insFlat.Parameters.Add("$age", SqliteType.Integer);
            var fscore = insFlat.Parameters.Add("$score", SqliteType.Real);
            for (int i = 0; i < Workload.Documents; i++)
            {
                fid.Value = i;
                fname.Value = "user " + i;
                fcity.Value = Workload.Cities[i % Workload.Cities.Length];
                fage.Value = i % 90;
                fscore.Value = i * 0.5;
                insFlat.ExecuteNonQuery();
            }
            tx.Commit();
        }
        _sqliteFlatByCity = _sqlite.CreateCommand();
        _sqliteFlatByCity.CommandText = "SELECT id, name, city, age, score FROM flat WHERE city = $city";
        _sqliteFlatByCity.Parameters.Add("$city", SqliteType.Text);
        _sqliteById = _sqlite.CreateCommand();
        _sqliteById.CommandText = "SELECT data FROM users WHERE id = $id";
        _sqliteById.Parameters.Add("$id", SqliteType.Integer);
        _sqliteByCity = _sqlite.CreateCommand();
        _sqliteByCity.CommandText = "SELECT data FROM users WHERE json_extract(data, '$.city') = $city";
        _sqliteByCity.Parameters.Add("$city", SqliteType.Text);
        _sqliteNameByCity = _sqlite.CreateCommand();
        _sqliteNameByCity.CommandText = "SELECT json_extract(data, '$.name') FROM users WHERE json_extract(data, '$.city') = $city";
        _sqliteNameByCity.Parameters.Add("$city", SqliteType.Text);

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
        _sqliteNameByCity.Dispose();
        _sqliteFlatByCity.Dispose();
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
    public int FolioDb_IndexedEq_VisitName()
    {
        int n = 0;
        _folioUsers.Visit(new FolioDb.Document { ["city"] = Workload.Cities[NextId() % 100] }, v =>
        {
            v.TryGetValue("name", out var name);
            _ = System.Text.Encoding.UTF8.GetString(name.AsUtf8String);
            n++;
            return true;
        });
        return n;
    }

    [Benchmark, BenchmarkCategory("IndexedEq")]
    public int Sqlite_IndexedEq_Name()
    {
        _sqliteNameByCity.Parameters[0].Value = Workload.Cities[NextId() % 100];
        using var r = _sqliteNameByCity.ExecuteReader();
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

    [Benchmark, BenchmarkCategory("FlatEq")]
    public int FolioDb_FlatEq_Find() => _folioFlat.Find(new FolioDb.Document { ["city"] = Workload.Cities[NextId() % 100] }).Count;

    [Benchmark(Baseline = true), BenchmarkCategory("FlatEq")]
    public int FolioDb_FlatEq_VisitAllFields()
    {
        int n = 0;
        _folioFlat.Visit(new FolioDb.Document { ["city"] = Workload.Cities[NextId() % 100] }, v =>
        {
            // One forward pass over the fields, like reading the columns of a row.
            foreach (var f in v)
            {
                switch (f.Value.Type)
                {
                    case FolioDb.DocType.String: _ = System.Text.Encoding.UTF8.GetString(f.Value.AsUtf8String); break;
                    case FolioDb.DocType.Double: _ = f.Value.AsDouble; break;
                    default: _ = f.Value.AsInt64; break;
                }
            }
            n++;
            return true;
        });
        return n;
    }

    [Benchmark, BenchmarkCategory("FlatEq")]
    public int Sqlite_FlatEq_AllColumns()
    {
        _sqliteFlatByCity.Parameters[0].Value = Workload.Cities[NextId() % 100];
        using var r = _sqliteFlatByCity.ExecuteReader();
        int n = 0;
        while (r.Read())
        {
            _ = r.GetInt64(0);
            _ = r.GetString(1);
            _ = r.GetString(2);
            _ = r.GetInt64(3);
            _ = r.GetDouble(4);
            n++;
        }
        return n;
    }
}
