using System.Diagnostics;

namespace Hex1b;

/// <summary>
/// A cursor-observation barrier the output pump stamps when it consumes it, so the waiter judges the
/// barrier's bound by when it was consumed, not by which of two thread-pool work items (its completion or
/// its timeout) happened to run first (issue 61: under thread-pool starvation the timeout could run first
/// and report a barrier consumed in time as a timeout).
/// </summary>
internal sealed class OutputProcessingBarrier : TaskCompletionSource<bool>
{
    private long _consumedTimestamp;

    public OutputProcessingBarrier()
        : base(TaskCreationOptions.RunContinuationsAsynchronously)
    {
    }

    /// <summary>Records the consumption time, then completes the barrier.</summary>
    public void Consume()
    {
        Interlocked.CompareExchange(ref _consumedTimestamp, Stopwatch.GetTimestamp(), 0);
        TrySetResult(true);
    }

    /// <summary>Whether the pump consumed the barrier no later than <paramref name="deadlineTimestamp"/>.</summary>
    public bool ConsumedBy(long deadlineTimestamp)
    {
        var consumed = Interlocked.Read(ref _consumedTimestamp);
        return consumed != 0 && consumed <= deadlineTimestamp;
    }
}
