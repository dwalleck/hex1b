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
    public async Task Stop_ModelLockBusyInsideAnApplicationNamesTheLastRecordedEvent()
    {
        // F28: a wedge inside an application (a blocking title callback) leaves that application unrecorded; the
        // stop checkpoint names the last event the case holds, so it stays a boundary of the artifact.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        terminal.WindowTitleChanged += _ =>
        {
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before ");
            workload.Enqueue("\u001b]0;wedged\u0007after"u8.ToArray());
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the callback never ran");
            var stop = new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(8), TestContext.Current.CancellationToken)) == stop;
            release.Set();
            Assert.IsTrue(finished, "the stop waited on the wedged application");
        }

        var artifact = Artifact.Read(path);
        var lastEvent = artifact.ModelEvents().Max(e => e.GetProperty("modelSequence").GetInt64());
        var stopLine = StopCheckpoint(artifact);
        Assert.AreEqual(lastEvent, stopLine.GetProperty("modelSequence").GetInt64(), "the stop checkpoint names an event the case does not hold");
        StringAssert.StartsWith(stopLine.GetProperty("checkpoint").GetProperty("reason").GetString(), "model-lock-busy");
        var result = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Captured, "unavailable", lastEvent), (result.Outcome, result.Comparison, result.AppliedThrough),
            $"{result.Problem?.Code} {result.Problem?.Message}");
    }

    [TestMethod]
    public async Task Mark_ReenteredInsideAnApplicationRecordsTheBoundary()
    {
        // F28: a mark from a callback inside an application would see it half applied; it records the boundary
        // at the last completed model event instead.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseMarkResult? inside = null;
        terminal.WindowTitleChanged += _ => inside ??= diagnostics.MarkCase("inside");
        long before;
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before ");
            before = terminal.CurrentModelSequence;
            await workload.WriteAndWaitAsync(terminal, "\u001b]0;titled\u0007after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsNotNull(inside, "fixture: the callback never marked");
        Assert.AreEqual((false, before), (inside.StateRecorded, inside.ModelSequence));
        StringAssert.StartsWith(inside.StateReason, "mid-application");
        var result = Reapply(path, label: "inside");
        Assert.AreEqual((DiagnosticOutcome.Captured, "unavailable", before), (result.Outcome, result.Comparison, result.AppliedThrough));
    }

    [TestMethod]
    public async Task Budget_EstimateCountsHistoryAtItsOwnWidth()
    {
        // F29: rows retained at 250 columns stay 250 cells wide after the model narrows; the estimate must count them.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, _) = Checkpointed(root, workload, width: 250, height: 5, scrollback: 3_000);
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(0, 3_000).Select(i => new string('w', 250) + "\r\n")));
            terminal.Resize(20, 5);
            var state = terminal.CaptureModelState();
            var cells = state.Screen.Sum(r => (long)r.Cells.Count) + state.History!.Rows.Sum(r => (long)r.Cells.Count);
            Assert.IsGreaterThan(500_000L, cells, "fixture: history did not keep its width");
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
            long estimate;
            lock (modelLock)
                estimate = terminal.EstimateModelStateBytesUnsafe();
            Assert.IsGreaterThanOrEqualTo(cells * 40, estimate, $"the estimate {estimate} undercounts {cells} cells");
        }

        // F32: a main screen saved at 300 × 100 when the alternate screen was entered keeps that size after a resize.
        workload = new ScriptedWorkload();
        var (alternate, _) = Checkpointed(root, workload, width: 300, height: 100, scrollback: null);
        await using (alternate)
        {
            await workload.WriteAndWaitAsync(alternate, "main\u001b[?1049halternate");
            alternate.Resize(20, 5);
            var state = alternate.CaptureModelState();
            var cells = state.Screen.Sum(r => (long)r.Cells.Count) + state.SavedMainScreen!.Sum(r => (long)r.Cells.Count);
            Assert.IsGreaterThan(29_000L, cells, "fixture: the saved screen was resized");
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(alternate)!;
            long estimate;
            lock (modelLock)
                estimate = alternate.EstimateModelStateBytesUnsafe();
            Assert.IsGreaterThanOrEqualTo(cells * 40, estimate, $"the estimate {estimate} undercounts the saved screen's {cells} cells");
        }
    }

    [TestMethod]
    public Task Stop_ModelLockBusyWaitsForAnEventTheHolderIsRecording() =>
        // F43: a slow (not wedged) lock holder records a model event while the busy stop runs; the stop checkpoint
        // must not name an event before one the case records. The holder pauses inside the offer, past its
        // recording check, until the stop has begun: the stop must wait for it.
        BusyStopAroundAHolderOffer(afterOffer: false);

    [TestMethod]
    public Task Stop_ModelLockBusyNamesAnEventWhoseOfferHasReturned() =>
        // F46: the holder pauses after its offer returned (no longer in flight) until the stop has finished; the
        // offer's model sequence must already be published, so the stop checkpoint names it.
        BusyStopAroundAHolderOffer(afterOffer: true);

    private async Task BusyStopAroundAHolderOffer(bool afterOffer)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        using var paused = new ManualResetEventSlim();
        var pauseArmed = 0;
        var resumedOnCondition = false;
        Func<bool>? resumeWhen = null;
        Action pause = () =>
        {
            if (Interlocked.Exchange(ref pauseArmed, 0) == 1)
            {
                paused.Set();
                resumedOnCondition = SpinWait.SpinUntil(() => Volatile.Read(ref resumeWhen) is { } resume && resume(), TimeSpan.FromSeconds(10));
            }
        };
        var hook = afterOffer ? DiagnosticCaseRecorder.AfterModelOfferForTesting : DiagnosticCaseRecorder.BeforeEnqueueForTesting;
        hook.Value = pause;
        Hex1bTerminal terminal;
        string path;
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            hook.Value = null;
        }

        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before");
            var diagnostics = new TerminalDiagnostics(terminal);
            var modelLock = typeof(Hex1bTerminal).GetField("_bufferLock", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;
            using var release = new ManualResetEventSlim();
            var holder = new Thread(() =>
            {
                lock (modelLock)
                {
                    // The holder's resize is a model event, offered under the lock and paused at the hook.
                    Volatile.Write(ref pauseArmed, 1);
                    terminal.Resize(30, 8);
                    release.Wait(TimeSpan.FromSeconds(30));
                }
            });
            holder.Start();
            Assert.IsTrue(paused.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the resize was never offered");
            var stop = Task.Run(() => diagnostics.StopCaseAsync(TestContext.Current.CancellationToken));
            // Resumed from the holder's own thread, so no test-thread scheduling delay counts against the stop's
            // bounded wait for offers in flight.
            Volatile.Write(ref resumeWhen, afterOffer
                ? () => stop.IsCompleted
                : () => diagnostics.GetCaseStatus().State == DiagnosticCaseState.Stopping);
            var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken)) == stop;
            release.Set();
            holder.Join();
            Assert.IsTrue(finished, "the stop did not finish");
            Assert.IsTrue(resumedOnCondition, "fixture: the holder resumed on its time bound, not on the stop");
        }

        var artifact = Artifact.Read(path);
        var lastEvent = artifact.ModelEvents().Max(e => e.GetProperty("modelSequence").GetInt64());
        Assert.AreEqual(2L, lastEvent, "fixture: the holder's resize was not recorded");
        Assert.AreEqual(lastEvent, StopCheckpoint(artifact).GetProperty("modelSequence").GetInt64(),
            "the stop checkpoint names an event before one the case recorded");
        var counts = artifact.Completion!.Value.GetProperty("checkpoints");
        Assert.AreEqual((1L, 1L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64()));
    }

    [TestMethod]
    public async Task Stop_CheckpointClaimIsFirstWinsWhileTheFirstIsBeingKept()
    {
        // F45: a busy stop records its checkpoint without the model lock, so two stops can race. The second must
        // lose while the first is still being kept: one checkpoint taken, and the loser's state bytes returned.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        using var claimed = new ManualResetEventSlim();
        using var secondReturned = new ManualResetEventSlim();
        var first = 1;
        DiagnosticCaseRecorder.AfterStopCheckpointClaimForTesting.Value = () =>
        {
            if (Interlocked.Exchange(ref first, 0) == 1)
            {
                claimed.Set();
                secondReturned.Wait(TimeSpan.FromSeconds(10));
            }
        };
        Hex1bTerminal terminal;
        string path;
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.AfterStopCheckpointClaimForTesting.Value = null;
        }

        var pendingField = typeof(DiagnosticCaseRecorder).GetField("_pendingStateBytes", BindingFlags.Instance | BindingFlags.NonPublic)!;
        DiagnosticCaseRecorder recorder;
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before");
            recorder = terminal.DiagnosticCase!;
            Assert.IsTrue(recorder.TryReserveStateBytes(10) && recorder.TryReserveStateBytes(20), "fixture: no budget");
            var keeping = Task.Run(() => recorder.RecordStopCheckpoint(5, new(null, null, "unavailable", "first", 10)));
            Assert.IsTrue(claimed.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the first claim never ran");
            var second = Task.Run(() => recorder.RecordStopCheckpoint(7, new(null, null, "unavailable", "second", 20)));
            await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            secondReturned.Set();
            await Task.WhenAll(keeping, second);
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var stop = StopCheckpoint(Artifact.Read(path));
        Assert.AreEqual((1L, 5L, "first"), (stop.GetProperty("checkpoint").GetProperty("ordinal").GetInt64(),
            stop.GetProperty("modelSequence").GetInt64(), stop.GetProperty("checkpoint").GetProperty("reason").GetString()),
            "the stop checkpoint is not the first claim's alone");
        Assert.AreEqual(0L, (long)pendingField.GetValue(recorder)!, "a losing stop checkpoint's state bytes were not returned");
    }

    [TestMethod]
    public async Task Stop_ALockedStopsClaimedCheckpointIsWrittenWhenABusyStopLoses()
    {
        // F47: a stop holding the model lock has claimed the stop checkpoint but not yet kept it when a second stop,
        // busy on that lock, stops the case and loses the claim. The writer must wait for the first to be kept.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        Hex1bTerminal? target = null;
        var closedEarly = true;
        var first = 1;
        DiagnosticCaseRecorder.AfterStopCheckpointClaimForTesting.Value = () =>
        {
            if (Interlocked.Exchange(ref first, 0) != 1)
                return;
            var recorder = target!.DiagnosticCase!;
            var busy = Task.Run(() => target.StopDiagnosticCaseWithCheckpoint(recorder, DiagnosticCaseStopReason.TimeLimit));
            // The busy stop waits out its lock timeout, stops the case and loses; the writer then drains.
            busy.Wait(TimeSpan.FromSeconds(10));
            closedEarly = !busy.IsCompleted || recorder.Completion.Wait(TimeSpan.FromMilliseconds(300));
        };
        Hex1bTerminal terminal;
        string path;
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.AfterStopCheckpointClaimForTesting.Value = null;
        }

        await using (terminal)
        {
            target = terminal;
            await workload.WriteAndWaitAsync(terminal, "before");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsFalse(closedEarly, "the writer closed while the claimed stop checkpoint was not yet kept (or the busy stop never finished)");
        var artifact = Artifact.Read(path);
        Assert.AreEqual("recorded", StopCheckpoint(artifact).GetProperty("checkpoint").GetProperty("status").GetString());
        var counts = artifact.Completion!.Value.GetProperty("checkpoints");
        Assert.AreEqual((1L, 1L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64()));
    }

    [TestMethod]
    public async Task Stop_BusyStopIsCountedBeforeItsRecordingCheck()
    {
        // F48: between a busy stop's recording check and its checkpoint, a stop that takes none (a collector
        // failure) stops the case. The busy stop, counted before its check, must hold the writer's close.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        DiagnosticCaseRecorder? recorder = null;
        var closedEarly = true;
        var first = 1;
        DiagnosticCaseRecorder.AfterBusyStopCheckForTesting.Value = () =>
        {
            if (Interlocked.Exchange(ref first, 0) != 1)
                return;
            recorder!.StopRecording(DiagnosticCaseStopReason.CollectorFailed);
            closedEarly = recorder.Completion.Wait(TimeSpan.FromMilliseconds(500));
        };
        Hex1bTerminal terminal;
        string path;
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.AfterBusyStopCheckForTesting.Value = null;
        }

        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before");
            recorder = terminal.DiagnosticCase!;
            await Task.Run(() => recorder.StopWithoutModelLock(DiagnosticCaseStopReason.TimeLimit, "model-lock-busy: fixture"));
            Assert.IsTrue(recorder.Completion.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "the case never completed");
        }

        Assert.IsFalse(closedEarly, "the writer closed before the busy stop's checkpoint");
        var artifact = Artifact.Read(path);
        Assert.AreEqual("model-lock-busy: fixture", StopCheckpoint(artifact).GetProperty("checkpoint").GetProperty("reason").GetString());
        var counts = artifact.Completion!.Value.GetProperty("checkpoints");
        Assert.AreEqual((1L, 1L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64()));
    }

    [TestMethod]
    public async Task Stop_LockedStopIsCountedBeforeItsRecordingCheck()
    {
        // F51: between the locked stop's recording check and its claim, a stop that takes no checkpoint stops the
        // case. The locked stop, counted before its check, must hold the writer's close and keep its checkpoint.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        DiagnosticCaseRecorder? recorder = null;
        var closedEarly = true;
        var first = 1;
        DiagnosticCaseRecorder.AfterLockedStopCheckForTesting.Value = () =>
        {
            if (Interlocked.Exchange(ref first, 0) != 1)
                return;
            recorder!.StopRecording(DiagnosticCaseStopReason.CollectorFailed);
            closedEarly = recorder.Completion.Wait(TimeSpan.FromMilliseconds(500));
        };
        Hex1bTerminal terminal;
        string path;
        try
        {
            (terminal, path) = Checkpointed(root, workload);
        }
        finally
        {
            DiagnosticCaseRecorder.AfterLockedStopCheckForTesting.Value = null;
        }

        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before");
            recorder = terminal.DiagnosticCase!;
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsFalse(closedEarly, "the writer closed before the locked stop's checkpoint");
        var artifact = Artifact.Read(path);
        Assert.AreEqual("recorded", StopCheckpoint(artifact).GetProperty("checkpoint").GetProperty("status").GetString());
        var counts = artifact.Completion!.Value.GetProperty("checkpoints");
        Assert.AreEqual((1L, 1L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64()));
    }

    [TestMethod]
    public async Task Stop_ModelLockBusyInALiveCaseNamesTheModelAtArming()
    {
        // F33: a case started mid-session whose first application wedges names the model's sequence at arming.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var wedge = false;
        terminal.WindowTitleChanged += _ =>
        {
            if (!wedge)
                return;
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(30));
        };
        string path;
        long armedAt;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "one ");
            await workload.WriteAndWaitAsync(terminal, "two ");
            var diagnostics = new TerminalDiagnostics(terminal);
            armedAt = terminal.CurrentModelSequence;
            path = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] }).Path!;
            wedge = true;
            workload.Enqueue("\u001b]0;wedged\u0007"u8.ToArray());
            Assert.IsTrue(entered.Wait(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "fixture: the callback never ran");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            release.Set();
        }

        Assert.AreEqual(armedAt, StopCheckpoint(Artifact.Read(path)).GetProperty("modelSequence").GetInt64());
    }

    [TestMethod]
    public async Task Mark_AfterANestedEventInsideAnApplicationRecordsTheBoundary()
    {
        // F34: after a callback raises a model event (a resize), the application is still unfinished; a mark there
        // must not record its half-applied state.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, _) = Checkpointed(root, workload);
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseMarkResult? inside = null;
        terminal.WindowTitleChanged += _ =>
        {
            if (inside is not null)
                return;
            terminal.Resize(30, 8);
            inside = diagnostics.MarkCase("after-nested");
        };
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "before ");
            await workload.WriteAndWaitAsync(terminal, "\u001b]0;titled\u0007after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        Assert.IsNotNull(inside, "fixture: the callback never marked");
        Assert.IsFalse(inside.StateRecorded, "a mark inside an unfinished application recorded its state");
        StringAssert.StartsWith(inside.StateReason, "mid-application");
    }

    [TestMethod]
    public async Task Reapply_ReplicaThatCannotBeAllocatedIsIncompatible()
    {
        // F35: an allocation failure building the detached model is a refusal of the configuration, with nothing written.
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("oom")], new HeadlessPresentationAdapter(20, 4));
        CaseReapplier.BuildReplicaForTesting.Value = () => throw new OutOfMemoryException("injected allocation failure");
        try
        {
            var result = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "a refused replica wrote a run");
        }
        finally
        {
            CaseReapplier.BuildReplicaForTesting.Value = null;
        }
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
            (c => c["scrollbackCapacity"] = int.MaxValue, "configuration.scrollbackCapacity"),
            (c => c["capabilities"]!["sixelCellMetrics"] = new JsonObject { ["height"] = 20, ["source"] = "Direct", ["reliability"] = "Authoritative" },
                "capabilities.sixelCellMetrics.width: missing"),
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
