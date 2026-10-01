using FolioDb.Storage;

namespace FolioDb.Tests;

public sealed class WriterAdmissionTests
{
    private static Task Run(Action action) => Task.Factory.StartNew(action, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default);

    private static void Until(Func<bool> condition) =>
        Assert.True(SpinWait.SpinUntil(condition, TimeSpan.FromSeconds(5)), "Waiter did not reach expected state.");

    [Fact]
    public async Task FifoPreservesQueueOrder()
    {
        var gate = new WriterLock(WriterAdmissionMode.Fifo);
        Assert.True(gate.Wait(TimeSpan.Zero));
        var order = new List<int>();
        var tasks = new List<Task>();
        try
        {
            for (int i = 0; i < 8; i++)
            {
                int id = i;
                tasks.Add(Run(() =>
                {
                    Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
                    try { order.Add(id); }
                    finally { gate.Release(); }
                }));
                Until(() => gate.WaitingCount == id + 1);
                Assert.False(gate.Wait(TimeSpan.Zero));
            }
        }
        finally { gate.Release(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.Equal(Enumerable.Range(0, 8), order);
        Assert.Equal(0, gate.WaitingCount);
    }

    [Fact]
    public async Task ExpiredAndInterruptedWaitersDoNotBlockSuccessors()
    {
        var gate = new WriterLock(WriterAdmissionMode.Fifo);
        Assert.True(gate.Wait(TimeSpan.Zero));
        Task? successor = null;
        try
        {
            var expired = Run(() => Assert.False(gate.Wait(TimeSpan.FromMilliseconds(100))));
            Until(() => gate.WaitingCount == 1);
            successor = Run(() =>
            {
                Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
                gate.Release();
            });
            Until(() => gate.WaitingCount == 2);
            await expired.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(1, gate.WaitingCount);
            var interrupted = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
            var thread = new Thread(() =>
            {
                try
                {
                    if (gate.Wait(Timeout.InfiniteTimeSpan)) gate.Release();
                    interrupted.SetResult(null);
                }
                catch (Exception e) { interrupted.SetResult(e); }
            }) { IsBackground = true };
            thread.Start();
            Until(() => gate.WaitingCount == 2);
            thread.Interrupt();
            Assert.IsType<ThreadInterruptedException>(await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            thread.Join();
            Assert.Equal(1, gate.WaitingCount);
        }
        finally { gate.Release(); }
        if (successor is not null) await successor.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.True(gate.Wait(TimeSpan.Zero));
        gate.Release();
    }

    [Theory]
    [InlineData(WriterAdmissionMode.Default)]
    [InlineData(WriterAdmissionMode.Fifo)]
    public async Task ZeroAndInfiniteTimeouts(WriterAdmissionMode mode)
    {
        var gate = new WriterLock(mode);
        Assert.True(gate.Wait(TimeSpan.Zero));
        Assert.False(gate.Wait(TimeSpan.Zero));
        using var ready = new ManualResetEventSlim();
        var task = Run(() =>
        {
            ready.Set();
            Assert.True(gate.Wait(Timeout.InfiniteTimeSpan));
            gate.Release();
        });
        try { Assert.True(ready.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken)); }
        finally { gate.Release(); }
        await task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task ExpiredSpinningHeadPromotesSuccessorsInOrder()
    {
        // The first two waiters spin before parking; when the head expires, the queue must still be served in order.
        var gate = new WriterLock(WriterAdmissionMode.Fifo);
        Assert.True(gate.Wait(TimeSpan.Zero));
        var order = new List<int>();
        var tasks = new List<Task>();
        try
        {
            var expired = Run(() => Assert.False(gate.Wait(TimeSpan.FromMilliseconds(50))));
            Until(() => gate.WaitingCount == 1);
            for (int i = 0; i < 3; i++)
            {
                int id = i;
                tasks.Add(Run(() =>
                {
                    Assert.True(gate.Wait(TimeSpan.FromSeconds(10)));
                    try { order.Add(id); }
                    finally { gate.Release(); }
                }));
                Until(() => gate.WaitingCount == id + 2);
            }
            await expired.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.Equal(3, gate.WaitingCount);
        }
        finally { gate.Release(); }
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal([0, 1, 2], order);
        Assert.Equal(0, gate.WaitingCount);
    }

    [Fact]
    public void InterruptedWakeAfterHandoffStillWakesTheGrantedWaiter()
    {
        var gate = new WriterLock(WriterAdmissionMode.Fifo);
        Assert.True(gate.Wait(TimeSpan.Zero));
        using var acquired = new ManualResetEventSlim();
        var waiter = new Thread(() =>
        {
            if (gate.Wait(Timeout.InfiniteTimeSpan)) acquired.Set();
        }) { IsBackground = true };
        waiter.Start();
        Until(() => gate.WaitingCount == 1);
        Thread.Sleep(50); // past the spin budget: the waiter is parked
        int calls = 0;
        gate.TestBeforeSignal = () => { if (Interlocked.Increment(ref calls) == 1) throw new ThreadInterruptedException(); };
        Exception? pending = null;
        var releaser = new Thread(() =>
        {
            gate.Release();
            try { Thread.Sleep(1); }
            catch (ThreadInterruptedException e) { pending = e; }
        });
        releaser.Start();
        releaser.Join();
        Assert.IsType<ThreadInterruptedException>(pending); // the interrupt is deferred, not swallowed
        Assert.True(acquired.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        waiter.Join();
        gate.TestBeforeSignal = null;
        gate.Release();
        Assert.True(gate.Wait(TimeSpan.Zero));
        gate.Release();
    }

    [Fact]
    public async Task TimeoutReleaseRacesDoNotLeakOrDoubleAdmit()
    {
        var gate = new WriterLock(WriterAdmissionMode.Fifo);
        int active = 0, completed = 0;
        var tasks = Enumerable.Range(0, 8).Select(_ => Run(() =>
        {
            for (int i = 0; i < 200; i++)
            {
                if (!gate.Wait(TimeSpan.FromMilliseconds(i % 3))) continue;
                try
                {
                    Assert.Equal(1, Interlocked.Increment(ref active));
                    Thread.SpinWait(100);
                    Assert.Equal(0, Interlocked.Decrement(ref active));
                    Interlocked.Increment(ref completed);
                }
                finally { gate.Release(); }
            }
        })).ToArray();
        await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.True(completed > 0);
        Assert.Equal(0, gate.WaitingCount);
        Assert.True(gate.Wait(TimeSpan.Zero));
        gate.Release();
    }

    [Fact]
    public async Task CheckpointAndWritesShareQueueAndReadersBypassIt()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { WriterAdmission = WriterAdmissionMode.Fifo, Synchronous = SynchronousMode.Off });
        var c = db.GetCollection("c");
        c.Insert("{_id:1}");
        using var holder = db.BeginTransaction();
        Task? checkpoint = null, writer = null;
        try
        {
            checkpoint = Run(() => Assert.True(db.Checkpoint()));
            Until(() => db.WaitingWriters == 1);
            writer = Run(() => c.Insert("{_id:2}"));
            Until(() => db.WaitingWriters == 2);
            using (var snap = db.BeginSnapshot()) Assert.Equal(1, snap.GetCollection("c").Count());
        }
        finally { holder.Rollback(); }
        await Task.WhenAll(checkpoint!, writer!).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(2, c.Count());
        db.CheckIntegrity();
    }

    [Theory]
    [InlineData(WriterAdmissionMode.Default)]
    [InlineData(WriterAdmissionMode.Fifo)]
    public void DisposalTimeoutRestoresUsabilityAndFailuresReleaseLock(WriterAdmissionMode mode)
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            WriterAdmission = mode, BusyTimeout = TimeSpan.Zero, Synchronous = SynchronousMode.Off,
        });
        var c = db.GetCollection("c");
        c.Insert("{_id:1}");
        using (var tx = db.BeginTransaction())
        {
            Assert.Throws<FolioException>(() => db.Dispose());
            Assert.False(db.Checkpoint());
            Assert.Throws<FolioException>(() => c.Insert("{_id:2}"));
            Assert.Equal(1, c.Count());
        }
        Assert.Throws<DuplicateKeyException>(() => c.Insert("{_id:1}"));
        c.Insert("{_id:2}");
        Assert.True(db.Checkpoint());
        db.CheckIntegrity();
    }

    [Fact]
    public async Task DisposalRejectsAlreadyQueuedWritersWithoutLeakingAdmission()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions { WriterAdmission = WriterAdmissionMode.Fifo, Synchronous = SynchronousMode.Off });
        using var holder = db.BeginTransaction();
        Task? writer = null, close = null;
        try
        {
            writer = Run(() => Assert.Throws<ObjectDisposedException>(() => db.GetCollection("c").Insert("{_id:1}")));
            Until(() => db.WaitingWriters == 1);
            close = Run(db.Dispose);
            Until(() => db.WaitingWriters == 2);
        }
        finally { holder.Rollback(); }
        await Task.WhenAll(writer!, close!).WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        Assert.Equal(0, db.WaitingWriters);
        Assert.Throws<ObjectDisposedException>(() => db.BeginTransaction());
    }

    [Fact]
    public async Task InterruptedDisposalRestoresUsability()
    {
        using var tmp = new TempDb();
        using var db = tmp.Open(new FolioOptions
        {
            WriterAdmission = WriterAdmissionMode.Fifo, BusyTimeout = Timeout.InfiniteTimeSpan,
            Synchronous = SynchronousMode.Off,
        });
        using var holder = db.BeginTransaction();
        var result = new TaskCompletionSource<Exception?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var closer = new Thread(() =>
        {
            try { db.Dispose(); result.SetResult(null); }
            catch (Exception e) { result.SetResult(e); }
        }) { IsBackground = true };
        closer.Start();
        try
        {
            Until(() => db.WaitingWriters == 1);
            closer.Interrupt();
            Assert.IsType<ThreadInterruptedException>(await result.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
            Assert.Equal(0, db.WaitingWriters);
            Assert.Equal(0, db.GetCollection("c").Count());
        }
        finally
        {
            holder.Rollback();
            closer.Join();
        }
        db.GetCollection("c").Insert("{_id:1}");
        Assert.True(db.Checkpoint());
    }

    [Fact]
    public void InvalidOptionsFailAtOpen()
    {
        using var tmp = new TempDb();
        Assert.Equal(WriterAdmissionMode.Default, new FolioOptions().WriterAdmission);
        Assert.Throws<ArgumentOutOfRangeException>(() => tmp.Open(new FolioOptions { WriterAdmission = (WriterAdmissionMode)99 }));
        foreach (var timeout in new[] { TimeSpan.FromMilliseconds(-2), TimeSpan.FromTicks(-1), TimeSpan.FromMilliseconds((double)int.MaxValue + 1) })
            Assert.Throws<ArgumentOutOfRangeException>(() => tmp.Open(new FolioOptions { BusyTimeout = timeout }));
    }
}
