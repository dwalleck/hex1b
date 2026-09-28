using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Flow;
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
        const int writers = 8, perWriter = 500, resizes = 200;

        // Every input-channel writer: async and sync events, mouse, keys, and queued resizes.
        var resizing = Task.Run(async () =>
        {
            for (var i = 0; i < resizes; i++)
                await workload.ApplyQueuedResize(21 - i % 2, 3);
        });
        await Task.WhenAll(Enumerable.Range(0, writers).Select(w => Task.Run(async () =>
        {
            for (var i = 0; i < perWriter; i++)
            {
                switch (i % 4)
                {
                    case 0: workload.SendMouse(MouseButton.Left, MouseAction.Move, w, i % 3); break;
                    case 1: workload.SendKey(Hex1bKey.B, 'b'); break;
                    case 2: workload.TryWriteInputEvent(new Hex1bKeyEvent(Hex1bKey.C, 'c', Hex1bModifiers.None)); break;
                    default: await workload.WriteInputEventAsync(new Hex1bKeyEvent(Hex1bKey.A, 'a', Hex1bModifiers.None)); break;
                }
            }
        })).Append(resizing));

        var dequeued = new List<long>();
        while (workload.InputEvents.TryRead(out var evt))
            dequeued.Add(tracker.IdOf(evt) ?? -1);

        Assert.HasCount(writers * perWriter + resizes, dequeued);
        for (var i = 0; i < dequeued.Count; i++)
            Assert.AreEqual(i + 1, dequeued[i], $"event {i} in channel order carries id {dequeued[i]}");
        Assert.AreEqual(writers * perWriter + resizes, tracker.AcceptedInput);
    }

    [TestMethod]
    public async Task Send_ReturnsTheRangeOfEnqueuedEvents()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "sends");
        await workload.WriteInputEventAsync(new Hex1bKeyEvent(Hex1bKey.B, 'b', Hex1bModifiers.None));

        var accepted = await diagnostics.TrackSendAsync(() => terminal.SendInputAsync(Encoding.UTF8.GetBytes("abc")), "text");

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
        // The timeout timer runs on the millisecond tick clock, so it may fire a tick before the stopwatch reads 300.
        Assert.IsTrue(elapsed >= TimeSpan.FromMilliseconds(280) && elapsed < TimeSpan.FromMilliseconds(1300), $"timed out after {elapsed.TotalMilliseconds:0.0} ms");
        Assert.IsFalse(pending.Milestone!.Met);
        Assert.AreEqual(queued, pending.Milestone.AcceptedInput);
        Assert.IsLessThan(queued, pending.Milestone.ProcessedInput, "the observed watermark reached an unprocessed input");
        Assert.IsNotNull(pending.Milestone.ModelSequence, "an unmet input-processed result hid the model progress it observed");

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
        Assert.AreEqual(0, harness.Terminal.InputMilestones!.PendingWaits);
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
                    new Hex1bKeyEvent(Hex1bKey.X, 'x', Hex1bModifiers.None)), "key"), "an unarmed send returned an input id");
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

    [TestMethod]
    public async Task FramePublished_NeverAttributesAnInterveningFrame()
    {
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("frames"));
        await harness.HoldNextBuildAsync();
        var input = await harness.SendAsync(Hex1bKey.X);
        var wait = harness.Diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest
        {
            Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.FramePublished, InputId = input, TimeoutMs = 5000 },
        });
        await harness.WaitForPendingWaitsAsync(1);

        harness.ReleaseBuild();
        var result = await wait;

        var intervening = harness.Published.Where(f => f.ProcessedInput < input).Select(f => f.FrameId).ToList();
        Assert.IsNotEmpty(intervening, "fixture: no frame was published between the input's acceptance and its processing");
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        var frame = result.Milestone!.Frame!;
        CollectionAssert.DoesNotContain(intervening, frame.FrameId, $"intervening frame {frame.FrameId} (watermark < {input}) was attributed to the input");
        Assert.IsGreaterThanOrEqualTo(input, frame.ProcessedInput);
        Assert.AreEqual(harness.Published.First(f => f.ProcessedInput >= input).FrameId, frame.FrameId, "not the first covering frame");
    }

    [TestMethod]
    public async Task ModelApplied_WaitsForThePumpAndReportsTheModelSequence()
    {
        var filter = new GatingFilter();
        var shown = 0;
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget($"shown {shown}"), filter: filter);
        await harness.WaitForQuietAsync();

        filter.Close();
        var input = await harness.SendAsync(Hex1bKey.X);
        shown = 1;
        harness.App.Invalidate();
        var gated = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.ModelApplied, input, 400));
        filter.Open();
        var applied = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.ModelApplied, input, 5000));

        Assert.AreEqual(DiagnosticOutcome.TimedOut, gated.Outcome, "model-applied met while the output pump was held");
        Assert.IsNotNull(gated.Milestone!.Frame, "the timed-out result hid the covering frame it was waiting on");
        Assert.IsNotNull(gated.Milestone.ModelSequence, "the timed-out result hid the model progress it observed");
        Assert.AreEqual(DiagnosticOutcome.Captured, applied.Outcome, applied.Problem?.Message);
        Assert.IsTrue(applied.Milestone!.Met);
        Assert.IsNotNull(applied.Milestone.Frame);
        Assert.IsGreaterThanOrEqualTo(input, applied.Milestone.Frame.ProcessedInput);
        Assert.IsNotNull(applied.Milestone.ModelSequence);
        Assert.IsGreaterThanOrEqualTo(applied.Milestone.ModelSequence.Value, applied.Identity!.ModelSequence!.Value,
            "the capture after the milestone read an older model");
        StringAssert.Contains(applied.Content, "shown 1", "the model did not contain the covering frame's output");
    }

    [TestMethod]
    public async Task ModelApplied_IsMetForAFrameThatWroteNothing()
    {
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("static"));
        await harness.WaitForQuietAsync();

        var input = await harness.SendAsync(Hex1bKey.X);
        var result = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.ModelApplied, input, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        Assert.IsFalse(result.Milestone!.Frame!.WroteOutput, "fixture: the covering frame wrote output");
    }

    [TestMethod]
    public async Task Termination_OutputPumpFailureFailsModelAppliedWaits()
    {
        var filter = new GatingFilter();
        var shown = 0;
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget($"shown {shown}"), filter: filter);
        await harness.WaitForQuietAsync();

        filter.Close();
        var input = await harness.SendAsync(Hex1bKey.X);
        shown = 1;
        harness.App.Invalidate();
        var wait = harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.ModelApplied, input, 30_000));
        await harness.WaitForPendingWaitsAsync(1);

        var stopwatch = Stopwatch.StartNew();
        filter.Fail();
        var result = await wait;

        Assert.AreEqual((DiagnosticOutcome.Failed, "output-pump-failed"), (result.Outcome, result.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(1), stopwatch.Elapsed);
        Assert.AreEqual(0, harness.Terminal.InputMilestones!.PendingWaits);
    }

    [TestMethod]
    public async Task Termination_CoveringFrameProjectionFailureFailsFrameWaits()
    {
        var armed = new ApplicationFrameTests.StrongBox<bool>();
        await using var harness = await GatedApp.StartAsync(content: _ => new VStackWidget(
            [new TextBlockWidget("armed"), new ApplicationFrameTests.ArmableProjectionFailureWidget(armed)]));
        await harness.WaitForQuietAsync();

        armed.Value = true;
        var input = await harness.SendAsync(Hex1bKey.X);
        // A model capture: only the milestone, not the capture itself, can report the failed frame.
        var stopwatch = Stopwatch.StartNew();
        var result = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.FramePublished, input, 5000));
        armed.Value = false;

        Assert.AreEqual((DiagnosticOutcome.Failed, "application-frame-projection-failed"), (result.Outcome, result.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromSeconds(2), stopwatch.Elapsed, "the failed frame's wait ran to its timeout");
        Assert.AreEqual(0, harness.Terminal.InputMilestones!.PendingWaits);
    }

    [TestMethod]
    public async Task RawInput_PayloadOnlyWithAuthorization()
    {
        const string Sentinel = "ZQX-RAW-SECRET";
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("raw"));
        var input = await harness.SendTextAsync(Sentinel);

        string Json(params DiagnosticAuthorization[] authorizations)
        {
            var result = harness.Diagnostics.CaptureAsync(new DiagnosticCaptureRequest
            {
                Authorizations = authorizations,
                Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = input, TimeoutMs = 5000 },
            }).GetAwaiter().GetResult();
            Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
            return System.Text.Json.JsonSerializer.Serialize(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult);
        }

        var plain = Json();
        var editor = Json(DiagnosticAuthorization.EditorText);
        var raw = Json(DiagnosticAuthorization.RawInput);

        Assert.IsFalse(plain.Contains(Sentinel, StringComparison.Ordinal), "sentinel in a default milestone result");
        Assert.IsFalse(editor.Contains(Sentinel, StringComparison.Ordinal), "sentinel under editor-text");
        Assert.IsTrue(raw.Contains(Sentinel, StringComparison.Ordinal), "raw-input did not include the payload");
        StringAssert.Contains(plain, "\"kind\":\"text\"");
        StringAssert.Contains(plain, "\"source\":\"diagnostic-send\"");
        StringAssert.Contains(plain, "\"content\":\"raw-input\",\"state\":\"excluded\"");
        StringAssert.Contains(raw, "\"content\":\"raw-input\",\"state\":\"included\"");
    }

    [TestMethod]
    public async Task Milestone_DisclosesAnActiveSynchronizedUpdate()
    {
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("sync"));
        var input = await harness.SendAsync(Hex1bKey.X);
        var processed = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, input, 5000));
        Assert.AreEqual(DiagnosticOutcome.Captured, processed.Outcome, "fixture: input processed");
        await harness.WaitForQuietAsync();

        harness.Workload.Write("\u001b[?2026h");
        for (var i = 0; i < 100 && harness.Diagnostics.Capture(new DiagnosticCaptureRequest()).SynchronizedUpdate?.Active != true; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        var immediate = harness.Diagnostics.Capture(new DiagnosticCaptureRequest());
        var milestone = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, input, 5000));

        Assert.IsTrue(immediate.SynchronizedUpdate!.Active, "fixture: no synchronized update active");
        Assert.AreEqual(DiagnosticOutcome.Captured, milestone.Outcome);
        Assert.IsTrue(milestone.SynchronizedUpdate!.Active, "a milestone capture hid the active synchronized update");
        harness.Workload.Write("\u001b[?2026l");
    }

    [TestMethod]
    public async Task Flow_InlineStepProcessesForwardedInputAndReportsModelApplicationUnobservable()
    {
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget("FLOW-STEP"), new TextBoxWidget("")]));
                await step.WaitForCompletionAsync(TestContext.Current.CancellationToken);
            })
            .WithHeadless()
            .WithDimensions(30, 6)
            .WithDiagnostics(appName: "milestone-flow", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("FLOW-STEP"), TimeSpan.FromSeconds(10), "flow step rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        var diagnostics = new TerminalDiagnostics(terminal, "milestone-flow");
        Assert.IsTrue(terminal.InputMilestones!.HostsFlow, "a flow session must be marked flow-hosted");

        var accepted = await diagnostics.TrackSendAsync(() =>
            terminal.SendEventAsync(new Hex1bKeyEvent(Hex1bKey.Q, "q", Hex1bModifiers.None)), "key");
        DiagnosticCaptureRequest At(DiagnosticMilestone milestone) => Milestone(milestone, accepted!.LastId, 5000);
        var processed = await diagnostics.CaptureAsync(At(DiagnosticMilestone.InputProcessed));
        var framed = await diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest
        {
            Milestone = At(DiagnosticMilestone.FramePublished).Milestone,
        });
        var model = await diagnostics.CaptureAsync(At(DiagnosticMilestone.ModelApplied));

        Assert.AreEqual(DiagnosticOutcome.Captured, processed.Outcome, processed.Problem?.Message);
        Assert.AreEqual(DiagnosticOutcome.Captured, framed.Outcome, framed.Problem?.Message);
        var stepInstance = framed.Frame!.ApplicationInstanceId;
        Assert.AreEqual(stepInstance, processed.Milestone!.ProcessedBy, "the step app did not process the forwarded input");
        Assert.AreEqual(stepInstance, framed.Milestone!.Frame!.ApplicationInstanceId);
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "model-application-unobservable"), (model.Outcome, model.Problem?.Code));
    }

    [TestMethod]
    public async Task Flow_InputSentBetweenStepsWaitsForTheNextStep()
    {
        var ct = TestContext.Current.CancellationToken;
        var betweenSteps = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FlowStep? first = null;
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                first = flow.Step(_ => new TextBlockWidget("STEP-ONE"));
                await first.WaitForCompletionAsync(ct);
                betweenSteps.TrySetResult();
                await proceed.Task;
                var second = flow.Step(_ => new VStackWidget([new TextBlockWidget("STEP-TWO"), new TextBoxWidget("")]));
                await second.WaitForCompletionAsync(ct);
            })
            .WithHeadless()
            .WithDimensions(30, 6)
            .WithDiagnostics(appName: "milestone-between", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(ct);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("STEP-ONE"), TimeSpan.FromSeconds(10), "first step rendered")
            .Build().ApplyAsync(terminal, ct);
        var diagnostics = new TerminalDiagnostics(terminal, "milestone-between");

        first!.Complete();
        await betweenSteps.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
        var accepted = await diagnostics.TrackSendAsync(() =>
            terminal.SendEventAsync(new Hex1bKeyEvent(Hex1bKey.Q, "q", Hex1bModifiers.None)), "key");
        var queued = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, accepted!.LastId, 300));
        proceed.TrySetResult();
        var processed = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, accepted.LastId, 5000));
        var framed = await diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest
        {
            Milestone = Milestone(DiagnosticMilestone.FramePublished, accepted.LastId, 5000).Milestone,
        });

        Assert.AreEqual(DiagnosticOutcome.TimedOut, queued.Outcome,
            $"input queued between steps must wait for the next step, not end as {queued.Problem?.Code}");
        Assert.AreEqual(DiagnosticOutcome.Captured, processed.Outcome, processed.Problem?.Message);
        Assert.AreEqual(DiagnosticOutcome.Captured, framed.Outcome, framed.Problem?.Message);
        Assert.AreEqual(framed.Frame!.ApplicationInstanceId, processed.Milestone!.ProcessedBy, "the next step did not process the queued input");
    }

    [TestMethod]
    public async Task Flow_FullScreenStepReachesEveryStage()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flow => flow.FullScreenStepAsync((_, _) => _ => new VStackWidget([new TextBlockWidget("FULL-STEP"), new TextBoxWidget("")])))
            .WithHeadless()
            .WithDimensions(30, 6)
            .WithDiagnostics(appName: "milestone-full", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(ct);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("FULL-STEP"), TimeSpan.FromSeconds(10), "full-screen step rendered")
            .Build().ApplyAsync(terminal, ct);
        var diagnostics = new TerminalDiagnostics(terminal, "milestone-full");

        var accepted = await diagnostics.TrackSendAsync(() =>
            terminal.SendEventAsync(new Hex1bKeyEvent(Hex1bKey.Q, "q", Hex1bModifiers.None)), "key");
        var processed = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, accepted!.LastId, 5000));
        var framed = await diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest
        {
            Milestone = Milestone(DiagnosticMilestone.FramePublished, accepted.LastId, 5000).Milestone,
        });
        var model = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.ModelApplied, accepted.LastId, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, processed.Outcome, processed.Problem?.Message);
        Assert.AreEqual(DiagnosticOutcome.Captured, framed.Outcome, framed.Problem?.Message);
        Assert.AreEqual(framed.Frame!.ApplicationInstanceId, processed.Milestone!.ProcessedBy);
        Assert.AreEqual(DiagnosticOutcome.Captured, model.Outcome, $"a full-screen step writes to the terminal directly: {model.Problem?.Code}");
        StringAssert.Contains(model.Content, "FULL-STEP");
    }

    [TestMethod]
    public void Accept_RegistersBeforeTheWriteSoAnImmediateReaderIsAttributed()
    {
        var tracker = new InputMilestoneTracker();
        var evt = Key();

        // A reader that dequeues and processes the event the instant it enters the channel.
        Assert.IsTrue(tracker.Accept(evt, e =>
        {
            tracker.Processed(e, "reader");
            return true;
        }));

        Assert.AreEqual(1, tracker.ProcessedInput, "an event processed straight after its write was not yet registered");
        var rejected = Key();
        Assert.IsFalse(tracker.Accept(rejected, _ => false));
        Assert.AreEqual(1, tracker.AcceptedInput, "a rejected write consumed an id");
        Assert.IsNull(tracker.IdOf(rejected), "a rejected write stayed registered");
    }

    [TestMethod]
    public void Processed_AttributesEachOccurrenceOfAReusedEventInOrder()
    {
        var tracker = new InputMilestoneTracker();
        var evt = Key();
        tracker.Accept(evt, _ => true);
        tracker.Accept(evt, _ => true);

        tracker.Processed(evt, "app");
        Assert.AreEqual(1, tracker.ProcessedInput, "processing the first occurrence marked the second processed");
        tracker.Processed(evt, "app");
        Assert.AreEqual(2, tracker.ProcessedInput);
    }

    [TestMethod]
    public async Task ModelApplied_CoveredInputOutlivesItsApplicationStopping()
    {
        var tracker = new InputMilestoneTracker();
        var evt = Key();
        tracker.Accept(evt, _ => true);
        tracker.Processed(evt, "app");
        tracker.FramePublished("app", 1, 1, wroteOutput: true, projectionFailed: false, outputMark: 5);
        var wait = tracker.WaitAsync(DiagnosticMilestone.ModelApplied, 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);

        tracker.ApplicationStopped("app");
        Assert.IsFalse(wait.IsCompleted, "the frame's output was still in the pump, yet the wait ended when the application stopped");
        tracker.OutputApplied(5);
        var status = await wait;

        Assert.IsTrue(status.IsMet, $"{status.Code}: {status.Message}");
        Assert.AreEqual(1, status.Frame!.FrameId);
        var uncovered = await tracker.WaitAsync(DiagnosticMilestone.FramePublished, 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.IsTrue(uncovered.IsMet, "fixture: the covering frame was published");
        tracker.Accept(Key(), _ => true);
        var never = await tracker.WaitAsync(DiagnosticMilestone.ModelApplied, 2, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (never.Outcome, never.Code),
            "an input the stopped application never processed must fail at once");
    }

    [TestMethod]
    public async Task Flow_InputLostByAnEndedStepFailsEvenAfterTheWatermarkPassesIt()
    {
        var tracker = new InputMilestoneTracker { HostsFlow = true };
        var lost = Key();
        var next = Key();
        tracker.Accept(lost, _ => true);
        tracker.Accept(next, _ => true);

        tracker.Forwarded(lost);
        tracker.ApplicationStopped("step-1");
        tracker.Forwarded(next);
        tracker.Processed(next, "step-2");
        tracker.FramePublished("step-2", 1, 2, wroteOutput: true, projectionFailed: false, outputMark: null);

        Assert.AreEqual(2, tracker.ProcessedInput, "fixture: the next step's input carried the watermark past the lost one");
        foreach (var milestone in new[] { DiagnosticMilestone.InputProcessed, DiagnosticMilestone.FramePublished, DiagnosticMilestone.ModelApplied })
        {
            var status = await tracker.WaitAsync(milestone, 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (status.Outcome, status.Code),
                $"{milestone}: an input no step processed was reported as {status.Outcome?.ToString() ?? "met"}");
        }
    }

    [TestMethod]
    public void Flow_InputForwardedAfterItsStepStoppedIsLostWhenThePumpEnds()
    {
        var tracker = new InputMilestoneTracker { HostsFlow = true };
        var late = Key();
        tracker.Accept(late, _ => true);
        tracker.ApplicationStopped("step-1");

        tracker.Forwarded(late);
        tracker.StepEnded("the ended flow step");

        var status = tracker.WaitAsync(DiagnosticMilestone.InputProcessed, 1, TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken).Result;
        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (status.Outcome, status.Code));
    }

    [TestMethod]
    public async Task TimedOut_ReportsTheCoveringFrameAndModelProgress()
    {
        var tracker = new InputMilestoneTracker();
        var evt = Key();
        tracker.Accept(evt, _ => true);
        tracker.Processed(evt, "app");
        tracker.FramePublished("app", 7, 1, wroteOutput: true, projectionFailed: false, outputMark: 3);

        var status = await tracker.WaitAsync(DiagnosticMilestone.ModelApplied, 1, TimeSpan.FromMilliseconds(100), TestContext.Current.CancellationToken);

        Assert.AreEqual(DiagnosticOutcome.TimedOut, status.Outcome);
        Assert.AreEqual(7, status.Frame?.FrameId, "a timed-out model-applied wait hid the frame it was waiting on");
        Assert.AreEqual(0, tracker.PendingWaits);
    }

    [TestMethod]
    public async Task SendScope_KeepsASendsIdsContiguousAndBelongsToItsTracker()
    {
        var tracker = new InputMilestoneTracker();
        var other = new InputMilestoneTracker();
        Task<bool> native;
        await tracker.WaitForSendTurnAsync(TestContext.Current.CancellationToken);
        using (var scope = tracker.BeginSend())
        {
            // A native writer on another flow (no scope) while the send is in progress.
            using (ExecutionContext.SuppressFlow())
                native = Task.Run(() => tracker.Accept(Key(), _ => true));
            await Task.Delay(100, TestContext.Current.CancellationToken);
            Assert.IsFalse(native.IsCompleted, "a native input entered the channel inside a send");

            tracker.Accept(Key(), _ => true);
            other.Accept(Key(), _ => true);
            tracker.Accept(Key(), _ => true);

            Assert.AreEqual((1L, 2L), (scope.FirstId, scope.LastId), "the send's range is not exactly its own events");
            Assert.AreEqual("native", other.Record(1)!.Source, "another session's input was attributed to this send");
        }

        Assert.IsTrue(await native.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        Assert.AreEqual(3, tracker.AcceptedInput);
        Assert.AreEqual("native", tracker.Record(3)!.Source);
    }

    [TestMethod]
    public async Task TrackedAsyncOutput_AwaitsAFullChannelAndHonoursCancellation()
    {
        await using var adapter = new Hex1bAppWorkloadAdapter(maxQueuedOutputItems: 1) { InputMilestones = new InputMilestoneTracker() };
        adapter.Write("a");
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        var write = Task.Run(async () => await adapter.WriteTokensWithBytesAsync([], "b"u8.ToArray(), cancellationToken: cts.Token));
        var ended = await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Assert.AreSame(write, ended, "a tracked async write ignored its cancellation while the channel was full");
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);
        Assert.AreEqual(1, adapter.MilestoneOutputSequence, "a cancelled write kept its output sequence");
        var first = await adapter.ReadOutputItemAsync(TestContext.Current.CancellationToken);
        adapter.Write("c");
        var next = await adapter.ReadOutputItemAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual((1L, 2L), (first.MilestoneSequence, next.MilestoneSequence), "output sequences are not dense after a cancelled write");
    }

    [TestMethod]
    public async Task MilestoneCaptures_ValidateTheRequestBeforeWaiting()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);
        var milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = queued, TimeoutMs = 2000 };

        var stopwatch = Stopwatch.StartNew();
        var capture = await harness.Diagnostics.CaptureAsync(new DiagnosticCaptureRequest { HistoryRows = -1, Milestone = milestone });
        var frame = await harness.Diagnostics.CaptureApplicationFrameAsync(new DiagnosticApplicationFrameRequest
        {
            Authorizations = [(DiagnosticAuthorization)99],
            Milestone = milestone,
        });
        var elapsed = stopwatch.Elapsed;

        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "invalid-history-rows"), (capture.Outcome, capture.Problem?.Code));
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "unsupported-authorization"), (frame.Outcome, frame.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1000), elapsed, "an invalid request waited for its milestone first");
        harness.Release();
    }

    [TestMethod]
    public async Task SyncCaptures_RejectAMilestoneInsteadOfBlocking()
    {
        await using var harness = await GatedApp.StartAsync();
        await harness.SendAsync(Hex1bKey.Enter);
        await harness.WaitUntilGatedAsync();
        var queued = await harness.SendAsync(Hex1bKey.X);

        var stopwatch = Stopwatch.StartNew();
        var capture = harness.Diagnostics.Capture(Milestone(DiagnosticMilestone.InputProcessed, queued, 2000));
        var frame = harness.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest
        {
            Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = queued, TimeoutMs = 2000 },
        });

        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "milestone-requires-async"), (capture.Outcome, capture.Problem?.Code));
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "milestone-requires-async"), (frame.Outcome, frame.Problem?.Code));
        Assert.IsLessThan(TimeSpan.FromMilliseconds(1000), stopwatch.Elapsed, "a synchronous capture blocked on a milestone");
        harness.Release();
    }

    [TestMethod]
    public async Task AcceptanceOnly_FailedSendConsumesNoIdAndLaterStagesAreUnavailable()
    {
        var workload = new CapturedWorkloadAdapter([]);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "pty");

        Assert.IsNull(await diagnostics.TrackSendAsync(() => Task.FromResult(false), "text"), "an undelivered send returned an id");
        var accepted = await diagnostics.TrackSendAsync(() => terminal.SendInputAsync("hi"u8.ToArray()), "text");

        Assert.AreEqual((1L, 1L), (accepted!.FirstId, accepted.LastId), "an undelivered send consumed an id");
        StringAssert.Contains(accepted.Meaning, "child process");
        var met = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputAccepted, 1, 1000));
        Assert.AreEqual(DiagnosticOutcome.Captured, met.Outcome, met.Problem?.Message);
        Assert.AreEqual("text", met.Milestone!.Input!.Kind);
        var key = await diagnostics.TrackSendAsync(() => terminal.SendInputAsync("\r"u8.ToArray()), "key");
        var keyRecord = await diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputAccepted, key!.LastId, 1000));
        Assert.AreEqual("key", keyRecord.Milestone!.Input!.Kind, "an acceptance-only record lost its send's kind");
        foreach (var later in new[] { DiagnosticMilestone.InputProcessed, DiagnosticMilestone.FramePublished, DiagnosticMilestone.ModelApplied })
        {
            var result = await diagnostics.CaptureAsync(Milestone(later, 1, 1000));
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "input-consumption-unobservable"), (result.Outcome, result.Problem?.Code), later.ToString());
        }
    }

    [TestMethod]
    public async Task Records_AreEvictedBeyondTheRetentionBound()
    {
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("evict"));
        for (var i = 0; i < InputMilestoneTracker.RetainedRecords + 5; i++)
            await harness.Workload.WriteInputEventAsync(Key());
        var last = harness.Terminal.InputMilestones!.AcceptedInput;

        var recent = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, last, 5000));
        var evicted = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, 1, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, recent.Outcome, recent.Problem?.Message);
        Assert.AreEqual(last, recent.Milestone!.Input!.Id);
        Assert.AreEqual(DiagnosticOutcome.Captured, evicted.Outcome, "an evicted input's milestone is still decided by the watermark");
        Assert.IsNull(evicted.Milestone!.Input, "an evicted input reported another input's record");
        Assert.IsTrue(evicted.UnavailableFields.Any(f => f.Field == "milestone.input"), "the evicted record was not explained");
    }

    [TestMethod]
    public async Task InputRecord_TimestampsShareTheCaptureClockDomain()
    {
        await using var harness = await GatedApp.StartAsync(content: _ => new TextBlockWidget("clock"));
        var input = await harness.SendAsync(Hex1bKey.X);

        var result = await harness.Diagnostics.CaptureAsync(Milestone(DiagnosticMilestone.InputProcessed, input, 5000));

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        var record = result.Milestone!.Input!;
        Assert.IsGreaterThan(0L, record.AcceptedTimestamp);
        Assert.IsGreaterThanOrEqualTo(record.AcceptedTimestamp, record.ProcessedTimestamp!.Value);
        Assert.IsGreaterThanOrEqualTo(record.ProcessedTimestamp.Value, result.Identity!.Acquisition!.StartTimestamp,
            "the capture's acquisition predates the input's processing");
    }

    [TestMethod]
    public async Task FrameAndModelWaits_FailWhenTheApplicationStopsBeforeACoveringFrame()
    {
        var tracker = new InputMilestoneTracker();
        var evt = Key();
        tracker.Accept(evt, _ => true);
        tracker.Processed(evt, "app");
        var frame = tracker.WaitAsync(DiagnosticMilestone.FramePublished, 1, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        tracker.ApplicationStopped("app");
        var framed = await frame;
        var model = await tracker.WaitAsync(DiagnosticMilestone.ModelApplied, 1, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);

        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (framed.Outcome, framed.Code),
            "a stopped application publishes no covering frame, so the wait must not run to its timeout");
        Assert.AreEqual((DiagnosticOutcome.Failed, "application-stopped"), (model.Outcome, model.Code));
    }

    [TestMethod]
    public async Task ModelApplied_GatedDeliveriesCountAsHandledOutput()
    {
        var ct = TestContext.Current.CancellationToken;

        // Not enforceable (a filter that is not an observer): faulted without reaching the model.
        var headlessWorkload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using (var headless = Hex1bTerminal.CreateBuilder().WithWorkload(headlessWorkload).WithHeadless().WithDimensions(20, 3)
                         .AddPresentationFilter(new PassThroughFilter()).Build())
        {
            await Assert.ThrowsAsync<NotSupportedException>(() => headlessWorkload.WriteRequiredIfGeometry("GATED", 20, 3).WaitAsync(TimeSpan.FromSeconds(5), ct));
            await AssertOutputHandledAsync(headless.InputMilestones!, headlessWorkload, "an unsupported gated delivery");
        }

        // A native gated presentation writes the batch itself.
        var driver = new FakeConsoleDriver { TerminalSize = (20, 3) };
        await using var presentation = new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25));
        var nativeWorkload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var native = Hex1bTerminal.CreateBuilder().WithWorkload(nativeWorkload).WithPresentation(presentation).WithDimensions(20, 3).Build();
        using var lifetime = new CancellationTokenSource();
        var run = native.RunAsync(lifetime.Token);
        try
        {
            Assert.AreEqual(NativeDeliveryOutcome.Applied, await nativeWorkload.WriteRequiredIfGeometry("GATED", 20, 3).WaitAsync(TimeSpan.FromSeconds(5), ct));
            await AssertOutputHandledAsync(native.InputMilestones!, nativeWorkload, "a natively delivered gated batch");
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await run.WaitAsync(TimeSpan.FromSeconds(10), ct); } catch (OperationCanceledException) { }
        }
    }

    // A frame whose output mark is the last enqueued item must reach model-applied once the pump has handled it.
    private static async Task AssertOutputHandledAsync(InputMilestoneTracker tracker, Hex1bAppWorkloadAdapter workload, string what)
    {
        var evt = Key();
        tracker.Accept(evt, _ => true);
        tracker.Processed(evt, "app");
        Assert.IsGreaterThan(0L, workload.MilestoneOutputSequence, "fixture: the gated batch carried no output sequence");
        tracker.FramePublished("app", 1, tracker.AcceptedInput, wroteOutput: true, projectionFailed: false, workload.MilestoneOutputSequence);
        var status = await tracker.WaitAsync(DiagnosticMilestone.ModelApplied, tracker.AcceptedInput, TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);
        Assert.IsTrue(status.IsMet, $"{what} was never reported handled, so model-applied {status.Outcome}");
    }

    private sealed class PassThroughFilter : IHex1bTerminalPresentationFilter
    {
        public ValueTask<IReadOnlyList<Hex1b.Tokens.AnsiToken>> OnOutputAsync(IReadOnlyList<Hex1b.Tokens.AppliedToken> appliedTokens, TimeSpan elapsed,
            CancellationToken ct = default) => ValueTask.FromResult<IReadOnlyList<Hex1b.Tokens.AnsiToken>>(appliedTokens.Select(t => t.Token).ToArray());
        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task SendTurn_DiagnosticAndNativeWritersUnderTheInputLockNeverDeadlock()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "order");
        const int Rounds = 300;
        var ranges = new System.Collections.Concurrent.ConcurrentBag<DiagnosticAcceptedInput>();

        // A diagnostic send takes the send turn, then the terminal's input write lock. A native
        // SendInputAsync (attach sessions) and the terminal's replies to the application's cursor
        // position queries take the write lock too.
        var tracker = terminal.InputMilestones!;
        var foreign = 0;
        var sends = Task.Run(async () =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                var range = (await diagnostics.TrackSendAsync(() => terminal.SendInputAsync("ab"u8.ToArray()), "text"))!;
                ranges.Add(range);
                // Checked at once, while the records are still retained.
                for (var id = range.FirstId; id <= range.LastId; id++)
                    if (tracker.Record(id)?.Source != "diagnostic-send")
                        Interlocked.Increment(ref foreign);
            }
        });
        Task natives;
        using (ExecutionContext.SuppressFlow())
            natives = Task.Run(async () =>
            {
                for (var i = 0; i < Rounds; i++)
                    await terminal.SendInputAsync("cd"u8.ToArray());
            });
        var queries = Task.Run(async () =>
        {
            for (var i = 0; i < Rounds; i++)
            {
                workload.Write("\u001b[6n");
                await Task.Yield();
            }
        });
        // Writers that bypass the input write lock pass the turn per event.
        Task keysAndResizes;
        using (ExecutionContext.SuppressFlow())
            keysAndResizes = Task.Run(async () =>
            {
                for (var i = 0; i < Rounds; i++)
                {
                    workload.SendKey(Hex1bKey.K, 'k');
                    await workload.ApplyQueuedResize(21 - i % 2, 3);
                }
            });
        var both = Task.WhenAll(sends, natives, queries, keysAndResizes);

        Assert.AreSame(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken)),
            "a diagnostic send and a native write deadlocked on the send turn and the input write lock");
        await both;
        Assert.IsGreaterThanOrEqualTo(Rounds * 6, tracker.AcceptedInput);
        foreach (var range in ranges)
            Assert.AreEqual(1, range.LastId - range.FirstId, "a send's range is not exactly its two events");
        Assert.AreEqual(0, foreign, "another writer's input landed inside a send's range");
    }

    [TestMethod]
    public async Task SendTurn_ProtocolRepliesTakeTheTurnBeforeTheInputLock()
    {
        var ct = TestContext.Current.CancellationToken;
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var tracker = terminal.InputMilestones!;

        await tracker.WaitForSendTurnAsync(ct);
        using (var send = tracker.BeginSend())
        {
            // The application asks for the cursor position; the terminal's reply is input.
            workload.Write("\u001b[6n");
            await Task.Delay(300, ct);
            var write = terminal.SendInputAsync("a"u8.ToArray(), ct);

            Assert.AreSame(write, await Task.WhenAny(write, Task.Delay(TimeSpan.FromSeconds(5), ct)),
                "the protocol reply held the input write lock while waiting for the send turn");
            await write;
            Assert.AreEqual((1L, 1L), (send.FirstId, send.LastId));
        }

        for (var i = 0; i < 200 && tracker.AcceptedInput < 2; i++)
            await Task.Delay(10, ct);
        Assert.IsGreaterThan(1L, tracker.AcceptedInput, "fixture: the terminal sent no cursor position reply");
    }

    [TestMethod]
    public async Task SendTurn_AWriteForkedInsideASendKeepsTheTurnUntilItFinishes()
    {
        var ct = TestContext.Current.CancellationToken;
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var tracker = terminal.InputMilestones!;
        // Stands in for the send's own long write: the input write lock is held while the fork waits.
        var writeLock = (SemaphoreSlim)typeof(Hex1bTerminal)
            .GetField("_workloadInputWriteLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(terminal)!;
        await writeLock.WaitAsync(ct);

        await tracker.WaitForSendTurnAsync(ct);
        var send = tracker.BeginSend();
        var forked = Task.Run(() => terminal.SendInputAsync("f"u8.ToArray(), ct));
        await Task.Delay(100, ct);
        send.Dispose();
        Task native;
        using (ExecutionContext.SuppressFlow())
            native = Task.Run(() => terminal.SendInputAsync("n"u8.ToArray(), ct));
        await Task.Delay(100, ct);
        writeLock.Release();

        var both = Task.WhenAll(forked, native);
        Assert.AreSame(both, await Task.WhenAny(both, Task.Delay(TimeSpan.FromSeconds(5), ct)),
            "a write forked inside a send lost the turn to a native writer and deadlocked");
        await both;
        Assert.AreEqual(2, tracker.AcceptedInput);
        Assert.IsNull(send.LastId, "a write that outlived its send joined the send's range");
        Assert.AreEqual(("native", "native"), (tracker.Record(1)!.Source, tracker.Record(2)!.Source),
            "input written after the send ended was credited to it");
    }

    [TestMethod]
    public async Task SendTurn_ForksOfAnEndedSendCannotHoldItsTurn()
    {
        var ct = TestContext.Current.CancellationToken;
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "chain");
        var tracker = terminal.InputMilestones!;
        var writeLock = (SemaphoreSlim)typeof(Hex1bTerminal)
            .GetField("_workloadInputWriteLock", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(terminal)!;
        var afterEnd = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var stop = new CancellationTokenSource();

        // The forks' first writes pin the send while it is in progress and are still pinned when
        // it ends (the write lock is held), then the forks keep writing, overlapping.
        await writeLock.WaitAsync(ct);
        await tracker.WaitForSendTurnAsync(ct);
        Task[] forks;
        Task<DiagnosticAcceptedInput?> forkSend;
        var send = tracker.BeginSend();
        // Long writes keep one fork's pin alive while another re-pins.
        var chunk = Enumerable.Repeat((byte)'x', 8192).ToArray();
        forks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
                await terminal.SendInputAsync(chunk, ct);
        })).ToArray();
        forkSend = Task.Run(async () =>
        {
            await afterEnd.Task;
            return await diagnostics.TrackSendAsync(() => terminal.SendInputAsync("s"u8.ToArray()), "text");
        });
        await Task.Delay(100, ct);
        send.Dispose();
        afterEnd.SetResult();
        Task native;
        using (ExecutionContext.SuppressFlow())
            native = Task.Run(() => terminal.SendInputAsync("n"u8.ToArray(), ct));
        await Task.Delay(100, ct);
        writeLock.Release();

        var ended = await Task.WhenAny(native, Task.Delay(TimeSpan.FromSeconds(5), ct));
        await stop.CancelAsync();
        await Task.WhenAll(forks).WaitAsync(TimeSpan.FromSeconds(10), ct);

        Assert.AreSame(native, ended, "forks of an ended send chained their pins and starved a native writer");
        var accepted = await forkSend.WaitAsync(TimeSpan.FromSeconds(10), ct);
        Assert.AreEqual(0, accepted!.LastId - accepted.FirstId, "a fork could not start its own send once its parent had ended");
    }

    [TestMethod]
    public async Task SendTurn_ASendCannotNestInsideAnother()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "nested");
        var tracker = terminal.InputMilestones!;
        await tracker.WaitForSendTurnAsync(TestContext.Current.CancellationToken);
        using var held = tracker.BeginNativeTurn();

        var nested = diagnostics.TrackSendAsync(() => Task.FromResult(true), "key")
            .WaitAsync(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => nested, "a nested send waited for the turn its own flow holds");
    }

    [TestMethod]
    public async Task SendTurn_ATaskForkedDuringAPinnedWriteLosesTheTurnWithThePin()
    {
        var ct = TestContext.Current.CancellationToken;
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "pinfork");
        var tracker = terminal.InputMilestones!;
        var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        await tracker.WaitForSendTurnAsync(ct);
        Task<(bool OwnsTurn, DiagnosticAcceptedInput? Send)> fork;
        using (tracker.BeginSend())
        {
            // As a write under the input lock does: pin the send's turn, and fork a task meanwhile.
            using (tracker.PinOwnTurn())
            {
                fork = Task.Run(async () =>
                {
                    await go.Task;
                    return (tracker.OwnsTurn, await diagnostics.TrackSendAsync(() => terminal.SendInputAsync("f"u8.ToArray()), "text"));
                });
            }
        }

        go.SetResult();
        var (ownsTurn, send) = await fork.WaitAsync(TimeSpan.FromSeconds(5), ct);

        Assert.IsFalse(ownsTurn, "a task forked during a pinned write kept the turn after the pin was released");
        Assert.AreEqual((1L, 1L), (send!.FirstId, send.LastId), "the fork could not start its own send");
    }

    [TestMethod]
    public async Task SendTurn_ASendCannotEndWhileAForkIsAcceptingInsideIt()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = new InputMilestoneTracker();
        var trackerLock = typeof(InputMilestoneTracker)
            .GetField("_sync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .GetValue(tracker)!;
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        // Parks the fork's accept between its ownership check and the write.
        var holder = new Thread(() =>
        {
            lock (trackerLock)
            {
                held.Set();
                release.Wait();
            }
        });
        holder.Start();
        held.Wait(ct);

        await tracker.WaitForSendTurnAsync(ct);
        Task<bool> fork;
        var sendA = tracker.BeginSend();
        fork = Task.Run(() => tracker.Accept(Key(), _ => true));
        await Task.Delay(100, ct);
        sendA.Dispose();
        Task nextTurn;
        using (ExecutionContext.SuppressFlow())
            nextTurn = Task.Run(() => tracker.WaitForSendTurnAsync(ct));
        await Task.Delay(100, ct);
        var nextTookTheTurn = nextTurn.IsCompleted;
        release.Set();
        holder.Join();
        Assert.IsTrue(await fork.WaitAsync(TimeSpan.FromSeconds(5), ct));
        await nextTurn.WaitAsync(TimeSpan.FromSeconds(5), ct);
        tracker.BeginSend().Dispose();

        Assert.IsFalse(nextTookTheTurn, "the next send took the turn while a fork of the ended send was still accepting inside it");
    }

    [TestMethod]
    public async Task Accept_AThrowingWriteLeavesNoRegistrationAndReleasesItsHold()
    {
        var ct = TestContext.Current.CancellationToken;
        var tracker = new InputMilestoneTracker();
        var evt = Key();

        // The same event is still pending from an earlier accept: only the failed occurrence goes.
        Assert.IsTrue(tracker.Accept(evt, _ => true));
        await tracker.WaitForSendTurnAsync(ct);
        using (tracker.BeginSend())
            Assert.ThrowsExactly<InvalidOperationException>(() => tracker.Accept(evt, _ => throw new InvalidOperationException("write failed")));
        // A native accept waits for the turn: it must not be kept by the send's failed write.
        var native = Task.Run(() => tracker.Accept(Key(), _ => throw new InvalidOperationException("write failed")));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => native.WaitAsync(TimeSpan.FromSeconds(2), ct),
            "a failed write in a send kept the send turn");

        Assert.AreEqual(1, tracker.AcceptedInput, "a failed write consumed an id");
        Assert.AreEqual(1L, tracker.IdOf(evt), "a failed write removed another occurrence's registration, or kept its own");
        tracker.Processed(evt, "app");
        Assert.IsNull(tracker.IdOf(evt), "a failed write stayed registered");
        // The send's hold and the native turn were both given back.
        Assert.IsTrue(tracker.WaitForSendTurnAsync(ct).Wait(TimeSpan.FromSeconds(2), ct), "a failed write kept the send turn");
        tracker.BeginSend().Dispose();
    }

    [TestMethod]
    public async Task SendTurn_WaitIsCancellable()
    {
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(terminal, "cancel");
        var tracker = terminal.InputMilestones!;
        await tracker.WaitForSendTurnAsync(TestContext.Current.CancellationToken);
        using var held = tracker.BeginSend();
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        Task<DiagnosticAcceptedInput?> blocked;
        using (ExecutionContext.SuppressFlow())
            blocked = Task.Run(() => diagnostics.TrackSendAsync(() => Task.FromResult(true), "key", cts.Token));
        var ended = await Task.WhenAny(blocked, Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken));

        Assert.AreSame(blocked, ended, "a send waiting for the turn ignored its cancellation");
        await Assert.ThrowsAsync<OperationCanceledException>(() => blocked);
    }

    [TestMethod]
    public async Task SendScope_ATaskForkedInsideASendIsNativeOnceTheSendEnds()
    {
        var tracker = new InputMilestoneTracker();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task<bool> forked;
        InputMilestoneTracker.SendScope scope;
        await tracker.WaitForSendTurnAsync(TestContext.Current.CancellationToken);
        using (scope = tracker.BeginSend())
        {
            tracker.Accept(Key(), _ => true);
            forked = Task.Run(async () =>
            {
                await release.Task;
                return tracker.Accept(Key(), _ => true);
            });
        }

        release.SetResult();
        Assert.IsTrue(await forked.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));

        Assert.AreEqual((1L, 1L), (scope.FirstId, scope.LastId), "an ended send kept collecting ids");
        Assert.AreEqual("native", tracker.Record(2)!.Source);
    }

    [TestMethod]
    public async Task OutputMark_NamesOnlyEnqueuedItems()
    {
        await using var adapter = new Hex1bAppWorkloadAdapter(maxQueuedOutputItems: 1) { InputMilestones = new InputMilestoneTracker() };
        adapter.Write("a");
        using var cts = new CancellationTokenSource();

        var write = Task.Run(async () => await adapter.WriteTokensWithBytesAsync([], "b"u8.ToArray(), cancellationToken: cts.Token));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        var markWhileBlocked = adapter.MilestoneOutputSequence;
        await cts.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => write);

        Assert.IsFalse(write.IsCompletedSuccessfully, "fixture: the second write was enqueued");
        Assert.AreEqual(1, markWhileBlocked, "a frame could name an output item still waiting for a channel slot");
    }

    private static Hex1bKeyEvent Key() => new(Hex1bKey.X, 'x', Hex1bModifiers.None);

    // Holds, releases or faults the terminal's output pump from a workload filter, which the pump
    // awaits after applying each output batch.
    private sealed class GatingFilter : IHex1bTerminalWorkloadFilter
    {
        private volatile TaskCompletionSource? _gate;
        private volatile bool _fail;

        public void Close() => _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        public void Open() => _gate?.TrySetResult();

        public void Fail()
        {
            _fail = true;
            Open();
        }

        public async ValueTask OnOutputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            if (_gate is { } gate)
                await gate.Task.ConfigureAwait(false);
            if (_fail)
                throw new InvalidOperationException("injected output pump failure");
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
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

        public static async Task<GatedApp> StartAsync(bool diagnostics = true,
            Func<GatedApp, Hex1bWidget>? content = null, IHex1bTerminalWorkloadFilter? filter = null)
        {
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = diagnostics };
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(30, 4);
            if (filter is not null)
                builder.AddWorkloadFilter(filter);
            var terminal = builder.Build();
            var harness = new GatedApp(workload, terminal);
            harness.App = new Hex1bApp(async _ =>
            {
                // A held build keeps the loop inside a render pass, so input accepted meanwhile
                // is processed only after that pass publishes its frame.
                if (harness._holdBuild is { } hold)
                {
                    harness._buildHeld.TrySetResult();
                    await hold.Task;
                }

                return content?.Invoke(harness) ?? new ButtonWidget("gate").OnClick(async _ =>
                {
                    harness._entered.TrySetResult();
                    await harness._gate.Task;
                });
            }, new Hex1bAppOptions { WorkloadAdapter = workload });
            harness.App.FrameRendered += () =>
            {
                if (harness.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame is { } frame)
                    harness.Published.Enqueue((frame.FrameId, frame.ProcessedInput ?? 0));
            };
            harness._run = harness.App.RunAsync(harness._cts.Token);
            for (var i = 0; i < 500 && harness.App.FrameCount < 1; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            return harness;
        }

        public async Task<long> SendAsync(Hex1bKey key)
        {
            var accepted = await Diagnostics.TrackSendAsync(() =>
                Terminal.SendEventAsync(new Hex1bKeyEvent(key, key == Hex1bKey.X ? 'x' : '\0', Hex1bModifiers.None)), "key");
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

        public async Task WaitForQuietAsync()
        {
            var last = -1L;
            while (App.FrameCount != last)
            {
                last = App.FrameCount;
                await Task.Delay(150, TestContext.Current.CancellationToken);
            }
        }

        // Frames as published, with their processed-input watermark (recorded on the app loop).
        public System.Collections.Concurrent.ConcurrentQueue<(long FrameId, long ProcessedInput)> Published { get; } = new();

        private TaskCompletionSource? _holdBuild;
        private TaskCompletionSource _buildHeld = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task HoldNextBuildAsync()
        {
            _buildHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _holdBuild = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            App.Invalidate();
            await _buildHeld.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        public void ReleaseBuild()
        {
            var hold = _holdBuild;
            _holdBuild = null;
            hold?.TrySetResult();
        }

        public async Task<long> SendTextAsync(string text)
        {
            var accepted = await Diagnostics.TrackSendAsync(() =>
                Terminal.SendEventAsync(new Hex1bKeyEvent(Hex1bKey.X, text, Hex1bModifiers.None)), "key");
            return accepted!.LastId;
        }

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
