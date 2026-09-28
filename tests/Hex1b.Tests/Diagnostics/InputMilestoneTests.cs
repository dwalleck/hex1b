using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A diagnostics-enabled session numbers every input as it enters the application's input
/// channel, records the application loop's processed-input watermark, and completes bounded
/// milestone waits with an explicit outcome.
/// </summary>
[TestClass]
public class InputMilestoneTests
{
    [TestMethod]
    public async Task Ids_DenseAndOrderedUnderConcurrentWriters()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var tracker = terminal.InputMilestones!;
        const int writers = 8, perWriter = 500;

        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                if (i % 50 == 0)
                    workload.SendMouse(MouseButton.Left, MouseAction.Move, w, i % 3);
                else
                    await workload.WriteInputEventAsync(new Hex1bKeyEvent(Hex1bKey.A, 'a', Hex1bModifiers.None));
            }
        })));

        var dequeued = new List<long>();
        while (workload.InputEvents.TryRead(out var evt))
            dequeued.Add(tracker.IdOf(evt) ?? -1);

        Assert.HasCount(writers * perWriter, dequeued);
        for (var i = 0; i < dequeued.Count; i++)
            Assert.AreEqual(i + 1, dequeued[i], $"event {i} in channel order carries id {dequeued[i]}");
        Assert.AreEqual(writers * perWriter, tracker.AcceptedInput);
    }

    [TestMethod]
    public async Task Send_ReturnsTheRangeOfEnqueuedEvents()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "sends");
        await workload.WriteInputEventAsync(new Hex1bKeyEvent(Hex1bKey.B, 'b', Hex1bModifiers.None));

        var accepted = await diagnostics.TrackSendAsync(() => terminal.SendInputAsync(Encoding.UTF8.GetBytes("abc")));

        var dequeued = new List<long>();
        while (workload.InputEvents.TryRead(out var evt))
            dequeued.Add(terminal.InputMilestones!.IdOf(evt)!.Value);
        Assert.IsNotNull(accepted);
        Assert.AreEqual((2L, 4L), (accepted.FirstId, accepted.LastId), "the send's range must be exactly its three events");
        CollectionAssert.AreEqual(new long[] { 1, 2, 3, 4 }, dequeued);
        StringAssert.Contains(accepted.Meaning, "Queued for the application");
    }

    [TestMethod]
    public async Task InputProcessed_WaitsForTheAppLoopAndTimesOutWithObservedState()
    {
        await using var harness = await GatedApp.StartAsync();
        var gated = await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);

        var stopwatch = Stopwatch.StartNew();
        var pending = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 300));
        var elapsed = stopwatch.Elapsed;

        Assert.AreEqual(DiagnosticOutcome.TimedOut, pending.Outcome, "processed while the app loop was blocked");
        Assert.AreEqual("milestone-timed-out", pending.Problem!.Code);
        Assert.IsNull(pending.Content, "a timed-out milestone capture returned content");
        Assert.IsTrue(elapsed >= TimeSpan.FromMilliseconds(300) && elapsed < TimeSpan.FromMilliseconds(1300), $"timed out after {elapsed.TotalMilliseconds:0} ms");
        Assert.IsFalse(pending.Milestone!.Met);
        Assert.AreEqual(queued, pending.Milestone.AcceptedInput);
        Assert.IsLessThan(queued, pending.Milestone.ProcessedInput, "the observed watermark reached an unprocessed input");

        harness.Release();
        var processed = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, processed.Outcome, processed.Problem?.Message);
        Assert.IsTrue(processed.Milestone!.Met);
        Assert.IsGreaterThanOrEqualTo(queued, processed.Milestone.ProcessedInput);
        Assert.AreEqual(harness.ApplicationInstanceId, processed.Milestone.ProcessedBy);
        Assert.IsGreaterThan(gated, queued);
    }

    [TestMethod]
    public async Task InputAccepted_IsMetForAnIssuedId()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);

        var result = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputAccepted, queued, 1000));

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.IsTrue(result.Milestone!.Met);
        Assert.IsLessThan(queued, result.Milestone.ProcessedInput, "fixture: the input is accepted but not processed");
    }

    [TestMethod]
    public async Task Termination_TargetDisposedFailsPendingWaitsImmediately()
    {
        var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);
        var wait = harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 30_000));
        await harness.WaitForPendingWaitsAsync(1);

        var stopwatch = Stopwatch.StartNew();
        var tracker = harness.Terminal.InputMilestones!;
        await harness.Terminal.DisposeAsync();
        var result = await wait;

        Assert.AreEqual((DiagnosticOutcome.Failed, "target-disposed"), (result.Outcome, result.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.AreEqual(0, tracker.PendingWaits);
        harness.Release();
        await harness.DisposeAsync();
    }

    [TestMethod]
    public async Task Termination_InputClosedFailsPendingWaitsImmediately()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);
        var wait = harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 30_000));
        await harness.WaitForPendingWaitsAsync(1);

        var stopwatch = Stopwatch.StartNew();
        await harness.Workload.DisposeAsync();
        var result = await wait;

        Assert.AreEqual((DiagnosticOutcome.Failed, "input-closed"), (result.Outcome, result.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.AreEqual(0, harness.Terminal.InputMilestones!.PendingWaits);
        harness.Release();
    }

    [TestMethod]
    public async Task Termination_ApplicationStoppedFailsWaitsForUnprocessedInput()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);
        var wait = harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 30_000));
        await harness.WaitForPendingWaitsAsync(1);

        var stopwatch = Stopwatch.StartNew();
        await harness.StopAsync();
        var result = await wait;
        var later = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 30_000));

        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (result.Outcome, result.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (later.Outcome, later.Problem?.Code),
            "a wait started after the application stopped must not pend");
    }

    [TestMethod]
    public async Task PendingWaits_AreBoundedAt64()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);
        var waits = Enumerable.Range(0, InputMilestoneTracker.MaxPendingWaits)
            .Select(_ => harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 20_000)))
            .ToArray();
        await harness.WaitForPendingWaitsAsync(InputMilestoneTracker.MaxPendingWaits);

        var stopwatch = Stopwatch.StartNew();
        var rejected = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, queued, 20_000));

        Assert.AreEqual((DiagnosticOutcome.Unavailable, "too-many-pending-waits"), (rejected.Outcome, rejected.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed, "the 65th wait was queued instead of rejected");
        harness.Release();
        foreach (var result in await Task.WhenAll(waits))
            Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
    }

    [TestMethod]
    public async Task InvalidMilestoneRequests_AreRejectedWithoutWaiting()
    {
        await using var harness = await GatedApp.StartAsync();
        var issued = await harness.SendAsync(Hex1bKey.X);

        var cases = new (DiagnosticMilestoneRequest Request, string Code)[]
        {
            (new() { Milestone = DiagnosticMilestone.InputProcessed, InputId = issued + 100 }, "unknown-input-id"),
            (new() { Milestone = DiagnosticMilestone.InputProcessed, InputId = 0 }, "unknown-input-id"),
            (new() { Milestone = DiagnosticMilestone.InputProcessed }, "missing-input-id"),
            (new() { Milestone = DiagnosticMilestone.InputProcessed, InputId = issued, TimeoutMs = 0 }, "invalid-milestone-timeout"),
            (new() { Milestone = DiagnosticMilestone.InputProcessed, InputId = issued, TimeoutMs = 60_001 }, "invalid-milestone-timeout"),
            (new() { Milestone = (DiagnosticMilestone)99, InputId = issued }, "unsupported-milestone"),
        };
        foreach (var (request, code) in cases)
        {
            var stopwatch = Stopwatch.StartNew();
            var result = await harness.Diagnostics.CaptureAsync(new DiagnosticCaptureRequest { Milestone = request });
            Assert.AreEqual((DiagnosticOutcome.InvalidRequest, code), (result.Outcome, result.Problem?.Code), $"{request.Milestone} id={request.InputId} timeout={request.TimeoutMs}");
            Assert.IsLessThan(TimeSpan.FromMilliseconds(500), stopwatch.Elapsed, $"{code} waited");
        }
    }

    [TestMethod]
    public async Task PastInput_IsMetImmediatelyAndReportsProgressBeyond()
    {
        await using var harness = await GatedApp.StartAsync();
        long last = 0;
        for (var i = 0; i < 10; i++)
            last = await harness.SendAsync(Hex1bKey.X);
        var processedAll = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, last, 5000));
        Assert.AreEqual(DiagnosticOutcome.Captured, processedAll.Outcome, "fixture: all inputs processed");

        var stopwatch = Stopwatch.StartNew();
        var past = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, last - 7, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, past.Outcome);
        Assert.IsLessThan(TimeSpan.FromMilliseconds(500), stopwatch.Elapsed);
        Assert.AreEqual(last - 7, past.Milestone!.InputId);
        Assert.AreEqual(last, past.Milestone.ProcessedInput, "the observed watermark must be the current one, not the requested id");
    }

    [TestMethod]
    public async Task Unarmed_NoTrackingWithoutDiagnostics()
    {
        var constructions = InputMilestoneTracker.ConstructionsForTesting.Value = new StrongBox<int>();
        await using (var unarmed = await GatedApp.StartAsync(diagnostics: false))
        {
            for (var i = 0; i < 50; i++)
                Assert.IsNull(await unarmed.Diagnostics.TrackSendAsync(() => unarmed.Terminal.SendEventAsync(
                    new Hex1bKeyEvent(Hex1bKey.X, 'x', Hex1bModifiers.None))), "an unarmed send returned an input id");
            Assert.IsNull(unarmed.Terminal.InputMilestones);
            var result = await unarmed.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, 1, 1000));
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "input-tracking-unavailable"), (result.Outcome, result.Problem?.Code));
        }

        Assert.AreEqual(0, constructions.Value, $"{constructions.Value} trackers created without diagnostics");
        await using (await GatedApp.StartAsync())
            Assert.IsGreaterThanOrEqualTo(1, constructions.Value, "fixture: the counter did not observe an armed session");
    }

    [TestMethod]
    public async Task Immediate_CapturesHaveNoMilestoneBlock()
    {
        await using var harness = await GatedApp.StartAsync();

        var model = await harness.Diagnostics.CaptureAsync(new DiagnosticCaptureRequest());
        var frame = await harness.Diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest());

        Assert.AreEqual(DiagnosticOutcome.Captured, model.Outcome);
        Assert.IsNull(model.Milestone);
        Assert.AreEqual(DiagnosticOutcome.Captured, frame.Outcome);
        Assert.IsNull(frame.Milestone);
    }

    private static DiagnosticCaptureRequest Milestone(DiagnosticMilestone milestone, long inputId, int timeoutMs) => new()
    {
        Milestone = new DiagnosticMilestoneRequest { Milestone = milestone, InputId = inputId, TimeoutMs = timeoutMs },
    };

    // A diagnostics-enabled app whose focused button blocks the app loop inside its click handler
    // until released, so inputs sent meanwhile are accepted but not processed.
    private sealed class GatedApp : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private Task _run = Task.CompletedTask;

        private GatedApp(Hex1bAppWorkloadAdapter workload, Hex1bTerminal terminal)
        {
            Workload = workload;
            Terminal = terminal;
            Diagnostics = new TerminalDiagnostics(terminal, "gated");
        }

        public Hex1bAppWorkloadAdapter Workload { get; }
        public Hex1bTerminal Terminal { get; }
        public Hex1bApp App { get; private set; } = null!;
        public TerminalDiagnostics Diagnostics { get; }

        public string ApplicationInstanceId =>
            Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame!.ApplicationInstanceId;

        public static async Task<GatedApp> StartAsync(bool diagnostics = true)
        {
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = diagnostics };
            var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(30, 4).Build();
            var harness = new GatedApp(workload, terminal);
            harness.App = new Hex1bApp(_ => new ButtonWidget("gate").OnClick(async _ =>
            {
                harness._entered.TrySetResult();
                await harness._gate.Task;
            }), new Hex1bAppOptions { WorkloadAdapter = workload });
            harness._run = harness.App.RunAsync(harness._cts.Token);
            for (var i = 0; i < 500 && harness.App.FrameCount < 1; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            return harness;
        }

        public async Task<long> SendAsync(Hex1bKey key)
        {
            var accepted = await Diagnostics.TrackSendAsync(() =>
                Terminal.SendEventAsync(new Hex1bKeyEvent(key, key == Hex1bKey.X ? 'x' : '\0', Hex1bModifiers.None)));
            return accepted!.LastId;
        }

        public Task WaitUntilGatedAsync() => _entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        public async Task WaitForPendingWaitsAsync(int count)
        {
            for (var i = 0; i < 500 && Terminal.InputMilestones!.PendingWaits < count; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.AreEqual(count, Terminal.InputMilestones!.PendingWaits, "fixture: waits did not become pending");
        }

        public void Release() => _gate.TrySetResult();

        public async Task StopAsync()
        {
            await _cts.CancelAsync();
            Release();
            try { await _run; } catch (OperationCanceledException) { }
        }

        public async ValueTask DisposeAsync()
        {
            await StopAsync();
            App.Dispose();
            await Terminal.DisposeAsync();
            _cts.Dispose();
        }
    }
}
