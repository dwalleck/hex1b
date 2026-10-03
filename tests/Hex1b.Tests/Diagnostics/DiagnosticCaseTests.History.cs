using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Ticket 10: a live start on a terminal that retains history holds every retained row, and re-applies through
// scrolling, eviction, shrink, grow and leaving the alternate screen.
public partial class DiagnosticCaseTests
{
    [TestMethod]
    public async Task Start_WithHistoryIsComplete()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, HistoryText(30));
        var retained = terminal.ScrollbackCount;
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        Assert.AreEqual("complete", artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString(),
            artifact.Manifest.GetProperty("checkpoint").ToString());
        var history = artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("history");
        Assert.IsGreaterThan(0, retained, "fixture: nothing scrolled into history");
        Assert.AreEqual(retained, history.GetProperty("rows").GetArrayLength(), "the start does not hold every retained row");
        Assert.IsTrue(history.GetProperty("rows").EnumerateArray().All(r => r.TryGetProperty("id", out _) && r.TryGetProperty("originalWidth", out _)),
            "a retained row lacks its identity or original width");
    }

    [TestMethod]
    public async Task Start_HistoryTooLargeForTheCase()
    {
        // A history whose start state cannot fit a 1 MiB case: unsupported at the size limit, no start line, and the
        // case records normally. No truncated history is claimed.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 5_000, width: 200, height: 20);
        await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(1, 600).Select(i => $"\u001b[3{i % 7}m{new string((char)('a' + i % 26), 190)}\u001b[m\r\n")));
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
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "size-limit:");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"), "a start line was written");
        Assert.IsNotEmpty(artifact.ModelEvents(), "the case stopped recording");
    }

    [TestMethod]
    [DataRow("styled rows", 250, 24)]
    [DataRow("blank rows", 305, 17)]
    public async Task Start_HistoryThatFitsTheCaseIsComplete(string shape, int lines, int bytesACellThatCannotFit)
    {
        // A history whose state fits a 1 MiB case exactly (about 16 bytes a cell; blank cells are the smallest real
        // ones), although a pre-check at the given bytes a cell would refuse it: the start is complete and re-applies;
        // only the geometry's floor (14 bytes a cell) refuses before projecting.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 5_000, width: 200, height: 20);
        await workload.WriteAndWaitAsync(terminal, shape == "blank rows"
            ? string.Concat(Enumerable.Repeat("\r\n", lines))
            : string.Concat(Enumerable.Range(1, lines).Select(i => $"\u001b[3{i % 7}m{new string((char)('a' + i % 26), 190)}\u001b[m\r\n")));
        const long maxBytes = 1024 * 1024;
        var cells = (terminal.ScrollbackCount + 20L) * 200;
        Assert.IsGreaterThan(maxBytes - DiagnosticCaseRecorder.EventReserve - DiagnosticCaseRecorder.StartLineAllowance, cells * bytesACellThatCannotFit,
            $"fixture: {bytesACellThatCannotFit} bytes a cell would fit");
        var path = new TerminalDiagnostics(terminal).StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = maxBytes,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
        await workload.WriteAndWaitAsync(terminal, " after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var checkpoint = Artifact.Read(path).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("complete", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        AssertMatched(Reapply(path, label: "start"), $"{shape}: a history that fits the case");
    }

    [TestMethod]
    [DataRow("scrollback above 1,000,000")]
    [DataRow("a custom reflow strategy")]
    public async Task Start_OnAConfigurationThatCannotBeRebuiltIsUnsupported(string shape)
    {
        // A live start whose recorded configuration the reapplier would refuse is unsupported, naming it; never complete.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = shape == "a custom reflow strategy"
            ? HistoryTerminal(workload, new CustomReflow(), 100)
            : HistoryTerminal(workload, strategy: null, capacity: 1_000_001);
        await workload.WriteAndWaitAsync(terminal, HistoryText(20));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var checkpoint = Artifact.Read(path).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "configuration: ");
        StringAssert.Contains(checkpoint.GetProperty("reason").GetString(), shape == "a custom reflow strategy" ? "reflowStrategy" : "scrollbackCapacity");
        Assert.AreEqual(DiagnosticOutcome.Unavailable, Reapply(path, label: "stop").Outcome, "an unsupported start re-applied");
    }

    [TestMethod]
    [DataRow(false, "scrollbackCapacity")]
    [DataRow(true, "reflowStrategy")]
    public async Task ConstructionStart_UnrebuildableConfigurationIsUnsupportedAndStillRecords(bool customReflow, string field)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var presentation = new HeadlessPresentationAdapter(40, 10);
        if (customReflow)
            presentation.WithReflowStrategy(new CustomReflow(), enabled: true);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation)
            .WithDimensions(40, 10).WithScrollback(customReflow ? 100 : 1_000_001)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                Authorizations = [DiagnosticAuthorization.ReapplicationData],
            }).Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var armed = diagnostics.GetCaseStatus();
        await workload.WriteAndWaitAsync(terminal, "recorded after unsupported start");
        var stopped = await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual(DiagnosticOutcome.Captured, stopped.Outcome, stopped.Problem?.Message);
        var artifact = Artifact.Read(stopped.Path!);
        Assert.AreEqual("construction", artifact.Manifest.GetProperty("startPath").GetString());
        Assert.IsTrue(artifact.Manifest.GetProperty("fresh").GetBoolean());
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("fresh-model/1", "unsupported"),
            (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()));
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Unsupported, armed.Checkpoint!.Status);
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "configuration: ");
        StringAssert.Contains(checkpoint.GetProperty("reason").GetString(), field);
        Assert.AreEqual(armed.Checkpoint.Reason, checkpoint.GetProperty("reason").GetString());
        Assert.AreEqual(0, checkpoint.GetProperty("coveredSurfaces").GetArrayLength());
        var application = artifact.ModelEvents().Single(e => e.GetProperty("kind").GetString() == "application");
        Assert.AreEqual(1L, application.GetProperty("modelSequence").GetInt64());
        Assert.AreEqual("recorded after unsupported start", Encoding.UTF8.GetString(Convert.FromBase64String(application.GetProperty("data").GetString()!)));
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        Assert.AreEqual(DiagnosticOutcome.Unavailable, Reapply(stopped.Path!, label: "stop").Outcome);
        Assert.IsFalse(Directory.Exists(Path.Combine(stopped.Path!, "reapplications")));
    }

    // A reflow strategy this build cannot name (recorded as custom:), delegating to Ghostty's.
    private sealed class CustomReflow : Hex1b.Reflow.ITerminalReflowProvider
    {
        private readonly Hex1b.Reflow.GhosttyReflowStrategy _inner = new();

        public Hex1b.Reflow.ReflowResult Reflow(Hex1b.Reflow.ReflowContext context) => _inner.Reflow(context);

        public bool ShouldClearSoftWrapOnAbsolutePosition => _inner.ShouldClearSoftWrapOnAbsolutePosition;
    }

    // The prototype's failing shape (styled, wrapped history under Ghostty reflow; shrink, then grow), a full ring
    // with eviction, a non-reflowing strategy, and history under the alternate screen.
    [TestMethod]
    [DataRow("ghostty", 100, false)]
    [DataRow("ghostty", 12, false)]
    [DataRow("xterm", 100, false)]
    [DataRow("kitty", 100, true)]
    public async Task Reapply_LiveStartWithHistory(string strategyId, int capacity, bool alternate)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy(strategyId), capacity);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, HistoryText(30) + (alternate ? "\u001b[?1049h\u001b[Halt text" : ""));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, HistoryText(8, "more"));
        Mark(diagnostics, "scrolled");
        terminal.Resize(24, 7);
        await WaitAsync(() => terminal.Width == 24);
        Mark(diagnostics, "shrunk");
        await workload.WriteAndWaitAsync(terminal, "\u001b[1;36mafter shrink with a line long enough to wrap\u001b[m\r\n");
        Mark(diagnostics, "output");
        terminal.Resize(70, 14);
        await WaitAsync(() => terminal.Width == 70);
        Mark(diagnostics, "grown");
        if (alternate)
        {
            await workload.WriteAndWaitAsync(terminal, "\u001b[?1049lmain again\r\n");
            Mark(diagnostics, "left");
        }
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        Assert.AreEqual("complete", Artifact.Read(path).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        foreach (var label in new[] { "start", "scrolled", "shrunk", "output", "grown", "stop" }.Concat(alternate ? ["left"] : []))
            AssertMatched(Reapply(path, label: label), $"{strategyId} capacity {capacity}{(alternate ? " alternate" : "")} {label}");
    }

    [TestMethod]
    public async Task Start_RecordsAScrollingApplicationReadBeforeArming()
    {
        // Ticket 09's pre-arming shape with history: the chunk the pump holds scrolls a row. That row is not in the
        // start's history; it is produced by the recorded application after the start, once.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy("ghostty"), 100);
        await workload.WriteAndWaitAsync(terminal, HistoryText(12));
        var retainedBefore = terminal.ScrollbackCount;
        var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
        string path;
        long start;
        lock (modelLock)
        {
            var read = terminal.OutputBytesRead;
            workload.Enqueue(Encoding.UTF8.GetBytes(HistoryText(3, "in-flight")));
            Assert.IsTrue(SpinWait.SpinUntil(() => terminal.OutputBytesRead > read, TimeSpan.FromSeconds(5)), "fixture: the chunk was never read");
            Thread.Sleep(20);
            start = terminal.CurrentModelSequence;
            path = StartLive(terminal, root);
        }
        await WaitAsync(() => terminal.ScrollbackCount > retainedBefore);
        await workload.WriteAndWaitAsync(terminal, " after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var startRows = artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("history").GetProperty("rows").GetArrayLength();
        Assert.AreEqual(retainedBefore, startRows, "the start's history includes rows the in-flight chunk scrolled");
        var first = artifact.ModelEvents()[0];
        Assert.AreEqual((start + 1, "application"), (first.GetProperty("modelSequence").GetInt64(), first.GetProperty("kind").GetString()),
            "the in-flight chunk is not the first event after the start");
        AssertMatched(Reapply(path, label: "stop"), "the scrolling chunk recorded after the start");
    }

    [TestMethod]
    public async Task Reapply_LiveStartCumulativeSurface()
    {
        // A full ring of history (its first identity is not 1), the saved main screen, REP's last cell, a pending wrap,
        // rendition and an open synchronized update, all at the start; later output uses each, times the update out,
        // resizes, and leaves the alternate screen.
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy("ghostty"), 12, clock: clock);
        var diagnostics = new TerminalDiagnostics(terminal);
        await workload.WriteAndWaitAsync(terminal, HistoryText(24) + "\u001b[?1049h\u001b[H\u001b[32malt\u001b[1;40HW\u001b[?2026h");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "Z\u001b[3b");
        var before = terminal.CurrentModelSequence;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        terminal.Resize(30, 8);
        await WaitAsync(() => terminal.Width == 30);
        await workload.WriteAndWaitAsync(terminal, "\u001b[?1049lmain again\r\n");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        var start = Artifact.Read(path).Events[0].GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual(("alternate", true, true), (start.GetProperty("activeBuffer").GetString(),
            start.GetProperty("history").GetProperty("rows")[0].GetProperty("id").GetInt64() > 1,
            start.GetProperty("synchronizedUpdate").GetProperty("active").GetBoolean()), "fixture: the start does not hold every surface");
        // A reflowing resize renumbers the rows, so identities are compared at the start as well as the stop.
        AssertMatched(Reapply(path, label: "start"), "the cumulative surface at the start");
        AssertMatched(Reapply(path, label: "stop"), "the cumulative surface");
    }

    [TestMethod]
    [DataRow("original width 0")]
    [DataRow("cells not the original width")]
    [DataRow("a row without cells")]
    [DataRow("a duplicate id")]
    public async Task Reapply_MalformedHistoryIsIncompatible(string shape)
    {
        // A start line whose history is malformed but checksum-valid (a hand-edited artifact): re-application is
        // unavailable (incompatible), naming the history, before anything is applied or written.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, HistoryText(20));
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        AssertMatched(Reapply(path, label: "stop"), "fixture: the unedited case");
        Directory.Delete(Path.Combine(path, "reapplications"), recursive: true);

        EditEventLine(path, node => node["kind"]?.GetValue<string>() == "checkpoint" && node["checkpoint"]?["trigger"]?.GetValue<string>() == "start", node =>
        {
            var rows = node["checkpoint"]!["state"]!["history"]!["rows"]!.AsArray();
            var first = rows[0]!.AsObject();
            switch (shape)
            {
                case "original width 0":
                    first["originalWidth"] = 0;
                    first["cells"] = new JsonArray();
                    break;
                case "cells not the original width":
                    first["cells"]!.AsArray().RemoveAt(0);
                    break;
                case "a row without cells":
                    first["cells"] = null;
                    break;
                default:
                    rows[1]!["id"] = first["id"]!.GetValue<long>();
                    break;
            }
        });

        var result = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        StringAssert.Contains(result.Problem!.Message, "history", shape);
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{shape}: a refused re-application wrote a run");
    }

    private static Hex1bTerminal HistoryTerminal(ScriptedWorkload workload, Hex1b.Reflow.ITerminalReflowProvider? strategy, int capacity,
        int width = 40, int height = 10, TimeProvider? clock = null)
    {
        var presentation = strategy is null
            ? new HeadlessPresentationAdapter(width, height)
            : new HeadlessPresentationAdapter(width, height).WithReflowStrategy(strategy, enabled: true);
        return new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = workload,
            Width = width,
            Height = height,
            ScrollbackCapacity = capacity,
            TimeProvider = clock ?? TimeProvider.System,
        });
    }

    private static void Mark(TerminalDiagnostics diagnostics, string label) =>
        Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.MarkCase(label).Outcome, $"fixture: mark {label}");

    private static string HistoryText(int lines, string prefix = "row") => string.Concat(Enumerable.Range(1, lines).Select(i =>
        i % 3 == 0
            ? $"\u001b[{31 + i % 6}m{prefix} {i} is a styled line long enough to soft-wrap past the right edge of the screen\u001b[m\r\n"
            : $"{prefix} {i} \u001b[1mbold\u001b[m plain\r\n"));
}
