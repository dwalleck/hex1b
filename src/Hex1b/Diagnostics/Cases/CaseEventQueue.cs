using System.Collections.Concurrent;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The bounded, never-waiting queue between event producers (the output pump, resize callers, input and
/// frame publishers) and the artifact writer. An event that would exceed either bound is refused
/// (drop-newest); the caller accounts for it outside the queue.
/// </summary>
internal sealed class CaseEventQueue
{
    internal const int MaxEvents = 4096;
    internal const long MaxBytes = 8L * 1024 * 1024;

    private readonly ConcurrentQueue<CaseEvent> _events = new();
    private int _count;
    private long _bytes;

    internal int Count => Volatile.Read(ref _count);

    internal long Bytes => Interlocked.Read(ref _bytes);

    /// <summary>Queues the event if both bounds still hold; never blocks.</summary>
    internal bool TryEnqueue(in CaseEvent item)
    {
        var size = item.QueuedBytes;
        if (Interlocked.Increment(ref _count) > MaxEvents)
        {
            Interlocked.Decrement(ref _count);
            return false;
        }

        if (Interlocked.Add(ref _bytes, size) > MaxBytes)
        {
            Interlocked.Add(ref _bytes, -size);
            Interlocked.Decrement(ref _count);
            return false;
        }

        _events.Enqueue(item);
        return true;
    }

    internal bool TryDequeue(out CaseEvent item)
    {
        if (!_events.TryDequeue(out item))
            return false;
        Interlocked.Add(ref _bytes, -item.QueuedBytes);
        Interlocked.Decrement(ref _count);
        return true;
    }
}
