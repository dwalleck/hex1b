using System.Reflection;
using Hex1b.Automation;
using Hex1b.Flow;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
public sealed class FlowLiveResumeTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Commit_NewAppFrameDuringFinalDelivery_IsPresentedAfterResume(bool cancelMode, bool priorMuted)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var commitStop = new CancellationTokenSource();
        using var gate = new DeliveryGate();
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource<FlowCommitResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovering = false;
        var label = "MODAL-OPEN";
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(ctx =>
                {
                    if (recovering) gate.Arm();
                    return ctx.Text(label);
                }, options => { options.MinHeight = 8; options.MaxHeight = 8; });
                try
                {
                    await step.WaitForReadyAsync();
                    ready.SetResult(step);
                    await start.Task.WaitAsync(stop.Token);
                    committed.SetResult(await step.CommitAsync(new Source(cancelMode, () =>
                    {
                        recovering = true;
                        commitStop.Cancel();
                    }), ctx =>
                    {
                        gate.Arm();
                        return Task.FromResult<Hex1bWidget>(ctx.Text(label));
                    }, commitStop.Token));
                    await finish.Task.WaitAsync(stop.Token);
                }
                finally { await step.CompleteAsync(); }
            }, options => options.UseSoftWrapTombstones = true)
            .WithDimensions(80, 24).WithTerminalWidget(out var handle).WithScrollback(100).Build();
        handle.OutputReceived += gate.HoldDelivery;
        var run = terminal.RunAsync(stop.Token);
        try
        {
            var step = await ready.Task.WaitAsync(stop.Token);
            await WaitForTextAsync(terminal, "MODAL-OPEN", stop.Token);
            var live = GetLiveHandle(step);
            if (priorMuted) Assert.IsFalse(live.SetLiveOutputMuted(true));
            start.SetResult();
            await gate.Entered.WaitAsync(stop.Token);
            await RenderNewStateAsync(step.AppForDiagnostics!, () => label = "READER-RESTORED", stop.Token);
            gate.Release();
            var result = await committed.Task.WaitAsync(stop.Token);
            if (priorMuted)
            {
                Assert.IsTrue(live.SetLiveOutputMuted(true), "Commitment must retain its caller's mute ownership.");
                Assert.IsFalse(terminal.GetScreenText().Contains("READER-RESTORED", StringComparison.Ordinal));
                Assert.IsTrue(live.SetLiveOutputMuted(false), "Only the caller explicitly releases its retained mute.");
            }
            await WaitForTextAsync(terminal, "READER-RESTORED", stop.Token);
            Assert.AreEqual(cancelMode ? FlowCommitStatus.Cancelled : FlowCommitStatus.Emitted, result.Status);
            Assert.AreEqual(1, result.CompletedUnits);
            AssertHistoryExactlyOnce(terminal);
            finish.SetResult();
            await run.WaitAsync(stop.Token);
        }
        finally
        {
            gate.Release();
            finish.TrySetResult();
            await stop.CancelAsync();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            handle.OutputReceived -= gate.HoldDelivery;
        }
    }

    // The delivery gate places an edit after the repaint snapshot. Although
    // resize retains queued frames, its pump can consume one before unmute.
    // The no-settle variant reproduced that loss; settle is retention coverage.
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Resize_WithOptionalSettle_PreservesNewFrameDuringDelivery(bool settleMode)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var gate = new DeliveryGate();
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var label = "MODAL-OPEN";
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(ctx => ctx.Text(label), options => { options.MinHeight = 8; options.MaxHeight = 8; });
                try
                {
                    await step.WaitForReadyAsync();
                    var result = await step.CommitAsync(new Source(false, () => { }), ctx => Task.FromResult<Hex1bWidget>(ctx.Text(label)));
                    Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
                    ready.SetResult(step);
                    await finish.Task.WaitAsync(stop.Token);
                }
                finally { await step.CompleteAsync(); }
            }, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = settleMode ? TimeSpan.FromMilliseconds(100) : null;
            })
            .WithDimensions(80, 24).WithTerminalWidget(out var handle).WithScrollback(100).Build();
        handle.OutputReceived += gate.HoldDelivery;
        var run = terminal.RunAsync(stop.Token);
        Hex1bApp? app = null;
        void ArmOnResizedFrame()
        {
            var surface = app!.SnapshotCurrentSurface();
            if (surface is null) return;
            try { if (surface.Width == 100) gate.Arm(); }
            finally { surface.ClearAndReleaseTrackedObjects(); }
        }
        try
        {
            var step = await ready.Task.WaitAsync(stop.Token);
            await WaitForTextAsync(terminal, "MODAL-OPEN", stop.Token);
            app = step.AppForDiagnostics!;
            app.FrameRendered += ArmOnResizedFrame;
            await terminal.ResizeWithWorkloadAsync(100, 24, stop.Token);
            await gate.Entered.WaitAsync(stop.Token);
            app.FrameRendered -= ArmOnResizedFrame;
            await RenderNewStateAsync(app, () => label = "READER-RESTORED", stop.Token);
            gate.Release();
            await WaitForTextAsync(terminal, "READER-RESTORED", stop.Token);
            Assert.AreEqual(100, terminal.Width);
            AssertHistoryExactlyOnce(terminal);
            finish.SetResult();
            await run.WaitAsync(stop.Token);
        }
        catch
        {
            if (app is not null)
            {
                var step = await ready.Task;
                var live = GetLiveHandle(step);
                const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
                var mute = live.GetType().GetField("_muteGate", flags)!.GetValue(live)!;
                var adapter = (InlineStepAdapter)live.GetType().GetField("_stepAdapter", flags)!.GetValue(live)!;
                var surface = app.SnapshotCurrentSurface();
                try
                {
                    Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(new
                    {
                        Stage = "resize-failure", settleMode, app.FrameCount,
                        Muted = mute.GetType().GetField("Value")!.GetValue(mute),
                        adapter.OutputQueueDepth, adapter.OutputEpoch, adapter.Width, adapter.Height,
                        live.RowOrigin, live.LiveHeight, live.TerminalWidth, live.TerminalHeight,
                        SurfaceFirstRow = surface is null ? null : string.Concat(Enumerable.Range(0, surface.Width).Select(x => surface.GetCell(x, 0).Character))
                    }));
                }
                finally { surface?.ClearAndReleaseTrackedObjects(); }
            }
            throw;
        }
        finally
        {
            if (app is not null) app.FrameRendered -= ArmOnResizedFrame;
            gate.Release();
            finish.TrySetResult();
            await stop.CancelAsync();
            await ((Task)run).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            handle.OutputReceived -= gate.HoldDelivery;
        }
    }

    private static async Task RenderNewStateAsync(Hex1bApp app, Action change, CancellationToken token)
    {
        var rendered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void ObserveFrame()
        {
            var surface = app.SnapshotCurrentSurface();
            if (surface is null) return;
            try
            {
                var firstRow = string.Concat(Enumerable.Range(0, surface.Width).Select(x => surface.GetCell(x, 0).Character));
                if (firstRow.Contains("READER-RESTORED", StringComparison.Ordinal)) rendered.TrySetResult();
            }
            finally { surface.ClearAndReleaseTrackedObjects(); }
        }
        app.FrameRendered += ObserveFrame;
        try
        {
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app.Dispatch(() => { change(); app.Invalidate(); }, out var dispatched));
            await dispatched.WaitAsync(token);
            await rendered.Task.WaitAsync(token);
        }
        finally { app.FrameRendered -= ObserveFrame; }
    }

    private static async Task WaitForTextAsync(Hex1bTerminal terminal, string text, CancellationToken token)
    {
        using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(screen => screen.ContainsText(text), TimeSpan.FromSeconds(5), $"presented {text} without further input")
            .Build().ApplyAsync(terminal, token);
    }

    private static ILiveStepHandle GetLiveHandle(FlowStep step)
    {
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var coordinator = typeof(FlowStep).GetField("_commitCoordinator", flags)!.GetValue(step)!;
        return (ILiveStepHandle)coordinator.GetType().GetField("_live", flags)!.GetValue(coordinator)!;
    }

    private static void AssertHistoryExactlyOnce(Hex1bTerminal terminal)
    {
        var history = string.Join('\n', terminal.GetScrollbackRows(terminal.ScrollbackCount)
            .Select(row => string.Concat(row.Cells.Select(cell => cell.Character))));
        var durable = history + terminal.GetScreenText();
        Assert.AreEqual(1, durable.Split("COMMITTED-ONCE", StringSplitOptions.None).Length - 1,
            "Repainting live state must not duplicate finalized history.");
        Assert.IsFalse(history.Contains("MODAL-OPEN", StringComparison.Ordinal), "Mutable modal content must not become history.");
        Assert.IsFalse(history.Contains("READER-RESTORED", StringComparison.Ordinal), "Mutable reader content must not become history.");
    }

    private sealed class DeliveryGate : IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _state;
        public Task Entered => _entered.Task;
        public void Arm() => Interlocked.CompareExchange(ref _state, 1, 0);
        public void HoldDelivery()
        {
            if (Interlocked.CompareExchange(ref _state, 2, 1) != 1) return;
            _entered.TrySetResult();
            if (!_release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("The test did not release the held native delivery.");
        }
        public void Release() => _release.Set();
        public void Dispose() => _release.Dispose();
    }

    private sealed class Source(bool cancelMode, Action cancel) : FlowCommitSource
    {
        public override int UnitCount => cancelMode ? 2 : 1;
        public override Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken token)
        {
            if (index == 1) { cancel(); token.ThrowIfCancellationRequested(); }
            var surface = new Surface(width, 1);
            surface.WriteText(0, 0, "COMMITTED-ONCE");
            return Task.FromResult(new FlowCommitUnit("only", surface));
        }
    }
}
