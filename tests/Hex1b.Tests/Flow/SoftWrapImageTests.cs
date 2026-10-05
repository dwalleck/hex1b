using Hex1b.Input;
using Hex1b.Flow;
using Hex1b.Surfaces;
using Hex1b.Tokens;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class SoftWrapImageTests
{
    [TestMethod]
    public async Task SelectedCaret_DiscardedHide_FreshFrameStillHidesNativeCursor()
    {
        using var adapter = new InlineStepAdapter(24, 10, 0);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(24, 10).Build();
        var state = new TextBoxState("draft") { CursorPosition = 0 };
        using var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(ctx.TextBox().State(state)),
            new Hex1bAppOptions { WorkloadAdapter = adapter, UseSoftWrapEmission = true });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = app.RunAsync(stop.Token);
        async Task<InlineOutputFrame> ReadRenderAsync()
        {
            while (true)
            {
                var output = await adapter.ReadOutputFrameAsync(stop.Token);
                if (output.Kind == InlineOutputFrameKind.RenderFrame) return output;
                stop.Token.ThrowIfCancellationRequested();
            }
        }
        try
        {
            var shown = await ReadRenderAsync();
            terminal.ApplyTokens(AnsiTokenizer.Tokenize(System.Text.Encoding.UTF8.GetString(shown.Bytes)));
            Assert.IsTrue(terminal.CursorVisible, "initial accepted frame must show the native caret");
            await adapter.WriteInputEventAsync(Hex1bKeyEvent.WithShift(Hex1bKey.RightArrow));
            var discarded = await ReadRenderAsync();
            Assert.IsTrue(state.HasSelection);
            Assert.IsTrue(System.Text.Encoding.UTF8.GetString(discarded.Bytes).Contains("\x1b[?25l", StringComparison.Ordinal));
            // Do not apply the selected frame. Force another complete selected
            // frame through the actual app/sink boundary with the same state.
            adapter.RequestRenderFrame();
            var fresh = await ReadRenderAsync();
            terminal.ApplyTokens(AnsiTokenizer.Tokenize(System.Text.Encoding.UTF8.GetString(fresh.Bytes)));
            Assert.IsFalse(terminal.CursorVisible, "discarding a hide must not leave the old accepted native caret visible");
            Assert.AreEqual("draft", state.Text);
            Assert.AreEqual("d", state.SelectedText);
        }
        finally
        {
            app.RequestStop();
            await running;
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task LiveImage_RedrawResizeRemove_PreservesImageLifecycleAndText(bool aboveText, bool unicode)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true, SupportsTrueColor = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        var uploads = 0;
        var observer = new ImageOutputObserver
        {
            Observe = text =>
            {
                if (text.Contains("a=t", StringComparison.Ordinal)) Interlocked.Increment(ref uploads);
            }
        };
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless(caps).WithDimensions(24, 10).AddPresentationFilter(observer).Build();
        var pixels = Enumerable.Repeat(new byte[] { 255, 128, 0, 255 }, 16).SelectMany(x => x).ToArray();
        var stage = 0;
        var fullWidthText = unicode ? "0123456789012345678901😀" : "012345678901234567890123";
        using var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(
            ctx.VStack(v => [
                v.Text(fullWidthText),
                v.Button($"Stage {stage}").OnClick(_ => stage++),
                stage < 2
                    ? new KgpImageWidget(pixels, 4, 4, v.Text("fallback"))
                        { Width = 4, Height = 2, ZOrder = aboveText ? KgpZOrder.AboveText : KgpZOrder.BelowText }
                    : v.Text("removed")
            ])), new Hex1bAppOptions { WorkloadAdapter = workload, UseSoftWrapEmission = true });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var running = app.RunAsync(stop.Token);
        try
        {
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("Stage 0") && s.KgpPlacements.Count == 1, TimeSpan.FromSeconds(5), "initial image")
                .Build().ApplyAsync(terminal, stop.Token);
            var initial = TestSeq.Single(terminal.KgpPlacements);
            Assert.AreEqual(2, initial.Row);
            Assert.AreEqual(0, initial.Column);
            Assert.AreEqual(1, terminal.KgpImageStore.ImageCount);
            Assert.AreEqual(1, uploads);
            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.Enter)
                .WaitUntil(s => s.ContainsText("Stage 1") && s.KgpPlacements.Count == 1, TimeSpan.FromSeconds(5), "redrawn image")
                .Build().ApplyAsync(terminal, stop.Token);
            Assert.AreEqual(initial.ImageId, TestSeq.Single(terminal.KgpPlacements).ImageId);
            Assert.AreEqual(1, terminal.KgpImageStore.ImageCount);
            Assert.AreEqual(1, uploads, "unchanged redraw must not retransmit pixel data");

            terminal.Resize(30, 12);
            terminal.ApplyTokens(AnsiTokenizer.Tokenize("\x1b_Ga=d,d=A,q=2\x1b\\"));
            Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
            await workload.ResizeAsync(30, 12, stop.Token);
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("Stage 1") && s.KgpPlacements.Count == 1 && terminal.KgpImageStore.ImageCount == 1,
                    TimeSpan.FromSeconds(5), "image rehydrated after resize")
                .Build().ApplyAsync(terminal, stop.Token);
            Assert.AreNotEqual(initial.ImageId, TestSeq.Single(terminal.KgpPlacements).ImageId);
            Assert.AreEqual(2, TestSeq.Single(terminal.KgpPlacements).Row);
            Assert.AreEqual(2, uploads, "resize must upload the new owned image exactly once");

            var final = await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.Enter)
                .WaitUntil(s => s.ContainsText("removed") && s.KgpPlacements.Count == 0 && terminal.KgpImageStore.ImageCount == 0,
                    TimeSpan.FromSeconds(5), "removed image released")
                .Capture("removed").Build().ApplyWithCaptureAsync(terminal, stop.Token);
            Assert.IsTrue(final.ContainsText(fullWidthText));
        }
        finally
        {
            app.RequestStop();
            await running;
        }
    }

    [TestMethod]
    [DataRow(2)]
    [DataRow(6)]
    public async Task LiveImage_FlowCommitResizeCompletion_ReleasesOwnedImagesAndPreservesUnrelatedData(int shrinkHeight)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true, SupportsTrueColor = true };
        var pixels = Enumerable.Repeat(new byte[] { 255, 128, 0, 255 }, 16).SelectMany(x => x).ToArray();
        Hex1bTerminal terminal = null!;
        Task<Hex1bWidget> Live(FlowStepContext ctx) => Task.FromResult<Hex1bWidget>(
            ctx.VStack(v => [v.Text("LIVE-IMAGE"), new KgpImageWidget(pixels, 4, 4, v.Text("fallback")) { Width = 3, Height = 2 }]));
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var lifetime = terminal = Hex1bTerminal.CreateBuilder().WithHex1bFlow(async flow =>
        {
            var step = flow.Step(Live, options => { options.MinHeight = 4; options.MaxHeight = 4; });
            try
            {
                await step.WaitForReadyAsync(stop.Token);
                await WaitForOwnedImageAsync("initial image");
                Assert.IsNotNull(terminal.KgpImageStore.GetImageByClientId(900), "unrelated data must survive initial rendering");
                for (var i = 0; i < 3; i++)
                {
                    var result = await step.CommitAsync(new ImageHistorySource($"HISTORY-{i}"), Live, stop.Token);
                    Assert.AreEqual(4, result.CompletedUnits);
                    await WaitForOwnedImageAsync("image after repeated history scroll");
                    var owned = TestSeq.Single(terminal.KgpPlacements.Where(p => p.ImageId != 900));
                    var liveRow = Array.FindIndex(terminal.GetScreenText().Split('\n'), row => row.Contains("LIVE-IMAGE", StringComparison.Ordinal));
                    Assert.IsTrue(liveRow >= 0);
                    Assert.AreEqual(liveRow + 1, owned.Row, "image must be placed directly below the current live text");
                    Assert.IsNotNull(terminal.KgpImageStore.GetImageByClientId(900), "owned relocation must retain unrelated pixels");
                }
                await terminal.ResizeWithWorkloadAsync(20, shrinkHeight, stop.Token);
                await WaitForOwnedImageAsync("image after shrink");
                Assert.IsTrue(terminal.KgpPlacements.Where(p => p.ImageId != 900)
                    .All(p => p.Row >= 0 && p.Row + p.DisplayRows <= shrinkHeight), "no image may extend below the live viewport");
                await terminal.ResizeWithWorkloadAsync(30, 10, stop.Token);
                await WaitForOwnedImageAsync("image after grow");
            }
            finally { await step.CompleteAsync(stop.Token); }
            Assert.IsTrue(terminal.KgpPlacements.All(p => p.ImageId == 900), "completion must leave no live-owned placement");
            Assert.AreEqual(1, terminal.KgpImageStore.ImageCount, "only unrelated data remains after live image cleanup");
            Assert.IsNotNull(terminal.KgpImageStore.GetImageByClientId(900));
            var next = flow.Step(ctx => ctx.Text("NEXT-STEP"));
            try { await next.WaitForReadyAsync(stop.Token); }
            finally { await next.CompleteAsync(stop.Token); }
            Assert.AreEqual(1, terminal.KgpImageStore.ImageCount, "the next step cannot inherit old live uploads");
        }, options => { options.UseSoftWrapTombstones = true; options.InitialCursorRow = 0; })
            .WithHeadless(caps).WithDimensions(30, 10).WithScrollback(100)
            .Build();
        terminal.ApplyTokens(AnsiTokenizer.Tokenize("\x1b_Ga=t,f=32,s=1,v=1,i=900,q=2;/4AA/w==\x1b\\\x1b[9;1H\x1b_Ga=p,i=900,c=1,r=1,q=2\x1b\\"));
        Assert.IsNotNull(terminal.KgpImageStore.GetImageByClientId(900), "unrelated positive control must exist before the app starts");
        async Task WaitForOwnedImageAsync(string description)
        {
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("LIVE-IMAGE") && s.KgpPlacements.Count(p => p.ImageId != 900) == 1,
                    TimeSpan.FromSeconds(5), description).Build().ApplyAsync(terminal, stop.Token);
        }
        await terminal.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task LiveImage_ObservedResizeOverflow_DeletesPlacementsBeforeReservationScroll()
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        var observer = new ImageOutputObserver();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless(caps)
            .WithDimensions(24, 10).WithScrollback(100).AddPresentationFilter(observer).Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var batches = new System.Collections.Concurrent.ConcurrentQueue<(string Output, int HistoryReferences)>();
        var forceHighAnchor = false;
        var queries = 0;
        workload.HeadlessCursorProvider = () =>
        {
            if (!forceHighAnchor) return (terminal.CursorX, terminal.CursorY);
            Interlocked.Increment(ref queries);
            return (0, 9);
        };
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => [v.Text("RESIZED-IMAGE"),
                new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, v.Text("fallback")) { Width = 2, Height = 2 }]),
                o => { o.MinHeight = 4; o.MaxHeight = 4; });
            try
            {
                await step.WaitForReadyAsync(stop.Token);
                await new Hex1bTerminalInputSequenceBuilder().WaitUntil(s => s.KgpPlacements.Count == 1,
                    TimeSpan.FromSeconds(5), "initial resize image").Build().ApplyAsync(terminal, stop.Token);
                var id = TestSeq.Single(terminal.KgpPlacements).ImageId;
                Assert.AreEqual(1, TestSeq.Single(terminal.KgpPlacements).Row);
                observer.Observe = output => batches.Enqueue((output, terminal.GetKgpHistoryReferenceCount(id)));
                forceHighAnchor = true;
                await terminal.ResizeWithWorkloadAsync(20, 10, stop.Token);
                await new Hex1bTerminalInputSequenceBuilder().WaitUntil(s => s.KgpPlacements.Count == 1
                    && s.KgpPlacements[0].Row == 7, TimeSpan.FromSeconds(5), "image below reserved high anchor")
                    .Build().ApplyAsync(terminal, stop.Token);
                Assert.IsTrue(queries > 0, "the observed high-anchor branch must actually execute");
                Assert.IsTrue(batches.Any(b => b.Output.Contains('\n')), "the viewport overflow must actually scroll");
                Assert.IsTrue(batches.All(b => b.HistoryReferences == 0), "no intermediate native batch may commit the old image into history");
                var scroll = batches.First(b => b.Output.Contains('\n')).Output;
                var deletion = scroll.IndexOf("a=d", StringComparison.Ordinal);
                Assert.IsTrue(deletion >= 0 && deletion < scroll.IndexOf('\n'), "owned deletion must precede scrolling in the same batch");
            }
            finally
            {
                forceHighAnchor = false;
                await step.CompleteAsync(stop.Token);
            }
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 0 }, workload);
        await runner.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(20));
        Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
        Assert.AreEqual(0, terminal.KgpHistoryPlacementCount);
    }

    private sealed class ImageOutputObserver : IHex1bTerminalOutputObserver
    {
        internal Action<string>? Observe { get; set; }
        internal Func<string, ValueTask>? Process { get; set; }
        public async ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(IReadOnlyList<AppliedToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            var output = tokens.Select(t => t.Token).ToList();
            var text = AnsiTokenSerializer.Serialize(output);
            Observe?.Invoke(text);
            if (Process is { } process) await process(text);
            return output;
        }
        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [TestMethod]
    [DataRow(0)] // Flow completion with an additional pending frame.
    [DataRow(1)] // Direct app cancellation during required processing.
    [DataRow(2)] // Direct app asynchronous disposal during required processing.
    public async Task LiveImage_TerminationDuringProcessing_JoinsReceiptAndReleasesImages(int termination)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var deletionBatches = 0;
        var observer = new ImageOutputObserver
        {
            Process = async text =>
            {
                if (text.Contains("a=d", StringComparison.Ordinal)) Interlocked.Increment(ref deletionBatches);
                if (text.Contains("a=t", StringComparison.Ordinal) && entered.TrySetResult()) await release.Task;
            }
        };
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless(caps)
            .WithDimensions(24, 10).AddPresentationFilter(observer).Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var stepCreated = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<Hex1bWidget> Image(RootContext ctx) => Task.FromResult<Hex1bWidget>(ctx.VStack(v => [
            v.Text("PENDING-IMAGE"), new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, v.Text("fallback")) { Width = 2, Height = 2 }]));
        Hex1bApp? app = null;
        FlowStep? step = null;
        Task running;
        if (termination == 0)
        {
            var runner = new Hex1bFlowRunner(async flow =>
            {
                var live = flow.Step(ctx => Image(ctx), o => { o.MinHeight = 4; o.MaxHeight = 4; });
                stepCreated.SetResult(live);
                await live.WaitForCompletionAsync(stop.Token);
            }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 0 }, workload);
            running = runner.RunAsync(stop.Token);
            step = await stepCreated.Task.WaitAsync(stop.Token);
        }
        else
        {
            app = new Hex1bApp(Image, new Hex1bAppOptions { WorkloadAdapter = workload, UseSoftWrapEmission = true });
            running = app.RunAsync(stop.Token);
        }
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, terminal.KgpImageStore.ImageCount, "the held write must have applied a real image before its processing receipt");
            if (step is not null)
            {
                // Existing diagnostic app frame counter supplies only a render
                // barrier; the real app and inline sink generate the pending frame.
                var liveApp = step.AppForDiagnostics!;
                var before = liveApp.FrameCount;
                step.Invalidate();
                while (liveApp.FrameCount <= before) await Task.Delay(1, stop.Token);
            }
            Task ending;
            if (step is not null) ending = step.CompleteAsync(stop.Token);
            else if (termination == 1)
            {
                await stop.CancelAsync();
                ending = running;
            }
            else ending = app!.DisposeAsync().AsTask();
            Assert.IsFalse(ending.IsCompleted, "termination must retain ownership of the unresolved receipt");
            Assert.AreEqual(0, deletionBatches, "cleanup cannot overtake required processing");
            release.TrySetResult();
            await ending.WaitAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, deletionBatches);
            Assert.AreEqual(0, terminal.KgpPlacements.Count);
            Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
        }
        finally
        {
            release.TrySetResult();
            if (step is not null) await step.CompleteAsync();
            if (app is not null) await app.DisposeAsync();
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RunningApp_AwaitedCallbackDisposal_StopsWithoutSelfAwait(bool softWrap)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless(caps).WithDimensions(24, 10).Build();
        Hex1bApp app = null!;
        var returned = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var lifetime = app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(
            ctx.VStack(v => [
                v.Button("Dispose callback").OnClick(async _ =>
                {
                    await Task.Yield();
                    await app.DisposeAsync();
                    returned.SetResult();
                }),
                new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, v.Text("fallback")) { Width = 2, Height = 2 }
            ])), new Hex1bAppOptions { WorkloadAdapter = workload, UseSoftWrapEmission = softWrap });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = app.RunAsync(stop.Token);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("Dispose callback") && s.KgpPlacements.Count == 1,
                TimeSpan.FromSeconds(5), "callback and image ready")
            .Key(Hex1bKey.Enter).Build().ApplyAsync(terminal, stop.Token);
        await returned.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        if (softWrap)
        {
            Assert.AreEqual(0, terminal.KgpPlacements.Count);
            Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
        }
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => app.RunAsync());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LiveImage_CompletionJoinsAdmittedCommit_BeforeCleanupAndNextStep(bool throwingCancellationCallback)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var source = new HeldImageHistorySource(throwingCancellationCallback);
        Hex1bTerminal terminal = null!;
        Task<Hex1bWidget> Live(FlowStepContext ctx) => Task.FromResult<Hex1bWidget>(
            new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, ctx.Text("fallback")) { Width = 2, Height = 2 });
        using var lifetime = terminal = Hex1bTerminal.CreateBuilder().WithHex1bFlow(async flow =>
        {
            var step = flow.Step(Live, o => { o.MinHeight = 3; o.MaxHeight = 3; });
            await step.WaitForReadyAsync(stop.Token);
            await new Hex1bTerminalInputSequenceBuilder().WaitUntil(s => s.KgpPlacements.Count == 1,
                TimeSpan.FromSeconds(5), "initial image").Build().ApplyAsync(terminal, stop.Token);
            var commit = step.CommitAsync(source, Live, stop.Token);
            await source.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var completion = step.CompleteAsync(stop.Token);
            try
            {
                await source.Stopped.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Assert.IsFalse(completion.IsCompleted, "cleanup must join the admitted source even when it ignores cancellation");
                Assert.AreEqual(1, terminal.KgpImageStore.ImageCount, "live state remains owned until the admitted operation is joined");
            }
            finally { source.Release.TrySetResult(); }
            var result = await commit.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(FlowCommitStatus.Cancelled, result.Status);
            Assert.AreEqual(0, result.CompletedUnits);
            if (throwingCancellationCallback)
            {
                var failure = await Assert.ThrowsExactlyAsync<AggregateException>(async () => await completion);
                Assert.IsTrue(failure.Flatten().InnerExceptions.Contains(source.CallbackFailure), "callback failure must remain explicit after joining and cleanup");
            }
            else await completion;
            Assert.IsFalse(step.CanCommit);
            Assert.ThrowsExactly<FlowCommitAdmissionException>(() => step.CommitAsync(new ImageHistorySource("LATE"), Live, stop.Token));
            Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
            Assert.AreEqual(0, terminal.KgpPlacements.Count);
            var next = flow.Step(ctx => ctx.Text("NEXT-AFTER-STOP"));
            await next.WaitForReadyAsync(stop.Token);
            await new Hex1bTerminalInputSequenceBuilder().WaitUntil(s => s.ContainsText("NEXT-AFTER-STOP"),
                TimeSpan.FromSeconds(5), "next step ready").Build().ApplyAsync(terminal, stop.Token);
            Assert.AreEqual(0, terminal.KgpPlacements.Count);
            Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
            Assert.IsFalse(terminal.GetScreenText().Contains("HELD-HISTORY", StringComparison.Ordinal));
            await next.CompleteAsync(stop.Token);
        }, o => { o.UseSoftWrapTombstones = true; o.InitialCursorRow = 0; })
            .WithHeadless(caps).WithDimensions(24, 10).Build();
        await terminal.RunAsync(stop.Token).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private sealed class HeldImageHistorySource(bool throwingCancellationCallback) : FlowCommitSource
    {
        internal IOException CallbackFailure { get; } = new("source cancellation callback failed");
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Stopped { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override int UnitCount => 1;
        public override async Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken)
        {
            using var registration = cancellationToken.Register(() =>
            {
                Stopped.TrySetResult();
                if (throwingCancellationCallback) throw CallbackFailure;
            });
            Entered.TrySetResult();
            await Release.Task;
            var surface = new Surface(width, 1);
            surface.WriteText(0, 0, "HELD-HISTORY");
            return new FlowCommitUnit("held", surface);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task LiveImage_RunningDirectApp_DisposalCleansBeforeClosingAdapter(bool asynchronous)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless(caps).WithDimensions(24, 10).Build();
        var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(
            new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, ctx.Text("fallback")) { Width = 2, Height = 2 }),
            new Hex1bAppOptions { WorkloadAdapter = workload, UseSoftWrapEmission = true });
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = app.RunAsync(stop.Token);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.KgpPlacements.Count == 1, TimeSpan.FromSeconds(5), "initial direct image")
            .Build().ApplyAsync(terminal, stop.Token);
        if (asynchronous) await app.DisposeAsync();
        else app.Dispose();
        await running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, terminal.KgpPlacements.Count);
        Assert.AreEqual(0, terminal.KgpImageStore.ImageCount);
    }

    private sealed class ImageHistorySource(string prefix) : FlowCommitSource
    {
        public override int UnitCount => 4;
        public override Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken)
        {
            var surface = new Surface(width, 1);
            surface.WriteText(0, 0, $"{prefix}-{index}");
            return Task.FromResult(new FlowCommitUnit($"{prefix}-{index}", surface));
        }
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task LiveImage_UncertainWrite_DoesNotReplayAndReportsUnsuccessfulCleanup(bool flowMode, bool cleanupFails)
    {
        var caps = new TerminalCapabilities { SupportsKgp = true };
        using var workload = new Hex1bAppWorkloadAdapter(caps);
        using var model = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless(caps).WithDimensions(24, 10).Build();
        await using var parent = new FailingImageParent(model, cleanupFails);
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        Task<Hex1bWidget> Image(RootContext ctx) => Task.FromResult<Hex1bWidget>(
            new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, ctx.Text("fallback")) { Width = 2, Height = 2 });
        Task running;
        Hex1bApp? app = null;
        if (flowMode)
        {
            var runner = new Hex1bFlowRunner(async flow =>
            {
                var step = flow.Step(ctx => Image(ctx), o => { o.MinHeight = 3; o.MaxHeight = 3; });
                await step.WaitForCompletionAsync(stop.Token);
            }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 0 }, parent);
            running = runner.RunAsync(stop.Token);
        }
        else
        {
            app = new Hex1bApp(Image, new Hex1bAppOptions { WorkloadAdapter = parent, UseSoftWrapEmission = true });
            running = app.RunAsync(stop.Token);
        }
        var failure = await Assert.ThrowsExactlyAsync<IOException>(async () => await running.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreSame(cleanupFails ? parent.CleanupFailure : parent.UploadFailure, failure);
        Assert.AreEqual(1, parent.UploadAttempts, "uncertain upload must never replay");
        Assert.AreEqual(1, parent.CleanupAttempts, "unwinding attempts exact owned cleanup once");
        Assert.IsTrue(parent.ImageAppliedBeforeFailure, "the uncertain-write positive control must really apply the image");
        Assert.AreEqual(cleanupFails ? 1 : 0, model.KgpImageStore.ImageCount);
        Assert.AreEqual(cleanupFails ? 1 : 0, model.KgpPlacements.Count);
        // A failed cleanup retains its identities for another explicit disposal
        // attempt; no successful cleanup is inferred from this exception.
        if (app is not null)
        {
            if (cleanupFails) await Assert.ThrowsExactlyAsync<IOException>(async () => await app.DisposeAsync());
            else await app.DisposeAsync();
        }
    }

    private sealed class FailingImageParent(Hex1bTerminal model, bool cleanupFails) : IHex1bAppTerminalWorkloadAdapter
    {
        private readonly Hex1b.Tests.Flow.RecordingParentAdapter _inner = new(24, 10);
        internal IOException UploadFailure { get; } = new("uncertain image upload");
        internal IOException CleanupFailure { get; } = new("owned cleanup unavailable");
        internal int UploadAttempts { get; private set; }
        internal int CleanupAttempts { get; private set; }
        internal bool ImageAppliedBeforeFailure { get; private set; }
        public void Write(string text)
        {
            if (text.Contains("a=t", StringComparison.Ordinal))
            {
                UploadAttempts++;
                model.ApplyTokens(AnsiTokenizer.Tokenize(text));
                ImageAppliedBeforeFailure = model.KgpImageStore.ImageCount == 1 && model.KgpPlacements.Count == 1;
                throw UploadFailure;
            }
            if (text.Contains("a=d", StringComparison.Ordinal))
            {
                CleanupAttempts++;
                if (cleanupFails) throw CleanupFailure;
            }
            model.ApplyTokens(AnsiTokenizer.Tokenize(text));
        }
        public void Write(ReadOnlySpan<byte> data) => Write(System.Text.Encoding.UTF8.GetString(data));
        public void SetCursorPosition(int left, int top) => Write($"\x1b[{top + 1};{left + 1}H");
        public void Clear() => Write("\x1b[2J");
        public void Flush() => _inner.Flush();
        public void EnterTuiMode() => _inner.EnterTuiMode();
        public void ExitTuiMode() => _inner.ExitTuiMode();
        public int Width => _inner.Width;
        public int Height => _inner.Height;
        public TerminalCapabilities Capabilities { get; } = new() { SupportsKgp = true };
        public int OutputQueueDepth => 0;
        public System.Threading.Channels.ChannelReader<Hex1bEvent> InputEvents => _inner.InputEvents;
        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => _inner.ReadOutputAsync(ct);
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _inner.WriteInputAsync(data, ct);
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => _inner.ResizeAsync(width, height, ct);
        public event Action? Disconnected { add => _inner.Disconnected += value; remove => _inner.Disconnected -= value; }
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    [TestMethod]
    public async Task LiveImage_UnsupportedHost_RendersFallback()
    {
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(options => options.UseSoftWrapEmission = true,
                ctx => new KgpImageWidget(new byte[] { 255, 128, 0, 255 }, 1, 1, ctx.Text("image fallback")))
            .WithHeadless(new TerminalCapabilities { SupportsKgp = false }).WithDimensions(24, 10).Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var running = terminal.RunAsync(stop.Token);
        try
        {
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("image fallback") && s.KgpPlacements.Count == 0, TimeSpan.FromSeconds(5), "fallback")
            .Ctrl().Key(Hex1bKey.C).Build().ApplyAsync(terminal, stop.Token);
        }
        finally
        {
            await stop.CancelAsync();
            await running;
        }
    }
}
