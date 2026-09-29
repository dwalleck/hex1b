using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

// Fences for ticket 08's review repairs (R1; review-decisions.md F1–F27).
public partial class DiagnosticCaseTests
{
    [TestMethod]
    public async Task Checkpoint_ContinuationTokenizedButNotAppliedIsNotRecorded()
    {
        // F1: the pump tokenizes a chunk before it applies it; a mark between the two must record the model as of
        // its model sequence, not the held chunk's decoder state.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var hold = new HoldingFilter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .AddWorkloadFilter(hold)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        string path;
        using (new Running(terminal))
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            path = diagnostics.GetCaseStatus().Path!;
            await workload.WriteAndWaitAsync(terminal, "first ");
            hold.Arm();
            workload.Enqueue([(byte)'X', 0xe6, 0xbc]);
            Assert.IsTrue(hold.Held.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the chunk was never held");
            var mark = diagnostics.MarkCase("held");
            hold.Release.Set();
            await WaitAsync(() => terminal.CurrentModelSequence > mark.ModelSequence);
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var held = Checkpoints(Artifact.Read(path)).Single(c => c.GetProperty("checkpoint").GetProperty("label").GetString() == "held");
        Assert.AreEqual("", held.GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("utf8").GetString(),
            "the mark recorded the held chunk's pending bytes");
        AssertMatched(Reapply(path, label: "held"), "held");
        AssertMatched(Reapply(path, label: "stop"), "stop");
    }

    [TestMethod]
    public async Task Stop_ModelLockBusyDoesNotHoldTheStop()
    {
        // F2: a wedged model lock bounds the stop's wait; the case stops without the stop checkpoint's state.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "wedged");
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
            using var held = new ManualResetEventSlim();
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (modelLock)
                {
                    held.Set();
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            });
            holder.Start();
            held.Wait(TestContext.Current.CancellationToken);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var stop = new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken)) == stop;
            release.Set();
            holder.Join();
            Assert.IsTrue(finished, "the stop waited on the wedged model lock");
            Assert.IsLessThan(8.0, watch.Elapsed.TotalSeconds);
        }

        var checkpoint = StopCheckpoint(Artifact.Read(path)).GetProperty("checkpoint");
        Assert.AreEqual("unavailable", checkpoint.GetProperty("status").GetString());
        StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "model-lock-busy");
    }

    [TestMethod]
    public async Task Reapply_ConfigurationProblemsAreIncompatible()
    {
        // F3, F4: missing, unknown and impossible configuration values are refused naming the field, before
        // anything is written; nothing escapes as an exception.
        using var root = new CaseRoot();
        async Task<DiagnosticCaseReapplyResult> Edited(Action<JsonObject> edit)
        {
            var path = await RecordCaseAsync(root, [new("configured")], new HeadlessPresentationAdapter(20, 4));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")))!.AsObject();
            edit(manifest["checkpoint"]!["configuration"]!.AsObject());
            RewriteOwnerOnly(Path.Combine(path, "manifest.json"), manifest.ToJsonString());
            var result = Reapply(path, label: "stop");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "a refused configuration wrote a run");
            return result;
        }

        foreach (var (edit, expected) in new (Action<JsonObject>, string)[]
        {
            (c => c.Remove("commandMarkHistoryCapacity"), "configuration.commandMarkHistoryCapacity: missing"),
            (c => c.Remove("reflowStrategy"), "configuration.reflowStrategy: missing"),
            (c => c["capabilities"]!.AsObject().Remove("handlesAlternateScreenNatively"), "capabilities.handlesAlternateScreenNatively: missing"),
            (c => c["graphics"]!.AsObject().Remove("maximumImagesPerScreen"), "graphics.maximumImagesPerScreen: missing"),
            (c => c["someFutureSetting"] = 1, "configuration.someFutureSetting: unknown field"),
            (c => c["width"] = 0, "configuration.width"),
            (c => c["escapeSequenceTimeoutMs"] = 1e308, "configuration.escapeSequenceTimeoutMs"),
        })
        {
            var result = await Edited(edit);
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
            StringAssert.StartsWith(result.Problem!.Message, expected);
        }

        // A run whose files cannot be written is a failed result, never an exception.
        var storage = await RecordCaseAsync(root, [new("storage")], new HeadlessPresentationAdapter(20, 4));
        CaseReapplier.FinishWritingForTesting.Value = () => throw new IOException("injected storage failure");
        try
        {
            var failed = Reapply(storage, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Failed, "storage-failed"), (failed.Outcome, failed.Problem?.Code));
        }
        finally
        {
            CaseReapplier.FinishWritingForTesting.Value = null;
        }

        // A format 1 case is inspected without inventing a configuration it never recorded.
        var legacy = await RecordCaseAsync(root, [new("legacy")], new HeadlessPresentationAdapter(20, 4));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(legacy, "manifest.json")))!;
        manifest["formatVersion"] = 1;
        manifest["checkpoint"]!["configuration"]!["capabilities"] = "TerminalCapabilities { }";
        manifest["checkpoint"]!["configuration"]!["graphics"] = "SixelCompatibilityPolicy { }";
        RewriteOwnerOnly(Path.Combine(legacy, "manifest.json"), manifest.ToJsonString());
        var configuration = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = legacy }).Manifest!.Checkpoint.Configuration!;
        Assert.IsNull(configuration.Capabilities);
        Assert.IsNull(configuration.Graphics);
    }

    [TestMethod]
    public async Task Reapply_ResultNamesWhatWasCompared()
    {
        // F5, F6: the result carries the checkpoint (with its configuration), the coverage and both builds; write
        // sequences are not state.
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("covered")], new HeadlessPresentationAdapter(20, 4));
        var result = Reapply(path, label: "stop");
        AssertMatched(result, "stop");
        Assert.AreEqual(DiagnosticCaseCheckpointProfiles.FreshModel, result.Checkpoint?.Profile);
        Assert.AreEqual(20, result.Checkpoint!.Configuration!.Width);
        Assert.AreEqual(DiagnosticCaseCheckpointProfiles.TextState, result.Coverage?.Profile);
        CollectionAssert.Contains(result.Coverage!.Compared.ToArray(), "screen");
        Assert.IsTrue(result.Coverage.Excluded.Any(e => e.StartsWith("write sequences", StringComparison.Ordinal)));
        Assert.IsFalse(string.IsNullOrEmpty(result.Producer?.Hex1bVersion));
        Assert.AreEqual(result.Producer!.Hex1bVersion, result.ConsumerHex1bVersion);

        var state = File.ReadAllText(Path.Combine(result.RunPath!, "reapplied.json"));
        Assert.IsFalse(state.Contains("\"q\":", StringComparison.Ordinal) || state.Contains("nextCellSequence", StringComparison.Ordinal),
            "the projection carries write sequences");

        // Refusals after the manifest is read carry the same context.
        var refused = Reapply(path, label: "nope");
        Assert.AreEqual(("unknown-label", DiagnosticCaseCheckpointProfiles.FreshModel), (refused.Problem?.Code, refused.Checkpoint?.Profile));
    }

    [TestMethod]
    public async Task Reapply_ModelSequencePastACompleteCaseIsUnknown()
    {
        // F7: a complete case knows every model sequence it recorded.
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("one"), new("two")], new HeadlessPresentationAdapter(20, 4));
        var unknown = Reapply(path, modelSequence: 3);
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "unknown-model-sequence"), (unknown.Outcome, unknown.Problem?.Code));
        File.Delete(Path.Combine(path, "completion.json"));
        Assert.AreEqual("beyond-interval", Reapply(path, modelSequence: 3).Problem?.Code, "an interrupted case's tail is unknown, not absent");
    }

    [TestMethod]
    public async Task Mark_PendingStateBudgetRecordsTheBoundaryOnly()
    {
        // F8: pending checkpoint state is bounded in bytes; past the budget a mark records the boundary only,
        // without taking any state.
        using var root = new CaseRoot();
        DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = 1;
        Hex1bTerminal terminal;
        string path;
        var workload = new ScriptedWorkload();
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.PendingStateBudgetForTesting.Value = null;
        }

        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "budget");
            var mark = new TerminalDiagnostics(terminal).MarkCase("over");
            Assert.AreEqual((DiagnosticOutcome.Captured, false), (mark.Outcome, mark.StateRecorded));
            StringAssert.StartsWith(mark.StateReason, "pending-state budget");
            Assert.AreEqual(0, terminal.ModelStateCapturesForTesting, "a mark over the budget took the model's state");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var over = Checkpoints(Artifact.Read(path)).Single(c => c.GetProperty("checkpoint").GetProperty("label").GetString() == "over");
        StringAssert.StartsWith(over.GetProperty("checkpoint").GetProperty("reason").GetString(), "pending-state budget");
    }

    [TestMethod]
    public async Task Mark_InProgressWhenTheWriterFailsIsAccounted()
    {
        // F9: a mark past its in-lock check when the writer fails is written or declared, never lost.
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        using var marking = new ManualResetEventSlim(false);
        using var proceed = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        DiagnosticCaseRecorder.WriterFaultForTesting.Value = new IOException("injected storage failure");
        Hex1bTerminal terminal;
        string path;
        var workload = new ScriptedWorkload();
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
        }

        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "queued");
            var mark = Task.Run(() =>
            {
                DiagnosticCaseRecorder.AfterMarkCaptureForTesting.Value = () =>
                {
                    marking.Set();
                    proceed.Wait(TimeSpan.FromSeconds(10));
                };
                return new TerminalDiagnostics(terminal).MarkCase("in-progress");
            });
            Assert.IsTrue(marking.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the mark never took its state");
            gate.Set();
            // The writer faults on the queued event and closes while the mark is still in progress.
            await Task.Delay(300, TestContext.Current.CancellationToken);
            proceed.Set();
            Assert.AreEqual(DiagnosticOutcome.Captured, (await mark).Outcome);
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
        }

        var artifact = Artifact.Read(path);
        Assert.AreEqual("collector-failed", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var counts = artifact.Completion.Value.GetProperty("checkpoints");
        Assert.AreEqual(counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64() + counts.GetProperty("dropped").GetInt64(),
            $"a checkpoint was neither written nor declared: {counts}");
        Assert.IsTrue(Checkpoints(artifact).Any(c => c.GetProperty("checkpoint").GetProperty("label").GetString() == "in-progress"),
            "the mark in progress was not written");
    }

    [TestMethod]
    public async Task Reapply_UnappliedOutputEndsTheInterval()
    {
        // F12: a gated batch refused after tokenizing moved the decoder without a model event, so coverage ends.
        using var root = new CaseRoot();
        var adapter = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(adapter).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        string path;
        using (new Running(terminal))
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            path = diagnostics.GetCaseStatus().Path!;
            Assert.AreEqual(NativeDeliveryOutcome.Applied, await adapter.WriteRequiredIfGeometry("before ", 40, 10));
            var refused = await adapter.WriteRequiredIfGeometry("\u001b[1", 41, 10);
            Assert.AreEqual(NativeDeliveryOutcome.GeometryChanged, refused, "fixture: the batch was not refused");
            Assert.AreEqual(NativeDeliveryOutcome.Applied, await adapter.WriteRequiredIfGeometry("m after", 40, 10));
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Intervals.Single();
        Assert.AreEqual((1L, "unapplied-output"), (interval.ToModelSequence, interval.EndReason));
        Assert.AreEqual("beyond-interval", Reapply(path, label: "stop").Problem?.Code);
    }

    [TestMethod]
    public async Task Reapply_RefusesAtEveryIntervalEnd()
    {
        // F27: application-without-ingress, re-entrant and stream-failed ends each refuse a target past them,
        // applying nothing.
        using var root = new CaseRoot();
        var paths = new List<string>();

        var workload = new ScriptedWorkload();
        await using (var foreign = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build())
        {
            using (new Running(foreign))
            {
                await workload.WriteAndWaitAsync(foreign, "before ");
                foreign.EnterAlternateScreen();
                await workload.WriteAndWaitAsync(foreign, "after");
                paths.Add((await new TerminalDiagnostics(foreign).StopCaseAsync(TestContext.Current.CancellationToken)).Path!);
            }
        }

        workload = new ScriptedWorkload();
        await using (var nested = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build())
        {
            nested.WindowTitleChanged += _ => nested.ApplyTokens(Hex1b.Tokens.AnsiTokenizer.Tokenize("nested"));
            using (new Running(nested))
            {
                await workload.WriteAndWaitAsync(nested, "before ");
                await workload.WriteAndWaitAsync(nested, "\u001b]0;renamed\u0007after");
                paths.Add((await new TerminalDiagnostics(nested).StopCaseAsync(TestContext.Current.CancellationToken)).Path!);
            }
        }

        DiagnosticCaseRecorder.StreamFaultForTesting.Value = "model";
        Hex1bTerminal failing;
        workload = new ScriptedWorkload();
        try
        {
            failing = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            DiagnosticCaseRecorder.StreamFaultForTesting.Value = null;
        }
        await using (failing)
        {
            using (new Running(failing))
            {
                await workload.WriteAndWaitAsync(failing, "fails");
                await workload.WriteAndWaitAsync(failing, "later");
                paths.Add((await new TerminalDiagnostics(failing).StopCaseAsync(TestContext.Current.CancellationToken)).Path!);
            }
        }

        var counter = new System.Runtime.CompilerServices.StrongBox<int>();
        CaseReapplier.AppliedEventsForTesting.Value = counter;
        try
        {
            foreach (var (path, reason) in paths.Zip(new[] { "application-without-ingress", "reentrant-application", "stream-failed" }))
            {
                var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Intervals.Single();
                Assert.AreEqual(reason, interval.EndReason, "fixture: another end");
                counter.Value = 0;
                var result = Reapply(path, modelSequence: interval.ToModelSequence!.Value + 1);
                Assert.AreEqual(("beyond-interval", reason), (result.Problem?.Code, result.IntervalEndReason), result.Problem?.Message);
                Assert.AreEqual(0, counter.Value, $"{reason}: events were applied before the target was refused");
            }
        }
        finally
        {
            CaseReapplier.AppliedEventsForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Reader_SkipsCheckpointStateUnlessAsked()
    {
        // F20, F22: inspection and target resolution never deserialize checkpoint state; re-application reads only
        // the chosen line's. Inspection reports checkpoint coverage.
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("one"), CaseStep.Mark("first"), new("two")], new HeadlessPresentationAdapter(20, 4));
        var events = Path.Combine(path, "events.jsonl");
        var lines = CaseArtifactReader.ReadEvents(events).Where(e => e.Checkpoint is not null).ToList();
        Assert.HasCount(2, lines);
        Assert.IsTrue(lines.All(e => e.Checkpoint!.State is null), "state was deserialized without being asked for");
        var chosen = lines[0].CaseSequence;
        var withState = CaseArtifactReader.ReadEvents(events, chosen).Where(e => e.Checkpoint is not null).ToList();
        Assert.IsNotNull(withState.Single(e => e.CaseSequence == chosen).Checkpoint!.State);
        Assert.IsNull(withState.Single(e => e.CaseSequence != chosen).Checkpoint!.State);

        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 100 });
        Assert.IsTrue(inspection.Events.Where(e => e.Checkpoint is not null).All(e => e.Checkpoint!.StateOmitted == true && e.Checkpoint.State is null));
        Assert.AreEqual((2L, "complete"), (inspection.Checkpoints!.Events, inspection.Checkpoints.State));

        // Completion counts beyond the lines are an unaccounted tail.
        var completion = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "completion.json")))!;
        completion["checkpoints"]!["offered"] = 3;
        RewriteOwnerOnly(Path.Combine(path, "completion.json"), completion.ToJsonString());
        var unaccounted = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Checkpoints!;
        Assert.AreEqual(("incomplete", "unaccounted", 3L), (unaccounted.State, unaccounted.Missing.Single().Reason, unaccounted.Missing.Single().FromOrdinal));
    }

    [TestMethod]
    public async Task Stop_SizeLimitSkipsAStateThatCannotFit()
    {
        // F21: a size-limit stop does not take a state it cannot write.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload, width: 20, height: 5, scrollback: 50_000, maxBytes: 1024 * 1024);
        await using (terminal)
        {
            for (var chunk = 0; chunk < 11; chunk++)
                await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(0, 5_000).Select(i => $"{chunk}:{i}\r\n")));
            var big = Enumerable.Repeat((byte)'z', 16 * 1024).ToArray();
            for (var i = 0; i < 128 && new TerminalDiagnostics(terminal).GetCaseStatus().Outcome == DiagnosticOutcome.Captured; i++)
                await workload.WriteAndWaitAsync(terminal, big);
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
            Assert.AreEqual(0, terminal.ModelStateCapturesForTesting, "the size-limit stop took a state it could not write");
        }
        var artifact = Artifact.Read(path);
        Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var checkpoint = StopCheckpoint(artifact).GetProperty("checkpoint");
        Assert.AreEqual(("missing", "size-limit"), (checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("reason").GetString()));
    }
}
