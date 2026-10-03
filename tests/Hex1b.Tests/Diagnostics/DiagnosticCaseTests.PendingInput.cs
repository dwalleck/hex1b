using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // Live starts and re-applications on a model holding pending input (ticket 12): the unfinished escape sequence, the
    // bytes of an unfinished scalar, a held ESC, and the framer's continuation count.

    [TestMethod]
    [DataRow("utf8", "e6", "bca2 done", "utf8", "5g==")]
    [DataRow("escape prefix", "1b5b313b", "33316d red", "escapePrefix", "\u001b[1;")]
    [DataRow("held esc", "1b", "5b6d ok", "groundEscape", "True")]
    [DataRow("framer count only", "e080", "90 ok", "framerUtf8Remaining", "1")]
    public async Task Start_WithPendingInputIsComplete(string shape, string firstHex, string restHex, string field, string expected)
    {
        // A live start on a model holding pending input is complete and holds it; re-application after the rest is matched.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "before ");
        await workload.WriteAndWaitAsync(terminal, Convert.FromHexString(firstHex));
        var path = StartLive(terminal, root);
        var restParts = restHex.Split(' ', 2);
        await workload.WriteAndWaitAsync(terminal, [.. Convert.FromHexString(restParts[0]), .. Encoding.UTF8.GetBytes(restParts[1])]);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("complete", checkpoint.GetProperty("status").GetString(), $"{shape}: {checkpoint}");
        var pending = artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty(field);
        Assert.AreEqual(expected, pending.ToString(), $"{shape}: the start's {field}");
        AssertMatched(Reapply(path, label: "stop"), shape);
    }

    [TestMethod]
    public async Task Reapply_PendingInputFaults()
    {
        // A fault is injected into the state reconstructed at the target, so the target is a checkpoint taken while that
        // input was pending: here the start. Each fault differs at exactly the path it names and is labelled; a
        // checkpoint holding none of that state refuses it.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        // An unfinished CSI, then an unfinished scalar (the prefix and the bytes together), then a held ESC alone.
        await workload.WriteAndWaitAsync(terminal, [.. "before "u8, 0x1b, 0x5b, 0x31, 0x3b, 0xe6]);
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. "31m after"u8]);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        foreach (var (fault, faultPath) in new[] { ("pending-input", "pendingInput.utf8"), ("pending-escape", "pendingInput.escapePrefix"), ("pending-framer", "pendingInput.framerUtf8Remaining") })
        {
            var result = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "start", Faults = [fault] });
            Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (result.Outcome, result.Comparison, result.FaultInjected), $"{fault}: {result.ComparisonReason} {result.Problem?.Message}");
            // Exactly one difference: a fault that also changed another holder would be a wrong control.
            Assert.AreEqual(faultPath, string.Join(",", result.Differences!.Differences.Select(d => d.Path)), fault);
            Assert.IsTrue(File.Exists(Path.Combine(result.RunPath!, "faulted.json")), $"{fault}: no faulted.json");
        }
        var groundRefused = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "start", Faults = ["pending-ground-escape"] });
        Assert.AreEqual(("unavailable", "fault-not-applicable"), (groundRefused.Comparison, groundRefused.ComparisonReason?.Split(':')[0]), groundRefused.ComparisonReason);

        using var escRoot = new CaseRoot();
        var escWorkload = new ScriptedWorkload();
        await using var esc = HistoryTerminal(escWorkload, strategy: null, capacity: 100);
        await escWorkload.WriteAndWaitAsync(esc, [.. "before"u8, 0x1b]);
        var escPath = StartLive(esc, escRoot);
        await escWorkload.WriteAndWaitAsync(esc, "[1;31m after");
        await new TerminalDiagnostics(esc).StopCaseAsync(TestContext.Current.CancellationToken);
        var ground = Reapply(new DiagnosticCaseReapplyRequest { Path = escPath, ToLabel = "start", Faults = ["pending-ground-escape"] });
        Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (ground.Outcome, ground.Comparison, ground.FaultInjected), ground.Problem?.Message);
        Assert.AreEqual("pendingInput.groundEscape", string.Join(",", ground.Differences!.Differences.Select(d => d.Path)));
        foreach (var fault in new[] { "pending-input", "pending-escape", "pending-framer" })
        {
            var refused = Reapply(new DiagnosticCaseReapplyRequest { Path = escPath, ToLabel = "start", Faults = [fault] });
            Assert.AreEqual(("unavailable", "fault-not-applicable"), (refused.Comparison, refused.ComparisonReason?.Split(':')[0]), $"{fault}: {refused.ComparisonReason}");
        }
        // At a later target where the pending input has been consumed, every pending fault is refused.
        var later = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["pending-input"] });
        Assert.AreEqual(("unavailable", "fault-not-applicable"), (later.Comparison, later.ComparisonReason?.Split(':')[0]), later.ComparisonReason);
    }


    [TestMethod]
    public async Task Reapply_PendingInputFaultsAtAMarkAndAStop()
    {
        // A mark or a stop taken while input is pending holds it too, so the faults apply there (review XR#2).
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "before ");
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, [0xe6]);
        diagnostics.MarkCase("mid-scalar");
        await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. "\u001b[1;"u8]);
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var atMark = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "mid-scalar", Faults = ["pending-input"] });
        Assert.AreEqual(("different", true, "pendingInput.utf8"), (atMark.Comparison, atMark.FaultInjected, string.Join(",", atMark.Differences!.Differences.Select(d => d.Path))), atMark.ComparisonReason);
        var atStop = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["pending-escape"] });
        Assert.AreEqual(("different", true, "pendingInput.escapePrefix"), (atStop.Comparison, atStop.FaultInjected, string.Join(",", atStop.Differences!.Differences.Select(d => d.Path))), atStop.ComparisonReason);
    }

    [TestMethod]
    public async Task Ingress_PendingScalarIsNotRegrouped()
    {
        // A start between E6 and BC A2: the first recorded application holds exactly the bytes read after the start, so a
        // replica fed the recording cannot be given the whole scalar again. The omission control: a copy of the case
        // whose start line drops the pending bytes re-applies different at the cell the scalar should fill.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, [.. "ok "u8, 0xe6]);
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. " x"u8]);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var first = artifact.ModelEvents().First(e => e.GetProperty("kind").GetString() == "application");
        CollectionAssert.AreEqual(new byte[] { 0xbc, 0xa2, (byte)' ', (byte)'x' }, Convert.FromBase64String(first.GetProperty("data").GetString()!), "the first application after the start");
        Assert.AreEqual("5g==", artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("utf8").GetString(), "the start's pending bytes");
        AssertMatched(Reapply(path, label: "stop"), "the split scalar");
        var omitted = CopyCase(root, path, "omitted");
        EditEventLine(omitted, IsStart, node =>
        {
            var pending = node["checkpoint"]!["state"]!["pendingInput"]!;
            pending["utf8"] = "";
            pending["framerUtf8Remaining"] = 0;
        });
        var faulted = Reapply(omitted, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Captured, "different"), (faulted.Outcome, faulted.Comparison), $"dropping the pending bytes went unnoticed: {faulted.ComparisonReason} {faulted.Problem?.Message}");
        CollectionAssert.Contains(faulted.Differences!.Differences.Select(d => d.Path).ToList(), "screen[0][3].text", "the lost scalar's cell");
    }

    [TestMethod]
    [DataRow("before the rest", 0, null)]
    [DataRow("between the two halves of the rest", 1, null)]
    [DataRow("after the rest", 2, null)]
    [DataRow("before the rest", 0, "ghostty")]
    [DataRow("between the two halves of the rest", 1, "xterm")]
    [DataRow("after the rest", 2, "kitty")]
    public async Task Reapply_PendingInputAcrossResize(string shape, int resizeAt, string? strategyId)
    {
        // A resize around the chunks that finish a pending 4-byte scalar and an unfinished CSI is in one event, once,
        // and re-application matches after it and at the stop, with and without a reflow strategy (review XR#6).
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategyId is null ? null : CaseConfiguration.CreateReflowStrategy(strategyId), capacity: 100);
        await workload.WriteAndWaitAsync(terminal, [.. "before "u8, 0xf0, 0x9f]);
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        var steps = new List<Func<Task>>
        {
            () => workload.WriteAndWaitAsync(terminal, [0x98, 0x80, .. "\u001b[1;"u8]),
            () => workload.WriteAndWaitAsync(terminal, "31m after"),
        };
        for (var i = 0; i <= 2; i++)
        {
            if (i == resizeAt)
            {
                terminal.Resize(30, 8);
                await WaitAsync(() => terminal.Width == 30);
                diagnostics.MarkCase("resized");
            }
            if (i < 2)
                await steps[i]();
        }
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var resizes = Artifact.Read(path).ModelEvents().Count(e => e.GetProperty("kind").GetString() == "resize");
        Assert.AreEqual(1, resizes, $"{shape}: resize events");
        AssertMatched(Reapply(path, label: "resized"), $"{shape}: at the resize");
        AssertMatched(Reapply(path, label: "stop"), $"{shape}: at the stop");
    }

    [TestMethod]
    public async Task Start_PendingPrefixTooLargeForTheCase()
    {
        // An unfinished OSC title whose bytes alone cannot fit a 1 MiB case: refused by the start's floor before
        // projecting, with no start line, and the case records normally.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "\u001b]0;" + new string('t', 2 * 1024 * 1024));
        var path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = 1024 * 1024,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
        await workload.WriteAndWaitAsync(terminal, "\u0007 after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        var reason = checkpoint.GetProperty("reason").GetString()!;
        StringAssert.StartsWith(reason, "size-limit:");
        Assert.IsFalse(reason.Contains("projected and measured", StringComparison.Ordinal), $"the prefix was projected before it was refused: {reason}");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint" && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "a start line was written");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case stopped recording");
    }

    [TestMethod]
    public async Task Reapply_LiveStartCumulativeSurfaceWithPendingInput()
    {
        // Ticket 11's cumulative surface (titles, a stack, gapped marks, a full ring of history, the saved main screen,
        // REP's last cell, a pending wrap, an open synchronized update) plus an unfinished scalar at the start.
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy("ghostty"), 12, clock: clock);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 3) + HistoryText(24) + ShellPrompts(4, 6)
            + "\u001b[?1049h\u001b[H\u001b]133;A\u0007\u001b[32malt\u001b[1;40HW\u001b[?2026h");
        await workload.WriteAndWaitAsync(terminal, [0xe6]);
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. "Z\u001b[3b\u001b]23;\u0007"u8]);
        var before = terminal.CurrentModelSequence;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        terminal.Resize(30, 8);
        await WaitAsync(() => terminal.Width == 30);
        await workload.WriteAndWaitAsync(terminal, "\u001b[?1049l" + ShellPrompts(7, 8));
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        var start = Artifact.Read(path).Events[0].GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual(("alternate", "5g==", true), (start.GetProperty("activeBuffer").GetString(),
            start.GetProperty("pendingInput").GetProperty("utf8").GetString(),
            start.GetProperty("titles").GetProperty("stack").GetArrayLength() > 0), "fixture: the start does not hold every surface");
        AssertMatched(Reapply(path, label: "start"), "the cumulative surface at the start");
        AssertMatched(Reapply(path, label: "stop"), "the cumulative surface");
    }
}
