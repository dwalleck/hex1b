using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using System.Reflection;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Ticket 09: a case started on a terminal that has already applied output owns a text-state/1 start checkpoint,
// taken in the arming hold, or names why it cannot.
public partial class DiagnosticCaseTests
{
    [TestMethod]
    public async Task Start_LiveCaseOwnsStartCheckpoint()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        string? atArming = null;
        long armedAt;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "\u001b[1;31mstyled\u001b[m \u001b[3;20r\u001b)0\u000eqq\u000f\u001b[2;5HQ");
            // Oracle: a projection taken in the same hold, just before the start's (the lock is re-entrant).
            DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = () => atArming = JsonSerializer.Serialize(
                terminal.CaptureModelState(), DiagnosticsJsonContext.Default.DiagnosticModelState);
            try
            {
                armedAt = terminal.CurrentModelSequence;
                path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
                {
                    Directory = root.Path,
                    Authorizations = [DiagnosticAuthorization.ReapplicationData],
                }).Path!;
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = null;
            }
            await workload.WriteAndWaitAsync(terminal, "\u001b[3bafter");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/1", "complete", armedAt), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString(),
            checkpoint.GetProperty("modelSequence").GetInt64()));
        Assert.AreEqual(0, checkpoint.GetProperty("unsupportedSurfaces").GetArrayLength());
        Assert.AreEqual(FreshModelCheckpoint.CoveredSurfaces.Count, checkpoint.GetProperty("coveredSurfaces").GetArrayLength());

        var first = artifact.Events.First(e => e.GetProperty("stream").GetString() == "case");
        var line = first.GetProperty("checkpoint");
        Assert.AreEqual(("checkpoint", 1L, "start", "start", "recorded", armedAt),
            (first.GetProperty("kind").GetString(), line.GetProperty("ordinal").GetInt64(), line.GetProperty("label").GetString(),
                line.GetProperty("trigger").GetString(), line.GetProperty("status").GetString(), first.GetProperty("modelSequence").GetInt64()));
        Assert.IsTrue(line.TryGetProperty("captureMilliseconds", out _), "the start carries no capture duration");
        Assert.IsNotNull(atArming, "fixture: the oracle projection was not taken");
        Assert.AreEqual(atArming, line.GetProperty("state").GetRawText(), "the start state is not the model at arming");
        Assert.IsTrue(artifact.ModelEvents().All(e => e.GetProperty("modelSequence").GetInt64() > armedAt), "a model event at or before the start was recorded");
        Assert.IsNotEmpty(artifact.ModelEvents(), "fixture: nothing was recorded after the start");
    }

    [TestMethod]
    public async Task Start_UnsupportedSurfaceStillRecords()
    {
        // Retained rows (ticket 10), a title (ticket 11) and a DCS in progress at once (the scalar before it is complete):
        // the one refused surface is named, and the case records.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithScrollback(100).Build();
        string path;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n")) + "\u001b]2;T\u0007");
            await workload.WriteAndWaitAsync(terminal, [.. "ok \u6f22\u001bP$q"u8]);
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, "m\u001b\\ after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/1", "unsupported"), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()));
        Assert.AreEqual("dcs-continuation",
            string.Join(",", checkpoint.GetProperty("unsupportedSurfaces").EnumerateArray().Select(s => s.GetString())));
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "unsupported-surfaces:");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "an unsupported start wrote a start line");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case stopped recording");
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        // Re-application of an unsupported start is refused before anything is written.
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-valid-interval"), (refused.Outcome, refused.Problem?.Code), refused.Problem?.Message);
        StringAssert.Contains(refused.Problem!.Message, "dcs-continuation");
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "a refused re-application wrote a run");
    }

    [TestMethod]
    public async Task Start_InputDuringArmingIsNotModelIngress()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        Task? typed = null;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "prompt> ");
            // Typed on another thread while the arming holds the model lock, as input arrives.
            DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = () =>
            {
                typed = Task.Run(() => terminal.SendInputAsync(Encoding.UTF8.GetBytes("TYPED")));
                Thread.Sleep(50);
            };
            try
            {
                path = StartLive(terminal, root);
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = null;
            }
            await typed!;
            // Positive control: output written after arming is model ingress.
            await workload.WriteAndWaitAsync(terminal, "ECHOED");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        var model = artifact.ModelEvents().Select(e => (e.GetProperty("kind").GetString(),
            e.TryGetProperty("data", out var data) ? Encoding.UTF8.GetString(Convert.FromBase64String(data.GetString()!)) : null)).ToList();
        // Exactly the output written after arming (the positive control); nothing from the input.
        CollectionAssert.AreEqual(new[] { ("application", (string?)"ECHOED") }, model, "the model stream holds more than the output: " + string.Join(", ", model));
        var start = artifact.Events.First(e => e.GetProperty("stream").GetString() == "case").GetProperty("checkpoint").GetProperty("state");
        var screen = string.Concat(start.GetProperty("screen").EnumerateArray().SelectMany(row => row.GetProperty("cells").EnumerateArray())
            .Select(cell => cell.GetProperty("t").GetString()));
        StringAssert.Contains(screen, "prompt>", "fixture: the start state's screen text was not read");
        Assert.DoesNotContain("TYPED", screen, "input during arming entered the start state");
    }

    [TestMethod]
    public async Task Start_OverBudgetTakesNoProjection()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long before, after;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "populated");
            DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = 1024;
            try
            {
                before = terminal.ModelStateCapturesForTesting;
                path = StartLive(terminal, root);
                after = terminal.ModelStateCapturesForTesting;
            }
            finally
            {
                DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = null;
            }
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.AreEqual(before, after, "a start over the budget took a projection");
        var checkpoint = Artifact.Read(path).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "pending-state budget:");
    }

    [TestMethod]
    public async Task Start_UnauthorizedTakesNoProjection()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long before, after;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "populated");
            before = terminal.ModelStateCapturesForTesting;
            path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Path!;
            after = terminal.ModelStateCapturesForTesting;
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.AreEqual(before, after, "an unauthorized start took a projection");
        Assert.AreEqual("excluded", Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task Start_ProjectionFailureStillArms()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        DiagnosticCaseResult result;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "populated");
            // A long message: the reason is bounded, as a mark's or stop's failure is.
            DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = () => throw new InvalidOperationException("injected" + new string('x', 5000));
            try
            {
                result = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
                {
                    Directory = root.Path,
                    Authorizations = [DiagnosticAuthorization.ReapplicationData],
                });
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = null;
            }
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsNotNull(result.Path, $"arming failed: {result.Problem?.Message}");
        var artifact = Artifact.Read(result.Path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        var reason = checkpoint.GetProperty("reason").GetString()!;
        StringAssert.StartsWith(reason, "capture-failed: InvalidOperationException: injectedxxx");
        Assert.IsLessThanOrEqualTo(DiagnosticCaseRecorder.MaxFailureMessage, reason.Length, $"the reason is unbounded ({reason.Length} characters)");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case did not record after a failed start projection");
    }

    [TestMethod]
    public async Task Start_NoCaseTakesNoProjection()
    {
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 30).Select(i => $"line {i}\r\n")));
            terminal.Resize(30, 8);
            await workload.WriteAndWaitAsync(terminal, "more");
        }
        Assert.AreEqual(0L, terminal.ModelStateCapturesForTesting, "a terminal without a case took a projection");
    }

    [TestMethod]
    public async Task Reader_LiveStartMapsLossBySequence()
    {
        // A live start at model sequence S records model ordinal n as sequence S + n. With the event of ordinal 3
        // lost, the interval runs from S to S + 2; an ordinal read as a sequence would end it at 2.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long start;
        using (new Running(terminal))
        {
            foreach (var chunk in new[] { "one ", "two ", "three " })
                await workload.WriteAndWaitAsync(terminal, chunk);
            start = terminal.CurrentModelSequence;
            path = StartLive(terminal, root);
            foreach (var chunk in new[] { "a", "b", "c", "d" })
                await workload.WriteAndWaitAsync(terminal, chunk);
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var events = Path.Combine(path, "events.jsonl");
        var lines = File.ReadAllLines(events);
        var lost = lines.Single(l => l.Contains("\"stream\":\"model\"", StringComparison.Ordinal) && l.Contains("\"ordinal\":3,", StringComparison.Ordinal));
        File.WriteAllText(events, string.Join("\n", lines.Where(l => l != lost)) + "\n");

        var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Intervals.Single();
        Assert.AreEqual((true, start, start + 2, "model-events-missing"),
            (interval.Valid, interval.FromModelSequence, interval.ToModelSequence, interval.EndReason));
    }

    // Continuation the start's viewport does not show, then input that uses it (evidence P2).
    private const string LiveLeave = "\u001b[1;31mstyled\u001b[m \u001b]8;id=a;https://x.test/a\u001b\\link\u001b]8;;\u001b\\ " +
        "\u001b[3g\u001b[1;7H\u001bH\u001b)0\u000e\u001b[3;9r\u001b[4;4H\u001b7\u001b[32m\u001b[1\"q\u001b[2;40HW";
    private const string LiveReveal = "Z\u001b[3bq\u000fq\r\tT\u001b8S\u001b[9;1H\n\n\nend";

    [TestMethod]
    public async Task Reapply_LiveStartMatchesAtEveryCheckpoint()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long start;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, LiveLeave);
            start = terminal.CurrentModelSequence;
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, LiveReveal);
            Assert.AreEqual(DiagnosticOutcome.Captured, new TerminalDiagnostics(terminal).MarkCase("revealed").Outcome);
            terminal.Resize(30, 8);
            await workload.WriteAndWaitAsync(terminal, "resized\r\n");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        foreach (var label in new[] { "start", "revealed", "stop" })
        {
            var result = Reapply(path, label: label);
            AssertMatched(result, $"raw {label}");
            Assert.AreEqual((DiagnosticCaseCheckpointProfiles.TextState, (long?)start), (result.Checkpoint!.Profile, result.Checkpoint.ModelSequence),
                $"{label}: the result does not name the start it restored");
        }
    }

    [TestMethod]
    public async Task Reapply_LiveStartHex1bApplication()
    {
        // A Hex1b application runs on the alternate screen; started without a case, its start restores both screens.
        using var root = new CaseRoot();
        await using var app = await TrackedApp.StartAsync();
        await app.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("before the case"));
        await Settle(app.Terminal);
        var appStarted = app.Diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
        Assert.AreEqual("complete", appStarted.Checkpoint?.Status is { } status ? DiagnosticContractNames.Of(status) : null,
            $"fixture: the application's start is not restorable: {appStarted.Checkpoint?.Reason}");
        await app.Terminal.SendInputAsync(Encoding.UTF8.GetBytes(" typed after"));
        await Settle(app.Terminal);
        Assert.AreEqual(DiagnosticOutcome.Captured, app.Diagnostics.MarkCase("typed").Outcome);
        // A resize on the alternate screen re-renders the application.
        app.Terminal.Resize(30, 6);
        await Settle(app.Terminal);
        Assert.AreEqual(DiagnosticOutcome.Captured, app.Diagnostics.MarkCase("resized").Outcome);
        await app.Diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(appStarted.Path!);
        Assert.IsNotEmpty(artifact.ModelEvents(), "fixture: the application wrote nothing after the start");
        var startState = artifact.Events.First(e => e.GetProperty("stream").GetString() == "case").GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual("alternate", startState.GetProperty("activeBuffer").GetString(), "fixture: the application's start is not on the alternate screen");
        foreach (var label in new[] { "start", "typed", "resized", "stop" })
            AssertMatched(Reapply(appStarted.Path!, label: label), $"app {label}");
    }

    [TestMethod]
    [DataRow(false, false)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(true, true)]
    public async Task Start_PartitionsInFlightApplicationAndResize(bool resizeFirst, bool pendingScalar)
    {
        // With pendingScalar (ticket 12), the model holds the first byte of a scalar at arming and the in-flight chunk
        // completes it: the start owns the byte, the chunk is the first application, and nothing is fed twice.
        // While the arming holds the model lock, just before the start projection, the pump reads and tokenizes a
        // chunk (it uses the last printed cell: REP) and waits for the lock, and a resize waits too. The test does not
        // hold the lock itself, so an arming that released it between its projection and its registration would let
        // them in unrecorded. Each must be in the start or recorded after it, once.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long start = -1;
        Task resize = Task.CompletedTask;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, pendingScalar ? [.. "\u001b[1;33mbefore Q"u8, 0xe6] : "\u001b[1;33mbefore Q"u8.ToArray());
            var first = 1;
            DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = () =>
            {
                if (Interlocked.Exchange(ref first, 0) != 1)
                    return;
                start = terminal.CurrentModelSequence;
                if (resizeFirst)
                {
                    resize = Task.Run(() => terminal.Resize(36, 12));
                    Thread.Sleep(50);
                }
                var read = terminal.OutputBytesRead;
                workload.Enqueue(pendingScalar ? [0xbc, 0xa2, .. " in-flight\u001b[3b"u8] : Encoding.UTF8.GetBytes(" in-flight\u001b[3b"));
                SpinWait.SpinUntil(() => terminal.OutputBytesRead > read, TimeSpan.FromSeconds(5));
                Thread.Sleep(20);
                if (!resizeFirst)
                    resize = Task.Run(() => terminal.Resize(36, 12));
                Thread.Sleep(50);
            };
            try
            {
                path = StartLive(terminal, root);
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = null;
            }
            await resize;
            await WaitAsync(() => terminal.CurrentModelSequence >= start + 2);
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.AreNotEqual(-1L, start, "fixture: the start hook never ran");
        var artifact = Artifact.Read(path);
        var model = artifact.ModelEvents();
        Assert.AreEqual(start + 1, model[0].GetProperty("modelSequence").GetInt64(), "the first recorded event is not the start's next");
        var kinds = model.Take(2).Select(e => e.GetProperty("kind").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "application", "resize" }, kinds, "the in-flight application and resize were not both recorded after the start");
        if (pendingScalar)
        {
            Assert.AreEqual("5g==", artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("utf8").GetString(), "the start does not own the pending byte");
            var application = model.First(e => e.GetProperty("kind").GetString() == "application");
            CollectionAssert.AreEqual(new byte[] { 0xbc, 0xa2 }, Convert.FromBase64String(application.GetProperty("data").GetString()!)[..2], "the in-flight chunk's bytes were regrouped");
        }
        AssertMatched(Reapply(path, label: "stop"), resizeFirst ? "resize first" : "application first");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_RecordsAnApplicationReadBeforeArming(bool resizeFirst)
    {
        // The pump has read, stashed and tokenized a chunk (it uses the last printed cell: REP) before the arming begins,
        // and waits for the model lock the test holds; a resize waits too. The case is armed in that window: the chunk's
        // stashed bytes must survive the arming and be recorded after the start, once.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string path;
        long start;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "\u001b[1;33mbefore Q");
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
            Task resize;
            lock (modelLock)
            {
                resize = Task.CompletedTask;
                if (resizeFirst)
                {
                    resize = Task.Run(() => terminal.Resize(36, 12));
                    Thread.Sleep(50);
                }
                var read = terminal.OutputBytesRead;
                workload.Enqueue(Encoding.UTF8.GetBytes(" in-flight\u001b[3b"));
                Assert.IsTrue(SpinWait.SpinUntil(() => terminal.OutputBytesRead > read, TimeSpan.FromSeconds(5)), "fixture: the chunk was never read");
                Thread.Sleep(20);
                if (!resizeFirst)
                    resize = Task.Run(() => terminal.Resize(36, 12));
                Thread.Sleep(50);
                start = terminal.CurrentModelSequence;
                path = StartLive(terminal, root);
            }
            await resize;
            await WaitAsync(() => terminal.CurrentModelSequence >= start + 2);
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var model = Artifact.Read(path).ModelEvents();
        Assert.AreEqual(start + 1, model[0].GetProperty("modelSequence").GetInt64(), "the first recorded event is not the start's next");
        var kinds = model.Take(2).Select(e => e.GetProperty("kind").GetString()).ToList();
        CollectionAssert.AreEquivalent(new[] { "application", "resize" }, kinds, "the in-flight application and resize were not both recorded after the start");
        AssertMatched(Reapply(path, label: "stop"), resizeFirst ? "resize first" : "application first");
    }

    [TestMethod]
    public async Task Reapply_LiveStartAcrossSynchronizedUpdate()
    {
        // Armed inside an open synchronized update that times out after the start: the restored update times out on
        // the replica's virtual clock at the recorded model sequence.
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(30, 5),
            WorkloadAdapter = workload,
            Width = 30,
            Height = 5,
            TimeProvider = clock,
        });
        await workload.WriteAndWaitAsync(terminal, "text\u001b[?2026hinside");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, " more");
        var before = terminal.CurrentModelSequence;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        await workload.WriteAndWaitAsync(terminal, " after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual(1, Artifact.Read(path).ModelEvents().Count(e => e.GetProperty("kind").GetString() == "synchronized-update-timeout"),
            "fixture: the timeout was not recorded after the start");
        AssertMatched(Reapply(path, label: "stop"), "across the restored update");
    }

    [TestMethod]
    public async Task Reapply_LiveStartWithoutStartStateUnavailable()
    {
        using var root = new CaseRoot();
        var path = await RecordLiveAsync(root);
        foreach (var (name, change) in new (string, Action<string>)[]
        {
            ("no state", copy => EditEventLine(copy, IsStart, node => node["checkpoint"]!.AsObject().Remove("state"))),
            ("no line", copy => RemoveEventLine(copy, IsStart)),
            ("wrong sequence", copy => EditEventLine(copy, IsStart, node => node["modelSequence"] = node["modelSequence"]!.GetValue<long>() + 1)),
        })
        {
            var copy = CopyCase(root, path, name.Replace(' ', '-'));
            change(copy);
            // Inspection sees no verified start with state, so there is no re-applicable interval.
            var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = copy }).Intervals.Single();
            Assert.IsFalse(interval.Valid, $"{name}: inspection reports a valid interval without a start");
            StringAssert.StartsWith(interval.EndReason, "start-missing:", name);
            var result = Reapply(copy, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-valid-interval"), (result.Outcome, result.Problem?.Code), $"{name}: {result.Problem?.Message}");
            StringAssert.Contains(result.Problem!.Message, "start-missing", name);
            Assert.IsFalse(Directory.Exists(Path.Combine(copy, "reapplications")), $"{name}: a refused re-application wrote a run");
        }
    }

    [TestMethod]
    [DataRow("titles", "titles", "incompatible")]
    [DataRow("pending input", "pendingInput", "incompatible")]
    [DataRow("dcs-continuation", "unsupported", "unsupported-start")]
    [DataRow("command mark", "commandMarks", "incompatible")]
    public async Task Reapply_OutOfSurfaceStartRefused(string surface, string field, string code)
    {
        // A start state the restore cannot represent, in a manifest that claims it complete: refused before anything.
        // Titles, marks (ticket 11) and pending input (ticket 12) are restorable, so their rows are malformed ones,
        // refused as incompatible; a DCS in progress is still an unsupported surface.
        using var root = new CaseRoot();
        var copy = CopyCase(root, await RecordLiveAsync(root), surface);
        EditEventLine(copy, IsStart, node =>
        {
            var state = node["checkpoint"]!["state"]!.AsObject();
            switch (field)
            {
                case "titles":
                    state["titles"]!["window"] = null;
                    break;
                case "commandMarks":
                    state["commandMarks"] = JsonNode.Parse("""[{"anchor":"1","phase":"prompt-start","rawParameters":"A"}]""");
                    break;
                case "unsupported":
                    state["unsupported"] = JsonNode.Parse("""["dcs-continuation"]""");
                    break;
                default:
                    // Not an unfinished sequence: no ESC introduces it.
                    state["pendingInput"]!["escapePrefix"] = "[";
                    break;
            }
        });
        var result = Reapply(copy, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, code), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        StringAssert.Contains(result.Problem!.Message, surface);
        Assert.IsFalse(Directory.Exists(Path.Combine(copy, "reapplications")), "a refused re-application wrote a run");
    }

    [TestMethod]
    public async Task Reapply_TargetBeforeStartInvalid()
    {
        using var root = new CaseRoot();
        var path = await RecordLiveAsync(root);
        var start = Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("modelSequence").GetInt64();
        var result = Reapply(path, modelSequence: start - 1);
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "unknown-model-sequence"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        AssertMatched(Reapply(path, modelSequence: start), "the start itself");
    }

    [TestMethod]
    public async Task Start_InsideAnApplicationIsUnsupported()
    {
        // Armed from a callback inside an application: the model is half applied, so no start state is taken.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        DiagnosticCaseResult? started = null;
        terminal.WindowTitleChanged += _ => started ??= new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before \u001b]2;armed here\u0007 rest");
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsNotNull(started?.Path, "fixture: the callback did not arm a case");
        var checkpoint = Artifact.Read(started.Path).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "mid-application:");
    }

    // Each continuation fault on a live start's stop: different, labelled, with its path among the differences.
    [TestMethod]
    [DataRow("pending-wrap", "cursor.pendingWrap")]
    [DataRow("last-printed", "lastPrinted.x")]
    [DataRow("rendition", "rendition.attributes")]
    [DataRow("margins", "margins.top")]
    [DataRow("saved-cursor", "savedCursor.x")]
    [DataRow("pending-grapheme", "pendingGraphemeCombine")]
    [DataRow("activity", "activity.progressPercentage")]
    [DataRow("synchronized-update", "synchronizedUpdate.active")]
    public async Task Reapply_EachContinuationFaultDiffers(string fault, string path)
    {
        Assert.AreEqual($"Fault '{fault}' takes no target.", ModelStateFault.Validate($"{fault}:1"));
        using var root = new CaseRoot();
        var casePath = await RecordLiveAsync(root);
        var result = Reapply(new DiagnosticCaseReapplyRequest { Path = casePath, ToLabel = "stop", Faults = [fault] });
        Assert.AreEqual(("different", true), (result.Comparison, result.FaultInjected), $"{fault}: {result.Problem?.Message} {result.ComparisonReason}");
        Assert.AreEqual(path, result.Faults!.Single().Path);
        CollectionAssert.Contains(result.Differences!.Differences.Select(d => d.Path).ToList(), path, $"{fault}: its path is not among the differences");
        // The recorded path of the same case is unchanged.
        AssertMatched(Reapply(casePath, label: "stop"), $"{fault}: the unchanged path");
    }

    [TestMethod]
    public async Task Reapply_LiveStartAfterClearedScrollback()
    {
        // A shell that scrolled and cleared its scrollback (ESC[3J) before the start: complete, and rows scrolled after
        // the start re-apply with the identities the original continued.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithScrollback(100).Build();
        string path;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n")) + "\u001b[H\u001b[2J\u001b[3J$ ");
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 12).Select(i => $"after {i}\r\n")));
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.AreEqual("complete", Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        AssertMatched(Reapply(path, label: "start"), "the start");
        AssertMatched(Reapply(path, label: "stop"), "rows scrolled after the start");
    }

    [TestMethod]
    [DataRow("geometry", 600, 200)]
    [DataRow("styles", 100, 100)]
    public async Task Start_TooLargeForTheCaseIsUnsupported(string kind, int width, int height)
    {
        // A start whose state cannot fit the case's size bound is refused at arming, not written as a missing line
        // under a manifest that claims it complete. "geometry": the estimate alone refuses it, without a projection;
        // "styles": a distinct truecolor style per cell fits the estimate but not the bound (review RR2-1).
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(width, height).Build();
        string path;
        long before, after;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, kind == "geometry" ? "populated" : string.Concat(Enumerable.Range(0, width * height)
                .Select(i => $"\u001b[38;2;{i % 256};{i / 256 % 256};7m\u001b[48;2;9;{i % 251};{i % 239}m\u2580")));
            before = terminal.ModelStateCapturesForTesting;
            path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                MaxBytes = 1024 * 1024,
                Authorizations = [DiagnosticAuthorization.ReapplicationData],
            }).Path!;
            after = terminal.ModelStateCapturesForTesting;
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        if (kind == "geometry")
            Assert.AreEqual(before, after, "a start that the estimate refuses took a projection");
        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "size-limit:");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "a start line was written");
        Assert.IsFalse(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Intervals.Single().Valid);
        if (kind == "styles")
            StringAssert.Contains(checkpoint.GetProperty("reason").GetString(), "bytes; projected and measured in", "a start refused after projecting does not disclose its cost");
    }

    [TestMethod]
    public async Task Start_AfterUnappliedOutputIsUnsupported()
    {
        // A batch tokenized but refused without a model event (a geometry-gated refusal) moves the live decoder past
        // the committed continuation; a start before the next application cannot hold that continuation.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        string refusedPath, laterPath;
        using (new Running(terminal))
        {
            // The model holds a pending scalar (restorable since ticket 12); the unapplied batch still refuses the start.
            await workload.WriteAndWaitAsync(terminal, [.. "before "u8, 0xe6]);
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
            lock (modelLock)
            {
                // The pump's notification for a batch it tokenized and refused (Hex1bTerminal's geometry-gated refusal).
                typeof(Hex1bTerminal).GetMethod("NotifyCaseUnappliedOutputUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(terminal, []);
                refusedPath = StartLive(terminal, root);
            }
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            // Positive control: after an application commits the continuation, a start is complete again.
            await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. "\u001b[1mred"u8]);
            laterPath = StartLive(terminal, root);
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var refused = Artifact.Read(refusedPath).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", refused.GetProperty("status").GetString());
        StringAssert.StartsWith(refused.GetProperty("reason").GetString(), "unapplied-output:");
        Assert.AreEqual("complete", Artifact.Read(laterPath).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
    }

    [TestMethod]
    public void FreshModelCheckpoint_RefusesToDescribeAModelThatIsNotFresh()
    {
        var configuration = new DiagnosticCaseModelConfiguration();
        Assert.ThrowsExactly<ArgumentException>(() => FreshModelCheckpoint.Describe(fresh: false, authorized: true, unsupported: null, configuration));
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Complete, FreshModelCheckpoint.Describe(true, true, null, configuration).Status);
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Excluded, FreshModelCheckpoint.Describe(false, false, null, configuration).Status);
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Unsupported, FreshModelCheckpoint.Describe(false, true, "hmp1-workload", configuration).Status);
    }

    [TestMethod]
    public async Task Start_LineIsWrittenBeforeEveryModelEvent()
    {
        // The start is sized for the room after the manifest (review RR2-1), so the writer writes it first: model events
        // queued before the writer's first pass follow it in the file.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        using var gate = new ManualResetEventSlim(false);
        string path;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before");
            DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
            try
            {
                path = StartLive(terminal, root);
            }
            finally
            {
                DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            }
            // Queued while the writer's first pass is held.
            await workload.WriteAndWaitAsync(terminal, " one");
            await workload.WriteAndWaitAsync(terminal, " two");
            gate.Set();
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var first = Artifact.Read(path).Events[0];
        Assert.AreEqual(("case", "checkpoint", "start"), (first.GetProperty("stream").GetString(), first.GetProperty("kind").GetString(),
            first.GetProperty("checkpoint").GetProperty("trigger").GetString()), "the start line is not the first event line");
    }

    [TestMethod]
    public async Task Start_SizedWithItsManifest()
    {
        // A start that fits the events tier on its own, but not with a large manifest (a long application name): the
        // start is unsupported at the size limit and no start line is written (review RR3-2). Its control: the same
        // terminal and bound with a short name records a complete start.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(200, 100).Build();
        string refusedPath, fittingPath;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(0, 60).Select(i => $"\u001b[{i % 7 + 31}m{new string('w', 150)}\u001b[m\r\n")).TrimEnd('\n', '\r'));
            var request = new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                MaxBytes = 1024 * 1024,
                Authorizations = [DiagnosticAuthorization.ReapplicationData],
            };
            refusedPath = new TerminalDiagnostics(terminal, new string('n', 800_000)).StartCase(request).Path!;
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            fittingPath = new TerminalDiagnostics(terminal, "short").StartCase(request).Path!;
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var refused = Artifact.Read(refusedPath);
        var checkpoint = refused.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "size-limit:");
        Assert.IsFalse(refused.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "a start line was written");
        var fitting = Artifact.Read(fittingPath);
        Assert.AreEqual("complete", fitting.Manifest.GetProperty("checkpoint").GetProperty("status").GetString(), "fixture: the control's start does not fit");
        Assert.AreEqual("recorded", fitting.Events[0].GetProperty("checkpoint").GetProperty("status").GetString());
    }

    [TestMethod]
    public void StartCheckpoint_FitsAtTheBoundary()
    {
        // The manifest, the line's allowance and the state's exact bytes are subtracted once from the events tier.
        const long maxBytes = 1024 * 1024;
        var room = maxBytes - DiagnosticCaseRecorder.EventReserve - DiagnosticCaseRecorder.StartLineAllowance - 5_000;
        Assert.IsTrue(StartCheckpoint.Fits(5_000, new DiagnosticCaseRecorder.CheckpointCapture(null, null, "recorded", null, 0, room), maxBytes));
        Assert.IsFalse(StartCheckpoint.Fits(5_000, new DiagnosticCaseRecorder.CheckpointCapture(null, null, "recorded", null, 0, room + 1), maxBytes));
    }

    private static bool IsStart(JsonNode node) => node["checkpoint"]?["trigger"]?.GetValue<string>() == "start";

    private static async Task<string> RecordLiveAsync(CaseRoot root)
    {
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, LiveLeave);
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, LiveReveal);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        return path;
    }

    private static string CopyCase(CaseRoot root, string path, string name)
    {
        var copy = Path.Combine(root.Path, "copy-" + name);
        if (OperatingSystem.IsWindows())
            Directory.CreateDirectory(copy);
        else
            Directory.CreateDirectory(copy, OwnerDirectory);
        foreach (var file in Directory.GetFiles(path))
            File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
        return copy;
    }

    private static void RemoveEventLine(string path, Func<JsonNode, bool> select)
    {
        var file = Path.Combine(path, "events.jsonl");
        var lines = File.ReadAllLines(file);
        var kept = lines.Where(line => !select(JsonNode.Parse(line[9..])!)).ToList();
        Assert.AreEqual(lines.Length - 1, kept.Count, "fixture: not exactly one line removed");
        File.WriteAllText(file, string.Join("\n", kept) + "\n");
    }

    private static string StartLive(Hex1bTerminal terminal, CaseRoot root) =>
        new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
}
