using System.Text;
using System.Threading.Channels;
using Hex1b.Input;
using Hex1b.Flow;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
public sealed class FlowStartupResizeTests
{
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public async Task InitialResize_TransientMissingCursorBeforeModeEntryDoesNotLoseVisibleStartup(bool resize, bool cursorUnavailableUntilEntry, bool multiple)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var presentation = new StartupPresentation { CursorUnavailableUntilEntry = cursorUnavailableUntilEntry };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBlockWidget("STARTUP-READY"),
                    options => { options.MinHeight = 3; options.MaxHeight = 3; });
                created.TrySetResult(step);
                await step.WaitForCompletionAsync(flow.CancellationToken);
            }, options => options.UseSoftWrapTombstones = true)
            .WithPresentation(presentation).WithDimensions(40, 12).Build();
        // The resize notification is already queued when Flow creates its live owner;
        // placing it behind held mode output would test queue serialization instead.
        if (resize) presentation.Resize(50, 13);
        if (multiple) presentation.Resize(60, 14);
        var running = terminal.RunAsync(stop.Token);
        FlowStep? active = null;
        try
        {
            active = await created.Task.WaitAsync(stop.Token);
            await presentation.EntryStarted.Task.WaitAsync(stop.Token);
            if (resize)
            {
                while (active.TerminalWidth != (multiple ? 60 : 50)) await Task.Delay(10, stop.Token);
                // Give already-admitted work a bounded opportunity to expose the old
                // premature observation. A repaired path may defer it until release;
                // do not require the faulty observation to let startup proceed.
                await Task.WhenAny(presentation.ObservationReturned.Task, Task.Delay(200, stop.Token));
            }
            presentation.AllowEntry.TrySetResult();
            var visible = await Task.WhenAny(presentation.ReadyPresented.Task, Task.Delay(2000, stop.Token));
            Assert.AreSame(presentation.ReadyPresented.Task, visible,
                $"startup must become visible after native readiness; commit eligible={active.CanCommit}");
            Assert.IsTrue(active.CanCommit, "visible startup remains eligible after required entry acknowledgement");
            Assert.AreEqual(resize ? (multiple ? 60 : 50) : 40, active.TerminalWidth,
                "latest admitted startup geometry survives release");
            await active.CompleteAsync();
            Assert.AreEqual(0, await running.WaitAsync(stop.Token));
        }
        finally
        {
            presentation.AllowEntry.TrySetResult();
            active?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    [DataRow("complete")]
    [DataRow("cancel")]
    [DataRow("entry-failure")]
    public async Task PendingStartupResize_TeardownJoinsAndPreservesOriginalCause(string ending)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var expected = new IOException("required startup mode failed");
        await using var presentation = new StartupPresentation
        {
            CursorUnavailableUntilEntry = true,
            EntryFailure = ending == "entry-failure" ? expected : null
        };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBlockWidget("STARTUP-READY"));
                created.TrySetResult(step);
                await step.WaitForCompletionAsync(flow.CancellationToken);
            }, options => options.UseSoftWrapTombstones = true)
            .WithPresentation(presentation).WithDimensions(40, 12).Build();
        presentation.Resize(50, 13);
        var running = terminal.RunAsync(stop.Token);
        try
        {
            var step = await created.Task.WaitAsync(stop.Token);
            await presentation.EntryStarted.Task.WaitAsync(stop.Token);
            while (step.TerminalWidth != 50) await Task.Delay(10, stop.Token);
            if (ending == "complete") step.Complete();
            if (ending == "cancel") await stop.CancelAsync();
            presentation.AllowEntry.TrySetResult();
            if (ending == "complete") Assert.AreEqual(0, await running.WaitAsync(TimeSpan.FromSeconds(5)));
            else if (ending == "cancel")
                await Assert.ThrowsAsync<OperationCanceledException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            else
            {
                var failure = await Assert.ThrowsAsync<Exception>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
                static bool HasCause(Exception error, Exception cause) => ReferenceEquals(error, cause)
                    || error is AggregateException aggregate && aggregate.InnerExceptions.Any(child => HasCause(child, cause))
                    || error.InnerException is { } nested && HasCause(nested, cause);
                Assert.IsTrue(HasCause(failure, expected), "native mode failure survives teardown");
            }
            // Only the normal completion control has a working output transport.
            // The other cases intentionally interrupt its required mode write;
            // their contract is joined teardown and the original failure, not a
            // successful native cleanup write through the failed transport.
            if (ending == "complete")
                Assert.IsFalse(terminal.BracketedPasteEnabled, "normal close delivers mode release before joining");
        }
        finally
        {
            presentation.AllowEntry.TrySetResult();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (running.IsCompleted) { /* asserted original outcome above */ }
        }
    }

    [TestMethod]
    public async Task PostStartupResize_MissingCursorStillSuspends()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var presentation = new StartupPresentation();
        presentation.AllowEntry.TrySetResult();
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new TextBlockWidget("STARTUP-READY"));
                created.TrySetResult(step);
                await step.WaitForCompletionAsync(flow.CancellationToken);
            }, options => options.UseSoftWrapTombstones = true)
            .WithPresentation(presentation).WithDimensions(40, 12).Build();
        var running = terminal.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            await presentation.ReadyPresented.Task.WaitAsync(stop.Token);
            Assert.IsTrue(step.CanCommit);
            presentation.CursorUnavailable = true;
            presentation.Resize(50, 13);
            while (step.CanCommit) await Task.Delay(10, stop.Token);
            Assert.AreEqual(50, step.TerminalWidth);
            Assert.IsTrue(presentation.ObservationReturned.Task.IsCompleted,
                "post-start failure actually consulted the authoritative native source");
            await step.CompleteAsync();
            Assert.AreEqual(0, await running.WaitAsync(stop.Token));
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task GenericParent_PublishedAppDoesNotStartFrameDeadlineBeforeModeWriteReturns()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        await using var parent = new BlockingModeParent();
        await parent.Inner.WriteRequiredForProcessing("\u001b[1;1HHOST-PREFIX-A\u001b[2;1HHOST-PREFIX-B\u001b[3;1H");
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(_ => new TextBlockWidget("STARTUP-READY"), o => { o.MinHeight = 3; o.MaxHeight = 3; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { InitialCursorRow = 2, UseSoftWrapTombstones = true }, parent);
        // Generic Write is synchronous. A separate caller thread permits actual
        // Flow input-pump progress while native mode delivery is blocked.
        var running = Task.Run(() => runner.RunAsync(stop.Token), stop.Token);
        FlowStep? active = null;
        try
        {
            await parent.EntryStarted.Task.WaitAsync(stop.Token);
            await parent.Model.ResizeWithWorkloadAsync(50, 13, stop.Token);
            await parent.Model.ResizeWithWorkloadAsync(60, 14, stop.Token);
            // This supported-adapter scheduling control deliberately exceeds the
            // existing 500ms frame deadline. It is not a Windows scheduler replay.
            await Task.Delay(900, stop.Token);
            parent.AllowEntry.Set();
            active = await created.Task.WaitAsync(stop.Token);
            var deadline = Environment.TickCount64 + 2000;
            while (!parent.Model.GetScreenText().Contains("STARTUP-READY", StringComparison.Ordinal)
                && Environment.TickCount64 < deadline) await Task.Delay(10, stop.Token);
            Assert.IsTrue(parent.Model.GetScreenText().Contains("STARTUP-READY", StringComparison.Ordinal),
                $"published app must render after mode delivery, eligible={active.CanCommit}");
            Assert.IsTrue(active.CanCommit, "no frame deadline runs while startup cannot render");
            Assert.AreEqual(60, active.TerminalWidth);
            await parent.Inner.WriteRequiredForProcessing("\u001b[0m");
            var rows = parent.Model.GetScreenText().Split('\n');
            Assert.AreEqual("HOST-PREFIX-A", rows[0].TrimEnd());
            Assert.AreEqual("HOST-PREFIX-B", rows[1].TrimEnd());
            await active.CompleteAsync();
            await running.WaitAsync(stop.Token);
        }
        finally
        {
            parent.AllowEntry.Set();
            active?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed class BlockingModeParent : IHex1bAppTerminalWorkloadAdapter, ICursorPositionSource, IFlowCurrentGeometrySource
    {
        public Hex1bAppWorkloadAdapter Inner { get; }
        public Hex1bTerminal Model { get; }
        public TaskCompletionSource EntryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim AllowEntry { get; } = new();
        public BlockingModeParent()
        {
            var caps = new TerminalCapabilities { SupportsBracketedPaste = true, SupportsTrueColor = true };
            Inner = new Hex1bAppWorkloadAdapter(caps);
            Model = Hex1bTerminal.CreateBuilder().WithWorkload(Inner).WithHeadless(caps).WithDimensions(40, 12).Build();
        }
        public void Write(string text)
        {
            if (text.Contains("\u001b[?2004h", StringComparison.Ordinal))
            {
                EntryStarted.TrySetResult();
                if (!AllowEntry.Wait(TimeSpan.FromSeconds(5))) throw new TimeoutException("fixture mode release missing");
            }
            Inner.Write(text);
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
        public (int Width, int Height) ReadCurrentGeometry() => (Width, Height);
        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken ct) => Task.FromResult<(int Column, int Row)?>((0, 2));
        public async ValueTask DisposeAsync() { AllowEntry.Dispose(); await Model.DisposeAsync(); }
    }

    private sealed class StartupPresentation : IHex1bTerminalPresentationAdapter, ICursorPositionSource
    {
        private readonly HeadlessPresentationAdapter input = new(40, 12);
        public TaskCompletionSource EntryStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource AllowEntry { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ObservationReturned { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReadyPresented { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CursorUnavailableUntilEntry { get; init; }
        public bool CursorUnavailable { get; set; }
        public Exception? EntryFailure { get; init; }
        public int Width { get; private set; } = 40;
        public int Height { get; private set; } = 12;
        public event Action<int, int>? Resized;
        public void Resize(int width, int height)
        {
            Width = width;
            Height = height;
            Resized?.Invoke(width, height);
        }
        public async ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            var text = Encoding.UTF8.GetString(data.Span);
            if (text.Contains("\u001b[?2004h", StringComparison.Ordinal))
            {
                EntryStarted.TrySetResult();
                await AllowEntry.Task.WaitAsync(ct);
                if (EntryFailure is not null) throw EntryFailure;
            }
            if (text.Contains("STARTUP-READY", StringComparison.Ordinal)) ReadyPresented.TrySetResult();
        }
        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default) => input.ReadInputAsync(ct);
        public TerminalCapabilities Capabilities { get; } = new() { SupportsBracketedPaste = true, SupportsTrueColor = true };
        public event Action? Disconnected { add => input.Disconnected += value; remove => input.Disconnected -= value; }
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken ct)
        {
            (int Column, int Row)? result = (CursorUnavailable || CursorUnavailableUntilEntry && !AllowEntry.Task.IsCompleted) ? null : (0, 0);
            var completed = Task.FromResult(result);
            ObservationReturned.TrySetResult();
            return completed;
        }
        public ValueTask DisposeAsync() => input.DisposeAsync();
    }
}
