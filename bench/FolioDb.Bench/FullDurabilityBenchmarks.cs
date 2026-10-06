using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;
using FolioDb;
using Microsoft.Data.Sqlite;

/// <summary>Matched, single-transaction batch writes with durable WAL commits.</summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByCategory)]
public class FullDurabilityBenchmarks
{
    private const int N = 50000;
    private string _emptyFolio = "", _populatedFolio = "", _emptySqlite = "", _populatedSqlite = "";
    private string _folioPath = "", _sqlitePath = "";
    private int[] _ids = [];

    [Params("Ascending", "Descending", "Shuffled")]
    public string Order { get; set; } = "";

    private static Document Doc(int i) => new()
    {
        ["_id"] = i,
        ["name"] = "user " + i,
        ["city"] = Workload.Cities[i % Workload.Cities.Length],
        ["age"] = i % 90,
        ["score"] = i * 0.5,
    };

    private static void ConfigureSqlite(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL;";
        command.ExecuteNonQuery();
    }

    [GlobalSetup]
    public void Setup()
    {
        _ids = Enumerable.Range(0, N).ToArray();
        if (Order == "Descending") Array.Reverse(_ids);
        else if (Order == "Shuffled")
        {
            var random = new Random(0xF0110);
            for (int i = _ids.Length - 1; i > 0; i--)
            {
                int j = random.Next(i + 1);
                (_ids[i], _ids[j]) = (_ids[j], _ids[i]);
            }
        }

        _emptyFolio = Workload.TempFile(".folio");
        _populatedFolio = Workload.TempFile(".folio");
        _emptySqlite = Workload.TempFile(".sqlite");
        _populatedSqlite = Workload.TempFile(".sqlite");

        CreateFolioTemplate(_emptyFolio, populated: false);
        CreateFolioTemplate(_populatedFolio, populated: true);
        CreateSqliteTemplate(_emptySqlite, populated: false);
        CreateSqliteTemplate(_populatedSqlite, populated: true);
    }

    private static void CreateFolioTemplate(string path, bool populated)
    {
        using var db = FolioDatabase.Open(path, new FolioOptions { Synchronous = SynchronousMode.Full });
        using (var tx = db.BeginTransaction())
        {
            var collection = tx.GetCollection("docs");
            collection.CreateIndex("city");
            if (populated)
                for (int i = 0; i < N; i++) collection.Insert(Doc(i));
            tx.Commit();
        }
        db.Checkpoint();
    }

    private static void CreateSqliteTemplate(string path, bool populated)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        ConfigureSqlite(connection);
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "CREATE TABLE docs(id INTEGER PRIMARY KEY, name TEXT NOT NULL, city TEXT NOT NULL, age INTEGER NOT NULL, score REAL NOT NULL); CREATE INDEX docs_city ON docs(city);";
            command.ExecuteNonQuery();
        }
        if (populated)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT INTO docs(id, name, city, age, score) VALUES ($id, $name, $city, $age, $score)";
            var id = command.Parameters.Add("$id", SqliteType.Integer);
            var name = command.Parameters.Add("$name", SqliteType.Text);
            var city = command.Parameters.Add("$city", SqliteType.Text);
            var age = command.Parameters.Add("$age", SqliteType.Integer);
            var score = command.Parameters.Add("$score", SqliteType.Real);
            for (int i = 0; i < N; i++)
            {
                id.Value = i;
                name.Value = "user " + i;
                city.Value = Workload.Cities[i % Workload.Cities.Length];
                age.Value = i % 90;
                score.Value = i * 0.5;
                command.ExecuteNonQuery();
            }
            transaction.Commit();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE)";
        checkpoint.ExecuteNonQuery();
    }

    [IterationSetup(Target = nameof(FolioInsert))]
    public void SetupFolioInsert() => PrepareFolio(_emptyFolio);

    [IterationSetup(Target = nameof(SqliteInsert))]
    public void SetupSqliteInsert() => PrepareSqlite(_emptySqlite);

    [IterationSetup(Target = nameof(FolioUpdate))]
    public void SetupFolioUpdate() => PrepareFolio(_populatedFolio);

    [IterationSetup(Target = nameof(SqliteUpdate))]
    public void SetupSqliteUpdate() => PrepareSqlite(_populatedSqlite);

    private void PrepareFolio(string template)
    {
        _folioPath = Workload.TempFile(".folio");
        File.Copy(template, _folioPath);
    }

    private void PrepareSqlite(string template)
    {
        _sqlitePath = Workload.TempFile(".sqlite");
        File.Copy(template, _sqlitePath);
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N), BenchmarkCategory("Insert")]
    public void FolioInsert()
    {
        using var db = FolioDatabase.Open(_folioPath, new FolioOptions { Synchronous = SynchronousMode.Full });
        using var tx = db.BeginTransaction();
        var collection = tx.GetCollection("docs");
        foreach (int i in _ids) collection.Insert(Doc(i));
        tx.Commit();
    }

    [Benchmark(OperationsPerInvoke = N), BenchmarkCategory("Insert")]
    public void SqliteInsert()
    {
        using var connection = new SqliteConnection($"Data Source={_sqlitePath};Pooling=False");
        connection.Open();
        ConfigureSqlite(connection);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO docs(id, name, city, age, score) VALUES ($id, $name, $city, $age, $score)";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var name = command.Parameters.Add("$name", SqliteType.Text);
        var city = command.Parameters.Add("$city", SqliteType.Text);
        var age = command.Parameters.Add("$age", SqliteType.Integer);
        var score = command.Parameters.Add("$score", SqliteType.Real);
        foreach (int i in _ids)
        {
            id.Value = i;
            name.Value = "user " + i;
            city.Value = Workload.Cities[i % Workload.Cities.Length];
            age.Value = i % 90;
            score.Value = i * 0.5;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    [Benchmark(Baseline = true, OperationsPerInvoke = N), BenchmarkCategory("Update")]
    public void FolioUpdate()
    {
        using var db = FolioDatabase.Open(_folioPath, new FolioOptions { Synchronous = SynchronousMode.Full });
        using var tx = db.BeginTransaction();
        var collection = tx.GetCollection("docs");
        foreach (int i in _ids)
            collection.UpdateOne(new Document { ["_id"] = i }, new Document { ["$set"] = new Document { ["score"] = i * 0.25 } });
        tx.Commit();
    }

    [Benchmark(OperationsPerInvoke = N), BenchmarkCategory("Update")]
    public void SqliteUpdate()
    {
        using var connection = new SqliteConnection($"Data Source={_sqlitePath};Pooling=False");
        connection.Open();
        ConfigureSqlite(connection);
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE docs SET score=$score WHERE id=$id";
        var id = command.Parameters.Add("$id", SqliteType.Integer);
        var score = command.Parameters.Add("$score", SqliteType.Real);
        foreach (int i in _ids)
        {
            id.Value = i;
            score.Value = i * 0.25;
            command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    [IterationCleanup]
    public void CleanupIteration()
    {
        if (_folioPath.Length > 0) Workload.Delete(_folioPath);
        if (_sqlitePath.Length > 0) Workload.Delete(_sqlitePath);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        Workload.Delete(_emptyFolio);
        Workload.Delete(_populatedFolio);
        Workload.Delete(_emptySqlite);
        Workload.Delete(_populatedSqlite);
    }
}
