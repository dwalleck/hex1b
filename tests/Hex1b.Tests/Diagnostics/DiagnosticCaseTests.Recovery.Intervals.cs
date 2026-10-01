using System.Text.Json;
using System.Runtime.CompilerServices;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // Intervals per origin, and re-application from a recovery checkpoint (ticket 13, slice 2).

    private sealed record ExpectedInterval(string Trigger, string? Label, bool Valid, long? From, long? To, string Reason);

    [TestMethod]
    [DataRow("no recovery")]
    [DataRow("recovery after loss")]
    [DataRow("recovery before loss")]
    [DataRow("unsupported recovery")]
    [DataRow("two losses, three recoveries")]
    [DataRow("origin inside loss")]
    [DataRow("ends per segment")]
    [DataRow("origin at the first lost event")]
    public async Task Intervals_OnePerOrigin(string shape)
    {
        // One interval per origin, each ending at the earliest end at or after it; a later origin never changes an
        // earlier interval; an origin inside a loss range starts nothing; an unsupported recovery is an invalid entry.
        using var root = new CaseRoot();
        var (terminal, workload, path, gate) = HeldCase(root);
        var expected = new List<ExpectedInterval>();
        long last;
        await using (terminal)
        using (gate)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                try
                {
                    switch (shape)
                    {
                        case "no recovery":
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            await DrainAsync(gate, diagnostics);
                            expected.Add(new("start", null, true, 0, CaseEventQueueMax, "overload"));
                            break;
                        case "recovery after loss":
                        {
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            await DrainAsync(gate, diagnostics);
                            var r1 = Recover(diagnostics, "r1");
                            await workload.WriteAndWaitAsync(terminal, "tail");
                            expected.Add(new("start", null, true, 0, CaseEventQueueMax, "overload"));
                            expected.Add(new("recovery", "r1", true, r1.ModelSequence, null, "case-stopped: requested"));
                            break;
                        }
                        case "recovery before loss":
                        {
                            await FloodAsync(terminal, workload, 10);
                            var r1 = Recover(diagnostics, "r1");
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            await DrainAsync(gate, diagnostics);
                            expected.Add(new("start", null, true, 0, CaseEventQueueMax, "overload"));
                            expected.Add(new("recovery", "r1", true, r1.ModelSequence, CaseEventQueueMax, "overload"));
                            break;
                        }
                        case "unsupported recovery":
                        {
                            await DrainAsync(gate, diagnostics);
                            await workload.WriteAndWaitAsync(terminal, "x\u001bP$q");
                            var u = diagnostics.RecoverCase("u");
                            Assert.AreEqual("unsupported", u.Status, $"fixture: {u.Problem?.Message}");
                            await workload.WriteAndWaitAsync(terminal, "m\u001b\\");
                            expected.Add(new("start", null, true, 0, null, "case-stopped: requested"));
                            expected.Add(new("recovery", "u", false, null, null, "checkpoint unsupported: unsupported-surfaces"));
                            break;
                        }
                        case "two losses, three recoveries":
                        {
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            await DrainAsync(gate, diagnostics);
                            await HoldAsync(gate);
                            var r1 = Recover(diagnostics, "r1");
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            await DrainAsync(gate, diagnostics);
                            var r2 = Recover(diagnostics, "r2");
                            await workload.WriteAndWaitAsync(terminal, "tail");
                            var r3 = Recover(diagnostics, "r3");
                            await workload.WriteAndWaitAsync(terminal, "end");
                            expected.Add(new("start", null, true, 0, CaseEventQueueMax, "overload"));
                            expected.Add(new("recovery", "r1", true, r1.ModelSequence, r1.ModelSequence + CaseEventQueueMax, "overload"));
                            expected.Add(new("recovery", "r2", true, r2.ModelSequence, null, "case-stopped: requested"));
                            expected.Add(new("recovery", "r3", true, r3.ModelSequence, null, "case-stopped: requested"));
                            break;
                        }
                        case "origin inside loss":
                        {
                            await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                            var r1 = Recover(diagnostics, "r1");
                            await FloodAsync(terminal, workload, 300);
                            await DrainAsync(gate, diagnostics);
                            expected.Add(new("start", null, true, 0, CaseEventQueueMax, "overload"));
                            expected.Add(new("recovery", "r1", false, r1.ModelSequence, null, "inside-loss"));
                            break;
                        }
                        case "ends per segment":
                        {
                            // An application without ingress ends each segment at its own sequence (ticket 10).
                            await DrainAsync(gate, diagnostics);
                            await workload.WriteAndWaitAsync(terminal, "a");
                            terminal.EnterAlternateScreen();
                            var firstEnd = terminal.CurrentModelSequence;
                            await workload.WriteAndWaitAsync(terminal, "b");
                            var r1 = Recover(diagnostics, "r1");
                            await workload.WriteAndWaitAsync(terminal, "c");
                            terminal.ExitAlternateScreen();
                            var secondEnd = terminal.CurrentModelSequence;
                            await workload.WriteAndWaitAsync(terminal, "d");
                            expected.Add(new("start", null, true, 0, firstEnd - 1, "application-without-ingress"));
                            expected.Add(new("recovery", "r1", true, r1.ModelSequence, secondEnd - 1, "application-without-ingress"));
                            break;
                        }
                        case "origin at the first lost event":
                        {
                            // A range that begins at the origin's own event and continues past it covers the origin.
                            await DrainAsync(gate, diagnostics);
                            await FloodAsync(terminal, workload, 10);
                            var r1 = Recover(diagnostics, "r1");
                            var ledger = terminal.DiagnosticCase!.LossForTesting;
                            ledger.Record(CaseStream.Model, r1.ModelSequence!.Value);
                            ledger.Record(CaseStream.Model, r1.ModelSequence.Value + 1);
                            await FloodAsync(terminal, workload, 5);
                            expected.Add(new("start", null, true, 0, r1.ModelSequence - 1, "overload"));
                            expected.Add(new("recovery", "r1", false, r1.ModelSequence, null, "inside-loss"));
                            break;
                        }
                    }
                    await Settle(terminal);
                    last = terminal.CurrentModelSequence;
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                finally
                {
                    gate.Set();
                }
            }
        }

        var inspection = Inspect(path);
        var intervals = inspection.Intervals;
        Assert.AreEqual(expected.Count, intervals.Count, $"{shape}: {Describe(intervals)}");
        for (var i = 0; i < expected.Count; i++)
        {
            var (e, a) = (expected[i], intervals[i]);
            var to = e.To ?? (e.Reason.StartsWith("case-stopped", StringComparison.Ordinal) ? last : null);
            Assert.AreEqual((e.Trigger, e.Label, e.Valid, e.From, to),
                (a.Origin?.Trigger, a.Origin is { Trigger: "recovery" } o ? o.Label : null, a.Valid, a.FromModelSequence, a.ToModelSequence), $"{shape} interval {i}: {a.EndReason}");
            StringAssert.StartsWith(a.EndReason, e.Reason, $"{shape} interval {i}");
        }
        var initial = intervals[0].Origin!;
        Assert.AreEqual(("start", DiagnosticCaseCheckpointProfiles.FreshModel, 0L), (initial.Trigger, initial.Profile, initial.ModelSequence), "the initial origin");
        foreach (var origin in intervals.Skip(1).Select(i => i.Origin!))
            Assert.IsTrue(origin.CheckpointOrdinal > 0 && origin.CaseSequence > 0 && origin.Profile == DiagnosticCaseCheckpointProfiles.TextState,
                $"{shape}: the recovery origin {origin.Label} lacks its identity");
    }

    [TestMethod]
    public async Task Intervals_EnvelopeBoundsRecovery()
    {
        // Past the ledger's cap the inspection bounds the unknown-extent range by its envelope: a recovery inside the
        // envelope starts nothing, one after it starts a valid interval. Without the envelope line (an interrupted case)
        // the range stays open-ended and the later recovery is invalid.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;
        DiagnosticCaseRecoverResult inside, after;
        const int Ranges = CaseLossLedger.MaxRangesPerStream + 76;
        long last;
        using (new Running(terminal))
        {
            await FloodAsync(terminal, workload, 2_100);
            inside = Recover(diagnostics, "inside");
            await FloodAsync(terminal, workload, 200);
            // Every other ordinal from 1 to 2,199 declared lost: 1,024 ranges, then unknown extent from 2,049 to 2,199.
            var recorder = terminal.DiagnosticCase!;
            for (var i = 0; i < Ranges; i++)
                recorder.LossForTesting.Record(CaseStream.Model, 2L * i + 1);
            after = Recover(diagnostics, "after");
            await FloodAsync(terminal, workload, 5);
            last = terminal.CurrentModelSequence;
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var envelopeEnd = 2L * (Ranges - 1) + 1;
        Assert.IsTrue(inside.ModelSequence <= envelopeEnd && after.ModelSequence > envelopeEnd, $"fixture: {inside.ModelSequence} {after.ModelSequence} {envelopeEnd}");

        var inspection = Inspect(path);
        var unknown = inspection.Streams.Single(s => s.Stream == "model").Missing.Single(m => m.Reason == "overload-unknown-extent");
        Assert.AreEqual((2L * CaseLossLedger.MaxRangesPerStream + 1, envelopeEnd, "envelope"), (unknown.FromOrdinal!.Value, unknown.ToOrdinal, unknown.Extent), "the envelope-bounded range");
        var byLabel = inspection.Intervals.Where(i => i.Origin is { Trigger: "recovery" }).ToDictionary(i => i.Origin!.Label!);
        Assert.IsFalse(byLabel["inside"].Valid, "a recovery inside the envelope started an interval");
        StringAssert.StartsWith(byLabel["inside"].EndReason, "inside-loss");
        Assert.AreEqual((true, after.ModelSequence, last), (byLabel["after"].Valid, byLabel["after"].FromModelSequence, byLabel["after"].ToModelSequence), byLabel["after"].EndReason);

        // The same artifact without its envelope line and completion: interrupted, the range open-ended.
        var interrupted = Path.Combine(root.Path, "interrupted");
        Directory.CreateDirectory(interrupted);
        foreach (var file in Directory.GetFiles(path))
            File.Copy(file, Path.Combine(interrupted, Path.GetFileName(file)));
        File.Delete(Path.Combine(interrupted, "completion.json"));
        var events = Path.Combine(interrupted, "events.jsonl");
        File.WriteAllLines(events, File.ReadAllLines(events).Where(l => !l.Contains("\"loss-envelope\"", StringComparison.Ordinal)));
        var open = Inspect(interrupted);
        Assert.AreEqual(DiagnosticCaseCompletionState.Interrupted, open.CompletionState);
        var openRange = open.Streams.Single(s => s.Stream == "model").Missing.Single(m => m.Reason == "overload-unknown-extent");
        Assert.AreEqual((null, null), (openRange.ToOrdinal, openRange.Extent), "an unbounded range was bounded");
        var reopened = open.Intervals.Single(i => i.Origin?.Label == "after");
        Assert.IsFalse(reopened.Valid, $"a recovery after an open-ended loss started an interval: {reopened.EndReason}");

        // The envelope kept and a model event after it removed: the gap past the envelope is loss of unknown cause, not
        // part of the bounded range.
        var gapped = Path.Combine(root.Path, "gapped");
        Directory.CreateDirectory(gapped);
        foreach (var file in Directory.GetFiles(path))
            File.Copy(file, Path.Combine(gapped, Path.GetFileName(file)));
        var removed = envelopeEnd + 100;
        var gappedEvents = Path.Combine(gapped, "events.jsonl");
        File.WriteAllLines(gappedEvents, File.ReadAllLines(gappedEvents).Where(l => !IsModelOrdinal(l, removed)));
        var unknownGap = Inspect(gapped).Streams.Single(s => s.Stream == "model").Missing.Where(m => m.Reason == "unknown").ToList();
        Assert.AreEqual(1, unknownGap.Count, "the gap past the envelope is not reported as unknown loss");
        Assert.AreEqual((removed, removed), (unknownGap[0].FromOrdinal, unknownGap[0].ToOrdinal));
    }

    private static bool IsModelOrdinal(string line, long ordinal)
    {
        using var document = System.Text.Json.JsonDocument.Parse(line[(line.IndexOf('\t') + 1)..]);
        return document.RootElement.GetProperty("stream").GetString() == "model" && document.RootElement.GetProperty("ordinal").GetInt64() == ordinal;
    }

    [TestMethod]
    public async Task Reapply_SelectsTheEarliestCoveringOrigin()
    {
        // With no loss before the recovery both intervals cover the later mark: the initial origin is restored from by
        // default; --from names the recovery; a target before a named origin is refused; two recoveries with one label
        // are ambiguous by label and distinct by case sequence.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;
        DiagnosticCaseRecoverResult r1, second;
        using (new Running(terminal))
        {
            await FloodAsync(terminal, workload, 10);
            r1 = Recover(diagnostics, "r1");
            await FloodAsync(terminal, workload, 10);
            Mark(diagnostics, "m");
            Recover(diagnostics, "dup");
            await FloodAsync(terminal, workload, 2);
            second = Recover(diagnostics, "dup");
            await FloodAsync(terminal, workload, 2);
            Mark(diagnostics, "end");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var byDefault = Reapply(path, label: "m");
        AssertMatched(byDefault, "the mark from the initial origin");
        Assert.AreEqual(("start", DiagnosticCaseCheckpointProfiles.FreshModel, 0L), (byDefault.Origin!.Trigger, byDefault.Origin.Profile, byDefault.Origin.ModelSequence), "the default origin");
        var fromRecovery = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "m", From = "r1" });
        AssertMatched(fromRecovery, "the mark from the recovery");
        Assert.AreEqual(("recovery", "r1", r1.ModelSequence, r1.CheckpointOrdinal),
            (fromRecovery.Origin!.Trigger, fromRecovery.Origin.Label, fromRecovery.Origin.ModelSequence, fromRecovery.Origin.CheckpointOrdinal), "the named origin");
        var before = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToModelSequence = 5, From = "r1" });
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval"), (before.Outcome, before.Problem?.Code), before.Problem?.Message);
        var ambiguous = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "end", From = "dup" });
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "ambiguous-label"), (ambiguous.Outcome, ambiguous.Problem?.Code), ambiguous.Problem?.Message);
        var secondLine = Recoveries(Artifact.Read(path)).Where(r => r.GetProperty("checkpoint").GetProperty("label").GetString() == "dup").Max(r => r.GetProperty("caseSequence").GetInt64());
        var bySequence = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "end", From = $"case:{secondLine}" });
        AssertMatched(bySequence, "the second duplicate by case sequence");
        Assert.AreEqual(("dup", second.ModelSequence, second.CheckpointOrdinal), (bySequence.Origin!.Label, bySequence.Origin.ModelSequence, bySequence.Origin.CheckpointOrdinal), "the disambiguated origin");
    }

    [TestMethod]
    [DataRow("start")]
    [DataRow("r1")]
    [DataRow("label:r1")]
    [DataRow("case:")]
    [DataRow("checkpoint:")]
    [DataRow("m")]
    [DataRow("nope")]
    [DataRow("case:999999")]
    [DataRow("checkpoint:99")]
    [DataRow("u")]
    public async Task Reapply_FromOverrides(string form)
    {
        // Every form of --from: the start (refused across the gap), the recovery by label, case sequence or ordinal
        // (matched, a mark sharing the label notwithstanding), a mark (not an origin), an unknown label, case sequence
        // or ordinal (each with its code), and an unsupported recovery (refused with its reason).
        using var root = new CaseRoot();
        var (terminal, workload, path, gate) = HeldCase(root);
        DiagnosticCaseRecoverResult r1, u;
        await using (terminal)
        using (gate)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                try
                {
                    await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                    await DrainAsync(gate, diagnostics);
                    r1 = Recover(diagnostics, "r1");
                    Mark(diagnostics, "r1");
                    await workload.WriteAndWaitAsync(terminal, "after ");
                    Mark(diagnostics, "m");
                    await workload.WriteAndWaitAsync(terminal, "\u001bP$q");
                    u = diagnostics.RecoverCase("u");
                    await workload.WriteAndWaitAsync(terminal, "m\u001b\\ tail");
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                finally
                {
                    gate.Set();
                }
            }
        }
        Assert.AreEqual("unsupported", u.Status, $"fixture: {u.Problem?.Message}");
        var r1Line = Recoveries(Artifact.Read(path)).Single(r => r.GetProperty("checkpoint").GetProperty("label").GetString() == "r1").GetProperty("caseSequence").GetInt64();
        var from = form switch { "case:" => $"case:{r1Line}", "checkpoint:" => $"checkpoint:{r1.CheckpointOrdinal}", _ => form };

        var result = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", From = from });
        switch (form)
        {
            case "r1" or "label:r1" or "case:" or "checkpoint:":
                AssertMatched(result, form);
                Assert.AreEqual(("r1", r1.ModelSequence), (result.Origin!.Label, result.Origin.ModelSequence), form);
                break;
            case "start":
                Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", (long?)CaseEventQueueMax, "overload"),
                    (result.Outcome, result.Problem?.Code, result.LastValidModelSequence, result.IntervalEndReason), result.Problem?.Message);
                break;
            case "m":
                Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "not-an-origin"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
                break;
            case "nope" or "case:999999" or "checkpoint:99":
                var unknown = form switch { "nope" => "unknown-label", "case:999999" => "unknown-case-sequence", _ => "unknown-checkpoint" };
                Assert.AreEqual((DiagnosticOutcome.InvalidRequest, unknown), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
                break;
            default:
                Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
                StringAssert.StartsWith(result.IntervalEndReason, "checkpoint unsupported", form);
                break;
        }
        if (result.Outcome != DiagnosticOutcome.Captured)
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{form}: a refused re-application wrote a run");
    }

    [TestMethod]
    public async Task Reapply_RefusesAcrossTheGapBeforeApplying()
    {
        // A target in the gap, or retained after it but before the recovery, is refused before any event is applied,
        // with no run written; the last retained event before the gap re-applies from the start; the stop from the
        // recovery.
        using var root = new CaseRoot();
        var (terminal, workload, path, gate) = HeldCase(root);
        DiagnosticCaseRecoverResult r1;
        await using (terminal)
        using (gate)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                try
                {
                    await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                    await DrainAsync(gate, diagnostics);
                    await workload.WriteAndWaitAsync(terminal, "between ");
                    await workload.WriteAndWaitAsync(terminal, "more ");
                    r1 = Recover(diagnostics, "r1");
                    await workload.WriteAndWaitAsync(terminal, "tail");
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                finally
                {
                    gate.Set();
                }
            }
        }
        var recoveryAt = r1.ModelSequence!.Value;
        Assert.IsTrue(recoveryAt - 1 > CaseEventQueueMax + 1, $"fixture: no retained event between the gap and the recovery at {recoveryAt}");

        var applied = new StrongBox<int>(0);
        CaseReapplier.AppliedEventsForTesting.Value = applied;
        try
        {
            foreach (var target in new[] { CaseEventQueueMax + 300, recoveryAt - 1 })
            {
                var refused = Reapply(path, modelSequence: target);
                Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", (long?)CaseEventQueueMax, "overload"),
                    (refused.Outcome, refused.Problem?.Code, refused.LastValidModelSequence, refused.IntervalEndReason), refused.Problem?.Message);
                Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{target}: a refused re-application wrote a run");
            }
            Assert.AreEqual(0, applied.Value, "a refused re-application applied events");
        }
        finally
        {
            CaseReapplier.AppliedEventsForTesting.Value = null;
        }
        var lastRetained = Reapply(path, modelSequence: CaseEventQueueMax);
        Assert.AreEqual((DiagnosticOutcome.Captured, "start", (long?)CaseEventQueueMax), (lastRetained.Outcome, lastRetained.Origin?.Trigger, lastRetained.AppliedThrough), lastRetained.Problem?.Message);
        var stop = Reapply(path, label: "stop");
        AssertMatched(stop, "the stop from the recovery");
        Assert.AreEqual("r1", stop.Origin!.Label);
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("ghostty")]
    [DataRow("xterm")]
    public async Task Reapply_AfterRecoveryMatchesAtEveryCheckpoint(string? strategyId)
    {
        // Real overload on a live-started case holding history, a title stack, command marks and a pending scalar at
        // the recovery; the recovery, the later marks and the stop re-apply matched from it, and a fault at a mark
        // after it is different.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategyId is null ? null : CaseConfiguration.CreateReflowStrategy(strategyId), 100);
        var diagnostics = new TerminalDiagnostics(terminal);
        string path;
        DiagnosticCaseRecoverResult r1;
        using (new Running(terminal))
        {
            try
            {
                await workload.WriteAndWaitAsync(terminal, ShellPrompts(1, 3) + HistoryText(24));
                DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
                try
                {
                    path = StartLive(terminal, root);
                }
                finally
                {
                    DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
                }
                await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                await DrainAsync(gate, diagnostics);
                // Retained after the loss: prompts whose marked rows are in history at the recovery, and the pending
                // scalar's lead byte as the last event before it, at the recovery's own model sequence.
                await workload.WriteAndWaitAsync(terminal, ShellPrompts(4, 6) + "\r\npending ");
                await workload.WriteAndWaitAsync(terminal, [0xe6]);
                r1 = Recover(diagnostics, "r1");
                Assert.AreEqual(terminal.CurrentModelSequence, r1.ModelSequence, "fixture: the recovery is not at the last retained event");
                await workload.WriteAndWaitAsync(terminal, [0xbc, 0xa2, .. Encoding.UTF8.GetBytes(ShellPrompts(7, 8))]);
                Mark(diagnostics, "after");
                terminal.Resize(30, 8);
                await WaitAsync(() => terminal.Width == 30);
                await workload.WriteAndWaitAsync(terminal, ShellPrompts(9, 9));
                Mark(diagnostics, "resized");
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                gate.Set();
            }
        }
        var state = Recoveries(Artifact.Read(path)).Single().GetProperty("checkpoint").GetProperty("state");
        var surfaces = (state.GetProperty("history").GetProperty("rows").GetArrayLength() > 0, state.GetProperty("titles").GetProperty("stack").GetArrayLength() > 0,
            state.GetProperty("commandMarks").GetArrayLength() > 0, state.GetProperty("pendingInput").GetProperty("utf8").GetString());
        Assert.AreEqual((true, true, true, "5g=="), surfaces, "fixture: the recovery does not hold every sibling surface (history, titles, marks, pending)");

        foreach (var label in new[] { "r1", "after", "resized", "stop" })
        {
            var result = Reapply(path, label: label);
            AssertMatched(result, $"{strategyId ?? "none"} {label}");
            Assert.AreEqual(("r1", r1.ModelSequence), (result.Origin!.Label, result.Origin.ModelSequence), $"{label}: the origin");
        }
        var faulted = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "after", Faults = ["title-stack"] });
        Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (faulted.Outcome, faulted.Comparison, faulted.FaultInjected), faulted.ComparisonReason);
        var fromStart = Reapply(path, label: "start");
        AssertMatched(fromStart, "the live start before the loss");
        var startLine = Artifact.Read(path).Events[0];
        Assert.AreEqual(("start", DiagnosticCaseCheckpointProfiles.TextState, startLine.GetProperty("caseSequence").GetInt64(), startLine.GetProperty("modelSequence").GetInt64()),
            (fromStart.Origin!.Trigger, fromStart.Origin.Profile, fromStart.Origin.CaseSequence, fromStart.Origin.ModelSequence), "the live start as the origin");
    }

    [TestMethod]
    [DataRow("truncation")]
    [DataRow("missing-completion")]
    [DataRow("disposal")]
    [DataRow("drain-timeout")]
    [DataRow("writer-failure")]
    public async Task Recovery_FailurePaths(string failure)
    {
        // After a recovery, each failure path ends the recovery's interval with the existing reason at the last
        // verified event; the earlier interval is unchanged. A recovery pending at a writer failure is written with
        // the failure: the loss it declares is before the origin, and nothing follows it.
        using var root = new CaseRoot();
        if (failure == "drain-timeout")
            DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = TimeSpan.FromMilliseconds(300);
        if (failure == "writer-failure")
            DiagnosticCaseRecorder.WriterFaultForTesting.Value = new IOException("injected storage failure");
        Hex1bTerminal terminal;
        ScriptedWorkload workload;
        string path;
        ManualResetEventSlim gate;
        try
        {
            (terminal, workload, path, gate) = HeldCase(root);
        }
        finally
        {
            DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = null;
            DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
        }
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseRecoverResult r1;
        var running = new Running(terminal);
        var disposed = false;
        try
        {
            if (failure == "writer-failure")
            {
                await FloodAsync(terminal, workload, 20);
                r1 = Recover(diagnostics, "r1");
                gate.Set();
            }
            else
            {
                await FloodAsync(terminal, workload, CaseEventQueueMax + 700);
                await DrainAsync(gate, diagnostics);
                r1 = Recover(diagnostics, "r1");
                await FloodAsync(terminal, workload, 20);
                if (failure == "drain-timeout")
                {
                    await DrainAsync(gate, diagnostics);
                    await HoldAsync(gate);
                    await FloodAsync(terminal, workload, 50);
                }
                if (failure == "disposal")
                {
                    await terminal.DisposeAsync();
                    disposed = true;
                }
                else
                {
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                gate.Set();
            }
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
        }
        finally
        {
            gate.Set();
            running.Dispose();
            if (!disposed)
                await terminal.DisposeAsync();
            gate.Dispose();
        }

        if (failure == "truncation")
        {
            var events = Path.Combine(path, "events.jsonl");
            File.WriteAllBytes(events, File.ReadAllBytes(events)[..^6]);
        }
        if (failure == "missing-completion")
            File.Delete(Path.Combine(path, "completion.json"));

        var inspection = Inspect(path);
        var intervals = inspection.Intervals;
        if (failure == "writer-failure")
        {
            Assert.AreEqual(DiagnosticCaseStopReason.CollectorFailed, inspection.Completion!.StopReason);
            var counts = inspection.Completion.Checkpoints!;
            Assert.AreEqual((1L, counts.Offered), (counts.Offered, counts.Written + counts.Dropped), "the pending recovery is not accounted as written or dropped");
            var failedInitial = intervals[0];
            Assert.AreEqual((true, 0L, 0L, "collector-failed"), (failedInitial.Valid, failedInitial.FromModelSequence, failedInitial.ToModelSequence, failedInitial.EndReason), Describe(intervals));
            var pending = intervals.Single(i => i.Origin is { Trigger: "recovery" });
            Assert.AreEqual((true, r1.ModelSequence, r1.ModelSequence, "case-stopped: collector-failed"),
                (pending.Valid, pending.FromModelSequence, pending.ToModelSequence, pending.EndReason), Describe(intervals));
            return;
        }
        var initial = intervals[0];
        Assert.AreEqual((true, 0L, (long?)CaseEventQueueMax, "overload"), (initial.Valid, initial.FromModelSequence, initial.ToModelSequence, initial.EndReason), "the earlier interval changed");
        var recovery = intervals.Single(i => i.Origin is { Trigger: "recovery" });
        Assert.IsTrue(recovery.Valid, recovery.EndReason);
        Assert.AreEqual(r1.ModelSequence, recovery.FromModelSequence);
        var expectedReason = failure switch
        {
            "truncation" => "truncated",
            "missing-completion" => "interrupted",
            "disposal" => "case-stopped: target-disposed",
            _ => "drain-timeout",
        };
        Assert.AreEqual(expectedReason, recovery.EndReason, failure);
        var lastVerified = inspection.Streams.Single(s => s.Stream == "model").LastOrdinal;
        Assert.AreEqual(failure == "drain-timeout" ? r1.ModelSequence + 20 : lastVerified, recovery.ToModelSequence,
            $"{failure}: the recovery interval does not end at the last verified event before the failure");
    }

    [TestMethod]
    public async Task Overload_PumpNeverWaitsWithARecovery()
    {
        // Ticket 07's output equivalence with a recovery requested in the flood: the writer held for the whole run,
        // the presentation bytes and the model equal an unarmed run's, and the recovery is complete.
        using var root = new CaseRoot();
        var chunks = Enumerable.Range(0, 20_000).Select(i => Encoding.ASCII.GetBytes($"{i % 97} ")).ToArray();
        async Task<(string Presentation, string Model)> RunAsync(bool armed, ManualResetEventSlim? gate)
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
            var workload = new ScriptedWorkload();
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10);
            if (armed)
                builder.WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
            await using var terminal = builder.Build();
            using (new Running(terminal))
            {
                try
                {
                    var total = chunks.Sum(c => (long)c.Length);
                    for (var i = 0; i < chunks.Length; i++)
                    {
                        workload.Enqueue(chunks[i]);
                        if (armed && i == chunks.Length / 2)
                        {
                            var recovery = Task.Run(() => new TerminalDiagnostics(terminal).RecoverCase("mid-flood"));
                            Assert.IsTrue(recovery.Wait(TimeSpan.FromSeconds(10)), "the recovery did not return while the writer was held");
                            Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (recovery.Result.Outcome, recovery.Result.Status), recovery.Result.Problem?.Message);
                        }
                    }
                    await WaitAsync(() => terminal.OutputBytesRead == total);
                    await Settle(terminal);
                    var result = (driver.WrittenText, Corpus.Digest(terminal));
                    if (armed)
                        Assert.AreEqual(0L, new TerminalDiagnostics(terminal).GetCaseStatus().Streams.Single(s => s.Stream == "model").Written, "fixture: the writer was not held");
                    return result;
                }
                finally
                {
                    gate?.Set();
                }
            }
        }

        var unarmed = await RunAsync(armed: false, gate: null);
        using var held = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = held;
        try
        {
            var armed = await RunAsync(armed: true, held);
            Assert.AreEqual(unarmed.Model, armed.Model, "overload with a recovery changed the model");
            Assert.AreEqual(unarmed.Presentation, armed.Presentation, "overload with a recovery changed the presentation output");
        }
        finally
        {
            held.Set();
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Reapply_RecoveryAfterAnUnsupportedStart()
    {
        // A live start taken with a DCS in progress is unsupported, so the case has no re-applicable interval until a
        // recovery: the recovery is the first valid origin; a target before it is refused naming the start's reason and
        // no last valid sequence, as is --from start; targets after it re-apply from the recovery.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, null, 100);
        var diagnostics = new TerminalDiagnostics(terminal);
        string path;
        DiagnosticCaseRecoverResult r1;
        long between;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before \u001bP$q");
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, "m\u001b\\ one");
            between = terminal.CurrentModelSequence;
            await workload.WriteAndWaitAsync(terminal, " two");
            r1 = Recover(diagnostics, "r1");
            await workload.WriteAndWaitAsync(terminal, " after");
            Mark(diagnostics, "m");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var inspection = Inspect(path);
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Unsupported, inspection.Manifest!.Checkpoint.Status, "fixture: the start is not unsupported");
        var intervals = inspection.Intervals;
        Assert.AreEqual(2, intervals.Count, Describe(intervals));
        Assert.IsFalse(intervals[0].Valid, Describe(intervals));
        StringAssert.StartsWith(intervals[0].EndReason, "checkpoint unsupported");
        Assert.IsTrue(intervals[1].Valid && intervals[1].Origin is { Trigger: "recovery", Label: "r1" }, Describe(intervals));

        var before = Reapply(path, modelSequence: between);
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", (long?)null), (before.Outcome, before.Problem?.Code, before.LastValidModelSequence), before.Problem?.Message);
        StringAssert.StartsWith(before.IntervalEndReason, "checkpoint unsupported", before.Problem?.Message);
        StringAssert.Contains(before.Problem!.Message, "checkpoint unsupported");
        var fromStart = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "m", From = "start" });
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", (long?)null), (fromStart.Outcome, fromStart.Problem?.Code, fromStart.LastValidModelSequence), fromStart.Problem?.Message);
        StringAssert.StartsWith(fromStart.IntervalEndReason, "checkpoint unsupported", fromStart.Problem?.Message);
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "a refused re-application wrote a run");
        foreach (var label in new[] { "r1", "m", "stop" })
        {
            var result = Reapply(path, label: label);
            AssertMatched(result, label);
            Assert.AreEqual("r1", result.Origin!.Label, label);
            // Ticket 14: the unsupported start declares no surfaces; the record says so and the recovery's profile is what was judged.
            var surfaces = result.Compatibility.Checks.Single(c => c.Check == "checkpoint.coveredSurfaces");
            Assert.AreEqual(("compatible", "(none declared: the start is not complete)"), (surfaces.Verdict, surfaces.Producer), label);
            Assert.AreEqual("text-state/1 at model sequence " + result.Origin.ModelSequence, result.Compatibility.Checks[6].Producer, label);
        }
    }

    [TestMethod]
    public void ReapplyRequest_FromForms()
    {
        // The parser trims and keeps an origin name, drops an empty one, and refuses a case: or checkpoint: form that
        // is not followed by a number, as it refuses such a target.
        static (DiagnosticCaseReapplyRequest? Request, DiagnosticCaseReapplyResult? Invalid) Parse(string? from) =>
            DiagnosticContractNames.ParseCaseReapplyRequest("/case", "stop", null, null, null, from);
        Assert.AreEqual("r1", Parse("r1").Request!.From);
        Assert.AreEqual("label:r1", Parse(" label:r1 ").Request!.From);
        Assert.AreEqual("checkpoint:2", Parse("checkpoint:2").Request!.From);
        Assert.AreEqual("case:34", Parse("case:34").Request!.From);
        Assert.IsNull(Parse("").Request!.From);
        Assert.IsNull(Parse(null).Request!.From);
        foreach (var bad in new[] { "case:abc", "checkpoint:x", "case:", "checkpoint:-1" })
        {
            var (request, invalid) = Parse(bad);
            Assert.IsNull(request, bad);
            Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "invalid-origin"), (invalid!.Outcome, invalid.Problem?.Code), bad);
        }
    }

    [TestMethod]
    public async Task Recovery_OneEndPerSegment()
    {
        // With the writer running, a segment's first end is written once: a later end in the same segment, raised after
        // the first was written, adds no line; a recovery opens a segment whose own first end is written.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;
        long firstEnd, recoveredEnd;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "a");
            terminal.EnterAlternateScreen();
            firstEnd = terminal.CurrentModelSequence;
            await workload.WriteAndWaitAsync(terminal, "b");
            await WrittenAsync(diagnostics, 3);
            terminal.ExitAlternateScreen();
            await workload.WriteAndWaitAsync(terminal, "c");
            await WrittenAsync(diagnostics, 5);
            Recover(diagnostics, "r1");
            await workload.WriteAndWaitAsync(terminal, "d");
            terminal.EnterAlternateScreen();
            recoveredEnd = terminal.CurrentModelSequence;
            await workload.WriteAndWaitAsync(terminal, "e");
            await WrittenAsync(diagnostics, 8);
            terminal.ExitAlternateScreen();
            await workload.WriteAndWaitAsync(terminal, "f");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var ends = Artifact.Read(path).Events.Where(e => e.GetProperty("kind").GetString() == "interval-end").Select(e => e.GetProperty("modelSequence").GetInt64()).ToList();
        CollectionAssert.AreEqual(new[] { firstEnd, recoveredEnd }, ends, $"the interval ends written: {string.Join(",", ends)}");
    }

    [TestMethod]
    public async Task Recover_RefusedWhenTheQueueLeavesNoRoom()
    {
        // The room a recovery's state must fit is what the events tier leaves after the queued events and the pending
        // checkpoint states, which are written before its line: with the writer held, a mark pending and the queue
        // holding most of a 1 MiB case, the recovery is refused size-limit, and a recovery reported complete always has
        // its state in the artifact.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1bTerminal terminal;
        var workload = new ScriptedWorkload();
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = DiagnosticCaseRecorder.MinMaxBytes, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            var path = diagnostics.GetCaseStatus().Path!;
            var recorder = terminal.DiagnosticCase!;
            DiagnosticCaseRecoverResult result;
            using (new Running(terminal))
            {
                try
                {
                    await FloodAsync(terminal, workload, 5);
                    Mark(diagnostics, "pending");
                    var stateBytes = recorder.PendingStateBytesForTesting;
                    Assert.IsTrue(stateBytes > 0, "fixture: the mark holds no state");
                    // What the tier leaves now, less half a state: once the queue holds that much, a recovery cannot fit
                    // behind it.
                    var target = recorder.CheckpointRoom + recorder.QueuedBytesForTesting + recorder.PendingStateBytesForTesting - stateBytes / 2;
                    var chunk = new byte[2048];
                    Array.Fill(chunk, (byte)'q');
                    var total = terminal.OutputBytesRead;
                    while (recorder.QueuedBytesForTesting < target)
                    {
                        workload.Enqueue(chunk);
                        total += chunk.Length;
                        await WaitAsync(() => terminal.OutputBytesRead == total, TimeSpan.FromSeconds(60));
                    }
                    await Settle(terminal);
                    result = diagnostics.RecoverCase("behind-the-queue");
                    Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "size-limit"), (result.Outcome, result.Status, result.Problem?.Code), result.Problem?.Message);
                    gate.Set();
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                finally
                {
                    gate.Set();
                }
            }
            var line = Recoveries(Artifact.Read(path)).Single().GetProperty("checkpoint");
            var hasState = line.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object;
            Assert.AreEqual(result.Status == "complete", hasState, $"a recovery reported {result.Status} has state in the artifact: {hasState} ({line.GetProperty("status")}, {line.TryGetProperty("reason", out var why)})");
            Assert.AreEqual("unsupported", line.GetProperty("status").GetString());
            StringAssert.StartsWith(line.GetProperty("reason").GetString(), "size-limit");
        }
    }

    [TestMethod]
    public async Task Recover_KeepsItsStateWhenLaterEventsCrossTheBound()
    {
        // A complete recovery's line is reserved in the case's size bound as it is accepted: events offered after it,
        // which the writer drains before its line, stop at the reduced bound (declared size-limit, as at the bound
        // today) instead of displacing the state, so the line lands with its state at the close.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1bTerminal terminal;
        var workload = new ScriptedWorkload();
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = DiagnosticCaseRecorder.MinMaxBytes, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            var path = diagnostics.GetCaseStatus().Path!;
            var recorder = terminal.DiagnosticCase!;
            DiagnosticCaseRecoverResult result;
            using (new Running(terminal))
            {
                try
                {
                    await FloodAsync(terminal, workload, 5);
                    Mark(diagnostics, "pending");
                    var stateBytes = recorder.PendingStateBytesForTesting;
                    Assert.IsTrue(stateBytes > 0, "fixture: the mark holds no state");
                    var chunk = new byte[2048];
                    Array.Fill(chunk, (byte)'q');
                    var total = terminal.OutputBytesRead;
                    async Task EnqueueAsync()
                    {
                        workload.Enqueue(chunk);
                        total += chunk.Length;
                        await WaitAsync(() => terminal.OutputBytesRead == total, TimeSpan.FromSeconds(60));
                    }
                    // Near the bound, with room for the recovery: it is accepted.
                    while (recorder.CheckpointRoom >= 4 * stateBytes + 32 * 1024)
                        await EnqueueAsync();
                    await Settle(terminal);
                    result = diagnostics.RecoverCase("near-the-bound");
                    Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (result.Outcome, result.Status), result.Problem?.Message);
                    // More output than the room holds, offered after the recovery and written before its line.
                    for (var bytes = 0L; bytes < 4 * stateBytes + 128 * 1024; bytes += chunk.Length)
                        await EnqueueAsync();
                    await Settle(terminal);
                    gate.Set();
                    await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
                }
                finally
                {
                    gate.Set();
                }
            }
            var artifact = Artifact.Read(path);
            Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString(), "fixture: the later events did not cross the bound");
            var line = Recoveries(artifact).Single().GetProperty("checkpoint");
            var hasState = line.TryGetProperty("state", out var state) && state.ValueKind == JsonValueKind.Object;
            Assert.AreEqual(result.Status == "complete", hasState, $"a recovery reported {result.Status} has state in the artifact: {hasState} ({line.GetProperty("status")})");
            Assert.AreEqual("recorded", line.GetProperty("status").GetString(), "the recovery line was written without its state");
            Assert.IsTrue(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "missing" && e.GetProperty("record").GetProperty("reason").GetString() == "size-limit"),
                "the events that crossed the bound are not declared");
        }
    }

    [TestMethod]
    public async Task Recover_ReservesItsPendingState()
    {
        // The recovery's state counts against the pending-state budget together with the marks already awaiting the
        // writer, as a mark's does: with a budget of two and a half states and two marks pending, the recovery is
        // refused (pending-state budget), the pending bytes never pass the budget, and the refusal holds nothing.
        long stateBytes;
        using (var probe = new CaseRoot())
        {
            var (terminal, workload, _, gate) = HeldCase(probe);
            await using (terminal)
            using (gate)
            using (new Running(terminal))
            {
                await FloodAsync(terminal, workload, 5);
                Mark(new TerminalDiagnostics(terminal), "m");
                stateBytes = terminal.DiagnosticCase!.PendingStateBytesForTesting;
                gate.Set();
            }
        }
        Assert.IsTrue(stateBytes > 0, "fixture: a mark holds no state");
        var budget = stateBytes * 5 / 2;
        using var root = new CaseRoot();
        DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = budget;
        (Hex1bTerminal Terminal, ScriptedWorkload Workload, string Path, ManualResetEventSlim Gate) held;
        try
        {
            held = HeldCase(root);
        }
        finally
        {
            DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = null;
        }
        var (terminal2, workload2, _, gate2) = held;
        await using (terminal2)
        using (gate2)
        {
            var diagnostics = new TerminalDiagnostics(terminal2);
            var recorder = terminal2.DiagnosticCase!;
            using (new Running(terminal2))
            {
                try
                {
                    await FloodAsync(terminal2, workload2, 5);
                    Mark(diagnostics, "one");
                    Mark(diagnostics, "two");
                    Assert.AreEqual(2 * stateBytes, recorder.PendingStateBytesForTesting, "fixture: two marks pending");
                    var result = diagnostics.RecoverCase("third");
                    Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", "pending-state-budget"), (result.Outcome, result.Status, result.Problem?.Code), result.Problem?.Message);
                    Assert.IsTrue(recorder.PendingStateBytesForTesting <= budget, $"the pending state {recorder.PendingStateBytesForTesting} passed the budget {budget}");
                    gate2.Set();
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                }
                finally
                {
                    gate2.Set();
                }
            }
            Assert.AreEqual(0L, recorder.PendingStateBytesForTesting, "the refused recovery kept a reservation");
        }
    }

    // Waits until the case has written at least `events` model events.
    private static Task WrittenAsync(TerminalDiagnostics diagnostics, long events) =>
        WaitAsync(() => diagnostics.GetCaseStatus().Streams.Single(s => s.Stream == "model").Written >= events, TimeSpan.FromSeconds(30));

    // A fresh-model case started at construction with reapplication-data and its writer held at a gate the caller
    // releases (and may hold again); the gate is read when the recorder is created.
    private static (Hex1bTerminal Terminal, ScriptedWorkload Workload, string Path, ManualResetEventSlim Gate) HeldCase(CaseRoot root)
    {
        var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var workload = new ScriptedWorkload();
            var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
            return (terminal, workload, new TerminalDiagnostics(terminal).GetCaseStatus().Path!, gate);
        }
        finally
        {
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    // Releases the writer and waits until the model queue is empty, so later output is not dropped behind it.
    private static async Task DrainAsync(ManualResetEventSlim gate, TerminalDiagnostics diagnostics)
    {
        gate.Set();
        await WaitAsync(() =>
        {
            var model = diagnostics.GetCaseStatus().Streams.Single(s => s.Stream == "model");
            return model.Offered == model.Written + model.Dropped;
        }, TimeSpan.FromSeconds(60));
    }

    // Holds the writer again: its pass after a drain ends within the delay, and it blocks at the gate on its next.
    private static async Task HoldAsync(ManualResetEventSlim gate)
    {
        gate.Reset();
        await Task.Delay(100, TestContext.Current.CancellationToken);
    }

    private static DiagnosticCaseRecoverResult Recover(TerminalDiagnostics diagnostics, string label)
    {
        var result = diagnostics.RecoverCase(label);
        Assert.AreEqual((DiagnosticOutcome.Captured, "complete"), (result.Outcome, result.Status), $"fixture: recovery {label}: {result.Problem?.Message} {result.Reason}");
        return result;
    }

    private static DiagnosticCaseInspection Inspect(string path) => DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path });

    private static string Describe(IReadOnlyList<DiagnosticCaseInterval> intervals) =>
        string.Join("; ", intervals.Select(i => $"{i.Origin?.Trigger}/{i.Origin?.Label} valid={i.Valid} {i.FromModelSequence}-{i.ToModelSequence} {i.EndReason}"));
}
