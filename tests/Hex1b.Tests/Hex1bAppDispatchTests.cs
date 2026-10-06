using Hex1b.Widgets;
using Hex1b.Input;
using Hex1b.Flow;

namespace Hex1b.Tests;

[TestClass]
public class Hex1bAppDispatchTests
{
    [TestMethod]
    public async Task Dispatch_IdleApp_ExecutesOnceAndRendersChangedState()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        Hex1bApp? app = null;
        var text = "before";
        var calls = 0;
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(_ => { }, instance => { app = instance; return _ => new TextBlockWidget(text); })
            .WithHeadless().WithDimensions(30, 5).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var before = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("before"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() => { calls++; text = "after"; }, out var completion));
            await completion.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.AreEqual(1, calls);
            using var after = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("after"), TimeSpan.FromSeconds(5), "dispatched state rendered")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }

    [TestMethod]
    public async Task Dispatch_FullQueue_WaitsForInputThenExecutesInOrder()
    {
        await using var fixture = new DispatchFixture(capacity: 2);
        await fixture.BlockInputAsync();
        var order = new List<int>();
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => order.Add(1), out var first));
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => order.Add(2), out var second));
        Assert.AreEqual(Hex1bDispatchAdmission.QueueFull, fixture.App.Dispatch(() => order.Add(99), out _));
        Assert.IsFalse(first.IsCompleted);
        Assert.IsFalse(second.IsCompleted);
        Assert.AreEqual(0, order.Count, "Dispatch must not execute concurrently with the held input handler.");
        fixture.ReleaseInput.TrySetResult();
        await Task.WhenAll(first, second).WaitAsync(TimeSpan.FromSeconds(5));
        TestSeq.AreEqual(new[] { 1, 2 }, order);
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => order.Add(3), out var third));
        await third.WaitAsync(TimeSpan.FromSeconds(5));
        TestSeq.AreEqual(new[] { 1, 2, 3 }, order);
    }

    [TestMethod]
    public async Task Dispatch_CallbackFails_ReportsExactExceptionAndRunsSuccessor()
    {
        await using var fixture = new DispatchFixture();
        await fixture.BlockInputAsync();
        var failure = new ApplicationException("callback failed");
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => throw failure, out var failed));
        var calls = 0;
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls++, out var successor));
        fixture.ReleaseInput.TrySetResult();
        var observed = await Assert.ThrowsExactlyAsync<ApplicationException>(() => failed.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreSame(failure, observed);
        await successor.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Dispatch_CancellationBeforeExecution_SkipsCallbackButNotSuccessor()
    {
        await using var fixture = new DispatchFixture();
        await fixture.BlockInputAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls += 100, out var canceled, cancellation.Token));
        cancellation.Cancel();
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls++, out var successor));
        fixture.ReleaseInput.TrySetResult();
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => canceled.WaitAsync(TimeSpan.FromSeconds(5)));
        await successor.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Dispatch_CancellationAfterStart_DoesNotReclassifySuccess()
    {
        await using var fixture = new DispatchFixture();
        await fixture.ReadyAsync();
        using var cancellation = new CancellationTokenSource();
        var calls = 0;
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() =>
        {
            cancellation.Cancel();
            calls++;
        }, out var completion, cancellation.Token));
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(TaskStatus.RanToCompletion, completion.Status);
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task Dispatch_StopDisposeOrCancel_SettlesQueuedWorkAndRefusesMore(int stopKind)
    {
        await using var fixture = new DispatchFixture();
        await fixture.BlockInputAsync();
        var calls = 0;
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls++, out var pending));
        if (stopKind == 0) fixture.App.RequestStop();
        else if (stopKind == 1) fixture.App.Dispose();
        else fixture.CancelRun();
        if (stopKind != 2)
            Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, fixture.App.Dispatch(() => calls += 10, out _));
        fixture.ReleaseInput.TrySetResult();
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(0, calls);
        Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, fixture.App.Dispatch(() => calls += 100, out _));
    }

    [TestMethod]
    public async Task Dispatch_RunFailsBeforeFirstFrame_SettlesAcceptedWork()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hex1bApp? app = null;
        var failure = new ApplicationException("build failed");
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(options => options.EnableRescue = false, instance =>
            {
                app = instance;
                return async _ => { entered.TrySetResult(); await release.Task; throw failure; };
            }).WithHeadless().WithDimensions(30, 5).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var calls = 0;
            Assert.AreEqual(Hex1bDispatchAdmission.Accepted, app!.Dispatch(() => calls++, out var pending));
            release.TrySetResult();
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => pending.WaitAsync(TimeSpan.FromSeconds(5)));
            var observedFailure = await Assert.ThrowsExactlyAsync<ApplicationException>(() => running.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.AreSame(failure, observedFailure);
            Assert.AreEqual(0, calls);
            Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, app.Dispatch(() => calls++, out _));
        }
        finally { release.TrySetResult(); cancellation.Cancel(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Dispatch_RenewingProducer_AllowsInputRenderingAndShutdown(bool coalesce)
    {
        await using var fixture = new DispatchFixture(coalesce: coalesce);
        await fixture.ReadyAsync();
        var calls = 0;
        var completions = new List<Task>();
        Action? produce = null;
        produce = () =>
        {
            calls++;
            if (calls == 1) fixture.Workload.SendKey(Hex1bKey.A);
            var admission = fixture.App.Dispatch(produce!, out var next);
            if (admission == Hex1bDispatchAdmission.Accepted) completions.Add(next);
            else Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, admission);
        };
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(produce, out var first));
        await first.WaitAsync(TimeSpan.FromSeconds(5));
        using var observed = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("input-progress"), TimeSpan.FromSeconds(5), "input renders during renewed dispatch")
            .Build().ApplyAsync(fixture.Terminal, TestContext.Current.CancellationToken);
        fixture.App.RequestStop();
        await fixture.Running.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsGreaterThan(1, calls);
        Assert.IsTrue(completions.All(t => t.IsCompleted), "Every admitted successor must settle at shutdown.");
        foreach (var task in completions)
            if (task.IsFaulted) Assert.IsInstanceOfType<InvalidOperationException>(task.Exception!.InnerException);
    }

    [TestMethod]
    public async Task Dispatch_PersistentFlow_ChangesLiveStateThroughSameSeam()
    {
        Hex1bTerminal terminal = null!;
        var text = "flow-before";
        FlowStep? retained = null;
        await using var lifetime = terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = retained = flow.Step(_ => new TextBlockWidget(text));
                await step.WaitForReadyAsync();
                try
                {
                    Assert.AreEqual(Hex1bDispatchAdmission.Accepted, step.Dispatch(() => text = "flow-after", out var completion));
                    await completion.WaitAsync(TimeSpan.FromSeconds(5));
                    using var observed = await new Hex1bTerminalInputSequenceBuilder()
                        .WaitUntil(s => s.ContainsText("flow-after"), TimeSpan.FromSeconds(5), "Flow dispatch updates live state")
                        .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
                }
                finally { step.Complete(); }
            }, options => options.UseSoftWrapTombstones = true)
            .WithHeadless().WithDimensions(40, 10).Build();
        await terminal.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.IsNotNull(retained);
        Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, retained.Dispatch(() => text = "incorrect", out _));
        Assert.AreEqual("flow-after", text);
    }

    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    public void Dispatch_InvalidCapacity_RejectsConstruction(int capacity)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new Hex1bApp(_ => new TextBlockWidget("x"),
            new Hex1bAppOptions { WorkloadAdapter = workload, DispatchQueueCapacity = capacity }));
    }

    [TestMethod]
    public async Task Dispatch_BeforeRunOrPreCanceled_ReportsExplicitDisposition()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var app = new Hex1bApp(_ => new TextBlockWidget("x"), new Hex1bAppOptions { WorkloadAdapter = workload });
        var calls = 0;
        Assert.AreEqual(Hex1bDispatchAdmission.NotRunning, app.Dispatch(() => calls++, out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => app.Dispatch(null!, out _));
        await using var fixture = new DispatchFixture(capacity: 1);
        await fixture.BlockInputAsync();
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls++, out var normal));
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() => calls += 100, out var completion, canceled.Token));
        await Assert.ThrowsExactlyAsync<TaskCanceledException>(() => completion);
        fixture.ReleaseInput.TrySetResult();
        await normal.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.AreEqual(1, calls);
    }

    [TestMethod]
    public async Task Dispatch_RunningCallback_CompletesOnlyAfterReturn()
    {
        await using var fixture = new DispatchFixture();
        await fixture.ReadyAsync();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        using var cancellation = new CancellationTokenSource();
        var returned = false;
        Assert.AreEqual(Hex1bDispatchAdmission.Accepted, fixture.App.Dispatch(() =>
        {
            entered.TrySetResult();
            Assert.IsTrue(release.Wait(TimeSpan.FromSeconds(5)), "Test must release the deliberately held callback.");
            returned = true;
        }, out var completion, cancellation.Token));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            Assert.IsFalse(completion.IsCompleted, "Neither admission nor cancellation completes a running callback.");
        }
        finally { release.Set(); }
        await completion.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsTrue(returned);
        Assert.AreEqual(TaskStatus.RanToCompletion, completion.Status);
    }

    private sealed class DispatchFixture : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        public Hex1bApp App { get; private set; } = null!;
        public Hex1bAppWorkloadAdapter Workload { get; private set; } = null!;
        public Hex1bTerminal Terminal { get; }
        public Task Running { get; }
        public TaskCompletionSource ReleaseInput { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private string _text = "ready";

        public DispatchFixture(int capacity = 16, bool coalesce = false)
        {
            Terminal = Hex1bTerminal.CreateBuilder().WithHex1bApp(options =>
            {
                options.DispatchQueueCapacity = capacity;
                options.EnableInputCoalescing = coalesce;
                Workload = TestSeq.IsType<Hex1bAppWorkloadAdapter>(options.WorkloadAdapter);
            }, app =>
            {
                App = app;
                return _ => new TextBlockWidget(_text).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.B).Action(async _ => { _entered.TrySetResult(); await ReleaseInput.Task; });
                    bindings.Key(Hex1bKey.A).Action(_ => _text = "input-progress");
                });
            }).WithHeadless().WithDimensions(30, 5).Build();
            Running = Task.Run(() => Terminal.RunAsync(_cancellation.Token));
        }

        public async Task ReadyAsync()
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("ready"), TimeSpan.FromSeconds(5), "fixture initial frame")
                .Build().ApplyAsync(Terminal, TestContext.Current.CancellationToken);
        }

        public async Task BlockInputAsync()
        {
            await ReadyAsync();
            Workload.SendKey(Hex1bKey.B);
            await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void CancelRun() => _cancellation.Cancel();

        public async ValueTask DisposeAsync()
        {
            ReleaseInput.TrySetResult();
            _cancellation.Cancel();
            await Running.WaitAsync(TimeSpan.FromSeconds(5));
            await Terminal.DisposeAsync();
            _cancellation.Dispose();
        }
    }
}
