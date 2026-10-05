using System.Text;
using System.Threading.Channels;
using Hex1b.Input;
using Hex1b.Kgp;

namespace Hex1b;

/// <summary>
/// Captures only render output until cursor restoration and ESU. Surface work
/// stays in the app; output admission and graphics state stay with the sink.
/// </summary>
internal sealed class SoftWrapFrameAdapter : IHex1bAppTerminalWorkloadAdapter
{
    private readonly IHex1bAppTerminalWorkloadAdapter _parent;
    private readonly ISoftWrapFrameSink? _sink;
    private readonly SoftWrapGraphicsState? _directGraphics;
    private readonly Action _requestRender;
    private Capture? _capture;

    internal SoftWrapFrameAdapter(IHex1bAppTerminalWorkloadAdapter parent, Action requestRender)
    {
        _parent = parent;
        _sink = parent as ISoftWrapFrameSink;
        _directGraphics = _sink is null ? new SoftWrapGraphicsState() : null;
        _requestRender = requestRender;
        if (_sink is not null) _sink.FrameRejected += requestRender;
    }

    internal void BeginFrame(int width, int height)
    {
        if (_capture is not null) throw new InvalidOperationException("Render capture is already active.");
        _capture = new Capture(width, height, _sink?.CaptureOutputEpoch() ?? 0);
    }

    internal void BeginBody(IReadOnlyList<KgpFragment> graphics)
    {
        if (_capture is not { } capture) return;
        capture.BodyStart = capture.Output.Length;
        capture.Graphics = graphics;
    }

    internal void EndBody()
    {
        if (_capture is { } capture) capture.BodyEnd = capture.Output.Length;
    }

    internal void AbortFrame() => _capture = null;

    internal async ValueTask CompleteFrameAsync(CancellationToken ct)
    {
        var capture = _capture ?? throw new InvalidOperationException("No render capture is active.");
        _capture = null;
        if (capture.BodyStart < 0 || capture.BodyEnd < capture.BodyStart)
            throw new InvalidOperationException("An incomplete render cannot be handed off.");
        var output = capture.Output.ToString();
        var frame = new SoftWrapRenderFrame(capture.Width, capture.Height,
            output[..capture.BodyStart], output[capture.BodyStart..capture.BodyEnd],
            output[capture.BodyEnd..], capture.Graphics);
        if (_sink is not null)
        {
            _sink.SubmitRenderFrame(frame, capture.Epoch);
            return;
        }

        var graphics = _directGraphics!;
        var applied = false;
        using (await graphics.AcquireAsync(ct).ConfigureAwait(false))
        {
            try
            {
                var batch = graphics.PrepareFrame(frame, 0, frame.Width, frame.Height);
                var outcome = await SubmitDirectAsync(batch, frame.Width, frame.Height).ConfigureAwait(false);
                graphics.Complete(outcome);
                applied = outcome == NativeDeliveryOutcome.Applied;
            }
            catch
            {
                graphics.DeliveryFailed();
                throw;
            }
        }
        if (!applied) _requestRender();
    }

    internal async ValueTask ReleaseDirectGraphicsAsync()
    {
        if (_directGraphics is not { } graphics) return;
        using (await graphics.AcquireAsync().ConfigureAwait(false))
        {
            var cleanup = graphics.PrepareOwnedCleanup();
            if (cleanup.Length == 0) return;
            // Per-image deletion is geometry independent and must not be refused.
            if (_parent is Hex1bAppWorkloadAdapter queued)
                await queued.WriteRequiredForProcessing(cleanup).ConfigureAwait(false);
            else _parent.Write(cleanup);
            graphics.CleanupCompleted();
        }
    }

    internal void Detach()
    {
        if (_sink is not null) _sink.FrameRejected -= _requestRender;
        AbortFrame();
    }

    private Task<NativeDeliveryOutcome> SubmitDirectAsync(string batch, int width, int height)
    {
        if (_parent is Hex1bAppWorkloadAdapter queued)
            return queued.GeometryGatedDeliveryEnforceable
                ? queued.WriteRequiredIfGeometry(batch, width, height)
                : queued.WriteRequiredForProcessing(batch);
        _parent.Write(batch);
        return Task.FromResult(NativeDeliveryOutcome.Applied);
    }

    public void Write(string text)
    {
        if (_capture is { } capture) capture.Output.Append(text);
        else _parent.Write(text);
    }
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_capture is not null) Write(Encoding.UTF8.GetString(data));
        else _parent.Write(data);
    }
    public void SetCursorPosition(int left, int top)
    {
        if (_capture is not null) Write($"\x1b[{top + 1};{left + 1}H");
        else _parent.SetCursorPosition(left, top);
    }
    public void Clear()
    {
        if (_capture is { } capture)
        {
            // Clear only this render's text rectangle. ED2 has global graphics
            // semantics on real hosts and would erase unrelated image owners.
            for (var row = 0; row < capture.Height; row++)
            {
                SetCursorPosition(0, row);
                Write("\x1b[2K");
            }
        }
        else _parent.Clear();
    }
    public void Flush() { if (_capture is null) _parent.Flush(); }
    public int Width => _capture?.Width ?? _parent.Width;
    public int Height => _capture?.Height ?? _parent.Height;
    public TerminalCapabilities Capabilities => _parent.Capabilities;
    public ChannelReader<Hex1bEvent> InputEvents => _parent.InputEvents;
    public int OutputQueueDepth => _parent.OutputQueueDepth;
    public bool HandlesProtocolQueries => _parent.HandlesProtocolQueries;
    public void EnterTuiMode() => _parent.EnterTuiMode();
    public void ExitTuiMode() => _parent.ExitTuiMode();
    public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => _parent.ReadOutputAsync(ct);
    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _parent.WriteInputAsync(data, ct);
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => _parent.ResizeAsync(width, height, ct);
    public event Action? Disconnected { add => _parent.Disconnected += value; remove => _parent.Disconnected -= value; }
    public ValueTask DisposeAsync() => _parent.DisposeAsync();

    private sealed class Capture(int width, int height, long epoch)
    {
        internal int Width { get; } = width;
        internal int Height { get; } = height;
        internal long Epoch { get; } = epoch;
        internal StringBuilder Output { get; } = new();
        internal int BodyStart { get; set; } = -1;
        internal int BodyEnd { get; set; } = -1;
        internal IReadOnlyList<KgpFragment> Graphics { get; set; } = Array.Empty<KgpFragment>();
    }
}
