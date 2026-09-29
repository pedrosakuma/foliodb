using System.Diagnostics;

namespace FolioDb.Storage;

/// <summary>Single-writer admission. FIFO order starts at enqueue, not at method invocation.</summary>
internal sealed class WriterLock
{
    private readonly SemaphoreSlim? _semaphore;
    private readonly object _gate = new();
    private readonly LinkedList<object> _queue = new();
    private bool _held;

    public WriterLock(WriterAdmissionMode mode)
    {
        if (mode == WriterAdmissionMode.Default) _semaphore = new SemaphoreSlim(1, 1);
    }

    internal int WaitingCount { get { lock (_gate) return _queue.Count; } }

    public bool Wait(TimeSpan timeout)
    {
        if (_semaphore is not null) return _semaphore.Wait(timeout);
        bool infinite = timeout == Timeout.InfiniteTimeSpan;
        long started = Stopwatch.GetTimestamp();
        bool entered = false;
        LinkedListNode<object>? node = null;
        try
        {
            Monitor.TryEnter(_gate, timeout, ref entered);
            if (!entered) return false;
            // A zero timeout is a non-blocking attempt, but must not bypass queued callers.
            if (!_held && _queue.Count == 0)
            {
                if (!infinite && timeout != TimeSpan.Zero && Stopwatch.GetElapsedTime(started) >= timeout) return false;
                _held = true;
                return true;
            }
            if (timeout == TimeSpan.Zero) return false;
            node = _queue.AddLast(new object());
            while (true)
            {
                var remaining = infinite ? Timeout.InfiniteTimeSpan : timeout - Stopwatch.GetElapsedTime(started);
                if (!infinite && remaining <= TimeSpan.Zero) return false;
                if (!_held && ReferenceEquals(_queue.First, node))
                {
                    _held = true;
                    return true;
                }
                Monitor.Wait(_gate, remaining);
            }
        }
        finally
        {
            if (entered)
            {
                // Also unlink a waiter interrupted while blocked in Monitor.Wait.
                if (node is not null)
                {
                    _queue.Remove(node);
                    if (!_held) Monitor.PulseAll(_gate);
                }
                Monitor.Exit(_gate);
            }
        }
    }

    public void Release()
    {
        if (_semaphore is not null) { _semaphore.Release(); return; }
        lock (_gate)
        {
            if (!_held) throw new SynchronizationLockException("Writer lock is not held.");
            _held = false;
            Monitor.PulseAll(_gate);
        }
    }
}
