using Hex1b.Automation;
using System.Text;
using System.Threading.Channels;
using Hex1b.Events;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class OrderedPasteTests
{
    [TestMethod]
    public async Task OrderedFlow_OuterCancellation_StopsActiveFullScreenOwnerAndReader()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = new List<OrderedPastePhase>();
        Hex1bApp? fullScreen = null;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bFlow(flow => flow.FullScreenStepAsync((app, _) =>
            {
                fullScreen = app;
                return _ => new VStackWidget([new TextBlockWidget("full-cancel-ready"), new TextBoxWidget().OnOrderedPaste(_ =>
                {
                    started.TrySetResult();
                    return update => { if (update.Phase != OrderedPastePhase.Chunk) outcomes.Add(update.Phase); };
                })]);
            })).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("full-cancel-ready"), TimeSpan.FromSeconds(5), "full-screen owner ready")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\u001b[200~");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            TestSeq.AreEqual(new[] { OrderedPastePhase.Shutdown }, outcomes);
            Assert.AreEqual(0, presentation.ReadsInFlight);
        }
        finally
        {
            // Rescue the pre-fix full-screen owner so a behavioral red does not leak it.
            fullScreen?.RequestStop();
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task OrderedFlow_AppDisposalPreservesExternallyOwnedAdapterAndBlockedInput()
    {
        using var cancellation = new CancellationTokenSource();
        using var adapter = new Hex1b.Flow.InlineStepAdapter(40, 6, 0,
            orderedInput: new Hex1b.Input.OrderedInputState(1));
        await using var app = new Hex1bApp(_ => new TextBlockWidget("unused"),
            new Hex1bAppOptions { WorkloadAdapter = adapter, OwnsWorkloadAdapter = false });
        var first = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.A, "a", Hex1b.Input.Hex1bModifiers.None);
        var second = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.B, "b", Hex1b.Input.Hex1bModifiers.None);
        Assert.IsTrue(adapter.TryWriteInputEvent(first));
        var pending = adapter.WriteInputEventAsync(second, cancellation.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted, "The forwarder is waiting on the full inline queue.");
        try
        {
            await app.DisposeAsync();
            Assert.IsTrue(adapter.InputEvents.TryRead(out var retained), "App disposal must leave the outer owner's input queue open and intact.");
            Assert.AreSame(first, retained);
            await pending.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreSame(second, await adapter.InputEvents.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is OperationCanceledException or ChannelClosedException) { }
        }
    }

    [TestMethod]
    public async Task OrderedFlow_InputEof_DoesNotSuppressUnrelatedCallbackCancellation()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var unrelated = new CancellationTokenSource();
        unrelated.Cancel();
        var failure = new OperationCanceledException("unrelated callback cancellation", unrelated.Token);
        var presentation = new InputPresentation();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBlockWidget("cancellation-ready"));
                try { await Task.Delay(Timeout.InfiniteTimeSpan, flow.CancellationToken); }
                catch (OperationCanceledException) when (flow.CancellationToken.IsCancellationRequested) { throw failure; }
                finally { await step.CompleteAsync(); }
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("cancellation-ready"), TimeSpan.FromSeconds(5), "flow ready")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.EndInput();
            var observed = await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(failure, observed);
            Assert.AreEqual(unrelated.Token, observed.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task OrderedFlow_InitialStaticOutput_DoesNotConsumeReservedInputOwner()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var allowHeader = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentation = new InputPresentation();
        var text = "";
        var starts = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(4)
            .WithHex1bFlow(async flow =>
            {
                await allowHeader.Task.WaitAsync(cancellation.Token);
                await flow.ShowAsync(_ => new TextBlockWidget("static-header"));
                var step = flow.Step(_ => new TextBoxWidget().OnOrderedPaste(_ =>
                {
                    starts++;
                    return update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk) text += update.Text;
                        else
                        {
                            Assert.AreEqual(OrderedPastePhase.Completed, update.Phase);
                            finished.TrySetResult();
                        }
                    };
                }));
                try { await finished.Task.WaitAsync(cancellation.Token); }
                finally { await step.CompleteAsync(); }
            }).WithDiagnostics().WithPresentation(presentation).WithDimensions(40, 6).Build();
        presentation.Send("\x1b[200~startup\x1b[201~");
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var admitted = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(_ => terminal.InputMilestones!.AcceptedInput >= 3, TimeSpan.FromSeconds(5), "startup paste admitted before static output")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            allowHeader.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("startup", text);
            Assert.AreEqual(1, starts);
        }
        finally { allowHeader.TrySetResult(); cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedFlow_DisconnectedEventOnly_CancelsBlockedReaderAndJoinsFlow()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var prefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = "";
        var outcomes = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget("disconnect-ready"),
                    new TextBoxWidget().OnOrderedPaste(_ => update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk) { text += update.Text; prefix.TrySetResult(); }
                        else
                        {
                            Assert.AreEqual(OrderedPastePhase.Failed, update.Phase);
                            outcomes++;
                        }
                    })]));
                try { await Task.Delay(Timeout.InfiniteTimeSpan, flow.CancellationToken); }
                finally { await step.CompleteAsync(); }
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("disconnect-ready"), TimeSpan.FromSeconds(5), "flow ready")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~prefix");
            await prefix.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Disconnect(); // Does not close or feed the read channel.
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("prefix", text);
            Assert.AreEqual(1, outcomes);
            Assert.AreEqual(0, presentation.ReadsInFlight);
            Assert.IsFalse(cancellation.IsCancellationRequested);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedPaste_NestedAndEmptyOperations_HaveOneOutcomePerCapturedReceiver()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var prefix = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var payloads = new List<StringBuilder>();
        var outcomes = new List<OrderedPastePhase>();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([new TextBlockWidget("ready"),
                new TextBoxWidget().OnOrderedPaste(_ =>
                {
                    var payload = new StringBuilder();
                    payloads.Add(payload);
                    return update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk)
                        {
                            payload.Append(update.Text);
                            if (payload.ToString() == "prefix") prefix.TrySetResult();
                        }
                        else outcomes.Add(update.Phase);
                    };
                }).OnSubmit(_ => submitted.TrySetResult())]))
            .WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~prefix");
            await prefix.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Send("\x1b[200~new\x1b[201~\x1b[200~\x1b[201~\r");
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            TestSeq.AreEqual(new[] { "prefix", "new", "" }, payloads.Select(value => value.ToString()));
            TestSeq.AreEqual(new[] { OrderedPastePhase.Cancelled, OrderedPastePhase.Completed, OrderedPastePhase.Completed }, outcomes);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedInput_ConcurrentReaderExposureAndConfiguration_NeverOrphansAcceptedInput()
    {
        for (var iteration = 0; iteration < 50; iteration++)
        {
            using var workload = new Hex1bAppWorkloadAdapter();
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var expose = Task.Run(async () => { await start.Task; return workload.InputEvents; });
            var configure = Task.Run(async () =>
            {
                await start.Task;
                try { return Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithOrderedPasteInput(1).WithHeadless().Build(); }
                catch (InvalidOperationException) { return null; }
            });
            start.TrySetResult();
            var reader = await expose.WaitAsync(TimeSpan.FromSeconds(5));
            await using var terminal = await configure.WaitAsync(TimeSpan.FromSeconds(5));
            var first = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.A, "a", Hex1b.Input.Hex1bModifiers.None);
            var second = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.B, "b", Hex1b.Input.Hex1bModifiers.None);
            Assert.IsTrue(workload.TryWriteInputEvent(first));
            Assert.AreEqual(terminal is null, workload.TryWriteInputEvent(second));
            Assert.AreSame(first, await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            if (terminal is null) Assert.AreSame(second, await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
    }

    [TestMethod]
    public async Task OrderedPaste_ReplacementCancelsReceiverBeforeReplacementCleansOldNode()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var original = new TextBoxState { Text = "owned" };
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replaced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var replace = false;
        var outcomes = 0;
        Hex1bApp? app = null;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, instance =>
            {
                app = instance;
                return _ => new VStackWidget([new TextBlockWidget("ready"), replace
                    ? new CleaningReplacementWidget(old =>
                    {
                        Assert.AreEqual(1, outcomes, "Receiver must settle before replacement code cleans old state.");
                        Assert.AreSame(original, ((TextBoxNode)old!).State);
                        original.Text = "cleaned";
                        replaced.TrySetResult();
                    })
                    : new TextBoxWidget().State(original).OnOrderedPaste(_ =>
                    {
                        started.TrySetResult();
                        return update =>
                        {
                            if (update.Phase == OrderedPastePhase.Chunk) return;
                            Assert.AreEqual(OrderedPastePhase.Cancelled, update.Phase);
                            Assert.AreEqual("owned", original.Text);
                            outcomes++;
                        };
                    })]);
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() => replace = true, out var dispatched));
            await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
            await replaced.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("cleaned", original.Text);
            Assert.AreEqual(1, outcomes);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private sealed record CleaningReplacementWidget(Action<Hex1bNode?> BeforeReconcile) : Hex1bWidget
    {
        internal override Type GetExpectedNodeType() => typeof(TextBlockNode);
        internal override Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            if (existingNode is TextBoxNode) BeforeReconcile(existingNode);
            return new TextBlockWidget("replacement").ReconcileAsync(existingNode, context);
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedFlow_InputEof_DrainsReservedOwnerAndCancelsFlowLifetime(bool beforeRun)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var text = "";
        var outcomes = 0;
        var flowCancelled = false;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget("eof-ready"),
                    new TextBoxWidget().OnOrderedPaste(_ => update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk) text += update.Text;
                        else
                        {
                            Assert.AreEqual(OrderedPastePhase.Failed, update.Phase);
                            Assert.IsInstanceOfType<EndOfStreamException>(update.Error);
                            outcomes++;
                        }
                    })]));
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, flow.CancellationToken);
                }
                catch (OperationCanceledException) when (flow.CancellationToken.IsCancellationRequested)
                {
                    flowCancelled = true;
                    throw;
                }
                finally { await step.CompleteAsync(); }
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        if (beforeRun) { presentation.Send("\x1b[200~prefix"); presentation.EndInput(); }
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            if (!beforeRun)
            {
                using var ready = await new Hex1bTerminalInputSequenceBuilder()
                    .WaitUntil(snapshot => snapshot.ContainsText("eof-ready"), TimeSpan.FromSeconds(5), "flow ready")
                    .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
                presentation.Send("\x1b[200~prefix");
                presentation.EndInput();
            }
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("prefix", text);
            Assert.AreEqual(1, outcomes);
            Assert.IsTrue(flowCancelled);
            Assert.IsFalse(cancellation.IsCancellationRequested, "EOF owns completion; the test's outer token remains live.");
            Assert.AreEqual(0, presentation.ReadsInFlight);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedPaste_RebuiltFactoryAndFocusChange_KeepCapturedReceiverAndState()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var first = new TextBoxState();
        var second = new TextBoxState();
        Hex1bApp? app = null;
        var rebuilt = false;
        var retargeted = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, instance =>
            {
                app = instance;
                return _ => new VStackWidget([new TextBlockWidget(rebuilt ? "rebuilt" : "ready"),
                    new TextBoxWidget().State(first).OnOrderedPaste(rebuilt
                        ? _ => { retargeted++; return _ => { }; }
                        : _ =>
                        {
                            started.TrySetResult();
                            return update => { if (update.Phase == OrderedPastePhase.Chunk) first.Text += update.Text; };
                        }),
                    new TextBoxWidget().State(second).OnOrderedPaste(_ => { retargeted++; return _ => { }; })
                        .OnSubmit(_ => submitted.TrySetResult(second.Text))]);
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() =>
            {
                rebuilt = true;
                app.RequestFocus(node => node is TextBoxNode box && ReferenceEquals(box.State, second));
            }, out var dispatched));
            await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
            using var changed = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("rebuilt"), TimeSpan.FromSeconds(5), "factory replacement and focus render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("captured\x1b[201~!\r");
            Assert.AreEqual("!", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("captured", first.Text);
            Assert.AreEqual(0, retargeted);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_LegacyHandlerWithoutExplicitFactory_RefusesActionably(bool textChanged)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var callbacks = 0;
        var state = new TextBoxState();
        var widget = new TextBoxWidget().State(state);
        widget = textChanged ? widget.OnTextChanged(_ => callbacks++) : widget.OnPaste(_ => callbacks++);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([new TextBlockWidget("ready"), widget]))
            .WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~payload\x1b[201~");
            var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            StringAssert.Contains(error.Message, "OnOrderedPaste");
            Assert.AreEqual("", state.Text);
            Assert.AreEqual(0, callbacks);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    [TestMethod]
    public async Task OrderedPaste_ExplicitFactoryOwnsMutation_OrdinaryTextChangedStillRuns()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var state = new TextBoxState();
        var changed = 0;
        var legacyPaste = 0;
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([new TextBlockWidget("ready"),
                new TextBoxWidget().State(state).OnTextChanged(_ => changed++).OnPaste(_ => legacyPaste++)
                    .OnOrderedPaste(_ => update =>
                    {
                        if (update.Phase != OrderedPastePhase.Chunk) return;
                        state.Text += update.Text;
                        state.CursorPosition = state.Text.Length;
                    }).OnSubmit(_ => submitted.TrySetResult(state.Text))]))
            .WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~paste\x1b[201~!\r");
            Assert.AreEqual("paste!", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, changed);
            Assert.AreEqual(0, legacyPaste);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedInput_TrackedRefusalAndCancellation_DoNotConsumeIds()
    {
        var tracker = new Hex1b.Diagnostics.InputMilestoneTracker();
        using var workload = new Hex1bAppWorkloadAdapter { InputMilestones = tracker };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithHeadless().Build();
        var first = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.A, "a", Hex1b.Input.Hex1bModifiers.None);
        var second = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.B, "b", Hex1b.Input.Hex1bModifiers.None);
        Assert.IsTrue(workload.TryWriteInputEvent(first));
        Assert.IsFalse(workload.TryWriteInputEvent(second));
        using var cancel = new CancellationTokenSource();
        var pending = workload.WriteInputEventAsync(second, cancel.Token).AsTask();
        Assert.IsFalse(pending.IsCompleted);
        Assert.AreEqual(1L, tracker.AcceptedInput);
        cancel.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => pending);
        Assert.AreSame(first, await workload.InputEvents.ReadAsync());
        Assert.IsTrue(workload.TryWriteInputEvent(second));
        Assert.AreSame(second, await workload.InputEvents.ReadAsync());
        Assert.AreEqual(2L, tracker.AcceptedInput);
        Assert.AreEqual(1L, tracker.IdOf(first));
        Assert.AreEqual(2L, tracker.IdOf(second));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedInput_ContinuousProducer_AllowsDispatchAndPublishedFrames(bool coalescing)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var producerCancellation = new CancellationTokenSource();
        var presentation = new InputPresentation();
        var active = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hex1bAppWorkloadAdapter? workload = null;
        Hex1bApp? app = null;
        var processed = 0;
        var label = "ready";
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options =>
            {
                options.EnableRescue = false;
                options.EnableInputCoalescing = coalescing;
                workload = (Hex1bAppWorkloadAdapter)options.WorkloadAdapter!;
            }, instance =>
            {
                app = instance;
                return _ => new VStackWidget([new TextBlockWidget(label), new TextBoxWidget()
                    .InputBindings(bindings => bindings.Key(Hex1b.Input.Hex1bKey.F6).Action(_ =>
                    { if (Interlocked.Increment(ref processed) == 100) active.TrySetResult(); }))]);
            }).WithDiagnostics().WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        Task? producer = null;
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            producer = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await workload!.WriteInputEventAsync(new Hex1b.Input.Hex1bKeyEvent(
                            Hex1b.Input.Hex1bKey.F6, "", Hex1b.Input.Hex1bModifiers.None), producerCancellation.Token);
                        await workload.WriteInputEventAsync(new Hex1b.Input.Hex1bResizeEvent(40, 6), producerCancellation.Token);
                    }
                }
                catch (OperationCanceledException) when (producerCancellation.IsCancellationRequested) { }
            });
            await active.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() => label = "dispatch-visible", out var dispatched));
            await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
            using var published = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("dispatch-visible"), TimeSpan.FromSeconds(5), "frame during continuous input")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            Assert.IsFalse(producer.IsCompleted, "Producer remains active through dispatch and publication.");
            Assert.IsTrue(Volatile.Read(ref processed) >= 100);
        }
        finally
        {
            producerCancellation.Cancel();
            if (producer is not null) await producer.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task OrderedInput_DisposedAdapter_RefusesAsyncAdmission()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithHeadless().Build();
        workload.Dispose();
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => workload.WriteInputEventAsync(
            new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.A, "a", Hex1b.Input.Hex1bModifiers.None)).AsTask());
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => workload.WriteInputAsync(Encoding.UTF8.GetBytes("a")).AsTask());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedInput_PasteMarkerInsideControlString_DoesNotStartPaste(bool splitEveryCharacter)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var state = new TextBoxState();
        var starts = 0;
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget("ready"), new TextBoxWidget().State(state).OnOrderedPaste(_ =>
                { starts++; return _ => { }; }).OnSubmit(_ => submitted.TrySetResult(state.Text))
            ])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            const string input = "\x1b]0;inside-\x1b[200~\u0007\x1b_Ginside-\x1b[200~\x1b\\\x1bPinside-\x1b[200~\x1b\\z\r";
            if (splitEveryCharacter) foreach (var character in input) presentation.Send(character.ToString());
            else presentation.Send(input);
            Assert.AreEqual("z", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(0, starts);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedFlow_FullScreenOwnerEnds_QueuedPasteDoesNotReachNextFullScreenOwner()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentation = new InputPresentation();
        var nextStarts = 0;
        var nextText = "";
        Hex1bApp? currentApp = null;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(4)
            .WithHex1bFlow(async flow =>
            {
                await flow.FullScreenStepAsync((app, _) =>
                {
                    currentApp = app;
                    return _ => new VStackWidget([new TextBlockWidget("full-a"), new TextBoxWidget().OnOrderedPaste(_ => update =>
                    {
                        if (update.Phase != OrderedPastePhase.Chunk) return;
                        held.TrySetResult();
                        if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Release old full-screen owner.");
                        app.RequestStop();
                    })]);
                });
                await flow.FullScreenStepAsync((app, _) =>
                {
                    currentApp = app;
                    nextReady.TrySetResult();
                    return _ => new VStackWidget([new TextBlockWidget("full-b"), new TextBoxWidget().OnOrderedPaste(_ =>
                    {
                        nextStarts++;
                        return update =>
                        {
                            if (update.Phase == OrderedPastePhase.Chunk) nextText += update.Text;
                            else if (update.Phase == OrderedPastePhase.Completed) finished.TrySetResult();
                        };
                    })]);
                });
            }).WithDiagnostics().WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("full-a"), TimeSpan.FromSeconds(5), "first full-screen owner ready")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~hold");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Send("\x1b[201~\x1b[200~\x1b[201~");
            using var queued = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(_ => terminal.InputMilestones!.AcceptedInput >= 5, TimeSpan.FromSeconds(5), "old paste admitted")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            release.Set();
            await nextReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var next = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("full-b"), TimeSpan.FromSeconds(5), "second full-screen rendered")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~fresh\x1b[201~");
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, nextStarts);
            Assert.AreEqual("fresh", nextText);
            currentApp!.RequestStop();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); currentApp?.RequestStop(); cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_EmbeddedAnsiAndControls_AreLiteralPayload(bool splitEveryCharacter)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        const string payload = "a\u001b[14~b\u001bODc\u001b]0;clipboard title\u0007d\r\n\t\u0003\u001b[20X";
        var text = "";
        var commands = 0;
        var completed = 0;
        var submitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget("ready"), new TextBoxWidget().OnOrderedPaste(_ => update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk) text += update.Text;
                    else { Assert.AreEqual(OrderedPastePhase.Completed, update.Phase); completed++; }
                }).InputBindings(bindings => bindings.Key(Hex1b.Input.Hex1bKey.F4).Action(_ => commands++))
                .OnSubmit(_ => submitted.TrySetResult())
            ])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            var input = "\x1b[200~" + payload + "\x1b[201~\r";
            if (splitEveryCharacter) foreach (var character in input) presentation.Send(character.ToString());
            else presentation.Send(input);
            await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(payload, text);
            Assert.AreEqual(0, commands);
            Assert.AreEqual(1, completed);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedFlow_QueuedOldPastesNeverRetargetNextStep_AndFreshPasteWorks()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var nextReady = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var freshDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentation = new InputPresentation();
        var firstStarts = 0;
        var firstEnds = 0;
        var nextStarts = 0;
        var nextText = "";
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(4)
            .WithHex1bFlow(async flow =>
            {
                var first = flow.Step(ctx => new VStackWidget([new TextBlockWidget("step-a"),
                    new TextBoxWidget().OnOrderedPaste(_ =>
                    {
                        firstStarts++;
                        return update =>
                        {
                            if (update.Phase != OrderedPastePhase.Chunk) { firstEnds++; return; }
                            held.TrySetResult();
                            if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Release old owner.");
                            ctx.Step.Complete();
                        };
                    })]));
                await first.WaitForCompletionAsync(cancellation.Token);
                var next = flow.Step(_ => new VStackWidget([new TextBlockWidget("step-b"),
                    new TextBoxWidget().OnOrderedPaste(_ =>
                    {
                        nextStarts++;
                        return update =>
                        {
                            if (update.Phase == OrderedPastePhase.Chunk) nextText += update.Text;
                            else if (update.Phase == OrderedPastePhase.Completed) freshDone.TrySetResult();
                        };
                    })]));
                await next.WaitForReadyAsync(cancellation.Token);
                nextReady.TrySetResult();
                try { await finish.Task.WaitAsync(cancellation.Token); }
                finally { await next.CompleteAsync(); }
            }).WithDiagnostics().WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("step-a"), TimeSpan.FromSeconds(5), "first owner ready")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~hold");
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Send("\x1b[201~" + string.Concat(Enumerable.Repeat("\x1b[200~\x1b[201~", 4)));
            // Begin+chunk+End+four empty pastes: eleven accepted events before ending A.
            using var queued = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(_ => terminal.InputMilestones!.AcceptedInput >= 11,
                    TimeSpan.FromSeconds(5), "old-epoch paste begins admitted")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            release.Set();
            await nextReady.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Send("\x1b[200~fresh\x1b[201~");
            await freshDone.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(1, firstStarts);
            Assert.AreEqual(1, firstEnds);
            Assert.AreEqual(1, nextStarts, "Unstarted old paste identities must not capture the next editor.");
            Assert.AreEqual("fresh", nextText);
            foreach (var milestone in new[] { Hex1b.Diagnostics.DiagnosticMilestone.InputProcessed, Hex1b.Diagnostics.DiagnosticMilestone.FramePublished })
            {
                var abandoned = await terminal.InputMilestones!.WaitAsync(milestone, 10,
                    TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
                Assert.AreEqual(Hex1b.Diagnostics.DiagnosticOutcome.Failed, abandoned.Outcome);
                Assert.AreEqual("ordered-paste-owner-ended", abandoned.Code);
                StringAssert.Contains(abandoned.Message!, "captured Flow input owner");
                Assert.IsNull(abandoned.Frame);
            }
            finish.TrySetResult();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally { release.Set(); finish.TrySetResult(); cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedPaste_ExternalCancelWhileOwnerHeld_DoesNotOverlapOwnerAndPublishesOutcome()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim();
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var started = new TaskCompletionSource<OrderedPasteStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var presentation = new InputPresentation();
        Hex1bApp? app = null;
        var label = "ready";
        var outcomes = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(options => options.EnableRescue = false, instance =>
            {
                app = instance;
                return _ => new VStackWidget([new TextBlockWidget(label), new TextBoxWidget().OnOrderedPaste(start =>
                {
                    started.TrySetResult(start);
                    return update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk) return;
                        Assert.AreEqual(OrderedPastePhase.Cancelled, update.Phase);
                        Interlocked.Increment(ref outcomes);
                        label = "cancelled-visible";
                        finished.TrySetResult();
                    };
                })]);
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            var identity = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() =>
            {
                held.TrySetResult();
                if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Owner must be released by test.");
            }, out var dispatched));
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Cancel synchronously returns while the owner is provably inside another callback.
            identity.Cancel();
            Assert.AreEqual(0, Volatile.Read(ref outcomes));
            Assert.IsFalse(finished.Task.IsCompleted);
            release.Set();
            await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var published = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("cancelled-visible"), TimeSpan.FromSeconds(5), "cancel outcome rendered without input")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            Assert.AreEqual(1, outcomes);
        }
        finally { release.Set(); cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedPaste_StateRebind_TerminatesCapturedReceiverBeforeReplacingState()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var original = new TextBoxState();
        var replacement = new TextBoxState { Text = "replacement" };
        var current = original;
        Hex1bApp? app = null;
        TextBoxNode? capturedNode = null;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<OrderedPastePhase>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = 0;
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, instance =>
            {
                app = instance;
                return _ => new VStackWidget([new TextBlockWidget("ready"),
                    new TextBoxWidget().State(current).OnOrderedPaste(_ =>
                    {
                        capturedNode = FindTextBox(app.RootNode!);
                        started.TrySetResult();
                        return update =>
                        {
                            if (update.Phase == OrderedPastePhase.Chunk) original.Text += update.Text;
                            else
                            {
                                Assert.AreSame(original, capturedNode!.State,
                                    "Cancellation must precede rebinding the captured node to different state.");
                                outcomes++;
                                ended.TrySetResult(update.Phase);
                            }
                        };
                    }).OnSubmit(_ => submitted.TrySetResult(current.Text))]);
            }).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() => current = replacement, out var dispatched));
            await dispatched.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual(OrderedPastePhase.Cancelled, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            presentation.Send("late\x1b[201~\r");
            Assert.AreEqual("replacement", await submitted.Task.WaitAsync(TimeSpan.FromSeconds(5)),
                "The later submit proves the old suffix and closing delimiter were consumed first.");
            Assert.AreEqual("", original.Text);
            Assert.AreEqual("replacement", replacement.Text);
            Assert.AreEqual(1, outcomes);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    private static TextBoxNode? FindTextBox(Hex1bNode node)
        => node as TextBoxNode ?? node.GetChildren().Select(FindTextBox).FirstOrDefault(child => child is not null);

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_CleanInputEof_DrainsPrefixAndTerminatesOnce(bool closingDelimiter)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var received = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<OrderedPasteUpdate>(TaskCreationOptions.RunContinuationsAsynchronously);
        var text = "";
        var outcomes = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget("ready"), new TextBoxWidget().OnOrderedPaste(_ => update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk)
                    { text += update.Text; received.TrySetResult(); }
                    else { outcomes++; ended.TrySetResult(update); }
                })])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~prefix" + (closingDelimiter ? "\x1b[201~" : ""));
            await received.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.EndInput();
            var outcome = await ended.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await running.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.AreEqual("prefix", text);
            Assert.AreEqual(1, outcomes);
            Assert.AreEqual(closingDelimiter ? OrderedPastePhase.Completed : OrderedPastePhase.Failed, outcome.Phase);
            if (closingDelimiter) Assert.IsNull(outcome.Error);
            else Assert.IsInstanceOfType<EndOfStreamException>(outcome.Error);
            Assert.AreEqual(0, presentation.ReadsInFlight);
        }
        finally { cancellation.Cancel(); await running.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [TestMethod]
    public async Task OrderedInput_ReusedBuilderWithUnsupportedWorkload_RejectsBeforePresentationCreation()
    {
        await using var workload = new StreamWorkloadAdapter(Stream.Null, Stream.Null);
        var presentationCreated = false;
        var builder = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bApp(_ => new TextBlockWidget("unused"))
            .WithWorkload(workload);
        builder.SetPresentationFactory((_, _) =>
        {
            presentationCreated = true;
            return new InputPresentation();
        });
        Assert.ThrowsExactly<InvalidOperationException>(() => builder.Build());
        Assert.IsFalse(presentationCreated, "Invalid ordered workloads must fail before resource construction.");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_100KiBThenImmediateKeyAndSubmit_DeliversExactTextOnOwner(bool unicode)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var state = new TextBoxState();
        var submitted = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = 0;
        var ends = 0;
        var chunks = 0;
        var payload = unicode ? string.Concat(Enumerable.Repeat("a🙂漢e\u0301", 17067)) : new string('p', 102400);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget("ready"),
                new TextBoxWidget().State(state).OnOrderedPaste(start =>
                {
                    starts++;
                    return update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk)
                        {
                            Assert.IsTrue(update.Text.Length <= 4096);
                            Assert.IsFalse(char.IsLowSurrogate(update.Text[0]));
                            Assert.IsFalse(char.IsHighSurrogate(update.Text[^1]));
                            chunks++;
                            state.Text = state.Text.Insert(state.CursorPosition, update.Text);
                            state.CursorPosition += update.Text.Length;
                        }
                        else
                        {
                            Assert.AreEqual(OrderedPastePhase.Completed, update.Phase);
                            ends++;
                        }
                    };
                }).OnSubmit(_ => submitted.TrySetResult(state.Text))
            ])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            // One native byte delivery; no render barrier between paste, key and submit.
            presentation.Send("\x1b[200~" + payload + "\x1b[201~!\r");
            var actual = await submitted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.AreEqual(1, starts, "The configured owner factory must receive this paste.");
            Assert.AreEqual(1, ends);
            Assert.IsTrue(chunks >= 25);
            Assert.AreEqual(payload + "!", actual);
            Assert.AreEqual(payload.Length + 1, state.CursorPosition);
            Assert.IsFalse(state.HasSelection);
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task OrderedInput_FullQueue_BackpressuresAsyncAndRefusesSyncWithoutLoss()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithHeadless().WithDimensions(40, 6).Build();
        var first = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.A, "a", Hex1b.Input.Hex1bModifiers.None);
        var second = new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.B, "b", Hex1b.Input.Hex1bModifiers.None);
        Assert.IsTrue(workload.TryWriteInputEvent(first));
        Assert.IsFalse(workload.TryWriteInputEvent(second), "Bounded admission must refuse a full channel.");
        Assert.ThrowsExactly<InvalidOperationException>(() => workload.SendKey(Hex1b.Input.Hex1bKey.C));
        var pending = workload.WriteInputEventAsync(second).AsTask();
        Assert.IsFalse(pending.IsCompleted, "Async producers must await capacity without dropping input.");
        Assert.AreSame(first, await workload.InputEvents.ReadAsync());
        await pending.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreSame(second, await workload.InputEvents.ReadAsync());
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
    }

    [TestMethod]
    public async Task OrderedPaste_ExternalCancellationWithoutFurtherInput_CompletesOnOwnerOnce()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var started = new TaskCompletionSource<OrderedPasteStart>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = new TaskCompletionSource<OrderedPastePhase>(TaskCreationOptions.RunContinuationsAsynchronously);
        var outcomes = 0;
        var label = "ready";
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([new TextBlockWidget(label), new TextBoxWidget().OnOrderedPaste(start =>
            {
                label = "started";
                started.TrySetResult(start);
                return update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk) return;
                    outcomes++;
                    finished.TrySetResult(update.Phase);
                };
            })])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            var start = await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var published = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("started"), TimeSpan.FromSeconds(5), "begin has rendered")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            start.Cancel();
            Assert.AreEqual(OrderedPastePhase.Cancelled, await finished.Task.WaitAsync(TimeSpan.FromSeconds(5)));
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        Assert.AreEqual(1, outcomes, "Shutdown must not repeat a cancellation outcome.");
    }

    [TestMethod]
    public async Task OrderedPaste_ThrowingShutdownReceiver_StillExitsAlternateScreenOnce()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var label = "ready";
        var outcomes = 0;
        var failure = new InvalidOperationException("shutdown receiver failed");
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget(label), new TextBoxWidget().OnOrderedPaste(start =>
                {
                    label = "started";
                    started.TrySetResult();
                    return update =>
                    {
                        if (update.Phase == OrderedPastePhase.Chunk) return;
                        outcomes++;
                        Assert.AreEqual(OrderedPastePhase.Shutdown, update.Phase);
                        throw failure;
                    };
                })])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~");
            await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var published = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("started"), TimeSpan.FromSeconds(5), "begin published")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            var before = presentation.Output.Count;
            cancellation.Cancel();
            var observed = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(failure, observed);
            Assert.AreEqual(1, outcomes);
            Assert.IsTrue(string.Concat(presentation.Output.Skip(before)).Contains("\x1b[?1049l"),
                "Application cleanup must exit alternate screen even when its terminal paste callback throws.");
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    [TestMethod]
    public async Task OrderedFlow_ResizeDuringBusyDiagnosticTurnWithEmptyInput_StillReflows()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(1)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBlockWidget($"width={flow.TerminalWidth}"));
                await step.WaitForReadyAsync();
                try { await finish.Task.WaitAsync(cancellation.Token); }
                finally { step.Complete(); }
            }, options => options.UseSoftWrapTombstones = true)
            .WithDiagnostics("ordered-geometry", forceEnable: true)
            .WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        Task? holder = null;
        try
        {
            using var initial = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("width=40"), TimeSpan.FromSeconds(5), "initial geometry")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            var tracker = terminal.InputMilestones!;
            holder = Task.Run(async () =>
            {
                await tracker.WaitForSendTurnAsync(cancellation.Token);
                using var turn = tracker.BeginNativeTurn();
                held.TrySetResult();
                await release.Task.WaitAsync(cancellation.Token);
            });
            await held.Task.WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Resize(30, 6);
            using var resized = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("width=30"), TimeSpan.FromSeconds(5), "resize while send turn remains held")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            Assert.IsFalse(holder.IsCompleted, "Geometry must progress without input/send-turn release.");
        }
        finally
        {
            release.TrySetResult();
            finish.TrySetResult();
            if (holder is not null) await holder.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [TestMethod]
    public async Task OrderedInput_StaleEscapeTimer_CannotFlushNewerIncompleteSequence()
    {
        var presentation = new InputPresentation();
        using var clock = new EscapeClock();
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithTimeProvider(clock).WithPresentation(presentation)
            .WithDimensions(40, 6).Build();
        presentation.Send("\x1b");
        var firstTimer = await clock.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        presentation.Send("a\x1b");
        _ = await clock.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        // Model a callback already queued by the old timer when it was disarmed.
        firstTimer.Fire();
        presentation.Send("[A");
        var first = TestSeq.IsType<Hex1b.Input.Hex1bKeyEvent>(await workload.InputEvents.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(Hex1b.Input.Hex1bKey.A, first.Key);
        Assert.IsTrue(first.Alt);
        var second = TestSeq.IsType<Hex1b.Input.Hex1bKeyEvent>(await workload.InputEvents.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(Hex1b.Input.Hex1bKey.UpArrow, second.Key, "Old timeout must not turn the newer arrow prefix into Escape.");
    }

    [TestMethod]
    public async Task OrderedPaste_InputSourceFails_ReportsFailedOnce()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var label = "ready";
        var outcomes = new List<OrderedPasteUpdate>();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => options.EnableRescue = false, _ => new VStackWidget([
                new TextBlockWidget(label), new TextBoxWidget().OnOrderedPaste(start =>
                {
                    label = "started";
                    return update => { if (update.Phase != OrderedPastePhase.Chunk) outcomes.Add(update); };
                })])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~partial");
            using var started = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("started"), TimeSpan.FromSeconds(5), "active paste")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Fail(new IOException("input source broke"));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            var outcome = TestSeq.Single(outcomes);
            Assert.AreEqual(OrderedPastePhase.Failed, outcome.Phase);
            Assert.IsNotNull(outcome.Error);
            StringAssert.Contains(outcome.Error.ToString(), "input source broke");
            Assert.AreEqual(0, presentation.ReadsInFlight);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    [TestMethod]
    public async Task OrderedInput_Dispose_JoinsBlockedReaderBeforePresentationDisposal()
    {
        var presentation = new InputPresentation();
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithPresentation(presentation).WithDimensions(40, 6).Build();
        await presentation.ReadStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await terminal.DisposeAsync();
        Assert.AreEqual(0, presentation.ReadsAtDisposal,
            "The owned input reader must finish before its presentation is disposed.");
        Assert.AreEqual(0, presentation.ReadsInFlight);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task OrderedPaste_PrimaryAndTerminalCallbackFail_PreservesBoth(bool chunkFails)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        var primary = new IOException("primary input failure");
        var secondary = new InvalidOperationException("terminal receiver failure");
        var outcomes = 0;
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hex1bAppWorkloadAdapter? workload = null;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(options => { options.EnableRescue = false; workload = (Hex1bAppWorkloadAdapter)options.WorkloadAdapter!; }, _ => new VStackWidget([
                new TextBlockWidget("ready"), new TextBoxWidget().OnOrderedPaste(start =>
                {
                    started.TrySetResult();
                    return update =>
                    {
                    if (update.Phase == OrderedPastePhase.Chunk) { if (chunkFails) throw primary; return; }
                    outcomes++;
                    throw secondary;
                    };
                }).InputBindings(bindings => bindings.Key(Hex1b.Input.Hex1bKey.F4).Action(_ => { throw primary; }))
            ])).WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~" + (chunkFails ? "x" : ""));
            if (!chunkFails)
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                await workload!.WriteInputEventAsync(new Hex1b.Input.Hex1bKeyEvent(
                    Hex1b.Input.Hex1bKey.F4, "", Hex1b.Input.Hex1bModifiers.None));
            }
            var failure = await Assert.ThrowsExactlyAsync<AggregateException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            TestSeq.AreEqual(new Exception[] { primary, secondary }, failure.Flatten().InnerExceptions);
            Assert.AreEqual(1, outcomes);
        }
        finally
        {
            cancellation.Cancel();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception error) when (error is IOException or InvalidOperationException or AggregateException) { }
        }
    }

    [TestMethod]
    public async Task OrderedInput_PreviouslyExposedEmptyReader_RefusesConfigurationWithoutOrphaning()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        var reader = workload.InputEvents;
        Hex1bTerminal? created = null;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => created = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload).WithOrderedPasteInput(1).WithHeadless().WithDimensions(40, 6).Build());
            workload.SendKey(Hex1b.Input.Hex1bKey.A, 'a');
            var key = TestSeq.IsType<Hex1b.Input.Hex1bKeyEvent>(await reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual("a", key.Text);
        }
        finally { if (created is not null) await created.DisposeAsync(); }
    }

    [TestMethod]
    public async Task OrderedInput_FullRawQueueAndLateOldTimeout_PreservesCurrentTimeoutWithoutMoreData()
    {
        var presentation = new InputPresentation();
        using var clock = new EscapeClock { HoldArmNumber = 2 };
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithOrderedPasteInput(1).WithTimeProvider(clock).WithPresentation(presentation)
            .WithDimensions(40, 6).Build();
        try
        {
            presentation.Send("\x1b");
            var oldTimer = await clock.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            presentation.Send("a\x1b");
            var currentTimer = await clock.Armed.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            await clock.ArmHeld.Task.WaitAsync(TimeSpan.FromSeconds(5));
            oldTimer.Fire(); // Occupies the sole raw queue slot while the parser is held.
            currentTimer.Fire(); // Must retain this newer timeout when admission refuses.
            oldTimer.Fire(); // Must not overwrite the retained newer generation.
            clock.ReleaseArm.Set();
            var first = TestSeq.IsType<Hex1b.Input.Hex1bKeyEvent>(await workload.InputEvents.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(Hex1b.Input.Hex1bKey.A, first.Key);
            Assert.IsTrue(first.Alt);
            var second = TestSeq.IsType<Hex1b.Input.Hex1bKeyEvent>(await workload.InputEvents.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(Hex1b.Input.Hex1bKey.Escape, second.Key);
            Assert.IsFalse(workload.InputEvents.TryRead(out _));
        }
        finally { clock.ReleaseArm.Set(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_ParserAheadThenTransportFails_PreservesCausalOutcome(bool ownerAlreadyCompleted)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var release = new ManualResetEventSlim(ownerAlreadyCompleted);
        var presentation = new InputPresentation();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new TaskCompletionSource<OrderedPastePhase>(TaskCreationOptions.RunContinuationsAsynchronously);
        var ownerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration registration = default;
        var registered = false;
        var outcomes = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(8)
            .WithHex1bApp(options => options.EnableRescue = false, ctx =>
            {
                if (!registered) { registered = true; registration = ctx.CancellationToken.Register(() => ownerCancelled.TrySetResult()); }
                return new VStackWidget([new TextBlockWidget("ready"), new TextBoxWidget().OnOrderedPaste(start => update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk)
                    {
                        entered.TrySetResult();
                        Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(10)), "Test must release held owner.");
                    }
                    else { outcomes++; ended.TrySetResult(update.Phase); }
                })]);
            }).WithDiagnostics("ordered-failure", forceEnable: true)
            .WithPresentation(presentation).WithDimensions(40, 6).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("\x1b[200~x\x1b[201~\x1b[15~");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var admitted = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(_ => terminal.InputMilestones!.AcceptedInput >= 4, TimeSpan.FromSeconds(5), "parser admitted End and successor key")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            Assert.AreEqual("paste-completed", terminal.InputMilestones!.Record(3)!.Kind);
            if (ownerAlreadyCompleted) Assert.AreEqual(OrderedPastePhase.Completed, await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            presentation.Fail(new IOException("after parsed End"));
            await ownerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            release.Set();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(ownerAlreadyCompleted ? OrderedPastePhase.Completed : OrderedPastePhase.Failed,
                await ended.Task.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreEqual(1, outcomes);
        }
        finally
        {
            release.Set(); cancellation.Cancel(); registration.Dispose();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); } catch (InvalidOperationException) { }
        }
    }

    private sealed class EscapeClock : TimeProvider, IDisposable
    {
        public Channel<EscapeTimer> Armed { get; } = Channel.CreateUnbounded<EscapeTimer>();
        public int HoldArmNumber { get; init; }
        public TaskCompletionSource ArmHeld { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim ReleaseArm { get; } = new();
        private int _arms;
        public void Dispose() => ReleaseArm.Dispose();
        private void Arm(EscapeTimer timer)
        {
            var number = Interlocked.Increment(ref _arms);
            Armed.Writer.TryWrite(timer);
            if (number != HoldArmNumber) return;
            ArmHeld.TrySetResult();
            if (!ReleaseArm.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Test must release held timer registration.");
        }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new EscapeTimer(this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }
        public sealed class EscapeTimer(EscapeClock clock, TimerCallback callback, object? state) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                if (dueTime != Timeout.InfiniteTimeSpan) clock.Arm(this);
                return true;
            }
            public void Fire() => callback(state);
            public void Dispose() { }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private sealed class InputPresentation : IHex1bTerminalPresentationAdapter
    {
        private readonly Channel<ReadOnlyMemory<byte>> _input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public System.Collections.Concurrent.ConcurrentQueue<string> Output { get; } = new();
        public int Width { get; private set; } = 40;
        public int Height { get; private set; } = 6;
        public TerminalCapabilities Capabilities { get; } = new();
        public event Action<int, int>? Resized;
        public void Resize(int width, int height) { Width = width; Height = height; Resized?.Invoke(width, height); }
        public event Action? Disconnected;
        public void Disconnect() => Disconnected?.Invoke();
        public void Send(string text) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public TaskCompletionSource ReadStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int ReadsAtDisposal { get; private set; } = -1;
        private int _readsInFlight;
        public int ReadsInFlight => Volatile.Read(ref _readsInFlight);
        public void EndInput() => _input.Writer.TryWrite(ReadOnlyMemory<byte>.Empty);
        public void Fail(Exception error) => _input.Writer.TryComplete(error);
        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _readsInFlight);
            ReadStarted.TrySetResult();
            try { return await _input.Reader.ReadAsync(ct); }
            finally { Interlocked.Decrement(ref _readsInFlight); }
        }
        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) { Output.Enqueue(Encoding.UTF8.GetString(data.Span)); return ValueTask.CompletedTask; }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public ValueTask DisposeAsync() { ReadsAtDisposal = ReadsInFlight; _input.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
