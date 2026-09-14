using System.Collections.Concurrent;
using System.Text;
using System.Threading.Channels;
using Hex1b;
using Hex1b.Input;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Records every <c>Write</c> call against a synthetic parent adapter and
/// exposes a settable <c>Width</c>/<c>Height</c> plus a writable input
/// event channel — the minimum surface the flow runner needs to observe a
/// "resize burst" in a unit test.
/// </summary>
internal sealed class RecordingParentAdapter : IHex1bAppTerminalWorkloadAdapter
{
    private readonly Channel<Hex1bEvent> _inputChannel = Channel.CreateUnbounded<Hex1bEvent>();
    private readonly ConcurrentQueue<string> _writes = new();
    private int _width;
    private int _height;

    public RecordingParentAdapter(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public int Width => _width;
    public int Height => _height;
    public TerminalCapabilities Capabilities { get; } = TerminalCapabilities.Modern;
    public ChannelReader<Hex1bEvent> InputEvents => _inputChannel.Reader;
    public int OutputQueueDepth => 0;

    public event Action? Disconnected;

    public void Write(string text) => _writes.Enqueue(text);
    public void Write(ReadOnlySpan<byte> data) => _writes.Enqueue(Encoding.UTF8.GetString(data));
    public void Flush() { }

    public void EnterTuiMode() { }
    public void ExitTuiMode() { }
    public void Clear() => _writes.Enqueue("\x1b[2J");
    public void SetCursorPosition(int left, int top) =>
        _writes.Enqueue($"\x1b[{top + 1};{left + 1}H");

    public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        => ValueTask.FromResult<ReadOnlyMemory<byte>>(ReadOnlyMemory<byte>.Empty);
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => ValueTask.CompletedTask;
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        _width = width;
        _height = height;
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _inputChannel.Writer.TryComplete();
        Disconnected?.Invoke();
        return ValueTask.CompletedTask;
    }

    public void SetSize(int width, int height)
    {
        _width = width;
        _height = height;
    }

    public ValueTask PushEventAsync(Hex1bEvent evt) => _inputChannel.Writer.WriteAsync(evt);

    /// <summary>Returns a snapshot count of writes so far.</summary>
    public int SnapshotWrites() => _writes.Count;

    /// <summary>Returns all writes captured since the supplied snapshot index.</summary>
    public IReadOnlyList<string> SnapshotWritesSince(int snapshotIndex)
    {
        var all = _writes.ToArray();
        if (snapshotIndex >= all.Length) return Array.Empty<string>();
        return all.AsSpan(snapshotIndex).ToArray();
    }
}
