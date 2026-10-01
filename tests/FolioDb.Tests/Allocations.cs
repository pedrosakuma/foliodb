namespace FolioDb.Tests;

internal static class Allocations
{
    /// <summary>
    /// Bytes allocated on this thread by <paramref name="action"/>, taking the best of a few attempts. The runtime
    /// occasionally allocates on a user thread while a background GC runs (~1 KB, a few times per thousand measured
    /// windows under GC pressure from parallel tests), so one sample can be nonzero for code that never allocates.
    /// A real per-call allocation shows up in every attempt.
    /// </summary>
    public static long Measure(Action action, int attempts = 3)
    {
        long best = long.MaxValue;
        for (int i = 0; i < attempts && best != 0; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            action();
            best = Math.Min(best, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        return best;
    }
}
