using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Ticket 11: a live start holds titles, the title stack and command marks (with their positions), and re-applies
// through later title and mark behaviour; too much of them is refused at the size limit, never truncated.
public partial class DiagnosticCaseTests
{
    [TestMethod]
    public async Task Start_WithTitlesAndMarksIsComplete()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 12));
        var (marks, title, depth) = (terminal.CommandMarks.Count, terminal.WindowTitle, ((Stack<(string, string)>)TerminalField(terminal, "_titleStack")).Count);
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("complete", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        var state = artifact.Events[0].GetProperty("checkpoint").GetProperty("state");
        var projectedMarks = state.GetProperty("commandMarks").EnumerateArray().ToList();
        var historyRows = state.GetProperty("history").GetProperty("rows").GetArrayLength();
        Assert.AreEqual((marks, title, depth), (projectedMarks.Count, state.GetProperty("titles").GetProperty("window").GetString(),
            state.GetProperty("titles").GetProperty("stack").GetArrayLength()), "the start does not hold the titles and marks");
        Assert.IsTrue(projectedMarks.Any(m => m.GetProperty("row").GetInt32() < historyRows), "fixture: no mark in history");
        Assert.IsGreaterThanOrEqualTo(3, depth, "fixture: the stack depth");
    }

    // Later output that pushes and pops titles, emits marks, evicts marked rows, reflows and crosses the alternate
    // screen: matched at every mark, under a reflowing and a non-reflowing strategy.
    [TestMethod]
    [DataRow("ghostty")]
    [DataRow("xterm")]
    public async Task Reapply_LiveStartWithTitlesAndMarks(string strategyId)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy(strategyId), capacity: 20);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 6) + HistoryText(24) + ShellPrompts(7, 10));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(11, 13));
        Mark(diagnostics, "prompts");
        terminal.Resize(24, 7);
        await WaitAsync(() => terminal.Width == 24);
        Mark(diagnostics, "shrunk");
        await workload.WriteAndWaitAsync(terminal, "\u001b[?1049h\u001b[H" + ShellPrompts(14, 14) + "\u001b[?1049l" + ShellPrompts(15, 15));
        Mark(diagnostics, "alternate");
        await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Repeat("\u001b]23;\u0007", 5)) + HistoryText(30));
        Mark(diagnostics, "popped and evicted");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual("complete", Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        foreach (var label in new[] { "start", "prompts", "shrunk", "alternate", "popped and evicted", "stop" })
            AssertMatched(Reapply(path, label: label), $"{strategyId} {label}");
    }

    // Ticket 22: the mark scenarios recorded live with a viewer on the original only. At the start and after every
    // later step an HMP1 capture, a jump to each mark and an HWT1 frame read the marks before the boundary is marked
    // (the start is recorded before its read, so the read start is marked too); re-applied without a viewer, the case
    // is matched at the start, the read start, every step and the stop.
    [TestMethod]
    [DynamicData(nameof(DiagnosticModelRestoreTests.MarkScenariosAndStrategies), typeof(DiagnosticModelRestoreTests))]
    public async Task Reapply_MarksObservedByAViewer(string scenario, string strategy)
    {
        var shape = DiagnosticModelRestoreTests.MarkScenarios[scenario];
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy == "none" ? null : CaseConfiguration.CreateReflowStrategy(strategy),
            shape.Capacity, shape.Width, shape.Height);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, shape.Before);
        var path = StartLive(terminal, root);
        var view = new Hwt1ViewState();
        var requestId = 0L;
        DiagnosticModelRestoreTests.ObserveMarks(terminal, view, shape.Capacity, ref requestId, $"{scenario} {strategy} start", static _ => { });
        Mark(diagnostics, "observed start");
        foreach (var (name, input) in shape.Steps)
        {
            if (input.StartsWith("RESIZE ", StringComparison.Ordinal))
            {
                var size = input.Split(' ');
                var (width, height) = (int.Parse(size[1]), int.Parse(size[2]));
                terminal.Resize(width, height);
                await WaitAsync(() => terminal.Width == width && terminal.Height == height);
            }
            else
                await workload.WriteAndWaitAsync(terminal, input);
            DiagnosticModelRestoreTests.ObserveMarks(terminal, view, shape.Capacity, ref requestId, $"{scenario} {strategy} {name}", static _ => { });
            Mark(diagnostics, name);
        }
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual("complete", Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("status").GetString(), $"{scenario} {strategy}: the start");
        foreach (var label in shape.Steps.Select(s => s.Name).Prepend("observed start").Prepend("start").Append("stop"))
            AssertMatched(Reapply(path, label: label), $"{scenario} {strategy} {label}");
    }

    [TestMethod]
    public async Task Start_TitlesTooLargeForTheCase()
    {
        // A title stack whose JSON alone cannot fit a 1 MiB case: refused by the start's floor before projecting (the
        // reason discloses no measurement), with no start line, and the case records normally.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 40_000).Select(i => $"\u001b]22;pushed title number {i:D8}\u0007")));
        var path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = 1024 * 1024,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
        await workload.WriteAndWaitAsync(terminal, " after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        var reason = checkpoint.GetProperty("reason").GetString()!;
        StringAssert.StartsWith(reason, "size-limit:");
        Assert.IsFalse(reason.Contains("projected and measured", StringComparison.Ordinal), $"the stack was projected before it was refused: {reason}");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "a start line was written");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case stopped recording");
    }

    [TestMethod]
    public void Estimates_CountTitlesAndMarks()
    {
        // The start's floor and the budget estimate grow with the stack and the marks by at least their smallest JSON
        // (the oracle serializes an empty entry and a mark), and the floor never exceeds the projection's exact size.
        var shape = HistoryTerminalOptions(strategy: null, capacity: 100);
        var bare = new Hex1bTerminal(shape());
        var full = new Hex1bTerminal(shape());
        full.ApplyRecordedOutput(Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(1, 2_000).Select(_ => "\u001b]22;\u0007"))
            + string.Concat(Enumerable.Range(1, 500).Select(i => $"\u001b]133;C;cmdline_url=command{i}\u0007"))));
        var entry = JsonSerializer.SerializeToUtf8Bytes(new DiagnosticModelTitleEntry(), DiagnosticsJsonContext.Default.DiagnosticModelTitleEntry).Length;
        var mark = JsonSerializer.SerializeToUtf8Bytes(new DiagnosticModelCommandMark { Anchor = "command:1", Phase = "", Buffer = "" },
            DiagnosticsJsonContext.Default.DiagnosticModelCommandMark).Length;
        var oracle = 2_000L * entry + 500L * (mark - "command:1".Length) + string.Concat(Enumerable.Range(1, 500).Select(i => $"command:{i}cmdline_url=command{i}")).Length;

        long Floor(Hex1bTerminal t) => t.MinimumModelStateJsonBytesUnsafe();
        long Budget(Hex1bTerminal t) => t.EstimateModelStateBytesUnsafe();
        Assert.AreEqual(500, full.CommandMarks.Count, "fixture: marks");
        Assert.IsGreaterThanOrEqualTo(oracle, Floor(full) - Floor(bare), "the floor does not count the stack and marks");
        Assert.IsGreaterThanOrEqualTo(oracle, Budget(full) - Budget(bare), "the budget estimate does not count the stack and marks");
        var exact = JsonSerializer.SerializeToUtf8Bytes(full.CaptureModelState(), DiagnosticsJsonContext.Default.DiagnosticModelState).LongLength;
        Assert.IsLessThanOrEqualTo(exact, Floor(full), "the floor exceeds the projection's exact size");
    }

    [TestMethod]
    public async Task Start_RecordsAMarkReadBeforeArming()
    {
        // Ticket 09's pre-arming shape: the chunk the pump holds sets a title and emits marks. They are not in the start;
        // they are produced by the recorded application after it, once.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 2));
        var (marksBefore, titleBefore) = (terminal.CommandMarks.Count, terminal.WindowTitle);
        var modelLock = TerminalField(terminal, "_bufferLock");
        string path;
        long start;
        lock (modelLock)
        {
            var read = terminal.OutputBytesRead;
            workload.Enqueue(Encoding.UTF8.GetBytes("\u001b]2;in flight\u0007" + ShellPrompts(3, 3)));
            Assert.IsTrue(SpinWait.SpinUntil(() => terminal.OutputBytesRead > read, TimeSpan.FromSeconds(5)), "fixture: the chunk was never read");
            Thread.Sleep(20);
            start = terminal.CurrentModelSequence;
            path = StartLive(terminal, root);
        }
        await WaitAsync(() => terminal.CommandMarks.Count > marksBefore);
        await workload.WriteAndWaitAsync(terminal, " after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var state = artifact.Events[0].GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual((marksBefore, titleBefore), (state.GetProperty("commandMarks").GetArrayLength(), state.GetProperty("titles").GetProperty("window").GetString()),
            "the start holds the in-flight chunk's marks or title");
        var first = artifact.ModelEvents()[0];
        Assert.AreEqual((start + 1, "application"), (first.GetProperty("modelSequence").GetInt64(), first.GetProperty("kind").GetString()),
            "the in-flight chunk is not the first event after the start");
        AssertMatched(Reapply(path, label: "stop"), "the marks recorded after the start");
    }

    [TestMethod]
    [DataRow("row outside the buffer")]
    [DataRow("unknown phase")]
    [DataRow("duplicate anchor")]
    [DataRow("null stack entry")]
    public async Task Reapply_MalformedMarksIncompatible(string shape)
    {
        // A checksum-valid start line whose marks or stack are malformed (a hand-edited artifact): re-application is
        // unavailable (incompatible), naming the field, before anything is applied or written.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 4));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        AssertMatched(Reapply(path, label: "stop"), "fixture: the unedited case");
        Directory.Delete(Path.Combine(path, "reapplications"), recursive: true);

        EditEventLine(path, node => node["kind"]?.GetValue<string>() == "checkpoint" && node["checkpoint"]?["trigger"]?.GetValue<string>() == "start", node =>
        {
            var state = node["checkpoint"]!["state"]!;
            var marks = state["commandMarks"]!.AsArray();
            switch (shape)
            {
                case "row outside the buffer":
                    marks[0]!["row"] = 9_999;
                    break;
                case "unknown phase":
                    marks[0]!["phase"] = "Prompt";
                    break;
                case "duplicate anchor":
                    marks[1]!["anchor"] = marks[0]!["anchor"]!.GetValue<string>();
                    break;
                default:
                    state["titles"]!["stack"]!.AsArray()[0]!["icon"] = null;
                    break;
            }
        });

        var result = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        StringAssert.Contains(result.Problem!.Message, shape == "null stack entry" ? "titles" : "command mark", shape);
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{shape}: a refused re-application wrote a run");
    }

    [TestMethod]
    public async Task Reapply_LiveStartCumulativeSurfaceWithMarks()
    {
        // Titles, a stack, marks with gapped ids (early ones evicted), a full ring of history, the saved main screen,
        // REP's last cell, a pending wrap and an open synchronized update, all at the start; later output uses each,
        // times the update out, resizes and leaves the alternate screen.
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy("ghostty"), 12, clock: clock);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 3) + HistoryText(24) + ShellPrompts(4, 6)
            + "\u001b[?1049h\u001b[H\u001b]133;A\u0007\u001b[32malt\u001b[1;40HW\u001b[?2026h");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "Z\u001b[3b\u001b]23;\u0007");
        var before = terminal.CurrentModelSequence;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        terminal.Resize(30, 8);
        await WaitAsync(() => terminal.Width == 30);
        await workload.WriteAndWaitAsync(terminal, "\u001b[?1049l" + ShellPrompts(7, 8));
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        var start = Artifact.Read(path).Events[0].GetProperty("checkpoint").GetProperty("state");
        var marks = start.GetProperty("commandMarks").EnumerateArray().ToList();
        Assert.AreEqual(("alternate", true, true, true, true), (start.GetProperty("activeBuffer").GetString(),
            start.GetProperty("history").GetProperty("rows")[0].GetProperty("id").GetInt64() > 1,
            start.GetProperty("synchronizedUpdate").GetProperty("active").GetBoolean(),
            marks.Count > 0 && marks[0].GetProperty("anchor").GetString() != "command:1" && marks.Any(m => m.GetProperty("buffer").GetString() == "alternate"),
            start.GetProperty("titles").GetProperty("stack").GetArrayLength() > 0), "fixture: the start does not hold every surface");
        AssertMatched(Reapply(path, label: "start"), "the cumulative surface at the start");
        AssertMatched(Reapply(path, label: "stop"), "the cumulative surface");
    }

    [TestMethod]
    public async Task Reapply_TitleAndMarkFaults()
    {
        // Each fault differs at the path it names and is labelled; a case with no stack or no placed mark refuses it.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 4));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        foreach (var (fault, faultPath) in new[] { ("title-stack", "titles.stack[0].window"), ("command-mark", "commandMarks[0].column") })
        {
            var result = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = [fault] });
            Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (result.Outcome, result.Comparison, result.FaultInjected), $"{fault}: {result.Problem?.Message}");
            CollectionAssert.Contains(result.Differences!.Differences.Select(d => d.Path).ToList(), faultPath, fault);
            Assert.IsTrue(File.Exists(Path.Combine(result.RunPath!, "faulted.json")), $"{fault}: no faulted.json");
        }

        using var bareRoot = new CaseRoot();
        var bareWorkload = new ScriptedWorkload();
        await using var bare = HistoryTerminal(bareWorkload, strategy: null, capacity: 100);
        await bareWorkload.WriteAndWaitAsync(bare, "plain text");
        var barePath = StartLive(bare, bareRoot);
        await bareWorkload.WriteAndWaitAsync(bare, " after");
        await new TerminalDiagnostics(bare).StopCaseAsync(TestContext.Current.CancellationToken);
        foreach (var fault in new[] { "title-stack", "command-mark" })
        {
            var refused = Reapply(new DiagnosticCaseReapplyRequest { Path = barePath, ToLabel = "stop", Faults = [fault] });
            Assert.AreEqual(("unavailable", "fault-not-applicable"), (refused.Comparison, refused.ComparisonReason?.Split(':')[0]),
                $"{fault}: {refused.ComparisonReason} {refused.Problem?.Message}");
        }
    }

    // Shell-like prompts: a title (a push every other prompt, push-and-set every third), and OSC 133 A/B/C/D marks
    // with parameters and exit codes.
    private static string ShellPrompts(int from, int to) => string.Concat(Enumerable.Range(from, to - from + 1).Select(i =>
        (i % 2 == 1 ? "\u001b]22;\u0007" : "") + $"\u001b]0;user@host: step {i}\u0007" + (i % 3 == 0 ? $"\u001b]22;pushed {i}\u0007" : "")
        + $"\u001b]133;A\u0007$ \u001b]133;B\u0007cmd {i}\r\n\u001b]133;C;cmdline_url=cmd%20{i}\u0007out {i}\r\n\u001b]133;D;{i % 3}\u0007"));

    private static object TerminalField(Hex1bTerminal terminal, string name) =>
        typeof(Hex1bTerminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;

    private static Func<Hex1bTerminalOptions> HistoryTerminalOptions(Hex1b.Reflow.ITerminalReflowProvider? strategy, int capacity) => () => new Hex1bTerminalOptions
    {
        PresentationAdapter = strategy is null
            ? new HeadlessPresentationAdapter(40, 10)
            : new HeadlessPresentationAdapter(40, 10).WithReflowStrategy(strategy, enabled: true),
        WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
        Width = 40,
        Height = 10,
        ScrollbackCapacity = capacity,
        DeferStart = true,
    };
}
