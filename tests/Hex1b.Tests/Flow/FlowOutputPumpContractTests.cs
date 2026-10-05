using System.Reflection;
using System.Text;
using Hex1b;
using Hex1b.Flow;
using Hex1b.Kgp;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Behavior fences for the live-step output pump's synchronized-frame seam.
/// </summary>
/// <remarks>
/// The tests invoke the existing private pump only to inject otherwise-unobservable
/// queued-frame interleavings. Output is still presented through a real
/// <see cref="Hex1bAppWorkloadAdapter"/>, <see cref="Hex1bTerminal"/> parser, and
/// <see cref="TerminalWidgetHandle"/>. Assertions inspect captured handle frames,
/// never the pump's escape-string writes.
/// </remarks>
[DoNotParallelize]
[TestClass]
public sealed class FlowOutputPumpContractTests
{
    private const string SyncUpdateBegin = "\x1b[?2026h";
    private const string SyncUpdateEnd = "\x1b[?2026l";
    private const string ClearPrompt = "\x1b[1;1H\x1b[2K";
    private const int Width = 40;
    private const int Height = 8;

    [TestMethod]
    public async Task AbandonedPartialFrame_IsNeverPresented_AndFreshFrameRendersPrompt()
    {
        using var harness = PumpHarness.Create(Width, Height);
        await harness.SeedPromptAsync("LIVE-PROMPT");
        harness.ClearCapturedFrames();

        // The first frame is abandoned as if DiscardQueuedLiveOutput removed its
        // ESU while the pump held the BSU/body prefix. A later BSU must reset that
        // prefix instead of concatenating it with the fresh frame.
        harness.StepAdapter.Write(SyncUpdateBegin);
        harness.StepAdapter.Write(ClearPrompt);
        harness.StepAdapter.Write(SyncUpdateBegin);
        harness.StepAdapter.Write(ClearPrompt + "FRESH-PROMPT" + SyncUpdateEnd);

        await harness.WaitForCapturedFramesAsync(1);
        var frames = harness.CapturedFrames;
        Assert.IsNotEmpty(frames, "the complete fresh frame must reach the presentation seam");
        Assert.IsTrue(
            frames.All(frame => frame.Contains("LIVE-PROMPT", StringComparison.Ordinal)
                || frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "an abandoned partial frame must never present a prompt-missing screen");
        Assert.IsTrue(
            frames[^1].Contains("FRESH-PROMPT", StringComparison.Ordinal),
            "a complete fresh frame after the abandoned prefix must render the prompt");
    }

    [TestMethod]
    public async Task EpochSwitch_DropsStaleBodyTail_AndFreshFrameRendersPrompt()
    {
        using var harness = PumpHarness.Create(Width, Height);
        await harness.SeedPromptAsync("LIVE-PROMPT");
        harness.ClearCapturedFrames();

        harness.StepAdapter.Write(SyncUpdateBegin);
        harness.StepAdapter.Write(ClearPrompt);
        await harness.WaitForStepQueueToDrainAsync();

        // ResizeAsync advances the adapter generation without changing geometry.
        // The body-only tail is still from the abandoned frame and must not leak
        // into a completed handle presentation after that epoch switch.
        await harness.StepAdapter.ResizeAsync(Width, Height);
        harness.StepAdapter.Write(ClearPrompt + "STALE-TAIL");
        harness.StepAdapter.Write(SyncUpdateBegin);
        harness.StepAdapter.Write(ClearPrompt + "FRESH-PROMPT" + SyncUpdateEnd);

        await harness.WaitForCapturedFramesAsync(1);
        var frames = harness.CapturedFrames;
        Assert.IsNotEmpty(frames, "the complete fresh frame must reach the handle");
        Assert.IsTrue(
            frames.All(frame => !frame.Contains("STALE-TAIL", StringComparison.Ordinal)),
            "the stale body tail must not reach a completed handle frame");
        Assert.IsTrue(
            frames.All(frame => frame.Contains("LIVE-PROMPT", StringComparison.Ordinal)
                || frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "the epoch switch must not expose a prompt-missing presentation");
        Assert.IsTrue(
            frames[^1].Contains("FRESH-PROMPT", StringComparison.Ordinal),
            "a complete fresh frame after the epoch switch must render the prompt");
    }

    [TestMethod]
    public async Task PumpDiscardBoundary_DropsUnreadBodyTail_AndFreshFrameRetainsPrompt()
    {
        using var harness = PumpHarness.Create(Width, Height, startPump: false);
        await harness.SeedPromptAsync("LIVE-PROMPT");
        harness.ClearCapturedFrames();

        // Queue the old frame before the pump starts. DiscardQueuedOutput removes
        // its BSU, so the remaining body and ESU arrive with no open frame.
        harness.StepAdapter.Write(SyncUpdateBegin);
        harness.StepAdapter.Write(ClearPrompt + "OLD-HEAD");
        harness.StepAdapter.DiscardQueuedOutput();
        harness.StepAdapter.Write("OLD-TAIL");
        harness.StepAdapter.Write(SyncUpdateEnd);
        harness.StepAdapter.Write(
            SyncUpdateBegin + ClearPrompt + "FRESH-PROMPT" + SyncUpdateEnd);
        harness.StartOutputPump();

        await harness.WaitForCapturedFramesAsync(1);
        var frames = harness.CapturedFrames;
        Assert.IsTrue(
            frames.All(frame => !frame.Contains("OLD-TAIL", StringComparison.Ordinal)),
            "body and ESU after an unread discarded BSU must not leak into presentation");
        Assert.IsTrue(
            frames.Any(frame => frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "a fresh frame after the discard boundary must reach presentation");
        Assert.IsTrue(
            frames.All(frame => frame.Contains("LIVE-PROMPT", StringComparison.Ordinal)
                || frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "discarding an unread BSU must not expose a promptless frame");
    }

    [TestMethod]
    public async Task PumpDiscardBoundary_DropsAlreadyReadBodyTail_AndFreshFrameRetainsPrompt()
    {
        using var harness = PumpHarness.Create(Width, Height);
        await harness.SeedPromptAsync("LIVE-PROMPT");
        harness.ClearCapturedFrames();

        // Hold the runner's existing step-operation lock while a complete stale
        // frame is dequeued. This blocks Forward after the frame is read, so the
        // discard must invalidate an already-read epoch, not just queued bytes.
        var stepOperationsLock = typeof(Hex1bFlowRunner)
            .GetField("_stepOpsLock", BindingFlags.Instance | BindingFlags.NonPublic)?
            .GetValue(harness.Runner)
            ?? throw new InvalidOperationException("runner step-operation lock is unavailable");
        Monitor.Enter(stepOperationsLock);
        try
        {
            harness.StepAdapter.Write(
                SyncUpdateBegin + ClearPrompt + "STALE-FRAME" + SyncUpdateEnd);
            harness.WaitForStepQueueToDrainSynchronously();
            harness.StepAdapter.DiscardQueuedOutput();
        }
        finally
        {
            Monitor.Exit(stepOperationsLock);
        }

        harness.StepAdapter.Write(
            SyncUpdateBegin + ClearPrompt + "FRESH-PROMPT" + SyncUpdateEnd);

        await harness.WaitForCapturedFrameContainingAsync("FRESH-PROMPT");
        var frames = harness.CapturedFrames;
        Assert.IsTrue(
            frames.All(frame => !frame.Contains("STALE-FRAME", StringComparison.Ordinal)),
            "a complete frame read before discard must be rejected after the epoch bump");
        Assert.IsTrue(
            frames.Any(frame => frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "a fresh frame after invalidating an already-read frame must present");
        Assert.IsTrue(
            frames.All(frame => frame.Contains("LIVE-PROMPT", StringComparison.Ordinal)
                || frame.Contains("FRESH-PROMPT", StringComparison.Ordinal)),
            "already-read discard recovery must not expose a promptless frame");
    }

    [TestMethod]
    public async Task PreservesBothFrameUpdates()
    {
        using var harness = PumpHarness.Create(Width, Height);
        harness.ClearCapturedFrames();

        // The second frame updates a different row without clearing the first:
        // a legal coalescing implementation may publish one or two completed
        // frames, but it must preserve both caller-visible updates.
        harness.StepAdapter.Write(
            SyncUpdateBegin + ClearPrompt + "FIRST-PROMPT" + SyncUpdateEnd
            + SyncUpdateBegin + "\x1b[2;1HSECOND-PROMPT" + SyncUpdateEnd);

        await harness.WaitForCapturedFrameContainingAsync("SECOND-PROMPT");
        var frame = harness.CapturedFrames[^1];
        var rows = frame.Split('\n');
        Assert.IsTrue(
            rows[0].Contains("FIRST-PROMPT", StringComparison.Ordinal),
            "the first frame update must remain on its original row");
        Assert.IsTrue(
            rows[1].Contains("SECOND-PROMPT", StringComparison.Ordinal),
            "the second frame update must remain on its updated row");
    }

    private static async Task WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string failure)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (!condition())
        {
            if (Environment.TickCount64 >= deadline)
            {
                Assert.Fail(failure);
            }

            await Task.Delay(1).ConfigureAwait(false);
        }
    }

    [TestMethod]
    public async Task CompleteImages_DiscardedUploadAndRemoval_DoNotAdvanceDeliveredState()
    {
        using var harness = PumpHarness.Create(Width, Height, startPump: false);
        var image = ImageFrame("INITIAL", includeImage: true);
        harness.StepAdapter.SubmitRenderFrame(image, harness.StepAdapter.OutputEpoch);
        Assert.AreEqual(1, harness.StepAdapter.DiscardQueuedOutput());
        harness.StepAdapter.SubmitRenderFrame(image with { Body = ClearPrompt + "DELIVERED" }, harness.StepAdapter.OutputEpoch);
        harness.StartOutputPump();
        await harness.WaitForCapturedFrameContainingAsync("DELIVERED");
        Assert.AreEqual(1, harness.Terminal.KgpImageStore.ImageCount);
        Assert.AreEqual(41u, TestSeq.Single(harness.Terminal.KgpPlacements).ImageId);

        // Hold the real pump before admission: an already-dequeued removal must
        // not mutate the accepted tracker when its generation is discarded.
        var stepLock = typeof(Hex1bFlowRunner).GetField("_stepOpsLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(harness.Runner)!;
        Monitor.Enter(stepLock);
        try
        {
            harness.StepAdapter.SubmitRenderFrame(ImageFrame("STALE-REMOVAL", false), harness.StepAdapter.OutputEpoch);
            harness.WaitForStepQueueToDrainSynchronously();
            harness.StepAdapter.DiscardQueuedOutput();
        }
        finally { Monitor.Exit(stepLock); }
        harness.StepAdapter.SubmitRenderFrame(ImageFrame("REMOVED", false), harness.StepAdapter.OutputEpoch);
        await harness.WaitForCapturedFrameContainingAsync("REMOVED");
        Assert.AreEqual(0, harness.Terminal.KgpPlacements.Count);
        Assert.AreEqual(0, harness.Terminal.KgpImageStore.ImageCount);
        Assert.IsFalse(harness.CapturedFrames.Any(frame => frame.Contains("STALE-REMOVAL", StringComparison.Ordinal)));
    }

    [TestMethod]
    public async Task CompleteImages_SlowProcessing_CoalescesOnePendingFrameBehindControls()
    {
        using var harness = PumpHarness.Create(Width, Height, startPump: false);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        void HoldFirstDelivery()
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.SetResult();
            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("delivery release was not signalled");
        }
        harness.Handle.OutputReceived += HoldFirstDelivery;
        try
        {
            harness.StepAdapter.SubmitRenderFrame(ImageFrame("FIRST", true), harness.StepAdapter.OutputEpoch);
            harness.StartOutputPump();
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            for (var i = 0; i < 30; i++)
                harness.StepAdapter.SubmitRenderFrame(ImageFrame($"SUPERSEDED-{i}", false), harness.StepAdapter.OutputEpoch);
            harness.StepAdapter.Write("\x1b[3;1HCONTROL");
            harness.StepAdapter.SubmitRenderFrame(ImageFrame("FINAL", true), harness.StepAdapter.OutputEpoch);
            Assert.AreEqual(2, harness.StepAdapter.OutputQueueDepth, "one pending complete frame and one ordered control");
            Assert.AreEqual(1, calls, "no second downstream frame may pass the unsettled receipt");
            release.Set();
            await harness.WaitForCapturedFrameContainingAsync("FINAL");
            Assert.AreEqual(1, harness.Terminal.KgpImageStore.ImageCount);
            Assert.AreEqual(1, harness.Terminal.KgpPlacements.Count);
            var frames = harness.CapturedFrames;
            var controlIndex = Array.FindIndex(frames.ToArray(), frame => frame.Contains("CONTROL", StringComparison.Ordinal));
            var finalIndex = Array.FindIndex(frames.ToArray(), frame => frame.Contains("FINAL", StringComparison.Ordinal));
            Assert.IsTrue(controlIndex >= 0 && finalIndex > controlIndex, "replacement must stay after intervening controls");
            Assert.IsFalse(frames.Any(frame => frame.Contains("SUPERSEDED-", StringComparison.Ordinal)));
        }
        finally
        {
            release.Set();
            harness.Handle.OutputReceived -= HoldFirstDelivery;
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CompleteImages_EpochInvalidation_ReleasesPendingFrameAndPreservesControls(bool resize)
    {
        using var adapter = new InlineStepAdapter(Width, Height, 0);
        adapter.SubmitRenderFrame(ImageFrame("STALE", true), adapter.OutputEpoch);
        adapter.Write("CONTROL");
        if (resize) await adapter.ResizeAsync(Width + 1, Height);
        else adapter.RowOrigin = 1;
        Assert.AreEqual(1, adapter.OutputQueueDepth, "epoch invalidation must release the pending image snapshot immediately");
        var control = await adapter.ReadOutputFrameAsync();
        Assert.AreEqual(InlineOutputFrameKind.Data, control.Kind);
        Assert.AreEqual("CONTROL", Encoding.UTF8.GetString(control.Bytes));
    }

    [TestMethod]
    public async Task CompleteImages_UnexpectedProcessingCancellation_FaultsPumpAndApplicationInput()
    {
        using var harness = PumpHarness.Create(Width, Height, startPump: false);
        void FailPresentation() => throw new OperationCanceledException("parent delivery cancelled while the step remains active");
        harness.Handle.OutputReceived += FailPresentation;
        try
        {
            harness.StepAdapter.SubmitRenderFrame(ImageFrame("FAILED", true), harness.StepAdapter.OutputEpoch);
            harness.StartOutputPump();
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await harness.OutputPump.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.IsInstanceOfType<OperationCanceledException>(harness.StepAdapter.OutputFailure);
            await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                await harness.StepAdapter.InputEvents.Completion.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally { harness.Handle.OutputReceived -= FailPresentation; }
    }

    private static SoftWrapRenderFrame ImageFrame(string text, bool includeImage)
    {
        var data = new KgpCellData("\x1b_Ga=t,f=32,s=1,v=1,i=41,q=2;/4AA/w==\x1b\\", 41, 1, 1, 1, 1, [1]);
        IReadOnlyList<KgpFragment> graphics = includeImage ? [new KgpFragment(41, 0, 1, 1, 1, 0, 0, 1, 1, data)] : [];
        return new SoftWrapRenderFrame(Width, Height, SyncUpdateBegin, ClearPrompt + text, SyncUpdateEnd, graphics);
    }

    private sealed class PumpHarness : IDisposable
    {
        private readonly object _captureSync = new();
        private readonly List<string> _capturedFrames = [];
        private readonly CancellationTokenSource _pumpCts;
        private Task? _pumpTask;

        private PumpHarness(
            Hex1bAppWorkloadAdapter workload,
            Hex1bTerminal terminal,
            TerminalWidgetHandle handle,
            Hex1bFlowRunner runner,
            InlineStepAdapter stepAdapter,
            CancellationTokenSource pumpCts,
            Task? pumpTask)
        {
            Workload = workload;
            Terminal = terminal;
            Handle = handle;
            Runner = runner;
            StepAdapter = stepAdapter;
            _pumpCts = pumpCts;
            _pumpTask = pumpTask;
        }

        public Task OutputPump => _pumpTask ?? throw new InvalidOperationException("pump has not started");

        public Hex1bAppWorkloadAdapter Workload { get; }

        public Hex1bTerminal Terminal { get; }

        public TerminalWidgetHandle Handle { get; }

        public Hex1bFlowRunner Runner { get; }

        public InlineStepAdapter StepAdapter { get; }

        public static PumpHarness Create(int width, int height, bool startPump = true)
        {
            var caps = new TerminalCapabilities { SupportsKgp = true, SupportsTrueColor = true };
            var workload = new Hex1bAppWorkloadAdapter(caps);
            var terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithDimensions(width, height)
                .WithTerminalWidget(out var handle)
                .Build();

            handle.UpdateHostCapabilities(caps);

            var harness = new PumpHarness(
                workload,
                terminal,
                handle,
                new Hex1bFlowRunner(
                    _ => Task.CompletedTask,
                    new Hex1bFlowOptions { InitialCursorRow = 0 },
                    workload),
                new InlineStepAdapter(
                    width,
                    height,
                    rowOrigin: 0,
                    workload.Capabilities),
                new CancellationTokenSource(),
                pumpTask: null);

            handle.OutputReceived += harness.CapturePresentation;
            if (startPump)
            {
                harness.StartOutputPump();
            }
            return harness;
        }

        public void StartOutputPump()
        {
            if (_pumpTask is not null)
            {
                throw new InvalidOperationException("the output pump has already started");
            }

            _pumpTask = StartPump(
                Runner,
                StepAdapter,
                _pumpCts.Token);
        }

        private static Task StartPump(
            Hex1bFlowRunner runner,
            InlineStepAdapter stepAdapter,
            CancellationToken cancellationToken)
        {
            var pumpMethod = typeof(Hex1bFlowRunner)
                .GetMethods(BindingFlags.Instance | BindingFlags.NonPublic)
                .Single(method =>
                    method.Name == "PumpStepOutputAsync"
                    && method.GetParameters().Length == 4);

            return (Task)(pumpMethod.Invoke(
                runner,
                [stepAdapter, cancellationToken, null, false])
                ?? throw new InvalidOperationException("output pump did not return a task"));
        }

        public async Task SeedPromptAsync(string prompt)
        {
            var before = CapturedFrameCount;
            Workload.Write(ClearPrompt + prompt);
            await WaitForCapturedFramesAsync(before + 1).ConfigureAwait(false);
            Assert.IsTrue(
                CapturedFrames[^1].Contains(prompt, StringComparison.Ordinal),
                "the baseline prompt must reach the real handle presentation");
        }

        public async Task WaitForCapturedFramesAsync(int count)
            => await WaitUntilAsync(
                () => CapturedFrameCount >= count,
                TimeSpan.FromSeconds(5),
                $"expected at least {count} captured handle presentation frame(s)")
                .ConfigureAwait(false);

        public async Task WaitForCapturedFrameContainingAsync(string text)
            => await WaitUntilAsync(
                () => CapturedFrames.Any(frame =>
                    frame.Contains(text, StringComparison.Ordinal)),
                TimeSpan.FromSeconds(5),
                $"expected a captured handle frame containing '{text}'")
                .ConfigureAwait(false);


        public void WaitForStepQueueToDrainSynchronously()
        {
            var deadline = Environment.TickCount64 + 5000;
            var spinner = new SpinWait();
            while (StepAdapter.OutputQueueDepth != 0
                && Environment.TickCount64 < deadline)
            {
                spinner.SpinOnce();
            }

            Assert.AreEqual(
                0,
                StepAdapter.OutputQueueDepth,
                "the pump did not dequeue the frame while the step lock was held");
        }


        public async Task WaitForStepQueueToDrainAsync()
            => await WaitUntilAsync(
                () => StepAdapter.OutputQueueDepth == 0,
                TimeSpan.FromSeconds(5),
                "the pump did not consume the queued partial frame")
                .ConfigureAwait(false);

        public int CapturedFrameCount
        {
            get
            {
                lock (_captureSync)
                {
                    return _capturedFrames.Count;
                }
            }
        }

        public IReadOnlyList<string> CapturedFrames
        {
            get
            {
                lock (_captureSync)
                {
                    return _capturedFrames.ToArray();
                }
            }
        }

        public void ClearCapturedFrames()
        {
            lock (_captureSync)
            {
                _capturedFrames.Clear();
            }
        }

        private void CapturePresentation()
        {
            if (Handle.TryCaptureRenderFrame(0, out var frame) && frame is not null)
            {
                lock (_captureSync)
                {
                    _capturedFrames.Add(FrameText(frame));
                }
            }
        }

        private static string FrameText(TerminalWidgetRenderFrame frame)
        {
            var text = new StringBuilder();
            for (var row = 0; row < frame.Height; row++)
            {
                for (var column = 0; column < frame.Width; column++)
                {
                    var character = frame.Cells[row, column].Character;
                    text.Append(string.IsNullOrEmpty(character) ? " " : character);
                }

                if (row + 1 < frame.Height)
                {
                    text.Append('\n');
                }
            }

            return text.ToString();
        }

        public void Dispose()
        {
            _pumpCts.Cancel();
            if (_pumpTask is not null)
            {
                try
                {
                    _pumpTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                }
            }

            StepAdapter.Dispose();
            Terminal.Dispose();
            Workload.Dispose();
        }
    }
}
