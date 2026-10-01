namespace FolioDb.Tests;

/// <summary>Full-mode group commit: commits release the write lock before their fsync and share it.</summary>
public class GroupCommitTests
{
    private static FolioOptions Full(int autoCheckpointFrames = 0) =>
        new() { Synchronous = SynchronousMode.Full, AutoCheckpointFrames = autoCheckpointFrames, BusyTimeout = TimeSpan.FromSeconds(10) };

    /// <summary>Dedicated thread that records its exception; Join rethrows it.</summary>
    private sealed class Worker
    {
        private readonly Thread _thread;
        private Exception? _error;

        public Worker(Action action)
        {
            _thread = new Thread(() => { try { action(); } catch (Exception e) { _error = e; } }) { IsBackground = true };
            _thread.Start();
        }

        public bool Join(TimeSpan timeout)
        {
            if (!_thread.Join(timeout)) return false;
            if (_error is not null) throw new AggregateException(_error);
            return true;
        }
    }

    [Fact]
    public void Readers_see_commits_only_once_durable_while_later_writers_join_the_pending_flush()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(Full());
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });

        using var flushing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        int flushes = 0;
        db.Pager.TestBeforeFlush = () =>
        {
            if (Interlocked.Increment(ref flushes) == 1)
            {
                flushing.Set();
                Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
            }
        };

        var first = new Worker(() => c.Insert(new Document { ["_id"] = 1 }));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        long appended = db.Pager.AppendedCommits;

        // The first commit is written but not durable: readers don't see it, later writers build on it.
        Assert.Null(c.FindById(1));
        Assert.Equal(1, c.Count());
        var followers = Enumerable.Range(2, 3).Select(id => new Worker(() =>
        {
            using var tx = db.BeginTransaction();
            var tc = tx.GetCollection("c");
            Assert.NotNull(tc.FindById(1));
            tc.Insert(new Document { ["_id"] = id });
            tx.Commit();
        })).ToArray();

        // All three append while the first fsync is still blocked: the write lock is not held across it.
        var sw = System.Diagnostics.Stopwatch.StartNew();
        while (db.Pager.AppendedCommits < appended + 3 && sw.Elapsed < TimeSpan.FromSeconds(10)) Thread.Sleep(1);
        Assert.Equal(appended + 3, db.Pager.AppendedCommits);
        Assert.Equal(1, flushes);
        Assert.All(followers, f => Assert.False(f.Join(TimeSpan.Zero))); // still waiting for durability
        Assert.Equal(1, c.Count());

        release.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(10)));
        Assert.All(followers, f => Assert.True(f.Join(TimeSpan.FromSeconds(10))));
        Assert.Equal(5, c.Count());
        Assert.Equal(1, flushes); // the first fsync started after the followers appended, so it covered all four
        db.CheckIntegrity();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_writer_that_read_a_pending_commit_waits_for_it_even_without_changes(bool rollback)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(Full());
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });

        using var flushing = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        db.Pager.TestBeforeFlush = () =>
        {
            flushing.Set();
            Assert.True(release.Wait(TimeSpan.FromSeconds(10)));
        };
        var first = new Worker(() => c.Insert(new Document { ["_id"] = 1 }));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));

        bool sawPending = false;
        var reader = new Worker(() =>
        {
            using var tx = db.BeginTransaction();
            sawPending = tx.GetCollection("c").FindById(1) is not null;
            if (!rollback) tx.Commit();
        });
        Assert.False(reader.Join(TimeSpan.FromMilliseconds(200))); // must not return before what it read is durable
        release.Set();
        Assert.True(first.Join(TimeSpan.FromSeconds(10)));
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)));
        Assert.True(sawPending);
        Assert.NotNull(c.FindById(1));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void A_writer_that_began_before_a_failed_flush_fails_its_commit_but_not_its_rollback(bool commit)
    {
        using var tmp = new TempDb();
        var db = tmp.Open(Full(autoCheckpointFrames: 1));
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });
        // An open snapshot blocks checkpoints, so the WAL stays above the threshold and every Finish checks it.
        using var snapshot = db.BeginSnapshot();

        using var flushing = new ManualResetEventSlim();
        using var began = new ManualResetEventSlim();
        db.Pager.TestBeforeFlush = () =>
        {
            flushing.Set();
            Assert.True(began.Wait(TimeSpan.FromSeconds(10)));
            throw new IOException("Simulated fsync failure.");
        };
        var failing = new Worker(() => Assert.Throws<FolioException>(() => c.Insert(new Document { ["_id"] = 1 })));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)); // flushing outside the write lock

        var tx = db.BeginTransaction();
        began.Set();
        Assert.True(failing.Join(TimeSpan.FromSeconds(10)));
        tx.GetCollection("c").Insert(new Document { ["_id"] = 2 });
        if (commit) Assert.Throws<FolioException>(tx.Commit);
        tx.Dispose(); // rollback (or cleanup after the failed commit) must not throw
        Assert.Throws<FolioException>(() => db.BeginTransaction());
        db.Pager.TestBeforeFlush = null;
        snapshot.Dispose();
        db.Dispose();

        using var reopened = tmp.Open(Full());
        var rc = reopened.GetCollection("c");
        Assert.Null(rc.FindById(2));
        Assert.InRange(rc.Count(), 1, 2);
        reopened.CheckIntegrity();
    }

    [Fact]
    public void A_rollback_at_the_checkpoint_threshold_keeps_its_exception_when_the_flush_it_waits_for_fails()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(Full(autoCheckpointFrames: 1));
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });

        var snapshot = db.BeginSnapshot(); // keeps the pending commit from checkpointing (and flushing) under the lock
        using var flushing = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        db.Pager.TestBeforeFlush = () =>
        {
            flushing.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(10)));
            throw new IOException("Simulated fsync failure.");
        };
        var pending = new Worker(() => Assert.Throws<FolioException>(() => c.Insert(new Document { ["_id"] = 1 })));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var tx = db.BeginTransaction();
        snapshot.Dispose();

        // The rollback's auto-checkpoint waits for the pending flush, which then fails.
        var original = new InvalidOperationException("user failure");
        Exception? thrown = null;
        var rollback = new Worker(() =>
        {
            try
            {
                using (tx)
                {
                    tx.GetCollection("c").Insert(new Document { ["_id"] = 2 });
                    throw original;
                }
            }
            catch (Exception e) { thrown = e; }
        });
        Thread.Sleep(200);
        proceed.Set();
        Assert.True(pending.Join(TimeSpan.FromSeconds(10)));
        Assert.True(rollback.Join(TimeSpan.FromSeconds(10)));
        Assert.Same(original, thrown);
        db.Pager.TestBeforeFlush = null;
        db.Dispose();
    }

    [Fact]
    public void Dispose_with_an_active_reader_waits_for_an_in_flight_group_flush()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(Full());
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });
        var snapshot = db.BeginSnapshot(); // the closing checkpoint is refused

        using var flushing = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        db.Pager.TestBeforeFlush = () =>
        {
            flushing.Set();
            Assert.True(proceed.Wait(TimeSpan.FromSeconds(10)));
        };
        var pending = new Worker(() => c.Insert(new Document { ["_id"] = 1 }));
        Assert.True(flushing.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        var dispose = new Worker(db.Dispose);
        Thread.Sleep(200);
        proceed.Set();
        Assert.True(pending.Join(TimeSpan.FromSeconds(10))); // acknowledged: not failed by streams closing under it
        Assert.True(dispose.Join(TimeSpan.FromSeconds(10)));
        snapshot.Dispose();

        using var reopened = tmp.Open(Full());
        Assert.NotNull(reopened.GetCollection("c").FindById(1));
    }

    [Fact]
    public void A_failed_flush_rejects_pending_and_later_commits_and_reopen_recovers_a_prefix()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(Full());
        var c = db.GetCollection("c");
        c.Insert(new Document { ["_id"] = 0 });

        db.Pager.TestBeforeFlush = () => throw new IOException("Simulated fsync failure.");
        var e = Assert.Throws<FolioException>(() => c.Insert(new Document { ["_id"] = 1 }));
        Assert.IsType<IOException>(e.InnerException);
        db.Pager.TestBeforeFlush = null;

        Assert.Throws<FolioException>(() => c.Insert(new Document { ["_id"] = 2 }));
        Assert.Null(c.FindById(1)); // never acknowledged, never visible
        Assert.Equal(1, c.Count());
        db.Dispose();

        using var reopened = tmp.Open(Full());
        var rc = reopened.GetCollection("c");
        Assert.NotNull(rc.FindById(0));
        Assert.Null(rc.FindById(2));
        Assert.InRange(rc.Count(), 1, 2); // the unacknowledged write may or may not have reached the disk
        rc.Insert(new Document { ["_id"] = 3 });
        reopened.CheckIntegrity();
    }

    [Fact]
    public void Concurrent_writers_share_flushes_and_lose_no_increment()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(Full(autoCheckpointFrames: 50));
        var c = db.GetCollection("c");
        c.InsertMany(Enumerable.Range(0, 8).Select(i => new Document { ["_id"] = i, ["n"] = 0L }));

        int flushes = 0;
        db.Pager.TestBeforeFlush = () => Interlocked.Increment(ref flushes);
        const int Writers = 8, PerWriter = 100;
        bool done = false;
        var reader = new Worker(() =>
        {
            long last = 0;
            while (!Volatile.Read(ref done))
            {
                long sum = c.Find((Document?)null).Sum(d => d["n"].AsInt64);
                Assert.True(sum >= last, "a reader observed a durable commit disappear");
                last = sum;
            }
        });
        var writers = Enumerable.Range(0, Writers).Select(w => new Worker(() =>
        {
            for (int i = 0; i < PerWriter; i++) c.UpdateOne($"{{ _id: {w} }}", "{ $inc: { n: 1 } }");
        })).ToArray();
        foreach (var w in writers) Assert.True(w.Join(TimeSpan.FromSeconds(60)));
        Volatile.Write(ref done, true);
        Assert.True(reader.Join(TimeSpan.FromSeconds(10)));

        Assert.All(c.Find((Document?)null), d => Assert.Equal(PerWriter, d["n"].AsInt64));
        Assert.InRange(flushes, 1, Writers * PerWriter);
        db.CheckIntegrity();
    }
}
