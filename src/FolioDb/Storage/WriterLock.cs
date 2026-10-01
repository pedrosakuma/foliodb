using System.Diagnostics;

namespace FolioDb.Storage;

/// <summary>
/// Single-writer admission. FIFO order starts at enqueue, not at method invocation.
/// <para>
/// FIFO hands the lock directly to the queue head on release. Waking a blocked thread costs tens of microseconds
/// (more on virtualized hosts), so only the head, which is next in line, spins briefly instead of blocking. The
/// releaser wakes the following waiter after the handoff, off the critical path, so it is usually already spinning
/// when its turn comes. At most one thread spins per lock.
/// </para>
/// </summary>
internal sealed class WriterLock
{
    private const int Waiting = 0, Granted = 1, Abandoned = 2;
    private static readonly bool CanSpin = Environment.ProcessorCount > 1;
    // About three typical short write transactions; a longer holder parks the head (and its release pays the wake).
    private static readonly long SpinTicks = Stopwatch.Frequency / 5_000;

    private readonly SemaphoreSlim? _semaphore;
    private readonly Lock _gate = new();
    private readonly LinkedList<Waiter> _queue = new();
    private bool _held;

    /// <summary>Test hook: runs before each wake attempt (may throw to simulate an interrupted monitor entry).</summary>
    internal Action? TestBeforeSignal;

    private sealed class Waiter
    {
        public int State;
        /// <summary>First in the queue: spins before parking.</summary>
        public bool Head;
        /// <summary>Guarded by the waiter's monitor.</summary>
        public bool Parked;
        public LinkedListNode<Waiter>? Node;
    }

    public WriterLock(WriterAdmissionMode mode)
    {
        if (mode == WriterAdmissionMode.Unordered) _semaphore = new SemaphoreSlim(1, 1);
    }

    internal int WaitingCount { get { lock (_gate) return _queue.Count; } }

    public bool Wait(TimeSpan timeout)
    {
        if (_semaphore is not null) return _semaphore.Wait(timeout);
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        long started = Stopwatch.GetTimestamp();
        var w = new Waiter();
        lock (_gate)
        {
            // A zero timeout is a non-blocking attempt, but must not bypass queued callers.
            if (!_held && _queue.Count == 0)
            {
                _held = true;
                return true;
            }
            if (timeout == TimeSpan.Zero) return false;
            w.Node = _queue.AddLast(w);
            w.Head = w.Node == _queue.First || w.Node.Previous == _queue.First;
        }

        bool done = false;
        try
        {
            long spinUntil = 0;
            while (true)
            {
                if (Volatile.Read(ref w.State) == Granted) { done = true; return true; }
                var remaining = infinite ? Timeout.InfiniteTimeSpan : timeout - Stopwatch.GetElapsedTime(started);
                if (!infinite && remaining <= TimeSpan.Zero) break;
                if (CanSpin && Volatile.Read(ref w.Head))
                {
                    long now = Stopwatch.GetTimestamp();
                    if (spinUntil == 0) spinUntil = now + SpinTicks;
                    if (now < spinUntil)
                    {
                        Thread.SpinWait(20);
                        continue;
                    }
                }
                lock (w)
                {
                    // Re-checked under the waiter's monitor, which Signal takes after publishing a change.
                    if (Volatile.Read(ref w.State) != Waiting || (CanSpin && spinUntil == 0 && Volatile.Read(ref w.Head))) continue;
                    w.Parked = true;
                    try { Monitor.Wait(w, remaining); }
                    finally { w.Parked = false; }
                }
            }
            // Timed out, unless the grant raced with the timeout: then the lock is ours.
            done = !TryAbandon(w);
            return done;
        }
        finally
        {
            // Interrupted (or another failure) while queued: leave the queue, or pass on a lock granted meanwhile.
            if (!done && !TryAbandon(w)) Release();
        }
    }

    /// <summary>Leaves the queue unless already granted. Returns false if the lock was granted (and is now owned).</summary>
    private bool TryAbandon(Waiter w)
    {
        int state = Interlocked.CompareExchange(ref w.State, Abandoned, Waiting);
        if (state == Granted) return false;
        if (state == Abandoned) return true;
        Waiter? head = null;
        lock (_gate)
        {
            if (w.Node!.List is not null)
            {
                bool wasSpinner = w.Node == _queue.First || w.Node.Previous == _queue.First;
                _queue.Remove(w.Node);
                // The first two waiters spin: promote whoever is now second (or first).
                if (wasSpinner && (_queue.First?.Next ?? _queue.First) is { } promoted)
                {
                    head = promoted.Value;
                    Volatile.Write(ref head.Head, true);
                }
            }
        }
        if (head is not null) Signal(head);
        return true;
    }

    public void Release()
    {
        if (_semaphore is not null) { _semaphore.Release(); return; }
        Waiter? next = null, head = null;
        lock (_gate)
        {
            if (!_held) throw new SynchronizationLockException("Writer lock is not held.");
            while (_queue.First is { } first)
            {
                _queue.RemoveFirst();
                if (Interlocked.CompareExchange(ref first.Value.State, Granted, Waiting) == Waiting)
                {
                    next = first.Value;
                    break;
                }
            }
            if (next is null)
            {
                _held = false;
                return;
            }
            if (_queue.First?.Next is { } following)
            {
                head = following.Value;
                Volatile.Write(ref head.Head, true);
            }
        }
        // The lock is already handed over; these wakes only cost the releaser.
        Signal(next);
        if (head is not null) Signal(head);
    }

    /// <summary>
    /// Wakes <paramref name="w"/> if parked. A contended monitor entry is interruptible, but the state change this
    /// publishes is already done (a grant can't be undone), so an interrupt is deferred until the pulse is delivered.
    /// </summary>
    private void Signal(Waiter w)
    {
        bool interrupted = false;
        while (true)
        {
            try
            {
                TestBeforeSignal?.Invoke();
                lock (w)
                {
                    if (w.Parked) Monitor.Pulse(w);
                }
                break;
            }
            catch (ThreadInterruptedException) { interrupted = true; }
        }
        if (interrupted) Thread.CurrentThread.Interrupt();
    }
}
