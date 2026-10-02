using System.Buffers.Binary;
using System.IO.Hashing;

namespace FolioDb.Tests;

/// <summary>Two alternating WAL files: switching, background checkpoints, retiring, and recovery of both files.</summary>
public sealed class Wal2Tests
{
    private static Document Doc(int id, int v) => new() { ["_id"] = id, ["v"] = v, ["pad"] = new string('p', 200) };

    private static string WalPath(TempDb tmp, int file) => tmp.Path + (file == 0 ? "-wal" : "-wal2");

    private static void WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) Assert.Fail($"Timed out waiting for {what}.");
            Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Opens a database whose old WAL file cannot be checkpointed: a snapshot taken right after a full checkpoint keeps
    /// every backfill at its mark. Inserts ids [0, n) until the writer switched files and then a few more; returns the
    /// first id written to the new active file.
    /// </summary>
    private static (FolioDatabase Db, Snapshot Pin, int FirstInActive, int Count) OpenWithPendingOldFile(TempDb tmp)
    {
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 16, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        c.Insert(Doc(-1, 0));
        Assert.True(db.Checkpoint());
        var pin = db.BeginSnapshot();
        int startFile = db.Pager.WalFiles.Active;
        int id = 0;
        while (db.Pager.WalFiles.Active == startFile)
        {
            c.Insert(Doc(id++, id));
            Assert.True(id < 1000, "the writer never switched WAL files");
        }
        // The commit that filled the old file switched after it: the next one goes to the new file.
        int firstInActive = id;
        for (int i = 0; i < 5; i++) c.Insert(Doc(id++, id));
        Assert.True(db.Pager.WalFiles.OldPending);
        return (db, pin, firstInActive, id);
    }

    [Fact]
    public void A_reader_of_the_old_file_keeps_it_and_its_view_until_it_finishes()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 16, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 50; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        int startFile = db.Pager.WalFiles.Active;
        c.UpdateMany("{}", "{ $set: { v: 1 } }"); // lands in the current file (one transaction)
        using (var snap = db.BeginSnapshot())
        {
            int n = 0;
            while (db.Pager.WalFiles.Active == startFile)
            {
                c.UpdateOne(new Document { ["_id"] = n++ % 50 }, Document.Parse("{ $inc: { v: 1 } }"));
                Assert.True(n < 1000, "the writer never switched WAL files");
            }
            for (int i = 0; i < 100; i++) c.UpdateOne(new Document { ["_id"] = i % 50 }, Document.Parse("{ $inc: { v: 1 } }"));

            // The checkpointer copied what it could but cannot retire the file the snapshot reads from.
            Thread.Sleep(100);
            Assert.True(db.Pager.WalFiles.OldPending);
            Assert.False(db.Checkpoint());
            db.Pager.TestDropWalFramesFromCache();
            var sc = snap.GetCollection("docs");
            for (int i = 0; i < 50; i++) Assert.Equal(1, sc.FindById(i)!["v"].AsInt32);
        }
        Assert.True(db.Checkpoint());
        Assert.Equal(0, db.Pager.WalFrameCount);
        Assert.False(db.Pager.WalFiles.OldPending);
        Assert.Equal(50, c.Count("{ v: { $gt: 1 } }"));
        db.CheckIntegrity();
    }

    [Fact]
    public void The_background_checkpointer_retires_the_old_file_once_its_readers_finish()
    {
        using var tmp = new TempDb();
        var (db, pin, _, count) = OpenWithPendingOldFile(tmp);
        using (db)
        {
            Thread.Sleep(50);
            Assert.True(db.Pager.WalFiles.OldPending);
            pin.Dispose();
            WaitUntil(() => !db.Pager.WalFiles.OldPending, "the old WAL file to be retired");
            Assert.Equal(count + 1, db.GetCollection("docs").Count());
            // The next switch reuses the retired file.
            int active = db.Pager.WalFiles.Active;
            var c = db.GetCollection("docs");
            for (int i = 0; db.Pager.WalFiles.Active == active; i++)
            {
                c.Insert(Doc(10_000 + i, i));
                Assert.True(i < 1000, "the writer never switched WAL files again");
            }
            db.CheckIntegrity();
        }
    }

    [Fact]
    public void A_crash_with_frames_in_both_files_replays_both()
    {
        using var tmp = new TempDb();
        var (db, pin, _, count) = OpenWithPendingOldFile(tmp);
        db.SimulateCrash();
        pin.Dispose();

        using var reopened = tmp.Open();
        var c = reopened.GetCollection("docs");
        Assert.Equal(count + 1, c.Count());
        for (int i = 0; i < count; i++) Assert.Equal(i + 1, c.FindById(i)!["v"].AsInt32);
        Assert.Equal(0, reopened.Pager.WalFrameCount);
        reopened.CheckIntegrity();
    }

    [Fact]
    public void A_torn_retired_header_falls_back_to_the_previous_record()
    {
        using var tmp = new TempDb();
        var (db, pin, _, count) = OpenWithPendingOldFile(tmp);
        using var retired = new ManualResetEventSlim();
        int tornFile = -1;
        long tornOffset = -1;
        db.Pager.TestAfterRetireRecord = (file, offset) =>
        {
            tornFile = file;
            tornOffset = offset;
            retired.Set();
            throw new IOException("Simulated power loss while writing the retired header.");
        };
        pin.Dispose();
        Assert.True(retired.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken));
        Thread.Sleep(20);
        db.SimulateCrash();

        // Power loss tore the record: the slot holds garbage, the frames are still there (never truncated).
        using (var fs = new FileStream(WalPath(tmp, tornFile), FileMode.Open))
        {
            fs.Position = tornOffset + 20;
            fs.Write(new byte[16]);
        }

        using var reopened = tmp.Open();
        var c = reopened.GetCollection("docs");
        Assert.Equal(count + 1, c.Count());
        for (int i = 0; i < count; i++) Assert.Equal(i + 1, c.FindById(i)!["v"].AsInt32);
        reopened.CheckIntegrity();
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void Frames_whose_predecessor_file_is_lost_are_not_replayed(bool bothRecords)
    {
        using var tmp = new TempDb();
        var (db, pin, _, _) = OpenWithPendingOldFile(tmp);
        int oldFile = 1 - db.Pager.WalFiles.Active;
        db.SimulateCrash();
        pin.Dispose();

        // The old file's header never reached the disk. Without both records the file looks foreign; with only its
        // newest one lost, its previous "retired" record (from two files ago) is what recovery finds.
        using (var fs = new FileStream(WalPath(tmp, oldFile), FileMode.Open))
        {
            var header = new byte[Pager.WalHeaderRegion];
            fs.ReadExactly(header);
            for (int slot = 0; slot < 2; slot++)
            {
                var record = header.AsSpan(slot * Pager.WalHeaderRegion / 2, Pager.WalHeaderRegion / 2);
                if (bothRecords || BitConverter.ToInt32(record[12..]) == 1) record.Clear();
                else Assert.Equal(2, BitConverter.ToInt32(record[12..]));
            }
            fs.Position = 0;
            fs.Write(header);
        }

        // The new file's frames build on the lost ones: applying them alone would mix two states.
        using var reopened = tmp.Open();
        var c = reopened.GetCollection("docs");
        Assert.Equal(1, c.Count());
        Assert.NotNull(c.FindById(-1));
        reopened.CheckIntegrity();
    }

    [Fact]
    public void Frames_after_a_broken_chain_in_the_old_file_are_not_replayed()
    {
        using var tmp = new TempDb();
        var (db, pin, firstInActive, _) = OpenWithPendingOldFile(tmp);
        int oldFile = 1 - db.Pager.WalFiles.Active;
        db.SimulateCrash();
        pin.Dispose();

        // Corrupt the last frame of the old file: its last transaction is lost, so the active file no longer follows.
        int frameSize = 24 + 4096;
        using (var fs = new FileStream(WalPath(tmp, oldFile), FileMode.Open))
        {
            long frames = (fs.Length - Pager.WalHeaderRegion) / frameSize;
            fs.Position = Pager.WalHeaderRegion + (frames - 1) * frameSize + 100;
            int b = fs.ReadByte();
            fs.Position -= 1;
            fs.WriteByte((byte)~b);
        }

        using var reopened = tmp.Open();
        var c = reopened.GetCollection("docs");
        Assert.Null(c.FindById(firstInActive - 1));
        Assert.Null(c.FindById(firstInActive));
        for (int i = 0; i < firstInActive - 1; i++) Assert.NotNull(c.FindById(i));
        Assert.Equal(firstInActive, c.Count());
        reopened.CheckIntegrity();
    }

    private static (int Slot, int State, long Epoch, long Seq) NewestRecord(byte[] wal)
    {
        (int, int, long, long) best = (-1, 0, -1, 0);
        for (int slot = 0; slot < 2; slot++)
        {
            var r = wal.AsSpan(slot * Pager.WalHeaderRegion / 2, 56);
            if (BinaryPrimitives.ReadUInt64LittleEndian(r[48..]) != XxHash64.HashToUInt64(r[..48])) continue;
            long epoch = BinaryPrimitives.ReadInt64LittleEndian(r[16..]);
            if (epoch > best.Item3) best = (slot, BinaryPrimitives.ReadInt32LittleEndian(r[12..]), epoch, BinaryPrimitives.ReadInt64LittleEndian(r[24..]));
        }
        return best;
    }

    [Fact]
    public void A_crash_between_the_reset_headers_does_not_replay_a_superseded_file()
    {
        using var tmp = new TempDb();
        var (db, pin, _, count) = OpenWithPendingOldFile(tmp);
        int active = db.Pager.WalFiles.Active;
        db.SimulateCrash();
        pin.Dispose();
        var older = File.ReadAllBytes(WalPath(tmp, 1 - active));
        var newer = File.ReadAllBytes(WalPath(tmp, active));
        using (var recovered = tmp.Open()) Assert.Equal(count + 1, recovered.GetCollection("docs").Count());

        // Recovery copied both files into the main file and started resetting them: the "retired" record of the newer
        // file (on -wal2) is on disk, the older file's active record (on -wal) is not replaced yet.
        var (slot, state, epoch, seq) = NewestRecord(newer);
        Assert.Equal(1, state);
        var record = newer.AsSpan((1 - slot) * Pager.WalHeaderRegion / 2, 56);
        newer.AsSpan(slot * Pager.WalHeaderRegion / 2, 48).CopyTo(record);
        BinaryPrimitives.WriteInt32LittleEndian(record[12..], 2);
        BinaryPrimitives.WriteInt64LittleEndian(record[16..], epoch + 100);
        BinaryPrimitives.WriteInt64LittleEndian(record[24..], seq + 1);
        BinaryPrimitives.WriteUInt64LittleEndian(record[48..], XxHash64.HashToUInt64(record[..48]));
        File.WriteAllBytes(tmp.Path + "-wal", older);
        File.WriteAllBytes(tmp.Path + "-wal2", newer);

        // Replaying the older file would put back page versions older than the main file's.
        using var reopened = tmp.Open();
        var c = reopened.GetCollection("docs");
        Assert.Equal(count + 1, c.Count());
        for (int i = 0; i < count; i++) Assert.Equal(i + 1, c.FindById(i)!["v"].AsInt32);
        reopened.CheckIntegrity();
    }

    [Fact]
    public void Transactions_dropped_by_recovery_do_not_come_back_after_a_retry()
    {
        using var tmp = new TempDb();
        var options = new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal };
        var db = tmp.Open(options);
        db.GetCollection("docs").Insert(Doc(0, 0));
        Assert.True(db.Checkpoint());
        int active = db.Pager.WalFiles.Active;
        db.GetCollection("docs").Insert(Doc(1, 1));
        db.GetCollection("docs").Insert(Doc(2, 2));
        db.SimulateCrash();

        // The first frame is damaged: recovery drops both transactions, but their bytes stay in the file.
        using (var fs = new FileStream(WalPath(tmp, active), FileMode.Open))
        {
            fs.Position = Pager.WalHeaderRegion + 24 + 100;
            int b = fs.ReadByte();
            fs.Position -= 1;
            fs.WriteByte((byte)~b);
        }
        db = tmp.Open(options);
        Assert.Equal(1, db.GetCollection("docs").Count());
        // Writing the first transaction again must not reconnect the dropped tail.
        db.GetCollection("docs").Insert(Doc(1, 1));
        db.SimulateCrash();

        using var reopened = tmp.Open(options);
        var c = reopened.GetCollection("docs");
        Assert.NotNull(c.FindById(1));
        Assert.Null(c.FindById(2));
        reopened.CheckIntegrity();
    }

    [Fact]
    public void A_single_file_wal_from_the_previous_format_is_recovered()
    {
        using var tmp = new TempDb();
        var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 0, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 30; i++) c.Insert(Doc(i, i));
        db.SimulateCrash();

        // Rewrite the active file's frames in the v1 layout: 32-byte header, frames chained from the salts.
        int frameSize = 24 + 4096;
        byte[] v2 = File.ReadAllBytes(tmp.Path + "-wal");
        int frames = (v2.Length - Pager.WalHeaderRegion) / frameSize;
        Assert.True(frames > 30);
        uint salt1 = 0x12345678, salt2 = 0x9abcdef0;
        var v1 = new byte[32 + frames * frameSize];
        "FOLIOWAL"u8.CopyTo(v1);
        BinaryPrimitives.WriteInt32LittleEndian(v1.AsSpan(8), 1);
        BinaryPrimitives.WriteInt32LittleEndian(v1.AsSpan(12), 4096);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(16), salt1);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(20), salt2);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(24), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(v1.AsSpan(28), (uint)XxHash64.HashToUInt64(v1.AsSpan(0, 28)));
        ulong chain = ((ulong)salt1 << 32) | salt2;
        for (int f = 0; f < frames; f++)
        {
            var frame = v1.AsSpan(32 + f * frameSize, frameSize);
            v2.AsSpan(Pager.WalHeaderRegion + f * frameSize, frameSize).CopyTo(frame);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[16..], salt1);
            BinaryPrimitives.WriteUInt32LittleEndian(frame[20..], salt2);
            chain = XxHash64.HashToUInt64(frame[8..], unchecked((long)chain));
            BinaryPrimitives.WriteUInt64LittleEndian(frame, chain);
        }
        File.WriteAllBytes(tmp.Path + "-wal", v1);
        File.Delete(tmp.Path + "-wal2");

        using (var reopened = tmp.Open())
        {
            var rc = reopened.GetCollection("docs");
            Assert.Equal(30, rc.Count());
            for (int i = 0; i < 30; i++) Assert.Equal(i, rc.FindById(i)!["v"].AsInt32);
            rc.Insert(Doc(100, 100));
        }
        using var again = tmp.Open();
        Assert.Equal(31, again.GetCollection("docs").Count());
        again.CheckIntegrity();
    }

    /// <summary>
    /// Switches WAL files while the checkpointer is held before its backfill writes (released by the returned event),
    /// with no commit after the switch: every page's latest frame is in the old file, and none of them is cached.
    /// </summary>
    private static (ManualResetEventSlim Backfill, int OldFile, int Increments) SwitchWithHeldBackfill(FolioDatabase db)
    {
        var c = db.GetCollection("docs");
        for (int i = 0; i < 50; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        var backfill = new ManualResetEventSlim();
        int held = 0;
        db.Pager.TestBeforeBackfillWrites = () =>
        {
            if (Interlocked.Exchange(ref held, 1) == 0) backfill.Wait();
        };
        int oldFile = db.Pager.WalFiles.Active, n = 0;
        while (db.Pager.WalFiles.Active == oldFile)
        {
            c.UpdateOne(new Document { ["_id"] = n++ % 50 }, Document.Parse("{ $inc: { v: 1 } }"));
            Assert.True(n < 1000, "the writer never switched WAL files");
        }
        WaitUntil(() => Volatile.Read(ref held) == 1, "the checkpointer to start a backfill");
        db.Pager.TestDropWalFramesFromCache();
        return (backfill, oldFile, n);
    }

    private static long SumOfV(Collection c)
    {
        long sum = 0;
        for (int i = 0; i < 50; i++) sum += c.FindById(i)!["v"].AsInt32;
        return sum;
    }

    [Fact]
    public void The_old_file_is_not_retired_while_the_writer_reads_from_it()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 16, Synchronous = SynchronousMode.Normal });
        var (backfill, oldFile, increments) = SwitchWithHeldBackfill(db);
        using var _ = backfill;

        int reads = 0;
        bool retiredMeanwhile = false;
        db.Pager.TestBeforeWalFrameRead = file =>
        {
            if (file != oldFile || Interlocked.Exchange(ref reads, 1) != 0) return;
            // The checkpointer may backfill the file now, but not retire it under this transaction.
            backfill.Set();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!retiredMeanwhile && clock.ElapsedMilliseconds < 300)
            {
                retiredMeanwhile = !db.Pager.WalFiles.OldPending;
                Thread.Sleep(5);
            }
            if (retiredMeanwhile) Thread.Sleep(50); // let it truncate the file
        };
        using (var tx = db.BeginTransaction())
        {
            Assert.Equal(increments, SumOfV(tx.GetCollection("docs")));
            tx.Commit();
        }
        Assert.Equal(1, reads);
        Assert.False(retiredMeanwhile);
        WaitUntil(() => !db.Pager.WalFiles.OldPending, "the old file to be retired");
        Assert.Equal(increments, SumOfV(db.GetCollection("docs")));
        db.CheckIntegrity();
    }

    [Fact]
    public async Task A_reader_registering_during_a_retire_does_not_keep_the_old_file()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 16, Synchronous = SynchronousMode.Normal });
        var (backfill, oldFile, increments) = SwitchWithHeldBackfill(db);
        using var _ = backfill;
        using var markChosen = new ManualResetEventSlim();
        using var register = new ManualResetEventSlim();
        using var scanned = new ManualResetEventSlim();
        using var retire = new ManualResetEventSlim();
        using var readingOld = new ManualResetEventSlim();
        int readerHeld = 0, retireHeld = 0, oldReads = 0;
        // The reader chooses its snapshot (with the old file) before the backfill, and registers it after the retire
        // found no readers but before it publishes the layout without the file.
        db.Pager.TestBeforeReadMark = () =>
        {
            if (Interlocked.Exchange(ref readerHeld, 1) != 0) return;
            markChosen.Set();
            register.Wait();
        };
        db.Pager.TestAfterRetireScan = () =>
        {
            if (Interlocked.Exchange(ref retireHeld, 1) != 0) return;
            scanned.Set();
            retire.Wait();
        };
        db.Pager.TestBeforeWalFrameRead = file =>
        {
            if (file != oldFile || Interlocked.Exchange(ref oldReads, 1) != 0) return;
            readingOld.Set();
            retire.Set();
            WaitUntil(() => !db.Pager.WalFiles.OldPending, "the old file to be retired");
            Thread.Sleep(50); // let it truncate the file
        };

        var token = TestContext.Current.CancellationToken;
        var reader = Task.Factory.StartNew(() =>
        {
            using var snap = db.BeginSnapshot();
            return SumOfV(snap.GetCollection("docs"));
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(markChosen.Wait(TimeSpan.FromSeconds(10), token));
        backfill.Set();
        Assert.True(scanned.Wait(TimeSpan.FromSeconds(10), token));
        register.Set();
        // It must retry with a snapshot past the retire rather than read the old file.
        Assert.False(readingOld.Wait(TimeSpan.FromMilliseconds(300), token));
        retire.Set();
        Assert.Equal(increments, await reader.WaitAsync(TimeSpan.FromSeconds(10), token));
        Assert.Equal(0, oldReads);
        db.CheckIntegrity();
    }

    [Fact]
    public void A_writer_faster_than_the_checkpointer_is_slowed_down()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 32, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        for (int i = 0; i < 100; i++) c.Insert(Doc(i, 0));
        Assert.True(db.Checkpoint());
        db.Pager.TestBeforeBackfillWrites = () => Thread.Sleep(20); // a slow disk
        long maxWal = 0;
        for (int n = 0; n < 3000; n++)
        {
            c.UpdateOne(new Document { ["_id"] = n % 100 }, Document.Parse("{ $inc: { v: 1 } }"));
            maxWal = Math.Max(maxWal, db.Pager.WalFrameCount);
        }
        // The active file stops growing at 6 files' worth (plus a commit); the old one held at most as much.
        Assert.True(maxWal <= 2 * (6 * 32 + 8), $"maxWal={maxWal}");
        Assert.True(db.Checkpoint());
        db.CheckIntegrity();
    }

    [Fact]
    public async Task A_reader_holding_the_checkpoint_back_does_not_block_the_writer()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 16, Synchronous = SynchronousMode.Normal });
        var c = db.GetCollection("docs");
        c.Insert(Doc(-1, 0));
        Assert.True(db.Checkpoint());
        using var pin = db.BeginSnapshot();
        var token = TestContext.Current.CancellationToken;
        // Far past the hard limit: the WAL grows instead, since waiting could not help.
        var writer = Task.Factory.StartNew(() =>
        {
            for (int i = 0; i < 400; i++) c.Insert(Doc(i, i));
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        await writer.WaitAsync(TimeSpan.FromSeconds(20), token);
        Assert.True(db.Pager.WalFrameCount > 6 * 16 * 2);
        Assert.Equal(1, pin.GetCollection("docs").Count());
    }

    [Fact]
    public async Task Readers_and_the_writer_stay_consistent_across_many_switches()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { AutoCheckpointFrames = 8, Synchronous = SynchronousMode.Normal, CacheSizePages = 64 });
        var accounts = db.GetCollection("accounts");
        for (int i = 0; i < 100; i++) accounts.Insert(new Document { ["_id"] = i, ["balance"] = 100, ["pad"] = new string('p', 300) });
        var token = TestContext.Current.CancellationToken;

        // Runs until enough happened rather than for a fixed time: the test may share the machine with others.
        using var cts = new CancellationTokenSource();
        long violations = 0, reads = 0, switches = 0;
        var writer = Task.Factory.StartNew(() =>
        {
            var rnd = new Random(1);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            int last = db.Pager.WalFiles.Active;
            try
            {
                while ((switches < 5 || Interlocked.Read(ref reads) < 50) && clock.Elapsed < TimeSpan.FromSeconds(20))
                {
                    int from = rnd.Next(100), to = rnd.Next(100);
                    using (var tx = db.BeginTransaction())
                    {
                        var c = tx.GetCollection("accounts");
                        c.UpdateOne(new Document { ["_id"] = from }, Document.Parse("{ $inc: { balance: -1 } }"));
                        c.UpdateOne(new Document { ["_id"] = to }, Document.Parse("{ $inc: { balance: 1 } }"));
                        tx.Commit();
                    }
                    int active = db.Pager.WalFiles.Active;
                    if (active != last) switches++;
                    last = active;
                }
            }
            finally
            {
                cts.Cancel();
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var readers = Enumerable.Range(0, 4).Select(r => Task.Factory.StartNew(() =>
        {
            var rnd = new Random(r);
            while (!cts.IsCancellationRequested)
            {
                using (var snap = db.BeginSnapshot())
                {
                    var c = snap.GetCollection("accounts");
                    long total = 0;
                    foreach (var d in c.Find("{}")) total += d["balance"].AsInt64;
                    if (rnd.Next(8) == 0) Thread.Sleep(1); // some snapshots outlive a switch
                    for (int i = 0; i < 100; i++) total -= c.FindById(i)!["balance"].AsInt64;
                    if (total != 0) Interlocked.Increment(ref violations);
                    if (c.Find("{}").Sum(d => d["balance"].AsInt64) != 10_000) Interlocked.Increment(ref violations);
                }
                Interlocked.Increment(ref reads);
            }
        }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default)).ToList();

        await Task.WhenAll(readers.Append(writer));
        Assert.Equal(0, violations);
        Assert.True(reads > 50, $"reads={reads}");
        Assert.True(switches >= 5, $"switches={switches}");
        Assert.True(db.Checkpoint());
        Assert.Equal(10_000, accounts.Find("{}").Sum(d => d["balance"].AsInt64));
        db.CheckIntegrity();
    }
}
