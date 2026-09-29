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
        // Retained rows, a title and pending UTF-8 at once: each is named, in order, and the case records.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithScrollback(100).Build();
        string path;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 14).Select(i => $"{i}\r\n")) + "\u001b]2;T\u0007");
            await workload.WriteAndWaitAsync(terminal, [.. "ok "u8, 0xe6, 0xbc]);
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, [0xa2, .. " after"u8]);
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/1", "unsupported"), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()));
        Assert.AreEqual("retained-history,titles,pending-input",
            string.Join(",", checkpoint.GetProperty("unsupportedSurfaces").EnumerateArray().Select(s => s.GetString())));
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "unsupported-surfaces:");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "an unsupported start wrote a start line");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case stopped recording");
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
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
            DiagnosticCaseRecorder.BeforeStartCaptureForTesting.Value = () => throw new InvalidOperationException("injected");
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
        Assert.AreEqual(("unsupported", "capture-failed: InvalidOperationException: injected"),
            (checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("reason").GetString()));
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
    [DataRow(false)]
    [DataRow(true)]
    public async Task Start_PartitionsInFlightApplicationAndResize(bool resizeFirst)
    {
        // With the model lock held, the pump reads and tokenizes a chunk (it uses the last printed cell: REP) and waits;
        // a resize waits too. The case is armed in that window. Each is in the start or recorded after it, once.
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
        })
        {
            var copy = CopyCase(root, path, name.Replace(' ', '-'));
            change(copy);
            var result = Reapply(copy, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "missing-start"), (result.Outcome, result.Problem?.Code), $"{name}: {result.Problem?.Message}");
            Assert.IsFalse(Directory.Exists(Path.Combine(copy, "reapplications")), $"{name}: a refused re-application wrote a run");
        }
    }

    [TestMethod]
    [DataRow("retained-history", "history")]
    [DataRow("titles", "titles")]
    [DataRow("pending-input", "pendingInput")]
    public async Task Reapply_OutOfSurfaceStartRefused(string surface, string field)
    {
        // A start state the restore cannot represent, in a manifest that claims it complete: refused before anything.
        using var root = new CaseRoot();
        var copy = CopyCase(root, await RecordLiveAsync(root), surface);
        EditEventLine(copy, IsStart, node =>
        {
            var state = node["checkpoint"]!["state"]!.AsObject();
            switch (field)
            {
                case "history":
                    state["history"] = JsonNode.Parse("""{"capacity":10,"nextRowId":2,"rows":[{"cells":[{"t":"x","s":0}],"id":1,"originalWidth":1}]}""");
                    break;
                case "titles":
                    state["titles"]!["window"] = "T";
                    break;
                default:
                    state["pendingInput"]!["escapePrefix"] = "[";
                    break;
            }
        });
        var result = Reapply(copy, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported-start"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
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
