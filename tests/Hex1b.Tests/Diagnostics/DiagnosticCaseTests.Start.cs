using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

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

    private static string StartLive(Hex1bTerminal terminal, CaseRoot root) =>
        new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
}
