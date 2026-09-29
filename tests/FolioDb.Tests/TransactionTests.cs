namespace FolioDb.Tests;

public class TransactionTests
{
    [Fact]
    public void Commit_and_rollback()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        using (var tx = db.BeginTransaction())
        {
            tx.GetCollection("a").Insert("{ _id: 1 }");
            tx.GetCollection("b").Insert("{ _id: 1 }");
            Assert.Equal(1, tx.GetCollection("a").Count()); // read-your-writes
            tx.Commit();
        }
        using (var tx = db.BeginTransaction())
        {
            tx.GetCollection("a").Insert("{ _id: 2 }");
            tx.DropCollection("b");
        } // disposed without commit => rollback
        Assert.Equal(1, db.GetCollection("a").Count());
        Assert.Equal(1, db.GetCollection("b").Count());
    }

    [Fact]
    public void Duplicate_key_leaves_transaction_usable()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("c");
        c.Insert("{ _id: 1 }");
        Assert.Throws<DuplicateKeyException>(() => c.Insert("{ _id: 1 }"));
        c.Insert("{ _id: 2 }");
        tx.Commit();
        Assert.Equal(2, db.GetCollection("c").Count());
    }

    [Fact]
    public void Failed_non_atomic_statement_dooms_transaction()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        using var tx = db.BeginTransaction();
        var c = tx.GetCollection("c");
        Assert.Throws<DuplicateKeyException>(() => c.InsertMany(new[] { Document.Parse("{ _id: 1 }"), Document.Parse("{ _id: 1 }") }));
        Assert.Throws<FolioException>(() => c.Insert("{ _id: 3 }"));
        Assert.Throws<FolioException>(() => tx.Commit());
    }

    [Fact]
    public void Snapshot_isolation_readers_do_not_see_later_commits()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        c.Insert("{ _id: 1, v: 'old' }");

        using var snap = db.BeginSnapshot();
        var sc = snap.GetCollection("c");
        c.UpdateOne("{ _id: 1 }", "{ $set: { v: 'new' } }");
        c.Insert("{ _id: 2 }");
        Assert.Equal("old", sc.FindById(1)!["v"].AsString);
        Assert.Equal(1, sc.Count());
        Assert.Equal("new", c.FindById(1)!["v"].AsString);
        Assert.Throws<InvalidOperationException>(() => sc.Insert("{ _id: 3 }"));
    }

    [Fact]
    public void Uncommitted_writes_are_invisible_to_readers()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open();
        var c = db.GetCollection("c");
        using var tx = db.BeginTransaction();
        tx.GetCollection("c").Insert("{ _id: 1 }");
        Assert.Equal(0, c.Count()); // reader does not block and sees the last committed state
        tx.Commit();
        Assert.Equal(1, c.Count());
    }

    [Fact]
    public void Second_writer_waits_then_times_out()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { BusyTimeout = TimeSpan.FromMilliseconds(100), Synchronous = SynchronousMode.Off });
        using var tx = db.BeginTransaction();
        var ex = Assert.Throws<FolioException>(() => db.GetCollection("c").Insert("{ a: 1 }"));
        Assert.Contains("busy", ex.Message);
    }

    [Theory]
    [InlineData(WriterAdmissionMode.Default)]
    [InlineData(WriterAdmissionMode.Fifo)]
    public async Task Concurrent_readers_and_writers_keep_consistent_invariants(WriterAdmissionMode admission)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 100, Synchronous = SynchronousMode.Off, WriterAdmission = admission });
        var accounts = db.GetCollection("accounts");
        accounts.CreateIndex("balance");
        for (int i = 0; i < 20; i++) accounts.Insert(new Document { ["_id"] = i, ["balance"] = 100 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        long violations = 0, reads = 0, transfers = 0;

        var writers = Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            var rnd = new Random(w);
            while (!cts.IsCancellationRequested)
            {
                int from = rnd.Next(20), to = rnd.Next(20), amount = rnd.Next(1, 10);
                using var tx = db.BeginTransaction();
                var c = tx.GetCollection("accounts");
                c.UpdateOne(new Document { ["_id"] = from }, Document.Parse($"{{ $inc: {{ balance: {-amount} }} }}"));
                c.UpdateOne(new Document { ["_id"] = to }, Document.Parse($"{{ $inc: {{ balance: {amount} }} }}"));
                tx.Commit();
                Interlocked.Increment(ref transfers);
            }
        })).ToList();

        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                using var snap = db.BeginSnapshot();
                var docs = snap.GetCollection("accounts").Find();
                long total = docs.Sum(d => d["balance"].AsInt64);
                if (docs.Count != 20 || total != 2000) Interlocked.Increment(ref violations);
                long viaIndex = snap.GetCollection("accounts").Find("{ balance: { $gte: -1000000 } }").Sum(d => d["balance"].AsInt64);
                if (viaIndex != 2000) Interlocked.Increment(ref violations);
                Interlocked.Increment(ref reads);
            }
        })).ToList();

        await Task.WhenAll(writers.Concat(readers));
        Assert.Equal(0, violations);
        Assert.True(transfers > 10, $"transfers={transfers}");
        Assert.True(reads > 10, $"reads={reads}");
        db.CheckIntegrity();
    }
}
