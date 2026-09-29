using System.Diagnostics;

/// <summary>Benchmark-only admission gate. Order is defined when a caller joins the queue under the monitor.</summary>
internal sealed class FifoAdmission
{
    private readonly object _gate = new();
    private readonly LinkedList<object> _queue = new();
    private bool _held;

    public IDisposable? Enter(TimeSpan timeout)
    {
        long started = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            var node = _queue.AddLast(new object());
            while (true)
            {
                var remaining = timeout - Stopwatch.GetElapsedTime(started);
                if (remaining <= TimeSpan.Zero)
                {
                    _queue.Remove(node);
                    Monitor.PulseAll(_gate);
                    return null;
                }
                if (!_held && ReferenceEquals(_queue.First, node))
                {
                    _queue.RemoveFirst();
                    _held = true;
                    return new Lease(this);
                }
                Monitor.Wait(_gate, remaining);
            }
        }
    }

    private sealed class Lease(FifoAdmission owner) : IDisposable
    {
        private FifoAdmission? _owner = owner;
        public void Dispose()
        {
            var gate = Interlocked.Exchange(ref _owner, null);
            if (gate is null) return;
            lock (gate._gate)
            {
                gate._held = false;
                Monitor.PulseAll(gate._gate);
            }
        }
    }

    // Deterministic checks outside measurement: no acquisition may pass a live predecessor;
    // a timed-out predecessor must not prevent subsequent progress.
    public static void Verify()
    {
        var gate = new FifoAdmission();
        var order = new List<int>();
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        var threads = new List<Thread>();
        var first = gate.Enter(TimeSpan.FromSeconds(5)) ?? throw new InvalidOperationException("Initial FIFO acquisition failed.");
        try
        {
            using var expiredHead = gate.Enter(TimeSpan.FromMilliseconds(20));
            if (expiredHead is not null) throw new InvalidOperationException("FIFO admitted a second owner.");
            for (int i = 0; i < 3; i++)
            {
                int id = i;
                var thread = new Thread(() =>
                {
                    try
                    {
                        using var lease = gate.Enter(TimeSpan.FromSeconds(10));
                        if (lease is null) throw new InvalidOperationException("FIFO verification timed out.");
                        order.Add(id);
                    }
                    catch (Exception e) { failures.Enqueue(e); }
                }) { IsBackground = true };
                threads.Add(thread);
                thread.Start();
                int expected = i + 1;
                if (!SpinWait.SpinUntil(() => { lock (gate._gate) return gate._queue.Count == expected; }, TimeSpan.FromSeconds(5)))
                    throw new InvalidOperationException("FIFO waiter did not enqueue.");
            }
            // All three predecessors remain blocked by first.
            using var expired = gate.Enter(TimeSpan.FromMilliseconds(20));
            if (expired is not null) throw new InvalidOperationException("FIFO admitted a waiter before release.");
        }
        finally
        {
            first.Dispose();
            first.Dispose();
            foreach (var thread in threads) thread.Join();
        }
        if (!failures.IsEmpty) throw new AggregateException(failures);
        if (!order.SequenceEqual(new[] { 0, 1, 2 })) throw new InvalidOperationException("FIFO order was violated.");
        using var last = gate.Enter(TimeSpan.FromSeconds(1));
        if (last is null) throw new InvalidOperationException("FIFO leaked a timed-out waiter or lease.");
        using var immediate = gate.Enter(TimeSpan.Zero);
        if (immediate is not null) throw new InvalidOperationException("FIFO ignored a zero timeout.");
    }
}
