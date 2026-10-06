using System.Text;
using System.Threading.Channels;
using Hex1b;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
public sealed class FlowInputModeTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task InlineStartup_InitialResizeMuteDoesNotLoseInputMode(bool supportsPaste)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ModeParent(supportsPaste);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await parent.Inner.WriteInputEventAsync(new Hex1bResizeEvent(40, 12), stop.Token);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(_ => new TextBoxWidget(), o => { o.MinHeight = 3; o.MaxHeight = 3; });
            try
            {
                await step.WaitForReadyAsync(stop.Token);
                ready.TrySetResult();
                await finish.Task.WaitAsync(stop.Token);
            }
            finally { await step.CompleteAsync(); }
        }, new Hex1bFlowOptions { InitialCursorRow = 0, UseSoftWrapTombstones = true }, parent);
        var running = runner.RunAsync(stop.Token);
        try
        {
            await parent.ObservationEntered.Task.WaitAsync(stop.Token);
            await ready.Task.WaitAsync(stop.Token);
            await parent.FlushAppliedAsync();
            Assert.AreEqual(supportsPaste, parent.Model.BracketedPasteEnabled,
                "input mode must be delivered while initial resize still mutes replaceable output");
            Assert.IsFalse(parent.AllowObservation.Task.IsCompleted, "the startup mute must remain held during the assertion");
        }
        finally
        {
            parent.AllowObservation.TrySetResult();
            finish.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        await parent.FlushAppliedAsync();
        Assert.IsFalse(parent.Model.BracketedPasteEnabled, "completed inline owner must release its input mode");
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("cancel")]
    [DataRow("failure")]
    [DataRow("failure-cleanup")]
    public async Task InlineTeardown_PendingNativeGeometryCannotDiscardInputModeRelease(string end)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ModeParent(true);
        parent.AllowObservation.TrySetResult();
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var expected = new InvalidOperationException("owned input callback failed");
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(_ => new VStackWidget([
                new TextBlockWidget("MODE-READY"),
                new TextBoxWidget().InputBindings(b => b.Key(Hex1bKey.F4).Action(_ => throw expected))
            ]), o => { o.MinHeight = 3; o.MaxHeight = 3; });
            await step.WaitForReadyAsync(stop.Token);
            ready.TrySetResult(step);
            try { await step.WaitForCompletionAsync(stop.Token); }
            finally { await step.CompleteAsync(); }
        }, new Hex1bFlowOptions { InitialCursorRow = 0, UseSoftWrapTombstones = true }, parent);
        var running = runner.RunAsync(stop.Token);
        FlowStep? active = null;
        try
        {
            active = await ready.Task.WaitAsync(stop.Token);
            await parent.WaitForTextAsync("MODE-READY", stop.Token);
            await parent.FlushAppliedAsync();
            Assert.IsTrue(parent.Model.BracketedPasteEnabled, "positive control: input mode reached the parent before teardown");
            // Model the real host resizing before its queued resize notification:
            // geometry-gated frame/cursor output must now refuse the old rectangle.
            parent.ReportedHeight = 13;
            if (end == "complete")
            {
                await active.CompleteAsync();
                await running.WaitAsync(stop.Token);
            }
            else if (end == "cancel")
            {
                stop.Cancel();
                await Assert.ThrowsAsync<OperationCanceledException>(async () =>
                    await running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                var cleanupFailure = new IOException("body failure followed by mode cleanup failure");
                if (end == "failure-cleanup") parent.ExitFailure = cleanupFailure;
                parent.Inner.SendKey(Hex1bKey.F4);
                if (end == "failure-cleanup")
                {
                    var failure = await Assert.ThrowsExactlyAsync<AggregateException>(async () =>
                        await running.WaitAsync(TimeSpan.FromSeconds(5)));
                    CollectionAssert.AreEquivalent(new Exception[] { expected, cleanupFailure },
                        failure.Flatten().InnerExceptions.ToArray());
                }
                else
                {
                    var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(async () =>
                        await running.WaitAsync(TimeSpan.FromSeconds(5)));
                    Assert.AreSame(expected, failure);
                }
            }
            await parent.FlushAppliedAsync();
            Assert.IsFalse(parent.Model.BracketedPasteEnabled,
                "mode cleanup must reach parent before lifecycle ends even with pending geometry");
        }
        finally
        {
            parent.AllowObservation.TrySetResult();
            if (active is not null && !running.IsCompleted)
            {
                active.Complete();
                try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            }
        }
    }

    [TestMethod]
    public async Task InlineFullScreenInline_EachOwnerEnablesAndReleasesItsOwnMode()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ModeParent(true);
        parent.AllowObservation.TrySetResult();
        var fullStarted = new TaskCompletionSource<Hex1bApp>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            async Task Inline(string marker)
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget(marker), new TextBoxWidget()]),
                    o => { o.MinHeight = 3; o.MaxHeight = 3; });
                try
                {
                    await step.WaitForReadyAsync(stop.Token);
                    await parent.WaitForTextAsync(marker, stop.Token);
                    await parent.FlushAppliedAsync();
                    Assert.IsTrue(parent.Model.BracketedPasteEnabled, marker);
                    Assert.IsFalse(parent.Model.InAlternateScreen, "inline input modes must not enter alternate screen");
                }
                finally { await step.CompleteAsync(); }
                await parent.FlushAppliedAsync();
                Assert.IsFalse(parent.Model.BracketedPasteEnabled, "inline mode leaked beyond owner");
            }
            await Inline("INLINE-A");
            var full = flow.FullScreenStepAsync((app, _) =>
            {
                fullStarted.TrySetResult(app);
                return _ => new VStackWidget([new TextBlockWidget("FULL-OWNER"), new TextBoxWidget()]);
            });
            var fullApp = await fullStarted.Task.WaitAsync(stop.Token);
            try
            {
                await parent.WaitForTextAsync("FULL-OWNER", stop.Token);
                await parent.FlushAppliedAsync();
                Assert.IsTrue(parent.Model.BracketedPasteEnabled);
                Assert.IsTrue(parent.Model.InAlternateScreen, "full-screen owner retains its existing mode path");
            }
            finally { fullApp.RequestStop(); await full.WaitAsync(stop.Token); }
            await parent.FlushAppliedAsync();
            Assert.IsFalse(parent.Model.BracketedPasteEnabled);
            Assert.IsFalse(parent.Model.InAlternateScreen);
            await Inline("INLINE-B");
        }, new Hex1bFlowOptions { InitialCursorRow = 0, UseSoftWrapTombstones = true }, parent);
        await runner.RunAsync(stop.Token);
        await parent.FlushAppliedAsync();
        Assert.IsFalse(parent.Model.BracketedPasteEnabled);
    }

    [TestMethod]
    [DataRow(true, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task InlineModeWriteFailure_AttemptsReleaseAndPreservesExactCauses(bool failEntry, bool failRelease)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ModeParent(true);
        parent.AllowObservation.TrySetResult();
        var entryFailure = new IOException("uncertain mode entry");
        var releaseFailure = new IOException("uncertain mode release");
        parent.EnterFailure = failEntry ? entryFailure : null;
        parent.ExitFailure = failRelease ? releaseFailure : null;
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(_ => new TextBoxWidget(), o => { o.MinHeight = 3; o.MaxHeight = 3; });
            if (!failEntry)
            {
                await step.WaitForReadyAsync(stop.Token);
                await step.CompleteAsync();
            }
            else await step.WaitForCompletionAsync(stop.Token);
        }, new Hex1bFlowOptions { InitialCursorRow = 0, UseSoftWrapTombstones = true }, parent);
        var failure = await Assert.ThrowsAsync<Exception>(async () => await runner.RunAsync(stop.Token));
        var causes = failure is AggregateException aggregate
            ? aggregate.Flatten().InnerExceptions.ToArray() : [failure];
        var expected = new List<Exception>();
        if (failEntry) expected.Add(entryFailure);
        if (failRelease) expected.Add(releaseFailure);
        CollectionAssert.AreEquivalent(expected.ToArray(), causes,
            "retain exact setup and cleanup causes, without replacement or duplicates");
        Assert.AreEqual(1, parent.EntryWrites, "entry is not retried after an uncertain write");
        Assert.AreEqual(1, parent.ReleaseWrites, "release is attempted exactly once even when setup throws");
        await parent.FlushAppliedAsync();
        Assert.IsFalse(parent.Model.BracketedPasteEnabled,
            "the injected write applies bytes before throwing; the release must therefore be parsed");
    }

    [TestMethod]
    public async Task StaticCompletionPage_DoesNotAcquireInteractiveInputModes()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new ModeParent(true);
        parent.AllowObservation.TrySetResult();
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(_ => new TextBoxWidget(), o => { o.MinHeight = 3; o.MaxHeight = 3; });
            await step.WaitForReadyAsync(stop.Token);
            await step.CompleteAsync(_ => new TextBlockWidget("STATIC-COMPLETION"), stop.Token);
        }, new Hex1bFlowOptions { InitialCursorRow = 0, UseSoftWrapTombstones = false }, parent);
        await runner.RunAsync(stop.Token);
        await parent.FlushAppliedAsync();
        Assert.AreEqual(1, parent.EntryWrites, "only the interactive step owns input modes");
        Assert.AreEqual(1, parent.ReleaseWrites, "static page must neither acquire nor release another owner's modes");
        Assert.IsFalse(parent.Model.BracketedPasteEnabled);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ConcreteParent_RequiredModeReleaseFinishesBeforeTerminalStopsOutput(bool cancel)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var callbackPhase = "not entered";
        var testPhase = "before run";
        Exception? primary = null;
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var caps = new TerminalCapabilities { SupportsBracketedPaste = true, SupportsTrueColor = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                callbackPhase = "creating step";
                var step = flow.Step(_ => new TextBoxWidget(), o => { o.MinHeight = 3; o.MaxHeight = 3; });
                try
                {
                    callbackPhase = "waiting step ready";
                    await step.WaitForReadyAsync(flow.CancellationToken);
                    callbackPhase = "waiting finish";
                    ready.TrySetResult();
                    await finish.Task.WaitAsync(flow.CancellationToken);
                }
                finally
                {
                    callbackPhase = "completing step";
                    if (flow.CancellationToken.IsCancellationRequested) await step.CompleteAsync();
                    else await step.CompleteAsync(_ => new TextBlockWidget("normal completion"));
                    callbackPhase = "step completed";
                }
            }, options => options.UseSoftWrapTombstones = true)
            .WithHeadless(caps).WithDimensions(40, 12).Build();
        var parent = (Hex1bAppWorkloadAdapter)terminal.Workload;
        var running = terminal.RunAsync(stop.Token);
        try
        {
            testPhase = "waiting ready";
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
            testPhase = "waiting positive entry barrier";
            await parent.WriteRequiredForProcessing("\u001b[0m").WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsTrue(terminal.BracketedPasteEnabled, "positive control: concrete required-delivery entry was processed");
            testPhase = "waiting terminal completion";
            if (cancel)
            {
                await stop.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            else
            {
                finish.TrySetResult();
                Assert.AreEqual(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            }
            Assert.IsFalse(terminal.BracketedPasteEnabled,
                "RunAsync must not finish or cancel until the concrete parent's required release was processed");
        }
        catch (Exception error)
        {
            primary = new InvalidOperationException(
                $"test={testPhase}; callback={callbackPhase}; mode={terminal.BracketedPasteEnabled}; " +
                $"outputDepth={parent.OutputQueueDepth}; running={running.Status}", error);
            throw primary;
        }
        finally
        {
            finish.TrySetResult();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            catch (Exception cleanupError) when (primary is not null)
            {
                throw new AggregateException(primary, new InvalidOperationException(
                    $"cleanup: callback={callbackPhase}; mode={terminal.BracketedPasteEnabled}; " +
                    $"outputDepth={parent.OutputQueueDepth}; running={running.Status}", cleanupError));
            }
        }
    }

    [TestMethod]
    public async Task ConcreteParent_CompleteWhileModeEntryIsHeldStopsAttachedApp()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        await using var presentation = new HeldModePresentation();
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBoxWidget());
                created.TrySetResult(step);
                await step.WaitForCompletionAsync(flow.CancellationToken);
            }, options => options.UseSoftWrapTombstones = true)
            .WithPresentation(presentation).WithDimensions(40, 12).Build();
        var running = terminal.RunAsync(stop.Token);
        try
        {
            var step = await created.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await presentation.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.IsNotNull(step.AppForDiagnostics, "Step returns an attached owner even while native entry delivery is pending");
            step.Complete();
            Assert.IsFalse(running.IsCompleted, "entry receipt is deliberately held");
            presentation.AllowEntry.TrySetResult();
            Assert.AreEqual(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, presentation.ReleaseWrites);
            Assert.IsFalse(terminal.BracketedPasteEnabled);
        }
        finally
        {
            presentation.AllowEntry.TrySetResult();
            await stop.CancelAsync();
        }
    }

    [TestMethod]
    public async Task ConcreteParent_ModeEntryWriteFailureSettlesWithOriginalCause()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var expected = new IOException("native mode entry write failed");
        await using var presentation = new HeldModePresentation { EntryFailure = expected };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBoxWidget());
                try { await step.WaitForReadyAsync(flow.CancellationToken); }
                finally { await step.CompleteAsync(); }
            }, options => options.UseSoftWrapTombstones = true)
            .WithPresentation(presentation).WithDimensions(40, 12).Build();
        var running = terminal.RunAsync(stop.Token);
        try
        {
            await presentation.EntryStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.AllowEntry.TrySetResult();
            var failure = await Assert.ThrowsAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            static bool ContainsCause(Exception observed, Exception cause) =>
                ReferenceEquals(observed, cause)
                || (observed is AggregateException aggregate && aggregate.InnerExceptions.Any(child => ContainsCause(child, cause)))
                || (observed.InnerException is { } nested && ContainsCause(nested, cause));
            Assert.IsTrue(ContainsCause(failure, expected), "native write failure must survive pump and mode-cleanup errors");
        }
        finally
        {
            presentation.AllowEntry.TrySetResult();
            await stop.CancelAsync();
        }
    }

    private sealed class HeldModePresentation : IHex1bTerminalPresentationAdapter, ICursorPositionSource
    {
        private readonly HeadlessPresentationAdapter inner = new(40, 12);
        public TaskCompletionSource EntryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReleaseWrites { get; private set; }
        public Exception? EntryFailure { get; init; }
        public async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            if (text.Contains("\u001b[?2004h", StringComparison.Ordinal))
            {
                EntryStarted.TrySetResult();
                await AllowEntry.Task.WaitAsync(ct);
                if (EntryFailure is not null) throw EntryFailure;
            }
            if (text.Contains("\u001b[?2004l", StringComparison.Ordinal)) ReleaseWrites++;
        }
        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default) => inner.ReadInputAsync(ct);
        public int Width => 40;
        public int Height => 12;
        public TerminalCapabilities Capabilities { get; } = new() { SupportsBracketedPaste = true, SupportsTrueColor = true };
        public event Action<int, int>? Resized { add { } remove { } }
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken ct) => Task.FromResult<(int Column, int Row)?>((0, 0));
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    // Uses the actual parent queue and terminal parser. Only cursor-observation
    // timing and native geometry visibility are controlled; no renderer is replaced.
    private sealed class ModeParent : IHex1bAppTerminalWorkloadAdapter, ICursorPositionSource, IFlowCurrentGeometrySource
    {
        private readonly object sync = new();
        private readonly StringBuilder output = new();
        private readonly Dictionary<string, TaskCompletionSource> markers = new();
        public Hex1bAppWorkloadAdapter Inner { get; }
        public Hex1bTerminal Model { get; }
        public int? ReportedHeight { get; set; }
        public Exception? EnterFailure { get; set; }
        public Exception? ExitFailure { get; set; }
        public int EntryWrites { get; private set; }
        public int ReleaseWrites { get; private set; }
        public TaskCompletionSource ObservationEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowObservation { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ModeParent(bool supportsPaste)
        {
            var caps = new TerminalCapabilities { SupportsBracketedPaste = supportsPaste, SupportsAlternateScreen = true, SupportsTrueColor = true };
            Inner = new Hex1bAppWorkloadAdapter(caps);
            Model = Hex1bTerminal.CreateBuilder().WithWorkload(Inner).WithHeadless(caps).WithDimensions(40, 12).Build();
        }
        public Task WaitForTextAsync(string marker, CancellationToken ct)
        {
            lock (sync)
            {
                if (output.ToString().Contains(marker, StringComparison.Ordinal)) return Task.CompletedTask;
                if (!markers.TryGetValue(marker, out var signal))
                    markers.Add(marker, signal = new(TaskCreationOptions.RunContinuationsAsynchronously));
                return signal.Task.WaitAsync(ct);
            }
        }
        public Task<NativeDeliveryOutcome> FlushAppliedAsync() => Inner.WriteRequiredForProcessing("\u001b[0m");
        public void Write(string text)
        {
            // A real writer can apply bytes before reporting an error. Preserve
            // that uncertainty; the lifecycle must attempt release regardless.
            Inner.Write(text);
            if (text.Contains("\u001b[?2004h", StringComparison.Ordinal))
            {
                EntryWrites++;
                if (EnterFailure is not null) throw EnterFailure;
            }
            if (text.Contains("\u001b[?2004l", StringComparison.Ordinal))
            {
                ReleaseWrites++;
                if (ExitFailure is not null) throw ExitFailure;
            }
            lock (sync)
            {
                output.Append(text);
                foreach (var (marker, signal) in markers)
                    if (output.ToString().Contains(marker, StringComparison.Ordinal)) signal.TrySetResult();
            }
        }
        public void Write(ReadOnlySpan<byte> bytes) => Write(Encoding.UTF8.GetString(bytes));
        public int Width => Inner.Width;
        public int Height => Inner.Height;
        public TerminalCapabilities Capabilities => Inner.Capabilities;
        public ChannelReader<Hex1bEvent> InputEvents => Inner.InputEvents;
        public int OutputQueueDepth => Inner.OutputQueueDepth;
        public event Action? Disconnected { add => Inner.Disconnected += value; remove => Inner.Disconnected -= value; }
        public void Flush() => Inner.Flush();
        public void EnterTuiMode() => Inner.EnterTuiMode();
        public void ExitTuiMode() => Inner.ExitTuiMode();
        public void Clear() => Inner.Clear();
        public void SetCursorPosition(int left, int top) => Inner.SetCursorPosition(left, top);
        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => Inner.ReadOutputAsync(ct);
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> bytes, CancellationToken ct = default) => Inner.WriteInputAsync(bytes, ct);
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => Inner.ResizeAsync(width, height, ct);
        public (int Width, int Height) ReadCurrentGeometry() => (Width, ReportedHeight ?? Height);
        public async Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken ct)
        {
            ObservationEntered.TrySetResult();
            await AllowObservation.Task.WaitAsync(ct);
            return (0, 0);
        }
        public async ValueTask DisposeAsync() => await Model.DisposeAsync();
    }
}
