using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    [TestMethod]
    [DataRow("DECRQSS", "\u001bP$q", "payload", "$q", "m\u001b\\ after")]
    [DataRow("ignored", "\u001bP1;2zpayload", "payload", "1;2zpayload", " more\u001b\\ after")]
    [DataRow("introducer", "\u001bP1;", "introducer", "1;", "2zignored\u001b\\ after")]
    [DataRow("malformed", "\u001bP1:", "malformed-introducer", "1:", "qignored\u001b\\ after")]
    [DataRow("held ESC", "\u001bPzignored\u001b", "escape", "zignored", "\\ after")]
    [DataRow("private non-Sixel q", "\u001bP?qignored", "payload", "?qignored", "\u001b\\ after")]
    [DataRow("intermediate non-Sixel q", "\u001bP!qignored", "payload", "!qignored", "\u001b\\ after")]
    public async Task Start_NonSixelDcsOwnsPrefixAndReappliesSuffix(string shape, string prefix, string expectedState, string retained, string suffix)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, null, 100);
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before " + prefix);
        var startSequence = terminal.CurrentModelSequence;
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        diagnostics.MarkCase("pending");
        await workload.WriteAndWaitAsync(terminal, suffix);
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/3", "complete", startSequence), (checkpoint.GetProperty("profile").GetString(),
            checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("modelSequence").GetInt64()), shape);
        var dcs = artifact.Events[0].GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("dcs");
        Assert.AreEqual((expectedState, Convert.ToBase64String(Encoding.UTF8.GetBytes(retained)), (long)retained.Length),
            (dcs.GetProperty("state").GetString(), dcs.GetProperty("retainedBytes").GetString(), dcs.GetProperty("byteCount").GetInt64()), shape);
        var applications = artifact.ModelEvents().Where(e => e.GetProperty("kind").GetString() == "application").ToList();
        Assert.HasCount(1, applications, shape + ": prefix was replayed as ingress");
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(suffix), Convert.FromBase64String(applications[0].GetProperty("data").GetString()!), shape);
        AssertMatched(Reapply(path, label: "start"), shape + ": start");
        AssertMatched(Reapply(path, label: "pending"), shape + ": pending target");
        AssertMatched(Reapply(path, label: "stop"), shape + ": completed target");
    }

    [TestMethod]
    [DataRow("append and grow", "\u001bPzprefix", "prefix", false)]
    [DataRow("complete and replace", "\u001bPzprefix", "prefix", true)]
    [DataRow("held ESC completes and replaces", "\u001bPzprefix\u001b", "prefix", true)]
    public async Task Start_DcsParserAheadKeepsCommittedPrefixAcrossResizeExactlyOnce(string shape, string prefix, string payload, bool replace)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var hold = new HoldingFilter();
        using var held = hold.Held;
        using var release = hold.Release;
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .AddWorkloadFilter(hold).Build();
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before " + prefix);
        var committed = terminal.CaptureModelState();
        var appended = new string('x', 20_000);
        var suffix = replace ? (prefix.EndsWith('\u001b') ? "\\" : "\u001b\\") + "VISIBLE\u001bPznext" : appended;
        hold.Arm();
        workload.Enqueue(Encoding.UTF8.GetBytes(suffix));
        Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), shape + ": suffix never held after tokenization");
        string path;
        try
        {
            var current = terminal.CaptureModelState();
            Assert.AreEqual(committed.PendingInput.Dcs, current.PendingInput.Dcs, shape + ": live parser contaminated committed capture");
            path = StartLive(terminal, root);
            terminal.Resize(20, 6);
            var diagnostics = new TerminalDiagnostics(terminal);
            diagnostics.MarkCase("held-resized");
            var recovery = diagnostics.RecoverCase("held-recovery");
            Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (recovery.Outcome, recovery.Status),
                shape + ": supported committed recovery refused: " + recovery.Problem?.Message);
        }
        finally
        {
            release.Set();
        }
        await WaitAsync(() => terminal.CurrentModelSequence >= committed.ModelSequence + 2);
        var afterSuffix = terminal.CaptureModelState();
        Assert.AreEqual(replace ? "znext" : "z" + payload + appended,
            Encoding.UTF8.GetString(Convert.FromBase64String(afterSuffix.PendingInput.Dcs!.RetainedBytes)), shape);
        await workload.WriteAndWaitAsync(terminal, "\u001b\\ after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        var model = artifact.ModelEvents();
        CollectionAssert.AreEqual(new[] { "resize", "application", "application" }, model.Select(e => e.GetProperty("kind").GetString()).ToArray(), shape);
        CollectionAssert.AreEqual(Enumerable.Range(1, 3).Select(i => committed.ModelSequence + i).ToArray(), model.Select(e => e.GetProperty("modelSequence").GetInt64()).ToArray(), shape);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(suffix), Convert.FromBase64String(model[1].GetProperty("data").GetString()!), shape + ": in-flight suffix recorded twice or changed");
        foreach (var label in new[] { "start", "held-resized", "held-recovery", "stop" })
            AssertMatched(Reapply(path, label: label), shape + ": " + label);
        AssertMatched(Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", From = "held-recovery" }), shape + ": restored resized origin");
    }

    [TestMethod]
    public async Task Reapply_DcsDeclaredFaultsAreIndependentAndPreserveReconstruction()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, null, 100);
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before \u001bPzignored\u001b");
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        diagnostics.MarkCase("held-escape");
        await workload.WriteAndWaitAsync(terminal, "\\VISIBLE");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        foreach (var (fault, expectedPath) in new[] { ("dcs-bytes", "pendingInput.dcs.retainedBytes"), ("dcs-state", "pendingInput.dcs.state") })
        {
            var result = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "held-escape", Faults = [fault] });
            Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (result.Outcome, result.Comparison, result.FaultInjected), fault + ": " + result.Problem?.Message);
            CollectionAssert.AreEqual(new[] { expectedPath }, result.Differences!.Differences.Select(d => d.Path).ToArray(), fault);
            var reconstructed = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.RunPath!, "reapplied.json"))).RootElement;
            Assert.AreEqual(("escape", Convert.ToBase64String("zignored"u8)), (reconstructed.GetProperty("pendingInput").GetProperty("dcs").GetProperty("state").GetString(),
                reconstructed.GetProperty("pendingInput").GetProperty("dcs").GetProperty("retainedBytes").GetString()), fault + ": reconstruction was faulted");
        }
        AssertMatched(Reapply(path, label: "stop"), "unmodified held-ESC continuation");
        foreach (var fault in new[] { "dcs-bytes", "dcs-state" })
        {
            var absent = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = [fault] });
            Assert.AreEqual(("unavailable", "fault-not-applicable", false), (absent.Comparison, absent.ComparisonReason?.Split(':')[0], absent.FaultInjected), fault);
        }
    }

    [TestMethod]
    public async Task Reapply_EmptyDcsBytesFaultIsNotApplicableButPresentStateIs()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, null, 100);
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before \u001bP");
        var path = StartLive(terminal, root);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var bytes = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "start", Faults = ["dcs-bytes"] });
        Assert.AreEqual(("unavailable", "fault-not-applicable", false), (bytes.Comparison, bytes.ComparisonReason?.Split(':')[0], bytes.FaultInjected));
        var state = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "start", Faults = ["dcs-state"] });
        Assert.AreEqual(("different", true), (state.Comparison, state.FaultInjected), state.Problem?.Message);
        CollectionAssert.AreEqual(new[] { "pendingInput.dcs.state" }, state.Differences!.Differences.Select(d => d.Path).ToArray());
    }

    [TestMethod]
    [DataRow("CAN", "q\u0018after")]
    [DataRow("SUB", "q\u001aafter")]
    [DataRow("complete", "q\u001b\\after")]
    public async Task Reapply_TransientSixelEndsIntervalBeforeApplicationAndRecoveryReopensIt(string shape, string suffix)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(40, 10),
            Width = 40,
            Height = 10,
            ScrollbackCapacity = 100,
            SixelPolicy = Hex1b.Sixel.SixelCompatibilityPolicy.Default with { RejectZeroExtentGraphics = true },
        });
        using var running = new Running(terminal);
        await WriteAndWaitForModelAsync(workload, terminal, "before \u001bP0;0;");
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        var startSequence = terminal.CurrentModelSequence;
        await WriteAndWaitForModelAsync(workload, terminal, "0" + suffix);
        var forbidden = terminal.CurrentModelSequence;
        Assert.IsNull(terminal.CaptureModelState().PendingInput.Dcs, shape + ": fixture did not finish DCS in one chunk");
        Assert.AreEqual(0, terminal.TrackedSixelCount, shape + ": fixture left resident graphics");
        Assert.IsEmpty(terminal.CaptureModelState().Unsupported, shape + ": zero-extent rejection must leave no residual graphics effects");
        StringAssert.Contains(terminal.GetScreenText(), "after", shape + ": suffix did not finish model application");
        diagnostics.MarkCase("ground-mark");
        var recovery = diagnostics.RecoverCase("supported-recovery");
        Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (recovery.Outcome, recovery.Status),
            shape + ": recovery remained poisoned: " + recovery.Problem?.Message);
        await WriteAndWaitForModelAsync(workload, terminal, "\u001bP$qm\u001b\\ supported");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        var endIndex = artifact.Events.FindIndex(e => e.GetProperty("kind").GetString() == "interval-end");
        var applicationIndex = artifact.Events.FindIndex(e => e.GetProperty("stream").GetString() == "model" && e.GetProperty("modelSequence").GetInt64() == forbidden);
        Assert.IsGreaterThanOrEqualTo(0, endIndex, shape);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes("0" + suffix),
            Convert.FromBase64String(artifact.Events[applicationIndex].GetProperty("data").GetString()!), shape + ": original classification chunk changed");
        Assert.IsLessThan(applicationIndex, endIndex, shape + ": forbidden application offered before refusal");
        Assert.AreEqual((forbidden, "sixel-continuation"), (artifact.Events[endIndex].GetProperty("modelSequence").GetInt64(), artifact.Events[endIndex].GetProperty("record").GetProperty("reason").GetString()), shape);
        var intervals = Inspect(path).Intervals;
        Assert.AreEqual((startSequence, forbidden - 1, "sixel-continuation"), (intervals[0].FromModelSequence, intervals[0].ToModelSequence, intervals[0].EndReason), shape);
        var applied = new List<long>();
        CaseReapplier.AfterEventForTesting.Value = sequence => applied.Add(sequence);
        try
        {
            var refused = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "ground-mark", From = "start" });
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", "sixel-continuation"), (refused.Outcome, refused.Problem?.Code, refused.IntervalEndReason), shape);
            Assert.IsEmpty(applied, shape + ": forbidden interval applied before refusing");
        }
        finally
        {
            CaseReapplier.AfterEventForTesting.Value = null;
        }
        var result = Reapply(path, label: "stop");
        AssertMatched(result, shape + ": supported recovery");
        Assert.AreEqual("supported-recovery", result.Origin!.Label, shape);
    }

    [TestMethod]
    public async Task Reapply_DcsContinuationRetainsCumulativeTextState()
    {
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, CaseConfiguration.CreateReflowStrategy("ghostty"), 12, clock: clock);
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 3) + HistoryText(24) + ShellPrompts(4, 6)
            + "\u001b]2;stacked\u0007\u001b]22;\u0007\u001b[?1049h\u001b[H\u001b]133;A\u0007\u001b[32malt\u001b[1;40HW\u001b[?2026h\u001bPzignored\u001b");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "\\Z\u001b[3b\u001b]23;\u0007");
        var before = terminal.CurrentModelSequence;
        clock.Advance(TimeSpan.FromSeconds(1));
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        terminal.Resize(30, 8);
        await workload.WriteAndWaitAsync(terminal, "\u001b[?1049l" + ShellPrompts(7, 8));
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var start = Artifact.Read(path).Events[0].GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual(("alternate", "escape", 12, true), (start.GetProperty("activeBuffer").GetString(),
            start.GetProperty("pendingInput").GetProperty("dcs").GetProperty("state").GetString(),
            start.GetProperty("history").GetProperty("rows").GetArrayLength(), start.GetProperty("titles").GetProperty("stack").GetArrayLength() > 0));
        AssertMatched(Reapply(path, label: "start"), "cumulative DCS origin");
        AssertMatched(Reapply(path, label: "stop"), "history, titles, marks, saved main screen, REP, sync timeout and resized continuation");
    }

    [TestMethod]
    public async Task Start_DcsParserRetentionOverflowRefusesWithoutTruncatedCheckpointAndRecordingContinues()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(20, 4),
            Width = 20,
            Height = 4,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 8 },
        });
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before \u001bPz1234567");
        var exactPath = StartLive(terminal, root);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual("complete", Artifact.Read(exactPath).Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        AssertMatched(Reapply(exactPath, label: "start"), "intact prefix at parser retention boundary");
        await workload.WriteAndWaitAsync(terminal, "8");
        var overflow = terminal.CaptureModelState();
        Assert.AreEqual((9L, true, 8), (overflow.PendingInput.Dcs!.ByteCount, overflow.PendingInput.Dcs.RetentionLimitExceeded,
            Convert.FromBase64String(overflow.PendingInput.Dcs.RetainedBytes).Length));
        CollectionAssert.Contains(overflow.Unsupported.ToArray(), "dcs-retention-limit");
        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        var refusedRecovery = diagnostics.RecoverCase("overflow");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported"), (refusedRecovery.Outcome, refusedRecovery.Status));
        CollectionAssert.Contains(refusedRecovery.UnsupportedSurfaces!.ToArray(), "dcs-retention-limit");
        await workload.WriteAndWaitAsync(terminal, "\u001b\\ ordinary recording");
        Assert.IsFalse(terminal.ContainsSixelData(), "ignored non-Sixel overflow created resident graphics");
        Assert.IsEmpty(terminal.CaptureModelState().Unsupported, "terminated ignored DCS must return to supported text state");
        var groundRecovery = diagnostics.RecoverCase("ground");
        Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (groundRecovery.Outcome, groundRecovery.Status), groundRecovery.Problem?.Message);
        await workload.WriteAndWaitAsync(terminal, " continues");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        Assert.AreEqual("unsupported", artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        CollectionAssert.Contains(artifact.Manifest.GetProperty("checkpoint").GetProperty("unsupportedSurfaces").EnumerateArray().Select(e => e.GetString()).ToArray(), "dcs-retention-limit");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint" && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"));
        var refusedCheckpoint = Recoveries(artifact).Single(e => e.GetProperty("checkpoint").GetProperty("ordinal").GetInt64() == refusedRecovery.CheckpointOrdinal)
            .GetProperty("checkpoint");
        Assert.AreEqual("unsupported", refusedCheckpoint.GetProperty("status").GetString());
        Assert.IsFalse(refusedCheckpoint.TryGetProperty("state", out var refusedState) && refusedState.ValueKind == JsonValueKind.Object,
            "overflow recovery must not record a usable truncated state");
        var groundCheckpoint = Recoveries(artifact).Single(e => e.GetProperty("checkpoint").GetProperty("ordinal").GetInt64() == groundRecovery.CheckpointOrdinal)
            .GetProperty("checkpoint");
        Assert.AreEqual("recorded", groundCheckpoint.GetProperty("status").GetString());
        Assert.AreEqual(JsonValueKind.Null, groundCheckpoint.GetProperty("state").GetProperty("pendingInput").GetProperty("dcs").ValueKind,
            "supported recovery must record the actual ground state");
        CollectionAssert.AreEqual(new[] { "\u001b\\ ordinary recording", " continues" }, artifact.ModelEvents().Where(e => e.GetProperty("kind").GetString() == "application")
            .Select(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.GetProperty("data").GetString()!))).ToArray());
        var reapplied = Reapply(path, label: "stop");
        AssertMatched(reapplied, "ordinary recording after overflow recovery");
        Assert.AreEqual("ground", reapplied.Origin!.Label, "only the supported recovery may reopen the interval");
    }

    [TestMethod]
    public void Start_DcsCheckpointHonorsExactCaseRoomWithoutTruncation()
    {
        using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = new CaseReapplier.DetachedWorkload(),
            PresentationAdapter = new HeadlessPresentationAdapter(20, 4),
            Width = 20,
            Height = 4,
            DeferStart = true,
        });
        terminal.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001bPz" + new string('p', 20_000)));
        var state = terminal.CaptureModelState();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(state, DiagnosticsJsonContext.Default.DiagnosticModelState).LongLength;
        var take = typeof(Hex1bTerminal).GetMethod("TakeCompleteCaptureUnsafe", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        var room = bytes + DiagnosticCaseRecorder.StartLineAllowance;
        DiagnosticCaseRecorder.CheckpointCapture Capture(long available) =>
            (DiagnosticCaseRecorder.CheckpointCapture)take.Invoke(terminal, [available, "start", null, null])!;
        var below = Capture(room + 1);
        var capture = Capture(room);
        var over = Capture(room - 1);
        Assert.IsNotNull(below.State, "below the available room refused intact state");
        Assert.AreEqual(("recorded", bytes), (capture.Status, capture.StateJsonBytes), "exact room did not record the complete serialization");
        Assert.AreEqual(state.PendingInput.Dcs, capture.State!.PendingInput.Dcs, "exact room lost retained content or parser state");
        Assert.IsNull(over.State, "over-budget capture returned a usable truncated checkpoint");
        Assert.AreEqual("unavailable", over.Status);
        StringAssert.StartsWith(over.Reason, "size-limit:");
        const long manifestBytes = 5_000;
        var exact = manifestBytes + DiagnosticCaseRecorder.StartLineAllowance + bytes + DiagnosticCaseRecorder.EventReserve;
        Assert.IsTrue(StartCheckpoint.Fits(manifestBytes, capture, exact + 1), "below available case room");
        Assert.IsTrue(StartCheckpoint.Fits(manifestBytes, capture, exact), "exact available case room");
        Assert.IsFalse(StartCheckpoint.Fits(manifestBytes, capture, exact - 1), "one byte over available case room");
        Assert.AreEqual(20_001L, state.PendingInput.Dcs!.ByteCount);
        Assert.AreEqual(20_001, Convert.FromBase64String(state.PendingInput.Dcs.RetainedBytes).Length, "case accounting truncated parser content");
    }

    [TestMethod]
    public async Task Start_DcsPrefixOverCaseBudgetNamesSizeLimitAndKeepsRecording()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(20, 4),
            Width = 20,
            Height = 4,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 2 * 1024 * 1024 },
        });
        using var running = new Running(terminal);
        await WriteAndWaitForModelAsync(workload, terminal, "\u001bPz" + new string('p', 900_000));
        Assert.IsFalse(terminal.CaptureModelState().PendingInput.Dcs!.RetentionLimitExceeded, "fixture crossed parser retention rather than case room");
        var diagnostics = new TerminalDiagnostics(terminal);
        var captures = terminal.ModelStateCapturesForTesting;
        var path = diagnostics.StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = 1024 * 1024,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
        Assert.AreEqual(captures, terminal.ModelStateCapturesForTesting, "case-budget floor refusal projected the large DCS state");
        var recovery = diagnostics.RecoverCase("over-budget");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "size-limit"),
            (recovery.Outcome, recovery.Status, recovery.Problem?.Code), recovery.Problem?.Message);
        Assert.AreEqual(captures, terminal.ModelStateCapturesForTesting, "recovery budget refusal projected the large DCS state");
        await WriteAndWaitForModelAsync(workload, terminal, "\u001b\\ recorded after refusal");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "size-limit:");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint" && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"));
        var refusedRecovery = Recoveries(artifact).Single().GetProperty("checkpoint");
        Assert.AreEqual("unsupported", refusedRecovery.GetProperty("status").GetString());
        Assert.IsFalse(refusedRecovery.TryGetProperty("state", out _), "size-refused recovery recorded a state");
        CollectionAssert.AreEqual("\u001b\\ recorded after refusal"u8.ToArray(), Convert.FromBase64String(artifact.ModelEvents().Single(e => e.GetProperty("kind").GetString() == "application").GetProperty("data").GetString()!));
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-valid-interval"), (refused.Outcome, refused.Problem?.Code), refused.Problem?.Message);
    }

    [TestMethod]
    public async Task Recover_DcsHeavyCheckpointRefusesBeforeProjectionAndPreservesCompleteOrigin()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(20, 4),
            Width = 20,
            Height = 4,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 2 * 1024 * 1024 },
        });
        using var running = new Running(terminal);
        await WriteAndWaitForModelAsync(workload, terminal, "\u001bPz" + new string('p', 500_000));
        var ready = terminal.CaptureModelState();
        Assert.AreEqual((500_001L, false), (ready.PendingInput.Dcs!.ByteCount, ready.PendingInput.Dcs.RetentionLimitExceeded));
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.StartCase(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = 1024 * 1024,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        }).Path!;
        // Judge artifact room after the complete start is written, not its transient pending-memory estimate.
        await WaitAsync(() => terminal.DiagnosticCase!.PendingStateBytesForTesting == 0);
        lock (TerminalField(terminal, "_bufferLock"))
        {
            var room = terminal.DiagnosticCase!.CheckpointRoom;
            Assert.IsTrue(terminal.MinimumModelStateJsonBytesUnsafe() + DiagnosticCaseRecorder.StartLineAllowance > room,
                "the complete DCS start must leave insufficient artifact room for another retained-prefix checkpoint");
        }
        var captures = terminal.ModelStateCapturesForTesting;
        var recovery = diagnostics.RecoverCase("over-budget-recovery");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "size-limit"),
            (recovery.Outcome, recovery.Status, recovery.Problem?.Code), recovery.Problem?.Message);
        Assert.AreEqual(captures, terminal.ModelStateCapturesForTesting, "size-refused recovery projected the large DCS state");
        await WriteAndWaitForModelAsync(workload, terminal, "\u0018 ordinary recording");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        Assert.AreEqual("complete", artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        var refusedRecovery = Recoveries(artifact).Single().GetProperty("checkpoint");
        Assert.AreEqual("unsupported", refusedRecovery.GetProperty("status").GetString());
        Assert.IsFalse(refusedRecovery.TryGetProperty("state", out _), "size-refused recovery recorded a state");
        CollectionAssert.AreEqual("\u0018 ordinary recording"u8.ToArray(),
            Convert.FromBase64String(artifact.ModelEvents().Single().GetProperty("data").GetString()!),
            "recovery budget refusal stopped ordinary recording");
        AssertMatched(Reapply(path, label: "stop"), "the failed recovery must leave the original complete DCS origin usable");
    }
}
