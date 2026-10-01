using System.Reflection;
using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // Recovery checkpoints after recording loss (ticket 13): a complete text-state/1 checkpoint taken mid-case through
    // the same coordinator as the start, from which a later re-application can begin; refusals recorded; the loss
    // envelope past the ledger's cap; interval ends per segment.

    [TestMethod]
    public async Task Recover_RecordsACompleteCheckpoint()
    {
        // Real overload (the writer held while the queue overflows), then a recovery: its line has trigger recovery,
        // status recorded and the state, at the model sequence the result names; every model event at or before that
        // sequence precedes the line, every later one is recorded once with a greater sequence (not necessarily after
        // the line in the file: the writer drains the queue before a pending checkpoint); the manifest and the loss
        // ranges are unchanged.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        DiagnosticCaseRecoverResult result;
        string path;
        byte[] manifestBefore;
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
            path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                // The writer writes the manifest before it waits at the gate.
                await WaitAsync(() => File.Exists(Path.Combine(path, "manifest.json")));
                manifestBefore = File.ReadAllBytes(Path.Combine(path, "manifest.json"));
                await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                gate.Set();
                result = new TerminalDiagnostics(terminal).RecoverCase("after-loss");
                await workload.WriteAndWaitAsync(terminal, "after ");
                await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            gate.Set();
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }

        Assert.AreEqual((DiagnosticOutcome.Captured, "complete", "after-loss"), (result.Outcome, result.Status, result.Label), result.Problem?.Message);
        var artifact = Artifact.Read(path);
        var recovery = Recoveries(artifact).Single();
        var checkpoint = recovery.GetProperty("checkpoint");
        Assert.AreEqual(("recovery", "recorded", result.CheckpointOrdinal, result.ModelSequence),
            (checkpoint.GetProperty("trigger").GetString(), checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("ordinal").GetInt64(), recovery.GetProperty("modelSequence").GetInt64()));
        Assert.IsTrue(checkpoint.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object, "the recovery line holds no state");
        // The writer drains the events queued before it writes a pending checkpoint (as for marks), so the line's
        // place in the file is after every event at or before its sequence; later events are ordered by sequence.
        var line = recovery.GetProperty("caseSequence").GetInt64();
        var model = artifact.ModelEvents();
        Assert.IsTrue(model.Where(e => e.GetProperty("modelSequence").GetInt64() <= result.ModelSequence).All(e => e.GetProperty("caseSequence").GetInt64() < line),
            "a model event at or before the recovery was written after its line");
        Assert.AreEqual(model.Count, model.Select(e => e.GetProperty("modelSequence").GetInt64()).Distinct().Count(), "a model event was recorded twice");
        Assert.IsTrue(model.Any(e => e.GetProperty("modelSequence").GetInt64() > result.ModelSequence && Convert.FromBase64String(e.GetProperty("data").GetString()!).AsSpan().IndexOf("after "u8) >= 0),
            "the output after the recovery was not recorded after it");
        CollectionAssert.AreEqual(manifestBefore, File.ReadAllBytes(Path.Combine(path, "manifest.json")), "the recovery changed the manifest");
        Assert.HasCount(700, MissingOrdinals(artifact, "model"), "the loss before the recovery changed");
    }

    [TestMethod]
    public async Task Recover_PartitionsAnInFlightChunk()
    {
        // A chunk the pump has read and tokenized while the recovery holds the model lock is not in the recovery's
        // state and is the first application recorded after its line, once (ticket 09's partition, at a recovery).
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
        DiagnosticCaseRecoverResult result;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before Q");
            var first = 1;
            DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value = () =>
            {
                if (Interlocked.Exchange(ref first, 0) != 1)
                    return;
                var read = terminal.OutputBytesRead;
                workload.Enqueue(Encoding.UTF8.GetBytes(" in-flight\u001b[3b"));
                SpinWait.SpinUntil(() => terminal.OutputBytesRead > read, TimeSpan.FromSeconds(5));
                Thread.Sleep(50);
            };
            try
            {
                result = new TerminalDiagnostics(terminal).RecoverCase("partition");
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value = null;
            }
            Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
            await WaitAsync(() => terminal.CurrentModelSequence >= result.ModelSequence!.Value + 1);
            await workload.WriteAndWaitAsync(terminal, " after");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        var recovery = Recoveries(artifact).Single();
        var screen = string.Concat(recovery.GetProperty("checkpoint").GetProperty("state").GetProperty("screen").EnumerateArray()
            .SelectMany(r => r.GetProperty("cells").EnumerateArray()).Select(c => c.GetProperty("t").GetString()));
        Assert.IsFalse(screen.Contains("in-flight", StringComparison.Ordinal), "the in-flight chunk is in the recovery's state");
        var next = artifact.ModelEvents().First(e => e.GetProperty("modelSequence").GetInt64() == result.ModelSequence + 1);
        Assert.AreEqual("application", next.GetProperty("kind").GetString(), "the event after the recovery");
        StringAssert.Contains(Encoding.UTF8.GetString(Convert.FromBase64String(next.GetProperty("data").GetString()!)), "in-flight");
        Assert.AreEqual(1, artifact.ModelEvents().Count(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.GetProperty("data").GetString() ?? "")).Contains("in-flight", StringComparison.Ordinal)),
            "the in-flight chunk was recorded twice");
    }

    [TestMethod]
    [DataRow("mid-application")]
    [DataRow("unapplied-output")]
    [DataRow("unsupported-surfaces: dcs")]
    [DataRow("unsupported-surfaces: graphics")]
    [DataRow("configuration")]
    [DataRow("pending-state-budget")]
    [DataRow("size-limit")]
    public async Task Recover_RefusesEachStartCheck(string shape)
    {
        // Each start check refuses a recovery with its reason; the attempt is recorded as a recovery line without state;
        // the case keeps recording.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (width, height, maxBytes) = shape == "size-limit" ? (800, 120, (long?)(1024 * 1024)) : (40, 10, null);
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = shape == "configuration"
                ? new HeadlessPresentationAdapter(width, height).WithReflowStrategy(new CustomReflow(), enabled: true)
                : new HeadlessPresentationAdapter(width, height),
            WorkloadAdapter = workload,
            Width = width,
            Height = height,
            ScrollbackCapacity = 100,
        };
        // The pending-state budget is the recorder's from its construction, as for marks.
        DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = shape == "pending-state-budget" ? 1 : null;
        Hex1bTerminal terminal;
        DiagnosticCaseResult started;
        try
        {
            (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options,
                new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = maxBytes, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
        }
        finally
        {
            DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = null;
        }
        var path = started.Path!;
        DiagnosticCaseRecoverResult? result = null;
        long beforeTitle = -1;
        var diagnostics = new TerminalDiagnostics(terminal);
        await using (terminal)
        {
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "before ");
                switch (shape)
                {
                    case "mid-application":
                        beforeTitle = terminal.CurrentModelSequence;
                        terminal.WindowTitleChanged += _ => result ??= diagnostics.RecoverCase("inside");
                        await workload.WriteAndWaitAsync(terminal, "\u001b]0;titled\u0007");
                        break;
                    case "unapplied-output":
                        var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
                        lock (modelLock)
                        {
                            typeof(Hex1bTerminal).GetMethod("NotifyCaseUnappliedOutputUnsafe", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(terminal, []);
                            result = diagnostics.RecoverCase("unapplied");
                        }
                        break;
                    case "unsupported-surfaces: dcs":
                        await workload.WriteAndWaitAsync(terminal, "\u001bP$q");
                        result = diagnostics.RecoverCase("dcs");
                        await workload.WriteAndWaitAsync(terminal, "m\u001b\\");
                        break;
                    case "unsupported-surfaces: graphics":
                        await workload.WriteAndWaitAsync(terminal, "\u001bPq#0;2;100;0;0#0~~~~\u001b\\");
                        result = diagnostics.RecoverCase("sixel");
                        break;
                    default:
                        result = diagnostics.RecoverCase(shape);
                        break;
                }
                await workload.WriteAndWaitAsync(terminal, "after");
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
        }

        Assert.IsNotNull(result, "fixture: no recovery was requested");
        if (shape == "mid-application")
            Assert.AreEqual(beforeTitle, result.ModelSequence, "the boundary of a recovery refused inside an application is not the model sequence before it, as a mark's is");
        var code = shape.Split(':')[0];
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", code), (result.Outcome, result.Status, result.Problem?.Code), result.Problem?.Message);
        StringAssert.StartsWith(result.Reason, code.Replace("pending-state-budget", "pending-state budget"), shape);
        if (shape.StartsWith("unsupported-surfaces", StringComparison.Ordinal))
            CollectionAssert.Contains(result.UnsupportedSurfaces!.ToList(), shape.EndsWith("dcs", StringComparison.Ordinal) ? "dcs-continuation" : "graphics");
        var artifact = Artifact.Read(path);
        var recovery = Recoveries(artifact).Single();
        var checkpoint = recovery.GetProperty("checkpoint");
        Assert.AreEqual(("unsupported", result.CheckpointOrdinal, false),
            (checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("ordinal").GetInt64(), checkpoint.TryGetProperty("state", out var s) && s.ValueKind == JsonValueKind.Object),
            "the refused recovery's line");
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), code.Replace("pending-state-budget", "pending-state budget"));
        Assert.IsTrue(artifact.ModelEvents().Any(e => e.GetProperty("modelSequence").GetInt64() > recovery.GetProperty("modelSequence").GetInt64()
            && Encoding.UTF8.GetString(Convert.FromBase64String(e.GetProperty("data").GetString() ?? "")).Contains("after", StringComparison.Ordinal)),
            "the case stopped recording after the refusal");
    }

    [TestMethod]
    [DataRow("no-authorization")]
    [DataRow("no-case")]
    [DataRow("busy")]
    [DataRow("hmp1")]
    [DataRow("invalid-label")]
    public async Task Recover_WithoutAuthorizationOrCase(string shape)
    {
        // Refusals that take no checkpoint at all: nothing is recorded and no mark slot is kept.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        if (shape == "busy")
            DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var workload = new ScriptedWorkload();
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(shape == "hmp1" ? new FakeHmp1Workload() : workload).WithHeadless().WithDimensions(40, 10);
            if (shape != "no-case")
                builder = builder.WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = shape == "no-authorization" ? [] : [DiagnosticAuthorization.ReapplicationData] });
            await using var terminal = builder.Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            var path = shape == "no-case" ? null : diagnostics.GetCaseStatus().Path;
            using (new Running(terminal))
            {
                if (shape is not ("hmp1" or "no-case"))
                    await workload.WriteAndWaitAsync(terminal, "text");
                if (shape == "busy")
                    Assert.AreEqual(64, Enumerable.Range(0, 64).Count(_ => diagnostics.MarkCase().Outcome == DiagnosticOutcome.Captured), "fixture: the mark bound");
                var result = diagnostics.RecoverCase(shape == "invalid-label" ? "tab\there" : null);
                var expected = shape switch
                {
                    "no-authorization" => "requires-reapplication-data",
                    "no-case" => "no-active-case",
                    "busy" => "busy",
                    "hmp1" => "hmp1-workload",
                    _ => "invalid-label",
                };
                Assert.AreEqual((shape == "invalid-label" ? DiagnosticOutcome.InvalidRequest : DiagnosticOutcome.Unavailable, expected), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
                Assert.IsNull(result.CheckpointOrdinal, "a refusal without a line numbered a checkpoint");
                gate.Set();
                if (path is not null)
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
            if (path is not null)
                Assert.IsEmpty(Recoveries(Artifact.Read(path)), "a refusal without a checkpoint wrote a recovery line");
        }
        finally
        {
            gate.Set();
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    [TestMethod]
    [DataRow("size-limit")]
    [DataRow("time-limit")]
    public async Task Recover_AfterALimitStop(string limit)
    {
        // A limit stop keeps its reason, records no overload and takes no recovery: the case is over.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var clock = new FakeTimeProvider();
        var (terminal, path) = limit == "size-limit"
            ? Checkpointed(root, workload, width: 200, height: 50, maxBytes: 1024 * 1024)
            : Checkpointed(root, workload, maxSeconds: 1, clock: clock);
        var diagnostics = new TerminalDiagnostics(terminal);
        await using (terminal)
        {
            if (limit == "size-limit")
            {
                var chunk = Enumerable.Repeat((byte)'s', 16 * 1024).ToArray();
                for (var i = 0; i < 128 && diagnostics.GetCaseStatus().Outcome == DiagnosticOutcome.Captured; i++)
                    await workload.WriteAndWaitAsync(terminal, chunk);
            }
            else
            {
                await workload.WriteAndWaitAsync(terminal, "tick");
                clock.Advance(TimeSpan.FromSeconds(2));
            }
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
            var result = diagnostics.RecoverCase("late");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-active-case"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        }
        var artifact = Artifact.Read(path);
        Assert.AreEqual(limit, artifact.Completion!.Value.GetProperty("stopReason").GetString());
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "missing" && e.GetProperty("record").GetProperty("reason").GetString()!.StartsWith("overload", StringComparison.Ordinal)),
            "a limit stop recorded overload");
        Assert.IsEmpty(Recoveries(artifact));
    }

    [TestMethod]
    public async Task Recover_TooLargeForTheRoom()
    {
        // A recovery whose state cannot fit what the size bound leaves is refused at the size limit, before projecting,
        // and the case keeps recording.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload, width: 200, height: 50, maxBytes: 1024 * 1024);
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseRecoverResult result;
        await using (terminal)
        {
            // 200x50 cells floor at about 137 KiB; the events tier ends 16 KiB under the bound. Fill until less than
            // that is left, in small chunks, so the bound itself is never crossed.
            var chunk = Enumerable.Repeat((byte)'s', 2 * 1024).ToArray();
            while (diagnostics.GetCaseStatus() is { Outcome: DiagnosticOutcome.Captured, BytesWritten: < 905 * 1024 })
                await workload.WriteAndWaitAsync(terminal, chunk);
            await WaitAsync(() => diagnostics.GetCaseStatus().BytesWritten >= 905 * 1024, TimeSpan.FromSeconds(30));
            Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.GetCaseStatus().Outcome, "fixture: the case crossed its bound");
            var captures = terminal.ModelStateCapturesForTesting;
            result = diagnostics.RecoverCase("late");
            Assert.AreEqual(captures, terminal.ModelStateCapturesForTesting, "a recovery refused at the floor projected the state");
            await workload.WriteAndWaitAsync(terminal, "after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "size-limit"), (result.Outcome, result.Status, result.Problem?.Code), result.Problem?.Message);
        var artifact = Artifact.Read(path);
        var recovery = Recoveries(artifact).Single();
        StringAssert.StartsWith(recovery.GetProperty("checkpoint").GetProperty("reason").GetString(), "size-limit");
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString(), "the refused recovery stopped the case");
        Assert.IsTrue(artifact.ModelEvents().Any(e => e.GetProperty("modelSequence").GetInt64() > recovery.GetProperty("modelSequence").GetInt64()), "the case stopped recording");
    }

    [TestMethod]
    public async Task Recover_TooLargeAfterProjecting()
    {
        // The floor (14 bytes a cell) fits the room but the exact state does not (wide glyphs and their continuation
        // cells serialize larger): refused after projecting, naming the measured size; the case keeps recording.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        // No scrollback: the floor counts retained history cells too, and the rows written below would fill it.
        var (terminal, path) = Checkpointed(root, workload, width: 200, height: 50, scrollback: null, maxBytes: 1024 * 1024);
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseRecoverResult result;
        await using (terminal)
        {
            // Rows of 100 wide glyphs: about 4,000 bytes a row serialized against a 2,800-byte floor, so the exact size
            // (about 195 KiB plus the line allowance) exceeds the room left once about 850 KiB is written (about 182 KiB),
            // while the floor (137 KiB plus the allowance) fits it.
            var row = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("\u6f22", 100)) + "\r\n");
            while (diagnostics.GetCaseStatus() is { Outcome: DiagnosticOutcome.Captured, BytesWritten: < 850 * 1024 })
                await workload.WriteAndWaitAsync(terminal, row);
            await WaitAsync(() => diagnostics.GetCaseStatus().BytesWritten >= 850 * 1024, TimeSpan.FromSeconds(30));
            var captures = terminal.ModelStateCapturesForTesting;
            result = diagnostics.RecoverCase("measured");
            Assert.AreEqual(captures + 1, terminal.ModelStateCapturesForTesting, "fixture: the recovery was refused before projecting (the room is under the floor)");
            await workload.WriteAndWaitAsync(terminal, "after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "size-limit"), (result.Outcome, result.Status, result.Problem?.Code), result.Problem?.Message);
        StringAssert.Contains(result.Reason, "projected and measured");
        var artifact = Artifact.Read(path);
        var recovery = Recoveries(artifact).Single();
        Assert.AreEqual("unsupported", recovery.GetProperty("checkpoint").GetProperty("status").GetString());
        Assert.IsTrue(artifact.ModelEvents().Any(e => e.GetProperty("modelSequence").GetInt64() > recovery.GetProperty("modelSequence").GetInt64()), "the case stopped recording");
    }

    [TestMethod]
    public async Task Recovery_IntervalEndsPerSegment()
    {
        // An unsupported event before the recovery ends the first segment (its end, pending while the writer is held, is
        // still written before the next model event at or after it); one after the recovery ends the second segment.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        long firstEnd, secondEnd;
        DiagnosticCaseRecoverResult result;
        string path;
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
            path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "a");
                terminal.EnterAlternateScreen();
                firstEnd = terminal.CurrentModelSequence;
                await workload.WriteAndWaitAsync(terminal, "b");
                result = new TerminalDiagnostics(terminal).RecoverCase("second");
                Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
                await workload.WriteAndWaitAsync(terminal, "c");
                terminal.ExitAlternateScreen();
                secondEnd = terminal.CurrentModelSequence;
                await workload.WriteAndWaitAsync(terminal, "d");
                gate.Set();
                await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            gate.Set();
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }

        var artifact = Artifact.Read(path);
        var ends = artifact.Events.Where(e => e.GetProperty("kind").GetString() == "interval-end")
            .Select(e => (Sequence: e.GetProperty("modelSequence").GetInt64(), Case: e.GetProperty("caseSequence").GetInt64(), Reason: e.GetProperty("record").GetProperty("reason").GetString()))
            .ToList();
        var recovery = Recoveries(artifact).Single().GetProperty("modelSequence").GetInt64();
        CollectionAssert.AreEqual(new[] { firstEnd, secondEnd }, ends.Select(e => e.Sequence).ToList(), "the interval ends per segment");
        Assert.IsTrue(ends.All(e => e.Reason == "application-without-ingress"), string.Join(",", ends.Select(e => e.Reason)));
        Assert.IsTrue(firstEnd < recovery && recovery < secondEnd, "fixture: the ends are not on their sides of the recovery");
        foreach (var end in ends)
        {
            var at = artifact.ModelEvents().First(e => e.GetProperty("modelSequence").GetInt64() == end.Sequence).GetProperty("caseSequence").GetInt64();
            Assert.IsLessThan(at, end.Case, $"the end at {end.Sequence} was written after the event it ends at");
        }
    }

    [TestMethod]
    public void Loss_EnvelopeAfterTheCap()
    {
        // Past the cap the ledger keeps the last dropped ordinal: the envelope of the loss it no longer enumerates, taken
        // once at the end. Under the cap there is no envelope.
        var ledger = new CaseLossLedger();
        for (var i = 0; i < 3; i++)
            ledger.Record(CaseStream.Model, 2L * i + 1);
        Assert.IsEmpty(ledger.Envelopes(), "an envelope under the cap");
        const int Ranges = CaseLossLedger.MaxRangesPerStream + 76;
        for (var i = 3; i < Ranges; i++)
            ledger.Record(CaseStream.Model, 2L * i + 1);
        var envelope = ledger.Envelopes().Single();
        Assert.AreEqual(("model", 2L * CaseLossLedger.MaxRangesPerStream + 1, 2L * (Ranges - 1) + 1, "overload-envelope"),
            (envelope.Stream, envelope.FromOrdinal!.Value, envelope.ToOrdinal!.Value, envelope.Reason));
        Assert.IsEmpty(ledger.Envelopes(), "the envelope was taken twice");
        Assert.IsNull(ledger.Take(final: true).Single(r => r.Reason == "overload-unknown-extent").ToOrdinal, "the unknown-extent range was bounded in place");
    }

    // The documentation fence (design C11): the guide, the CLI reference and the MCP skill describe the recovery
    // operation, origins, --from, the envelope and the refusals. The CLI and MCP descriptions are checked by the client
    // tests, which read them through the tools.
    [TestMethod]
    [DataRow("src/content/guide/diagnostic-capture.md")]
    [DataRow("src/content/reference/cli.md")]
    [DataRow("src/Hex1b.McpServer/SKILL.md")]
    public void DocsMentionRecovery(string relativePath)
    {
        var root = AppContext.BaseDirectory;
        while (!File.Exists(Path.Combine(root, "src/Hex1b/Hex1b.csproj")))
            root = Path.GetDirectoryName(root) ?? throw new InvalidOperationException("repository root not found");
        var text = File.ReadAllText(Path.Combine(root, relativePath));
        string[] expected = relativePath.EndsWith("cli.md", StringComparison.Ordinal)
            ? ["capture case recover", "`--from`", "origin", "envelope", "`not-an-origin`", "`unknown-label`", "`beyond-interval`", "`invalid-origin`"]
            : relativePath.EndsWith("SKILL.md", StringComparison.Ordinal)
                ? ["`recover_diagnostic_case`", "`from`", "origin", "envelope", "`beyond-interval`"]
                : ["`recover_diagnostic_case`", "`case-recover`", "`--from`", "`origin`", "`loss-envelope`", "`inside-loss`", "`not-an-origin`", "`unknown-label`", "`unknown-case-sequence`", "`unknown-checkpoint`", "`invalid-origin`", "`beyond-interval`"];
        foreach (var term in expected)
            StringAssert.Contains(text, term, relativePath);
    }

    // The writer held, more chunks than the queue holds: real drop-newest overload, accounted outside the queue.
    private static async Task FloodAsync(Hex1bTerminal terminal, ScriptedWorkload workload, int chunks)
    {
        var total = terminal.OutputBytesRead;
        for (var i = 0; i < chunks; i++)
        {
            var chunk = Encoding.ASCII.GetBytes($"q{i} ");
            total += chunk.Length;
            workload.Enqueue(chunk);
        }
        await WaitAsync(() => terminal.OutputBytesRead == total, TimeSpan.FromSeconds(60));
        await Settle(terminal);
    }

    private static List<JsonElement> Recoveries(Artifact artifact) =>
        artifact.Events.Where(e => e.GetProperty("kind").GetString() == "checkpoint" && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "recovery").ToList();
}
