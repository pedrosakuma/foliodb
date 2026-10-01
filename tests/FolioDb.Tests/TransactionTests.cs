namespace FolioDb.Tests;

public class TransactionTests
{
    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    [InlineData(8192)]
    public void Materialized_reads_own_buffers_and_preserve_overflow_snapshots(int payloadSize)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var c = db.GetCollection("items");
        var original = new Document
        {
            ["_id"] = 1,
            ["payload"] = new string('x', payloadSize),
            ["nested"] = new Document { ["bytes"] = new byte[] { 1, 2, 3 } },
            ["array"] = new DocArray { new byte[] { 4, 5, 6 } },
        };
        c.Insert(original);
        db.Checkpoint();
        Document retained;
        using (var snapshot = db.BeginSnapshot())
        {
            var sc = snapshot.GetCollection("items");
            retained = sc.FindById(1)!;
            var mutated = Assert.Single(sc.Find());
            mutated["nested"].AsDocument["bytes"].AsBinary[0] = 99;
            mutated["array"].AsArray[0].AsBinary[0] = 99;
            mutated["payload"] = "changed";
            Assert.Equal(original.ToJson(), sc.FindById(1)!.ToJson());
            Assert.Equal(original.ToJson(), retained.ToJson());

            using (var tx = db.BeginTransaction())
            {
                var tc = tx.GetCollection("items");
                tc.UpdateOne(new Document { ["_id"] = 1 }, new Document { ["$set"] = new Document { ["payload"] = new string('y', payloadSize) } });
                var uncommitted = tc.FindById(1)!;
                Assert.Equal(new string('y', payloadSize), uncommitted["payload"].AsString);
                tc.DeleteById(1);
                tc.Insert(new Document { ["_id"] = 1, ["payload"] = new string('z', payloadSize) });
                Assert.Equal(new string('y', payloadSize), uncommitted["payload"].AsString);
                tx.Commit();
            }
            Assert.Equal(original.ToJson(), sc.FindById(1)!.ToJson());
            Assert.Equal(new string('z', payloadSize), c.FindById(1)!["payload"].AsString);
        }
        db.Checkpoint();
        db.Dispose();
        Assert.Equal(original.ToJson(), retained.ToJson());
    }

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
    public void Leaf_rebalance_preserves_indexes_snapshots_and_rollback()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            PageSize = 1024,
            AutoCheckpointFrames = 0,
            DeleteRebalance = BTreeDeleteRebalanceMode.LeafByteOccupancy,
        });
        var c = db.GetCollection("c");
        c.CreateIndex("group");
        c.CreateIndex(Document.Parse("{ group: 1, score: -1 }"));
        c.CreateIndex("tags");
        c.InsertMany(Enumerable.Range(0, 600).Select(i => new Document
        {
            ["_id"] = i,
            ["group"] = i % 19,
            ["score"] = i,
            ["tags"] = new DocArray { $"tag-{i % 23}", $"tag-{i % 29}" },
            ["payload"] = new string((char)('a' + i % 20), 30 + i % 190),
        }));
        Assert.True(db.Checkpoint());

        using var snapshot = db.BeginSnapshot();
        using (var tx = db.BeginTransaction())
        {
            var tc = tx.GetCollection("c");
            for (int i = 0; i < 450; i++) Assert.True(tc.DeleteById(i));
            tx.Commit();
        }
        Assert.Equal(600, snapshot.GetCollection("c").Count());
        Assert.Equal(150, c.Count());
        Assert.Equal(8, c.Count("{ group: 18 }"));
        db.CheckIntegrity();

        using (var tx = db.BeginTransaction())
        {
            var tc = tx.GetCollection("c");
            for (int i = 450; i < 575; i++) Assert.True(tc.DeleteById(i));
        }
        Assert.Equal(150, c.Count());
        Assert.NotNull(c.FindById(450));
        db.CheckIntegrity();
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
    [InlineData(WriterAdmissionMode.Unordered)]
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

    [Fact]
    public async Task Snapshots_stay_stable_while_the_wal_index_grows_without_checkpoints()
    {
        // No checkpoints: every page read goes through the WAL index while the writer keeps appending frames
        // (and growing per-page frame lists) concurrently with lock-free lookups.
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Off });
        var accounts = db.GetCollection("accounts");
        for (int i = 0; i < 50; i++) accounts.Insert(new Document { ["_id"] = i, ["balance"] = 100, ["version"] = 0 });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        long violations = 0, reads = 0, transfers = 0;
        var writer = Task.Run(() =>
        {
            var rnd = new Random(1);
            while (!cts.IsCancellationRequested)
            {
                int from = rnd.Next(50), to = rnd.Next(50);
                using var tx = db.BeginTransaction();
                var c = tx.GetCollection("accounts");
                c.UpdateOne(new Document { ["_id"] = from }, Document.Parse("{ $inc: { balance: -3, version: 1 } }"));
                c.UpdateOne(new Document { ["_id"] = to }, Document.Parse("{ $inc: { balance: 3, version: 1 } }"));
                tx.Commit();
                transfers++;
            }
        }, TestContext.Current.CancellationToken);
        var readers = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
        {
            long lastVersions = 0;
            while (!cts.IsCancellationRequested)
            {
                using var snap = db.BeginSnapshot();
                var first = snap.GetCollection("accounts").Find();
                Thread.SpinWait(2000);
                var second = snap.GetCollection("accounts").Find();
                long versions = first.Sum(d => d["version"].AsInt64);
                if (first.Count != 50 || first.Sum(d => d["balance"].AsInt64) != 5000) Interlocked.Increment(ref violations);
                if (versions != second.Sum(d => d["version"].AsInt64) || versions < lastVersions) Interlocked.Increment(ref violations);
                lastVersions = versions;
                Interlocked.Increment(ref reads);
            }
        }, TestContext.Current.CancellationToken)).ToList();

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, violations);
        Assert.True(transfers > 10, $"transfers={transfers}");
        Assert.True(reads > 10, $"reads={reads}");
        Assert.True(db.Checkpoint());
        db.CheckIntegrity();
    }

    [Fact]
    public async Task Short_snapshots_racing_frequent_checkpoints_stay_consistent()
    {
        // Readers register without the pager gate; checkpoints must never reset the WAL under a registered reader.
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 8, Synchronous = SynchronousMode.Off });
        var accounts = db.GetCollection("accounts");
        for (int i = 0; i < 10; i++) accounts.Insert(new Document { ["_id"] = i, ["balance"] = 100 });
        var token = TestContext.Current.CancellationToken;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        long violations = 0, reads = 0, checkpoints = 0;
        var writer = Task.Run(() =>
        {
            var rnd = new Random(1);
            while (!cts.IsCancellationRequested)
            {
                int from = rnd.Next(10), to = rnd.Next(10);
                using (var tx = db.BeginTransaction())
                {
                    var c = tx.GetCollection("accounts");
                    c.UpdateOne(new Document { ["_id"] = from }, Document.Parse("{ $inc: { balance: -1 } }"));
                    c.UpdateOne(new Document { ["_id"] = to }, Document.Parse("{ $inc: { balance: 1 } }"));
                    tx.Commit();
                }
                if (db.Pager.WalFrameCount == 0) Interlocked.Increment(ref checkpoints);
            }
        }, token);
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            var rnd = new Random(r + 10);
            while (!cts.IsCancellationRequested)
            {
                using (var snap = db.BeginSnapshot())
                {
                    var c = snap.GetCollection("accounts");
                    long total = 0;
                    for (int i = 0; i < 10; i++) total += c.FindById(i)!["balance"].AsInt64;
                    if (total != 1000) Interlocked.Increment(ref violations);
                }
                Interlocked.Increment(ref reads);
                if (rnd.Next(4) == 0) Thread.Yield();
            }
        }, token)).ToList();

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, violations);
        Assert.True(reads > 100, $"reads={reads}");
        Assert.True(checkpoints > 0, $"checkpoints={checkpoints}");
        db.CheckIntegrity();
    }

    [Fact]
    public async Task Recycled_pages_never_reach_readers_that_may_still_hold_them()
    {
        // A tiny cache evicts constantly, so page images are recycled while readers walk the tree. Recycled images
        // are poisoned (TestSetup), so a reader handed one too early sees corrupt documents or wrong sums.
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { CacheSizePages = 16, AutoCheckpointFrames = 64, Synchronous = SynchronousMode.Off });
        var accounts = db.GetCollection("accounts");
        accounts.CreateIndex("balance");
        const int n = 400;
        using (var tx = db.BeginTransaction())
        {
            var c = tx.GetCollection("accounts");
            for (int i = 0; i < n; i++) c.Insert(new Document { ["_id"] = i, ["balance"] = 100, ["pad"] = new string('x', 200) });
            tx.Commit();
        }
        var token = TestContext.Current.CancellationToken;

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        long violations = 0, reads = 0, errors = 0;
        var writer = Task.Run(() =>
        {
            var rnd = new Random(1);
            while (!cts.IsCancellationRequested)
            {
                int from = rnd.Next(n), to = rnd.Next(n);
                using var tx = db.BeginTransaction();
                var c = tx.GetCollection("accounts");
                c.UpdateOne(new Document { ["_id"] = from }, Document.Parse("{ $inc: { balance: -1 } }"));
                c.UpdateOne(new Document { ["_id"] = to }, Document.Parse("{ $inc: { balance: 1 } }"));
                tx.Commit();
            }
        }, token);
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            var rnd = new Random(r + 10);
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    using var snap = db.BeginSnapshot();
                    var c = snap.GetCollection("accounts");
                    long total = 0;
                    if (r % 2 == 0)
                    {
                        foreach (var d in c.Find()) total += d["balance"].AsInt64;
                    }
                    else
                    {
                        for (int i = 0; i < n; i++) total += c.FindById(i)!["balance"].AsInt64;
                    }
                    if (total != 100L * n) Interlocked.Increment(ref violations);
                    if (c.Count(Document.Parse("{ balance: { $gte: 0 } }")) != n) Interlocked.Increment(ref violations);
                }
                catch (FolioException) { Interlocked.Increment(ref errors); }
                Interlocked.Increment(ref reads);
            }
        }, token)).ToList();

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, errors);
        Assert.Equal(0, violations);
        Assert.True(reads > 10, $"reads={reads}");
        db.CheckIntegrity();
    }
}
