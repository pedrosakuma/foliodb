using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class ConcurrentCheckpointTests
{
    private static Document Doc(int id, int v) => new() { ["_id"] = id, ["v"] = v, ["pad"] = new string('p', 200) };

    private static void SetAll(Collection c, int count, int v)
    {
        for (int i = 0; i < count; i++) c.UpdateOne(new Document { ["_id"] = i }, Document.Parse($"{{ $set: {{ v: {v} }} }}"));
    }

    private static void AssertAll(Collection c, int count, int v)
    {
        for (int i = 0; i < count; i++) Assert.Equal(v, c.FindById(i)!["v"].AsInt32);
    }

    [Fact]
    public async Task Wal_restarts_under_continuous_overlapping_readers()
    {
        // Before backfill/restart, any active reader blocked the whole checkpoint, so overlapping readers grew the WAL
        // without bound. Now readers of older snapshots only limit how far a backfill goes, and the WAL restarts once
        // the readers that still use it are gone.
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 64, Synchronous = SynchronousMode.Normal });
        var accounts = db.GetCollection("accounts");
        for (int i = 0; i < 200; i++) accounts.Insert(new Document { ["_id"] = i, ["balance"] = 100, ["pad"] = new string('p', 100) });
        var token = TestContext.Current.CancellationToken;

        using var cts = new CancellationTokenSource();
        long violations = 0, reads = 0, restarts = 0, maxWal = 0;
        var readers = Enumerable.Range(0, 4).Select(r => Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                using (var snap = db.BeginSnapshot())
                {
                    var c = snap.GetCollection("accounts");
                    long scanned = 0, looked = 0;
                    foreach (var d in c.Find("{}")) scanned += d["balance"].AsInt64;
                    for (int i = 0; i < 200; i++) looked += c.FindById(i)!["balance"].AsInt64;
                    if (scanned != 20000 || looked != 20000) Interlocked.Increment(ref violations);
                }
                Interlocked.Increment(ref reads);
            }
        }, token)).ToList();

        var rnd = new Random(1);
        long previous = 0;
        for (int n = 0; n < 1500; n++)
        {
            int from = rnd.Next(200), to = rnd.Next(200);
            using (var tx = db.BeginTransaction())
            {
                var c = tx.GetCollection("accounts");
                c.UpdateOne(new Document { ["_id"] = from }, Document.Parse("{ $inc: { balance: -1 } }"));
                c.UpdateOne(new Document { ["_id"] = to }, Document.Parse("{ $inc: { balance: 1 } }"));
                tx.Commit();
            }
            long wal = db.Pager.WalFrameCount;
            if (wal < previous) restarts++;
            previous = wal;
            maxWal = Math.Max(maxWal, wal);
        }
        cts.Cancel();
        await Task.WhenAll(readers);

        Assert.Equal(0, violations);
        Assert.True(reads > 50, $"reads={reads}");
        Assert.True(restarts >= 3, $"restarts={restarts} maxWal={maxWal}");
        Assert.True(maxWal < 1500, $"maxWal={maxWal}");
        db.CheckIntegrity();
    }

    [Fact]
    public void Readers_are_not_blocked_while_a_checkpoint_copies_pages()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        SetAll(c, 300, 1);

        int inside = 0;
        db.Pager.TestDuringBackfill = () =>
        {
            inside++;
            // The main file now holds the new images, unsynced; a reader must neither wait nor see a mix.
            var read = Task.Run(() =>
            {
                using var snap = db.BeginSnapshot();
                AssertAll(snap.GetCollection("docs"), 300, 1);
                Assert.Equal(300, snap.GetCollection("docs").Count("{ v: 1 }"));
            }, TestContext.Current.CancellationToken);
            Assert.True(read.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        };
        Assert.True(db.Checkpoint());
        Assert.Equal(1, inside);
        Assert.Equal(0, db.Pager.WalFrameCount);
        AssertAll(c, 300, 1);
        db.CheckIntegrity();
    }

    [Fact]
    public async Task A_reader_that_chose_the_main_file_before_a_backfill_started_retries_with_a_wal_snapshot()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        var token = TestContext.Current.CancellationToken;

        // The reader picks mark 0 (everything is in the main file), then stalls before registering it.
        using var chosen = new ManualResetEventSlim();
        using var register = new ManualResetEventSlim();
        int armed = 1;
        db.Pager.TestBeforeReadMark = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0) return;
            chosen.Set();
            Assert.True(register.Wait(TimeSpan.FromSeconds(10)));
        };
        using var halfRead = new ManualResetEventSlim();
        using var backfilled = new ManualResetEventSlim();
        var values = new int[300];
        var reader = Task.Run(() =>
        {
            using var snap = db.BeginSnapshot();
            var sc = snap.GetCollection("docs");
            for (int i = 0; i < 150; i++) values[i] = sc.FindById(i)!["v"].AsInt32;
            halfRead.Set();
            Assert.True(backfilled.Wait(TimeSpan.FromSeconds(10)));
            for (int i = 150; i < 300; i++) values[i] = sc.FindById(i)!["v"].AsInt32;
        }, token);
        Assert.True(chosen.Wait(TimeSpan.FromSeconds(10), token));

        SetAll(c, 300, 1);
        using var keeper = db.BeginSnapshot(); // keeps the WAL from restarting, so only the backfill target protects the reader
        // The backfill has looked at the read marks (the reader is not registered yet) and is about to overwrite pages.
        db.Pager.TestBeforeBackfillWrites = () =>
        {
            register.Set();
            halfRead.Wait(TimeSpan.FromMilliseconds(500));
        };
        Assert.False(db.Checkpoint());
        backfilled.Set();
        await reader.WaitAsync(TimeSpan.FromSeconds(10), token);

        // Either snapshot is fine; a mix of both is not.
        Assert.True(values.All(v => v == values[0]), $"mixed snapshot: {values.Count(v => v == 0)} old, {values.Count(v => v == 1)} new");
    }

    [Fact]
    public async Task A_reader_that_chose_a_mark_before_the_wal_restarted_does_not_keep_it()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        SetAll(c, 300, 1);
        var token = TestContext.Current.CancellationToken;

        // The reader picks the current WAL mark, then stalls before registering it.
        using var chosen = new ManualResetEventSlim();
        using var register = new ManualResetEventSlim();
        using var firstRead = new ManualResetEventSlim();
        using var rewritten = new ManualResetEventSlim();
        int armed = 1;
        db.Pager.TestBeforeReadMark = () =>
        {
            if (Interlocked.Exchange(ref armed, 0) == 0) return;
            chosen.Set();
            Assert.True(register.Wait(TimeSpan.FromSeconds(10)));
        };
        var values = new int[300];
        var reader = Task.Run(() =>
        {
            using var snap = db.BeginSnapshot();
            var sc = snap.GetCollection("docs");
            for (int i = 0; i < 150; i++) values[i] = sc.FindById(i)!["v"].AsInt32;
            firstRead.Set();
            Assert.True(rewritten.Wait(TimeSpan.FromSeconds(10)));
            // Pages this transaction has not read yet: they must still come from its snapshot.
            for (int i = 150; i < 300; i++) values[i] = sc.FindById(i)!["v"].AsInt32;
        }, token);
        Assert.True(chosen.Wait(TimeSpan.FromSeconds(10), token));

        // The WAL restarts meanwhile; new frames reuse the numbers the reader's mark covered.
        Assert.True(db.Checkpoint());
        register.Set();
        Assert.True(firstRead.Wait(TimeSpan.FromSeconds(10), token));
        SetAll(c, 300, 2);
        rewritten.Set();
        await reader.WaitAsync(TimeSpan.FromSeconds(10), token);

        Assert.True(values.All(v => v == 1), $"snapshot changed: {values.Count(v => v == 2)} of 300 values are newer");
    }

    [Fact]
    public void An_old_snapshot_limits_the_backfill_and_keeps_its_view()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        SetAll(c, 300, 1);
        long mark = db.Pager.WalFrameCount;

        using (var snap = db.BeginSnapshot())
        {
            SetAll(c, 300, 2);
            Assert.False(db.Checkpoint());
            // Everything the snapshot can see was copied; the newer frames wait for it.
            Assert.Equal(mark, db.Pager.Backfilled);
            AssertAll(snap.GetCollection("docs"), 300, 1);
            AssertAll(c, 300, 2);
        }

        // A crash now must recover the latest commits on top of the partially backfilled main file.
        db.SimulateCrash();
        using var reopened = tmp.Open();
        AssertAll(reopened.GetCollection("docs"), 300, 2);
        reopened.CheckIntegrity();
    }

    [Fact]
    public void A_crash_in_the_middle_of_a_backfill_recovers_from_the_wal()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        SetAll(c, 300, 1);
        SetAll(c, 300, 2);

        db.Pager.TestDuringBackfill = () => throw new IOException("Simulated crash during backfill.");
        Assert.Throws<IOException>(() => db.Checkpoint());
        Assert.Equal(0, db.Pager.Backfilled);
        db.SimulateCrash();

        using var reopened = tmp.Open();
        AssertAll(reopened.GetCollection("docs"), 300, 2);
        Assert.Equal(0, reopened.Pager.WalFrameCount);
        reopened.CheckIntegrity();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Readers_of_the_main_file_never_see_images_cached_before_the_backfill(bool walFramesEvicted)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { CacheSizePages = 4096, AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 300; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        AssertAll(c, 300, 0); // caches every page under its main-file key
        SetAll(c, 300, 1);
        if (walFramesEvicted) db.Pager.TestDropWalFramesFromCache();

        using (var snap = db.BeginSnapshot())
        {
            Assert.False(db.Checkpoint()); // fully backfilled, but the snapshot still reads the WAL
            Assert.Equal(db.Pager.WalFrameCount, db.Pager.Backfilled);
        }
        // New readers read the main file only now; the cache must not hand them the old images.
        AssertAll(c, 300, 1);
        Assert.Equal(300, c.Count("{ v: 1 }"));
        Assert.True(db.Checkpoint());
        AssertAll(c, 300, 1);
    }

    [Fact]
    public void A_snapshot_opened_while_every_read_mark_slot_is_taken_sees_the_latest_commit()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0 });
        var c = db.GetCollection("c");
        var snaps = new List<Snapshot>();
        try
        {
            // Each snapshot holds a distinct WAL mark, filling every slot.
            for (int i = 0; i < ReadMarks.Slots; i++)
            {
                c.Insert(Doc(i, i));
                snaps.Add(db.BeginSnapshot());
            }
            c.Insert(Doc(1000, 1000));
            using var latest = db.BeginSnapshot();
            Assert.NotNull(latest.GetCollection("c").FindById(1000));
            Assert.Equal(ReadMarks.Slots + 1, latest.GetCollection("c").Count());
            for (int i = 0; i < snaps.Count; i++) Assert.Equal(i + 1, snaps[i].GetCollection("c").Count());
        }
        finally
        {
            foreach (var s in snaps) s.Dispose();
        }
    }

    [Fact]
    public async Task A_full_set_of_read_marks_never_registers_a_reader_above_its_own_mark()
    {
        var marks = new ReadMarks();
        var slots = new List<int>();
        for (long m = 1; m <= ReadMarks.Slots; m++) slots.Add(marks.Enter(m * 10, out _));
        // A WAL reader joins the newest lower mark, which protects more than it needs.
        int lower = marks.Enter(55, out long joined);
        Assert.Equal(50, joined);
        marks.Exit(lower);
        // A main-file reader cannot join any WAL mark (it would be counted as using the WAL) nor a mark above it,
        // so it waits for a free slot.
        var zero = Task.Run(() => (marks.Enter(0, out long j), j), TestContext.Current.CancellationToken);
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(zero.IsCompleted);
        marks.Exit(slots[3]);
        var (slot, j0) = await zero.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, j0);
        Assert.Equal(0, marks.MinActive());
        marks.Exit(slot);
        foreach (int s in slots.Where((_, i) => i != 3)) marks.Exit(s);
        Assert.Equal(long.MaxValue, marks.MinActive());
    }

    [Fact]
    public void Read_marks_track_the_oldest_snapshot_and_share_the_newest_slot_when_full()
    {
        var marks = new ReadMarks();
        Assert.Equal(long.MaxValue, marks.MinActive());
        Assert.False(marks.AnyWalReaders());

        int zero = marks.Enter(0, out long joined);
        Assert.Equal(0, joined);
        Assert.False(marks.AnyWalReaders());
        Assert.Equal(0, marks.MinActive());
        marks.Exit(zero);

        var slots = new List<int>();
        for (long m = 1; m <= ReadMarks.Slots; m++)
        {
            slots.Add(marks.Enter(m, out joined));
            Assert.Equal(m, joined);
        }
        Assert.Equal(1, marks.MinActive());
        Assert.True(marks.AnyWalReaders());
        // Same mark: shares its slot. No free slot for a new mark: joins the newest snapshot.
        int again = marks.Enter(5, out joined);
        Assert.Equal(5, joined);
        Assert.Equal(slots[4], again);
        int newest = marks.Enter(100, out joined);
        Assert.Equal(ReadMarks.Slots, joined);
        Assert.Equal(slots[^1], newest);

        marks.Exit(slots[0]);
        Assert.Equal(2, marks.MinActive());
        foreach (int s in slots.Skip(1)) marks.Exit(s);
        Assert.Equal(5, marks.MinActive());
        marks.Exit(again);
        Assert.Equal(ReadMarks.Slots, marks.MinActive());
        marks.Exit(newest);
        Assert.Equal(long.MaxValue, marks.MinActive());
        Assert.False(marks.AnyWalReaders());
    }
}
