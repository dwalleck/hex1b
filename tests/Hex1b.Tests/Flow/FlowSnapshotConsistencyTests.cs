using System.Text;
using System.Threading.Channels;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Fences <see cref="Hex1bApp.SnapshotCurrentSurface"/> against the frame that
/// reuses and clears the surface it copies: a snapshot taken while a frame is in
/// flight must be one complete frame — the one before it or the one after it —
/// never blank and never torn.
/// </summary>
/// <remarks>
/// A gated node pauses a frame before composition completes. The test requests
/// a snapshot concurrently and checks for a complete old or new frame. It does
/// not require a particular locking strategy or require the snapshot to block.
/// </remarks>
[DoNotParallelize]
[TestClass]
public class FlowSnapshotConsistencyTests
{
    private const string OldHead = "OLD-FRAME-HEAD";
    private const string OldTail = "OLD-FRAME-TAIL";
    private const string NewHead = "NEW-FRAME-HEAD";
    private const string NewTail = "NEW-FRAME-TAIL";

    [TestMethod]
    public async Task SnapshotDuringAnInFlightFrame_IsCompleteAndNeverTorn()
    {
        await using var workload = new SnapshotProbeWorkloadAdapter(width: 60, height: 6);
        var gate = new SnapshotDrawGate();
        var frame = 0;

        using var app = new Hex1bApp(
            _ => Task.FromResult<Hex1bWidget>(new GatedMarkersWidget
            {
                Head = frame == 0 ? OldHead : NewHead,
                Tail = frame == 0 ? OldTail : NewTail,
                Gate = gate,
            }),
            new Hex1bAppOptions
            {
                WorkloadAdapter = workload,
                EnableInputCoalescing = false,
                EnableRenderCaching = false,
                EnableSurfacePooling = false,
                // The fence must fail loudly if the gated render misbehaves: the
                // rescue wrapper would otherwise swallow it into a rescue frame.
                EnableRescue = false,
                FrameRateLimitMs = 1,
            });

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        // The app runs on its own thread: the gate blocks a frame from inside the
        // render, and the test thread has to stay free to observe it. The handshake
        // reports the run task so the test never races the thread's start.
        var runStarted = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var appThread = new Thread(() => runStarted.TrySetResult(RunAppAsync(app, cts.Token)))
        {
            IsBackground = true,
            Name = "snapshot-consistency-fence",
        };
        appThread.Start();
        var runTask = await runStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

        try
        {
            // The first frame must have completed and been emitted, so the
            // snapshot under test has a defined "previous frame" to compare with.
            await workload.WaitForOutputContainingAsync(OldTail, cts.Token);
            var framesBeforeGatedFrame = app.FrameCount;

            gate.Arm();
            frame = 1;
            app.Invalidate();

            // Inside the frame: the surface has been cleared and only its head drawn.
            await gate.Entered.WaitAsync(TimeSpan.FromSeconds(10));

            var snapshotStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var snapshotTask = Task.Run(() =>
            {
                snapshotStarted.SetResult();
                return app.SnapshotCurrentSurface();
            });
            await snapshotStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));

            // Give an unsynchronized copy the opportunity to read the half-drawn
            // surface. Returning a complete previous frame is equally valid;
            // the assertion below checks content, not whether this call blocks.
            await Task.WhenAny(snapshotTask, Task.Delay(250));

            gate.Release();

            var snapshot = await snapshotTask.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitForFrameCountAboveAsync(app, framesBeforeGatedFrame);

            Assert.IsNotNull(
                snapshot,
                "a snapshot taken during an in-flight frame must still return the frame that was rendered");

            var text = ReadSurfaceText(snapshot!);
            var completeNewFrame =
                text.Contains(NewHead, StringComparison.Ordinal)
                && text.Contains(NewTail, StringComparison.Ordinal)
                && !text.Contains(OldHead, StringComparison.Ordinal)
                && !text.Contains(OldTail, StringComparison.Ordinal);
            var completeOldFrame =
                text.Contains(OldHead, StringComparison.Ordinal)
                && text.Contains(OldTail, StringComparison.Ordinal)
                && !text.Contains(NewHead, StringComparison.Ordinal)
                && !text.Contains(NewTail, StringComparison.Ordinal);

            Assert.IsTrue(
                completeNewFrame || completeOldFrame,
                "a snapshot taken while a frame is in flight must contain a complete frame — the " +
                "one before it or the one after it — never a blank or torn surface.\n" +
                $"newHead={text.Contains(NewHead, StringComparison.Ordinal)} " +
                $"newTail={text.Contains(NewTail, StringComparison.Ordinal)} " +
                $"oldHead={text.Contains(OldHead, StringComparison.Ordinal)} " +
                $"oldTail={text.Contains(OldTail, StringComparison.Ordinal)}\n{text}");
        }
        finally
        {
            // Always release first: a still-held gate would leave the app thread
            // blocked inside a frame and the stop below could not complete.
            gate.Release();
            app.RequestStop();
            if (runTask is not null)
            {
                await runTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
        }
    }

    private static async Task RunAppAsync(Hex1bApp app, CancellationToken cancellationToken)
    {
        try
        {
            await app.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The stop path is the test's own; nothing here is under test.
        }
    }

    private static async Task WaitForFrameCountAboveAsync(Hex1bApp app, long frameCount)
    {
        for (var attempt = 0; attempt < 200; attempt++)
        {
            if (app.FrameCount > frameCount)
            {
                return;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        Assert.Fail($"the gated frame never completed: FrameCount stayed at {frameCount}");
    }

    /// <summary>
    /// Reads a snapshot as text, row by row, so an assertion failure shows what
    /// the copy actually contained.
    /// </summary>
    private static string ReadSurfaceText(Surface surface)
    {
        var text = new StringBuilder();
        for (var y = 0; y < surface.Height; y++)
        {
            for (var x = 0; x < surface.Width; x++)
            {
                var character = surface.GetCell(x, y).Character;
                text.Append(string.IsNullOrEmpty(character) ? ' ' : character);
            }

            text.Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Draws <see cref="Head"/> on the node's first row, pauses inside the frame,
    /// then draws <see cref="Tail"/> on the next row.
    /// </summary>
    /// <remarks>
    /// The pause is deliberately inside the node's render: that is the only point
    /// that is both inside the frame's surface lock and after the frame has
    /// cleared the surface it is drawing, which is exactly the window a snapshot
    /// must never be able to observe.
    /// </remarks>
    private sealed record GatedMarkersWidget : Hex1bWidget
    {
        internal required string Head { get; init; }

        internal required string Tail { get; init; }

        internal required SnapshotDrawGate Gate { get; init; }

        internal override Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            var node = existingNode as GatedMarkersNode ?? new GatedMarkersNode();
            if (node.Head != Head || node.Tail != Tail)
            {
                node.MarkDirty();
            }

            node.Head = Head;
            node.Tail = Tail;
            node.Gate = Gate;
            return Task.FromResult<Hex1bNode>(node);
        }

        internal override Type GetExpectedNodeType() => typeof(GatedMarkersNode);
    }

    private sealed class GatedMarkersNode : Hex1bNode
    {
        internal string Head = string.Empty;

        internal string Tail = string.Empty;

        internal SnapshotDrawGate? Gate;

        protected override Size MeasureCore(Constraints constraints)
            => constraints.Constrain(new Size(Math.Max(1, Math.Max(Head.Length, Tail.Length)), 2));

        public override void Render(Hex1bRenderContext context)
        {
            context.SetCursorPosition(Bounds.X, Bounds.Y);
            context.Write(Head);
            Gate?.Pause();
            context.SetCursorPosition(Bounds.X, Bounds.Y + 1);
            context.Write(Tail);
        }
    }

    /// <summary>
    /// One-shot rendezvous between the render loop and the test: the renderer
    /// signals that it is inside the frame and then blocks until the test
    /// releases it. One-shot so that later frames render normally.
    /// </summary>
    internal sealed class SnapshotDrawGate
    {
        private readonly TaskCompletionSource _entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _armed;

        /// <summary>Completes when the armed frame has reached the pause.</summary>
        public Task Entered => _entered.Task;

        /// <summary>Arms the gate for the next frame's render.</summary>
        public void Arm() => Volatile.Write(ref _armed, 1);

        /// <summary>Blocks the render thread — once — until <see cref="Release"/>.</summary>
        public void Pause()
        {
            if (Interlocked.Exchange(ref _armed, 0) == 0)
            {
                return;
            }

            _entered.TrySetResult();
            _release.Task.GetAwaiter().GetResult();
        }

        /// <summary>Lets the paused frame finish.</summary>
        public void Release() => _release.TrySetResult();
    }

    /// <summary>
    /// Minimal parent adapter: records every byte the app emits and lets the test
    /// wait for a marker to appear, which is how the fence knows the first frame
    /// is complete before the gated one starts.
    /// </summary>
    private sealed class SnapshotProbeWorkloadAdapter : IHex1bAppTerminalWorkloadAdapter
    {
        private readonly Channel<ReadOnlyMemory<byte>> _output =
            Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        private readonly Channel<Hex1bEvent> _input = Channel.CreateUnbounded<Hex1bEvent>();
        private readonly Channel<bool> _writeSignals = Channel.CreateUnbounded<bool>();
        private readonly object _gate = new();
        private readonly StringBuilder _captured = new();
        private readonly int _width;
        private readonly int _height;
        private int _outputQueueDepth;

        public SnapshotProbeWorkloadAdapter(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public int Width => _width;

        public int Height => _height;

        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;

        public ChannelReader<Hex1bEvent> InputEvents => _input.Reader;

        public int OutputQueueDepth => Volatile.Read(ref _outputQueueDepth);

        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public void Write(string text) => Record(Encoding.UTF8.GetBytes(text));

        public void Write(ReadOnlySpan<byte> data) => Record(data.ToArray());

        public void Flush()
        {
        }

        public void EnterTuiMode()
        {
        }

        public void ExitTuiMode()
        {
        }

        public void Clear() => Write("\x1b[2J");

        public void SetCursorPosition(int left, int top) => Write($"\x1b[{top + 1};{left + 1}H");

        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
            => ReadOutputAsyncCore(ct);

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask DisposeAsync()
        {
            _output.Writer.TryComplete();
            _input.Writer.TryComplete();
            _writeSignals.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }

        public async Task<string> WaitForOutputContainingAsync(
            string text,
            CancellationToken cancellationToken)
        {
            while (true)
            {
                string captured;
                lock (_gate)
                {
                    captured = _captured.ToString();
                }

                if (captured.Contains(text, StringComparison.Ordinal))
                {
                    return captured;
                }

                await _writeSignals.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
        }

        private void Record(ReadOnlyMemory<byte> data)
        {
            lock (_gate)
            {
                _captured.Append(Encoding.UTF8.GetString(data.Span));
            }

            if (_output.Writer.TryWrite(data))
            {
                Interlocked.Increment(ref _outputQueueDepth);
            }

            _writeSignals.Writer.TryWrite(true);
        }

        private async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsyncCore(CancellationToken cancellationToken)
        {
            var data = await _output.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            Interlocked.Decrement(ref _outputQueueDepth);
            return data;
        }
    }
}
