using System.Text;
using System.Threading.Channels;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Replays the native Windows resize boundary, not a claim that the stock model
/// implements Windows reflow: the host preserves its prefix, reports the parked
/// live anchor, and leaves an old live row below the new eight-row image.
/// Actual Flow output is processed by the real terminal parser and queue.
/// </summary>
[TestClass]
public sealed class FlowResizeOwnedTailTests
{
    [TestMethod]
    [DataRow(false, false, 2)]
    [DataRow(true, false, 2)]
    [DataRow(true, false, 0)]
    [DataRow(true, true, 2)]
    public async Task AuthoritativeResize_RetiresOldLiveTailAndPreservesHostPrefix(bool resize, bool staleObservation, int anchor)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ReplayParent(anchor);
        await parent.SeedInitialAsync();
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var prompt = false;
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => Enumerable.Range(0, 8).Select(i =>
                (Hex1bWidget)v.Text(prompt ? i == 0 ? "RESTORED-PROMPT" : "" : i == 7 ? new string('.', 74) + "OLD-LIVE-TAIL" : $"LIVE-READER-{i}")).ToArray()),
                o => { o.MinHeight = 8; o.MaxHeight = 8; });
            try
            {
                await step.WaitForReadyAsync(stop.Token);
                ready.SetResult(step);
                await finish.Task.WaitAsync(stop.Token);
            }
            finally { await step.CompleteAsync(); }
        }, new Hex1bFlowOptions { InitialCursorRow = anchor, UseSoftWrapTombstones = true, ResizeSettleDelay = TimeSpan.Zero }, parent);
        var running = runner.RunAsync(stop.Token);
        try
        {
            var step = await ready.Task.WaitAsync(stop.Token);
            await WaitUntilAsync(() => parent.Model.GetScreenText().Contains("OLD-LIVE-TAIL", StringComparison.Ordinal), stop.Token);
            await parent.FlushAppliedAsync();
            AssertPrefix(parent.Model, anchor);
            if (resize)
            {
                await parent.ReplayResizeAsync(stop.Token);
                Assert.IsTrue(parent.Model.GetScreenText().Contains("OLD-LIVE-TAIL", StringComparison.Ordinal), "Replay must install the native old-live footprint before Flow sees resize.");
                parent.HoldNextObservation = staleObservation;
                await parent.PublishResizeAsync(stop.Token);
                if (staleObservation)
                {
                    await parent.ObservationEntered.Task.WaitAsync(stop.Token);
                    await parent.ChangeGeometryDuringObservationAsync(stop.Token);
                    parent.ReleaseObservation.TrySetResult();
                }
                await WaitUntilAsync(() => step.TerminalWidth == (staleObservation ? 75 : 74), stop.Token);
            }
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, step.Dispatch(() => { prompt = true; step.Invalidate(); }, out var changed));
            await changed.WaitAsync(stop.Token);
            await WaitUntilAsync(() => parent.Model.GetScreenText().Contains("RESTORED-PROMPT", StringComparison.Ordinal), stop.Token);
            await parent.FlushAppliedAsync();
            // App frame publication can precede the serialized resize repaint.
            // Wait for its observable effect, protecting the history on every poll.
            var deadline = Environment.TickCount64 + 2000;
            while (parent.Model.GetScreenText().Contains("OLD-LIVE-TAIL", StringComparison.Ordinal)
                && Environment.TickCount64 < deadline)
            {
                AssertPrefix(parent.Model, anchor);
                await Task.Delay(10, stop.Token);
            }
            AssertPrefix(parent.Model, anchor);
            Assert.IsFalse(parent.Model.GetScreenText().Contains("OLD-LIVE-TAIL", StringComparison.Ordinal),
                "Actual Flow output must retire the captured old-live tail below its new image.\n" + parent.Model.GetScreenText());
            finish.SetResult();
            await running.WaitAsync(stop.Token);
        }
        finally
        {
            parent.ReleaseObservation.TrySetResult();
            finish.TrySetResult();
            await stop.CancelAsync();
            await running.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private static void AssertPrefix(Hex1bTerminal terminal, int anchor)
    {
        var rows = (anchor == 0
            ? string.Join("\n", terminal.GetScrollbackRows(terminal.ScrollbackCount).Select(row => string.Concat(row.Cells.Select(cell => cell.Character))))
            : terminal.GetScreenText()).Split('\n');
        Assert.AreEqual("HOST-HISTORY-000", rows[0].TrimEnd());
        Assert.AreEqual("HOST-HISTORY-001", rows[1].TrimEnd());
    }

    private static async Task WaitUntilAsync(Func<bool> predicate, CancellationToken token)
    {
        while (!predicate()) await Task.Delay(10, token);
    }

    private sealed class ReplayParent : IHex1bAppTerminalWorkloadAdapter, ICursorPositionSource, IFlowCurrentGeometrySource
    {
        private readonly Channel<Hex1bEvent> input = Channel.CreateUnbounded<Hex1bEvent>();
        private readonly Hex1bAppWorkloadAdapter inner = new();
        public Hex1bTerminal Model { get; }
        public bool HoldNextObservation { get; set; }
        public TaskCompletionSource ObservationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly int anchor;
        public ReplayParent(int anchor)
        {
            this.anchor = anchor;
            Model = Hex1bTerminal.CreateBuilder().WithWorkload(inner).WithHeadless().WithDimensions(120, 40).WithScrollback(100).Build();
        }
        public async Task SeedInitialAsync()
        {
            await inner.WriteRequiredForProcessing("\u001b[1;1HHOST-HISTORY-000\u001b[2;1HHOST-HISTORY-001"
                + (anchor == 0 ? "\u001b[40;1H\n\n\u001b[2J\u001b[1;1H" : "\u001b[3;1H"));
        }
        public Task<NativeDeliveryOutcome> FlushAppliedAsync() => inner.WriteRequiredForProcessing("\u001b[0m");
        public async Task ReplayResizeAsync(CancellationToken token)
        {
            // The captured native environment supplies resize/reflow, not Flow.
            // A nonzero anchor additionally fences the protected history above it.
            var oldLiveRow = Model.GetScreenText().Split('\n')[anchor + 7].TrimEnd();
            Assert.AreEqual(new string('.', 74) + "OLD-LIVE-TAIL", oldLiveRow,
                "The replay tail must come from actual pre-resize Flow-rendered live content.\n" + Model.GetScreenText());
            await Model.ResizeWithWorkloadAsync(74, 11, token);
            await inner.WriteRequiredForProcessing((anchor == 2 ? "\u001b[1;1HHOST-HISTORY-000\u001b[2;1HHOST-HISTORY-001" : "")
                + $"\u001b[{anchor + 9};1H" + oldLiveRow[74..] + $"\u001b[{anchor + 1};1H");
        }
        public ValueTask PublishResizeAsync(CancellationToken token) => input.Writer.WriteAsync(new Hex1bResizeEvent(74, 11), token);
        public async Task ChangeGeometryDuringObservationAsync(CancellationToken token)
        {
            await Model.ResizeWithWorkloadAsync(75, 11, token);
            await inner.WriteRequiredForProcessing("\u001b[1;1HHOST-HISTORY-000\u001b[2;1HHOST-HISTORY-001\u001b[11;1HOLD-LIVE-TAIL\u001b[3;1H");
        }
        public async Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken token)
        {
            if (HoldNextObservation)
            {
                HoldNextObservation = false;
                ObservationEntered.TrySetResult();
                await ReleaseObservation.Task.WaitAsync(token);
                // This deliberately stale row would destroy both protected rows
                // if used after the geometry changed during observation.
                return (0, 0);
            }
            return (0, anchor);
        }
        public (int Width, int Height) ReadCurrentGeometry() => (Width, Height);
        public void Write(string value) => inner.Write(value);
        public void Write(ReadOnlySpan<byte> value) => inner.Write(value);
        public int Width => inner.Width;
        public int Height => inner.Height;
        public TerminalCapabilities Capabilities => inner.Capabilities;
        public ChannelReader<Hex1bEvent> InputEvents => input.Reader;
        public int OutputQueueDepth => inner.OutputQueueDepth;
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public void Flush() => inner.Flush();
        public void EnterTuiMode() => inner.EnterTuiMode();
        public void ExitTuiMode() => inner.ExitTuiMode();
        public void Clear() => inner.Clear();
        public void SetCursorPosition(int left, int top) => inner.SetCursorPosition(left, top);
        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken token = default) => inner.ReadOutputAsync(token);
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken token = default) => inner.WriteInputAsync(bytes, token);
        public ValueTask ResizeAsync(int width, int height, CancellationToken token = default) => inner.ResizeAsync(width, height, token);
        public ValueTask DisposeAsync() => Model.DisposeAsync();
    }
}
