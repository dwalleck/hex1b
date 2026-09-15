using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Hex1b;
using Hex1b.Automation;
using Hex1b.Flow;
using Hex1b.Reflow;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Retention fence for persistent-step continuous-history commitment
/// (<see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>).
/// </summary>
/// <remarks>
/// <para>
/// The fence drives the real flow runner over an in-process terminal configured
/// with <see cref="GhosttyReflowStrategy"/> — the same re-wrapping model the
/// retained native runs exercised — and counts committed payload keys in the
/// full buffer (scrollback + screen) afterwards. It exists because the retained
/// native failures could not distinguish "never delivered" from "delivered, then
/// overwritten": this test makes the interleaving deterministic.
/// </para>
/// <para>
/// The commit source mirrors the Janet driver's row grammar: a BEGIN unit, one
/// unit per payload row carrying the stable key <c>{commit}:r{index}</c>, and an
/// END unit. Rows are padded to the full terminal width so a width change
/// genuinely re-wraps already-emitted content. Emission is gated at chosen unit
/// indices so a resize provably lands mid-commit; complete payload keys and
/// right-hand sentinels must survive the resulting reflow.
/// </para>
/// </remarks>
[DoNotParallelize]
[TestClass]
public class ContinuousHistoryRetentionFenceTests
{
    private const int PayloadRows = 160;
    private const string CommitId = "fence-k0001";
    private const string LiveMarker = "FENCE-LIVE-PROMPT";

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnqualifiedNativeHost_RejectsCommitWithoutHistoryAndLeavesPromptVisible(bool identifiedGhostty)
    {
        var profile = identifiedGhostty
            ? FlowTerminalHostProfile.Ghostty_Unqualified
            : FlowTerminalHostProfile.Unknown;
        var source = new FenceCommitSource(CommitId, PayloadRows);
        string? bufferAfterRefusal = null;
        string? screenAfterRefusal = null;
        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            var step = flow.Step(BuildLive);
            await step.WaitForReadyAsync();
            try
            {
                await Assert.ThrowsAsync<NotSupportedException>(
                    async () => await step.CommitAsync(source, BuildLive));
                Assert.IsTrue(
                    await WaitForScreenTextAsync(terminal, LiveMarker, TimeSpan.FromSeconds(2)),
                    "the live prompt must be visible before the refusal is observed");
                bufferAfterRefusal = ReadFullBuffer(terminal);
                screenAfterRefusal = terminal.GetScreenText();
            }
            finally
            {
                step.Complete();
            }
        }, width: 100, height: 30, hostProfile: profile);

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsFalse(bufferAfterRefusal!.Contains(CommitId, StringComparison.Ordinal),
            "An unsupported native host must be refused before any committed row is emitted.");
        Assert.IsTrue(screenAfterRefusal!.Contains(LiveMarker, StringComparison.Ordinal),
            "Refusing history commitment must leave the live prompt visible.");
    }

    [TestMethod]
    public async Task TallCommit_WithMidCommitShrinkAndGrow_RetainsEveryPayloadKeyExactlyOnce()
    {
        var tracePath = Path.Combine(Path.GetTempPath(), $"hex1b-fence-{Guid.NewGuid():N}.jsonl");
        Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", tracePath);
        try
        {
            var scenario = Environment.GetEnvironmentVariable("FENCE_SCENARIO") ?? "shrink-grow-shrink";
            var source = new FenceCommitSource(CommitId, PayloadRows);
            FlowCommitResult? result = null;
            Exception? failure = null;
            var steps = new List<string>();

            using var terminal = CreateTerminal(async flow =>
            {
                var step = flow.Step(BuildLive, options =>
                {
                    options.MinHeight = 24;
                    options.MaxHeight = 24;
                });
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(source, BuildLive);
                }
                catch (Exception ex)
                {
                    failure = ex;
                }
                finally
                {
                    step.Complete();
                }
            }, width: scenario == "grow" ? 74 : 138, height: scenario == "grow" ? 19 : 37);

            // Resize while a unit is being materialized, before the committer
            // selects the geometry used to emit that unit.
            // One-shot per index: the coordinator legitimately re-materializes a
            // unit at the settled width, so the gate must not resize twice.
            var gated = new HashSet<int>();
            source.Gate = async index =>
            {
                if (!gated.Add(index))
                {
                    return;
                }

                switch (index)
                {
                    case 15 when scenario is "shrink-grow-shrink" or "shrink":
                        _ = terminal.ResizeWithWorkloadAsync(74, 19);
                        steps.Add("shrink@15");
                        break;
                    case 15 when scenario is "grow":
                        _ = terminal.ResizeWithWorkloadAsync(138, 37);
                        steps.Add("grow@15");
                        break;
                    case 71 when scenario is "shrink-grow-shrink":
                        _ = terminal.ResizeWithWorkloadAsync(138, 37);
                        steps.Add("grow@71");
                        break;
                    case 119 when scenario is "shrink-grow-shrink":
                        _ = terminal.ResizeWithWorkloadAsync(74, 19);
                        steps.Add("shrink@119");
                        break;
                    default:
                        return;
                }

                // Let the workload resize event reach the runner before the
                // pending unit is emitted.
                await Task.Delay(150);
            };

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
            Console.WriteLine($"terminal model: {terminal.Width}x{terminal.Height}");

            Assert.IsNull(failure, $"commit failed: {failure}");
            Assert.IsNotNull(result);
            Assert.AreEqual(PayloadRows + 2, result!.CompletedUnits);
            if (scenario == "shrink-grow-shrink")
            {
                Assert.IsTrue(
                    steps.Contains("shrink@15") && steps.Contains("grow@71") && steps.Contains("shrink@119"),
                    $"expected all three resizes to run mid-commit; observed: {string.Join(",", steps)}");
            }
            else
            {
                Assert.IsTrue(steps.Count >= 1, $"expected the scenario's resize to run mid-commit; observed: {string.Join(",", steps)}");
            }

            var trace = File.Exists(tracePath) ? File.ReadAllText(tracePath) : "";

            var buffer = ReadFullBuffer(terminal);
            AssertKeysExactlyOnce(buffer, CommitId, PayloadRows, trace);
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", null);
            if (File.Exists(tracePath))
            {
                File.Delete(tracePath);
            }
        }
    }

    /// <summary>
    /// The real impact-aware handle must never publish a presentable frame that
    /// loses the live prompt while the coordinator completes normally or recovers
    /// from an emission failure.
    /// </summary>
    [TestMethod]
    [DataRow(false, true, null)]
    [DataRow(true, true, 3)]
    [DataRow(true, false, 16)]
    public async Task FlowCompletionOrRecovery_PresentsPromptOnEveryImpactFrame(
        bool injectFailure,
        bool useCursorProvider,
        int? failureAfterRows)
    {
        const int presentationPayloadRows = 16;
        var source = new FenceCommitSource(CommitId, presentationPayloadRows);
        var postCommitMarker = injectFailure
            ? $"{LiveMarker}-RECOVERED"
            : $"{LiveMarker}-COMPLETED";
        var initialPromptFrame = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var postCommitFrame = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var captures = new List<string>();
        var captureSync = new object();
        FlowCommitResult? result = null;
        Exception? failure = null;
        var promptReady = false;
        var postCommitFrameReady = false;
        var readyCaptureCount = -1;
        var captureWindow = true;
        var liveText = LiveMarker;
        Hex1bTerminal terminal = null!;
        TerminalWidgetHandle handle = null!;

        Task<Hex1bWidget> BuildTrackedLive(FlowStepContext ctx)
            => Task.FromResult<Hex1bWidget>(ctx.Text(liveText));

        Task<Hex1bWidget> BuildPostCommitLive(FlowStepContext ctx)
            => Task.FromResult<Hex1bWidget>(ctx.Text(postCommitMarker));

        void CapturePresentation()
        {
            if (!handle.TryCaptureRenderFrame(0, out var frame) || frame is null)
            {
                return;
            }

            var text = FrameText(frame);
            lock (captureSync)
            {
                if (!captureWindow)
                {
                    return;
                }

                captures.Add(text);
                if (text.Contains(LiveMarker, StringComparison.Ordinal))
                {
                    initialPromptFrame.TrySetResult(true);
                }

                if (text.Contains(postCommitMarker, StringComparison.Ordinal))
                {
                    postCommitFrame.TrySetResult(true);
                    // The marker is the final observed commit/recovery frame;
                    // exclude step teardown from this presentation window.
                    captureWindow = false;
                }
            }
        }

        Environment.SetEnvironmentVariable(
            "HEX1B_PROTOTYPE_FAIL_AFTER_ROWS",
            failureAfterRows?.ToString());
        try
        {
            using var terminalLifetime = terminal = CreateTerminalWithWidget(
                async flow =>
                {
                    var step = flow.Step(BuildTrackedLive, options =>
                    {
                        options.MinHeight = 14;
                        options.MaxHeight = 14;
                    });
                    await step.WaitForReadyAsync();
                    try
                    {
                        promptReady = await WaitForCapturedFrameAsync(
                            initialPromptFrame, TimeSpan.FromSeconds(2));
                        lock (captureSync)
                        {
                            readyCaptureCount = captures.Count;
                        }

                        try
                        {
                            result = await step.CommitAsync(source, BuildPostCommitLive);
                        }
                        catch (Exception ex)
                        {
                            failure = ex;
                        }

                        // Ensure the post-commit layout is rendered through the
                        // real handle before the flow callback ends. On recovery,
                        // this is a fresh live render after the coordinator's
                        // retained-layout re-anchor.
                        liveText = postCommitMarker;
                        step.Invalidate();
                        postCommitFrameReady = await WaitForCapturedFrameAsync(
                            postCommitFrame, TimeSpan.FromSeconds(2));
                    }
                    finally
                    {
                        step.Complete();
                    }
                },
                width: 121,
                height: 30,
                useCursorProvider,
                out handle);

            handle.OutputReceived += CapturePresentation;
            try
            {
                await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
            }
            finally
            {
                handle.OutputReceived -= CapturePresentation;
            }
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX1B_PROTOTYPE_FAIL_AFTER_ROWS", null);
        }

        Assert.IsTrue(promptReady, "the initial prompt must be ready before commitment");
        Assert.IsTrue(
            postCommitFrameReady,
            "a unique completion or recovery frame must reach the real handle");
        Assert.IsGreaterThanOrEqualTo(0, readyCaptureCount);

        string[] postReadyFrames;
        lock (captureSync)
        {
            postReadyFrames = captures.Skip(readyCaptureCount).ToArray();
        }

        Assert.IsTrue(
            postReadyFrames.Length > 0,
            "the real impact-aware handle must publish a frame after prompt readiness");
        Assert.IsTrue(
            postReadyFrames.Any(frame =>
                frame.Contains(postCommitMarker, StringComparison.Ordinal)),
            $"the unique post-commit marker was never captured:\n{string.Join("\n---\n", postReadyFrames)}");
        for (var index = 0; index < postReadyFrames.Length; index++)
        {
            Assert.AreEqual(
                1,
                postReadyFrames[index].Split(LiveMarker, StringSplitOptions.None).Length - 1,
                $"presentable frame {index} must retain exactly one live prompt:\n" +
                postReadyFrames[index]);
        }

        if (injectFailure)
        {
            Assert.IsInstanceOfType<FlowCommitException>(failure);
            Assert.IsNull(result);
        }
        else
        {
            Assert.IsNull(failure, $"normal completion failed: {failure}");
            Assert.IsNotNull(result);
            Assert.AreEqual(FlowCommitStatus.Emitted, result!.Status);
        }
    }


    /// <summary>
    /// The live region must stay on screen for every committed unit, and an edit
    /// typed while the batch is committing must be rendered, without waiting for
    /// a turn boundary.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the fence for the defect the native typing leg measured: with the
    /// region repainted only per bounded turn, the commit's own rows overwrite
    /// the region's top rows for the first units of every turn, and a frame the
    /// live app renders while the output pump is muted stays invisible until the
    /// next boundary. A screenshot taken mid-commit showed history only.
    /// </para>
    /// <para>
    /// The assertions are taken at unit indices inside a turn (2 and 11, with
    /// turns ending at 8 and 16), so a per-turn repaint cannot satisfy them, and
    /// the oracle is the terminal model's screen — what a host would have
    /// displayed — not the application's draft ledger, which records edits the
    /// screen never showed.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task LiveRegion_StaysOnScreenAndRendersMidCommitEditInsideATurn()
    {
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitResult? result = null;
        FlowStep? step = null;
        var liveText = LiveMarker;
        string? screenEarlyInFirstTurn = null;
        string? screenAfterMidTurnEdit = null;

        using var terminal = CreateTerminal(async flow =>
        {
            step = flow.Step(
                ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v =>
                [
                    v.Text(liveText),
                    v.Text("committed rows leave this region"),
                    v.Text("prompt stays usable across commitment"),
                ])),
                options =>
                {
                    options.MinHeight = 24;
                    options.MaxHeight = 24;
                });
            await step.WaitForReadyAsync();
            try
            {
                result = await step.CommitAsync(
                    source,
                    ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v => [v.Text(liveText)])));
            }
            finally
            {
                step.Complete();
            }
        }, width: 138, height: 37);

        // Gate before each unit is materialized: a gate at index N reads the
        // screen as it stands after units 0..N-1 were written and repainted.
        source.Gate = async index =>
        {
            switch (index)
            {
                case 2:
                    // Inside the first turn: before the repair, the commit's own
                    // rows had already overwritten the region's top row. Wait for
                    // a known prior hand-off instead of sampling after a delay.
                    await WaitForScreenTextAsync(
                        terminal, $"{CommitId}:r00", TimeSpan.FromSeconds(2));
                    screenEarlyInFirstTurn = terminal.GetScreenText();
                    break;

                case 10:
                    // A non-boundary index: type, and let the app — muted to the
                    // terminal but still rendering — produce a frame carrying the
                    // edit.
                    liveText = "FENCE-LIVE-TYPED";
                    step!.Invalidate();
                    await Task.Delay(200);
                    break;

                case 11:
                    // Still inside the turn that began at unit 8, so only a
                    // per-unit repaint can have rendered the edit by now. The
                    // expected prompt is also the presentation barrier: a timeout
                    // leaves the later assertion with the actual screen.
                    await WaitForScreenTextAsync(
                        terminal, "FENCE-LIVE-TYPED", TimeSpan.FromSeconds(2));
                    screenAfterMidTurnEdit = terminal.GetScreenText();
                    break;
            }
        };

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsNotNull(result);
        Assert.AreEqual(PayloadRows + 2, result!.CompletedUnits);

        // Both readings are collected before asserting, so one run of this fence
        // against a broken build reports every failure with its screen instead of
        // stopping at the first.
        var findings = new List<string>();
        if (screenEarlyInFirstTurn is null ||
            !screenEarlyInFirstTurn.Contains(LiveMarker, StringComparison.Ordinal))
        {
            findings.Add(
                "the live region must still be on screen after the first units of a commit.\n" +
                $"screen at unit 2:\n{screenEarlyInFirstTurn}");
        }

        if (screenAfterMidTurnEdit is null ||
            !screenAfterMidTurnEdit.Contains("FENCE-LIVE-TYPED", StringComparison.Ordinal))
        {
            findings.Add(
                "an edit typed mid-commit must be rendered without waiting for a turn boundary.\n" +
                $"screen at unit 11:\n{screenAfterMidTurnEdit}");
        }

        if (screenAfterMidTurnEdit is null ||
            !screenAfterMidTurnEdit.Contains("prompt stays usable across commitment", StringComparison.Ordinal))
        {
            findings.Add(
                "the whole live region, not just its first row, must be on screen mid-commit.\n" +
                $"screen at unit 11:\n{screenAfterMidTurnEdit}");
        }

        Assert.AreEqual(0, findings.Count, string.Join("\n\n", findings));

        // Visibility must not have cost retention.
        AssertKeysExactlyOnce(ReadFullBuffer(terminal), CommitId, PayloadRows, trace: "(trace not captured)");
    }

    /// <summary>
    /// Each unit's displacement and the live-region repaint that follows it must
    /// reach the terminal as ONE write, bracketed by synchronized output.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This fences the emitted update used by the visibility case. It requires
    /// the committed unit and the restored live region to share an update.
    /// One write per unit brackets the update with DEC mode 2026. This checks
    /// the emitted protocol, not the host's support for synchronized output or
    /// a bound on host presentation latency.
    /// </para>
    /// <para>
    /// The runner writes to a recording parent adapter, so the assertions are on
    /// the emitted byte stream rather than on a terminal model's screen.
    /// </para>
    /// </remarks>
    [TestMethod]
    public async Task EveryUnitUpdate_IsOneBracketedWriteCarryingTheLiveRegion()
    {
        const string SyncBegin = "\x1b[?2026h";
        const string SyncEnd = "\x1b[?2026l";
        var adapter = new RecordingParentAdapter(width: 100, height: 30);
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitResult? result = null;

        var runner = new Hex1bFlowRunner(
            flowCallback: async flow =>
            {
                var step = flow.Step(
                    ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v =>
                    [
                        v.Text(LiveMarker),
                        v.Text("committed rows leave this region"),
                    ])),
                    options =>
                    {
                        options.MinHeight = 8;
                        options.MaxHeight = 8;
                    });
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(
                        source,
                        ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v => [v.Text(LiveMarker)])));
                }
                finally
                {
                    step.Complete();
                }
            },
            options: new Hex1bFlowOptions
            {
                UseSoftWrapTombstones = true,
                InitialCursorRow = 0,
            },
            parentAdapter: adapter);

        var runTask = runner.RunAsync(CancellationToken.None);
        await runTask.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsNotNull(result);
        Assert.AreEqual(PayloadRows + 2, result!.CompletedUnits);

        var writes = adapter.SnapshotWritesSince(0);
        var unitWrites = writes
            .Where(w => w.Contains(CommitId + ":r", StringComparison.Ordinal)
                        || w.Contains($"<<<COMMIT {CommitId} BEGIN", StringComparison.Ordinal)
                        || w.Contains($"<<<COMMIT {CommitId} END", StringComparison.Ordinal))
            .ToArray();

        Assert.AreEqual(
            PayloadRows + 2,
            unitWrites.Length,
            "each unit must reach the terminal as exactly one write (BEGIN, each payload row, END)");

        var unbracketed = unitWrites
            .Where(w => !w.StartsWith(SyncBegin, StringComparison.Ordinal)
                        || !w.EndsWith(SyncEnd, StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(
            0,
            unbracketed.Length,
            "every unit update must be bracketed by synchronized output; first offender:\n" +
            (unbracketed.Length > 0 ? unbracketed[0] : ""));

        var withoutRegion = unitWrites
            .Where(w => !w.Contains(LiveMarker, StringComparison.Ordinal))
            .ToArray();
        Assert.AreEqual(
            0,
            withoutRegion.Length,
            "the unit's rows and the live-region repaint must be in the same write; first offender:\n" +
            (withoutRegion.Length > 0 ? withoutRegion[0] : ""));
    }

    /// <summary>
    /// Reflow a real terminal below the live region's original height while
    /// committing; both the history and the remaining prompt must survive.
    /// </summary>
    [TestMethod]
    public async Task ShrinkingBelowLiveRegion_PreservesHistoryAndVisiblePrompt()
    {
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitResult? result = null;
        string? buffer = null;
        string? screen = null;
        var shrunk = false;
        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            var step = flow.Step(BuildLive, options =>
            {
                options.MinHeight = 24;
                options.MaxHeight = 24;
            });
            await step.WaitForReadyAsync();
            try
            {
                result = await step.CommitAsync(source, BuildLive);
                buffer = ReadFullBuffer(terminal);
                screen = terminal.GetScreenText();
            }
            finally
            {
                step.Complete();
            }
        }, width: 100, height: 30);

        source.Gate = index =>
        {
            if (index == 40 && !shrunk)
            {
                shrunk = true;
                _ = terminal.ResizeWithWorkloadAsync(60, 11);
            }

            return Task.CompletedTask;
        };

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        Assert.IsTrue(shrunk, "the resize must occur before commitment ends");
        Assert.IsNotNull(result);
        Assert.AreEqual(PayloadRows + 2, result.CompletedUnits);
        AssertKeysExactlyOnce(buffer!, CommitId, PayloadRows, "shrink from 100x30 to 60x11");
        Assert.IsTrue(screen!.Contains(LiveMarker, StringComparison.Ordinal),
            $"the prompt must remain on the visible screen\n{screen}");
    }

    [TestMethod]
    public async Task FaultAfterThreeUnits_PreservesReportedPrefixAndNeverReplays()
    {
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitException? failure = null;
        var secondCommitThrew = false;

        Environment.SetEnvironmentVariable("HEX1B_PROTOTYPE_FAIL_AFTER_ROWS", "3");
        var tracePath = Path.Combine(Path.GetTempPath(), $"hex1b-fence-fault-{Guid.NewGuid():N}.jsonl");
        Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", tracePath);
        try
        {
            using var terminal = CreateTerminal(async flow =>
            {
                var step = flow.Step(BuildLive, options =>
                {
                    options.MinHeight = 14;
                    options.MaxHeight = 14;
                });
                await step.WaitForReadyAsync();
                try
                {
                    await step.CommitAsync(source, BuildLive);
                }
                catch (FlowCommitException ex)
                {
                    failure = ex;
                }

                // History commitment must now be suspended: a second request is
                // refused rather than replaying the uncertain batch.
                try
                {
                    await step.CommitAsync(source, BuildLive);
                    secondCommitThrew = false;
                }
                catch (FlowCommitException)
                {
                    secondCommitThrew = true;
                }

                // The app resumes rendering after the failure. The reported prefix
                // must survive that, and the uncertain tail must not be replayed.
                for (var i = 0; i < 5; i++)
                {
                    await Task.Delay(100);
                    step.Invalidate();
                }

                step.Complete();
            }, width: 121, height: 30);

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsNotNull(failure, "expected the injected emission failure");
            Assert.AreEqual(3, failure!.CompletedUnits);
            Assert.IsTrue(failure.MayHavePartialRow, "an IOException after a partial row must be reported as uncertain");
            Assert.IsTrue(secondCommitThrew, "commitment must stay suspended after an uncertain emission failure");

            var buffer = ReadFullBuffer(terminal);
            var faultTrace = File.Exists(tracePath) ? File.ReadAllText(tracePath) : "(no trace)";
            Console.WriteLine($"fault trace:\n{faultTrace}");
            Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape($"COMMIT {CommitId} BEGIN")).Count, $"BEGIN row must survive the resumed live rendering exactly once\nrows after recovery: live region top should be below the emitted rows\n{buffer}\n{tracePath}");
            Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape($"{CommitId}:r00 ")).Count, "r00 must survive the resumed live rendering exactly once");
            Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape($"{CommitId}:r01 ")).Count, "r01 must survive the resumed live rendering exactly once");

            // The aborted unit may legitimately remain as a truncated remnant
            // (uncertain bytes did reach the terminal), but it must never be
            // emitted a second time.
            var uncertain = Regex.Matches(buffer, Regex.Escape($"{CommitId}:r02 ")).Count;
            Assert.IsLessThanOrEqualTo(1, uncertain, "the uncertain unit must never be replayed");

            var screen = terminal.GetScreenText();
            Assert.IsTrue(screen.Contains(LiveMarker, StringComparison.Ordinal), "the live region must be visible after the failure");
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX1B_PROTOTYPE_FAIL_AFTER_ROWS", null);
            Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", null);
            File.Delete(tracePath);
        }
    }

    /// <summary>
    /// Cancellation between units: the token is signalled while the commit is
    /// admitted, and the coordinator observes it at the next boundary.
    /// </summary>
    /// <remarks>
    /// This is the path that must not attribute a previous unit's rows to the
    /// aborted one, so it is what pins the per-unit abort tracker and the
    /// whole-unit accounting. A cancellation landing between two rows of the
    /// same unit is NOT covered here: the commit source has no per-row hook, and
    /// adding a test-only one to the coordinator would be a production seam for
    /// the sake of a test. This case makes no claim about cancellation during
    /// a partial unit or a terminal drain.
    /// </remarks>
    [TestMethod]
    public async Task CancelBetweenUnits_ReportsWholeUnitsAndPreservesThePrefix()
    {
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitResult? result = null;
        using var cancellation = new CancellationTokenSource();
        const int CancelAtUnit = 24;

        using var terminal = CreateTerminal(async flow =>
        {
            var step = flow.Step(BuildLive, options =>
            {
                options.MinHeight = 14;
                options.MaxHeight = 14;
            });
            await step.WaitForReadyAsync();
            try
            {
                result = await step.CommitAsync(source, BuildLive, cancellation.Token);
            }
            finally
            {
                step.Complete();
            }
        }, width: 121, height: 30);

        source.Gate = index =>
        {
            if (index == CancelAtUnit)
            {
                cancellation.Cancel();
            }

            return Task.CompletedTask;
        };

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsNotNull(result);
        Assert.AreEqual(FlowCommitStatus.Cancelled, result!.Status);
        Assert.IsTrue(result.Cancelled);
        // Cancellation is not rollback: the units emitted before it stay
        // completed and are never replayed.
        Assert.AreEqual(CancelAtUnit, result.CompletedUnits);
        Assert.AreEqual(CancelAtUnit, result.RowKeys.Count);
        Assert.AreEqual(0, result.AbortedRows);

        var buffer = ReadFullBuffer(terminal);
        Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape($"COMMIT {CommitId} BEGIN")).Count, "BEGIN count");
        for (var row = 0; row < CancelAtUnit - 1; row++)
        {
            Assert.AreEqual(
                1,
                Regex.Matches(buffer, Regex.Escape($"{CommitId}:r{row:00} ")).Count,
                $"payload row {row} must remain exactly once after cancellation");
        }

        // Unit i renders payload row i-1 (unit 0 is BEGIN), so the unit that was
        // pending when cancellation arrived is payload row CancelAtUnit - 1.
        Assert.AreEqual(
            0,
            Regex.Matches(buffer, Regex.Escape($"{CommitId}:r{CancelAtUnit - 1:00} ")).Count,
            "the pending unit's row must not appear");
        Assert.IsTrue(terminal.GetScreenText().Contains(LiveMarker, StringComparison.Ordinal), "the live region must still be visible after cancellation");
    }

    // === harness =============================================================

    private sealed class ProfiledHeadlessPresentationAdapter :
        IHex1bTerminalPresentationAdapter,
        ITerminalReflowProvider,
        IInternalTerminalReflowProvider,
        IFlowTerminalHostProfileSource
    {
        private readonly HeadlessPresentationAdapter _inner;

        public ProfiledHeadlessPresentationAdapter(
            int width,
            int height,
            FlowTerminalHostProfile profile)
        {
            _inner = new HeadlessPresentationAdapter(width, height);
            FlowHostProfile = profile;
        }

        public FlowTerminalHostProfile FlowHostProfile { get; }

        public int Width => _inner.Width;
        public int Height => _inner.Height;
        public TerminalCapabilities Capabilities => _inner.Capabilities;
        public bool ReflowEnabled => _inner.ReflowEnabled;
        public bool ShouldClearSoftWrapOnAbsolutePosition =>
            _inner.ShouldClearSoftWrapOnAbsolutePosition;

        public event Action<int, int>? Resized
        {
            add => _inner.Resized += value;
            remove => _inner.Resized -= value;
        }

        public event Action? Disconnected
        {
            add => _inner.Disconnected += value;
            remove => _inner.Disconnected -= value;
        }

        public ValueTask WriteOutputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken ct = default)
            => _inner.WriteOutputAsync(data, ct);

        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(
            CancellationToken ct = default)
            => _inner.ReadInputAsync(ct);

        public ValueTask FlushAsync(CancellationToken ct = default)
            => _inner.FlushAsync(ct);

        public ValueTask EnterRawModeAsync(CancellationToken ct = default)
            => _inner.EnterRawModeAsync(ct);

        public ValueTask ExitRawModeAsync(CancellationToken ct = default)
            => _inner.ExitRawModeAsync(ct);

        public (int Row, int Column) GetCursorPosition()
            => _inner.GetCursorPosition();
        public ReflowResult Reflow(ReflowContext context)
            => _inner.Reflow(context);

        bool IInternalTerminalReflowProvider.TryReflowWithAnchors(
            ReflowContext context,
            IReadOnlyList<TerminalReflowAnchor> anchors,
            out InternalReflowResult result)
            => ((IInternalTerminalReflowProvider)_inner)
                .TryReflowWithAnchors(context, anchors, out result);

        public ProfiledHeadlessPresentationAdapter WithReflow(
            ITerminalReflowProvider strategy)
        {
            _inner.WithReflow(strategy);
            return this;
        }

        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }

    private static Hex1bTerminal CreateTerminal(
        Func<Hex1bFlowContext, Task> flowCallback, int width, int height,
        FlowTerminalHostProfile? hostProfile = null)
    {
        var builder = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flowCallback, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = TimeSpan.FromMilliseconds(80);
            })
            .WithDimensions(width, height);

        if (hostProfile is { } profile)
        {
            builder.WithPresentation(
                new ProfiledHeadlessPresentationAdapter(width, height, profile)
                    .WithReflow(GhosttyReflowStrategy.Instance));
        }
        else
        {
            builder.WithHeadless();
        }

        return builder
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000)
            .Build();
    }

    private static Hex1bTerminal CreateTerminalWithWidget(
        Func<Hex1bFlowContext, Task> flowCallback,
        int width,
        int height,
        bool useCursorProvider,
        out TerminalWidgetHandle handle)
    {
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flowCallback, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = TimeSpan.FromMilliseconds(80);
            })
            .WithDimensions(width, height)
            .WithTerminalWidget(out handle)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000)
            .Build();

        if (useCursorProvider)
        {
            var workload = (Hex1bAppWorkloadAdapter)terminal.Workload;
            workload.HeadlessCursorProvider = () =>
            {
                var snapshot = terminal.GetScreenBufferSnapshot();
                return (snapshot.CursorX, snapshot.CursorY);
            };
        }

        return terminal;
    }

    private static async Task<bool> WaitForCapturedFrameAsync(
        TaskCompletionSource<bool> frameReady,
        TimeSpan timeout)
    {
        try
        {
            await frameReady.Task.WaitAsync(timeout).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
    }

    private static string FrameText(TerminalWidgetRenderFrame frame)
    {
        var text = new StringBuilder();
        for (var row = 0; row < frame.Height; row++)
        {
            for (var column = 0; column < frame.Width; column++)
            {
                var character = frame.Cells[row, column].Character;
                text.Append(string.IsNullOrEmpty(character) ? " " : character);
            }

            if (row + 1 < frame.Height)
            {
                text.Append('\n');
            }
        }

        return text.ToString();
    }



    private static Task<Hex1bWidget> BuildLive(FlowStepContext ctx)
        => Task.FromResult<Hex1bWidget>(
            ctx.VStack(v =>
            [
                v.Text(LiveMarker),
                v.Text("committed rows leave this region"),
                v.Text("prompt stays usable across commitment"),
            ]));
    private static async Task<bool> WaitForScreenTextAsync(
        Hex1bTerminal terminal,
        string text,
        TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (terminal.GetScreenText().Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return terminal.GetScreenText().Contains(text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Full buffer as the host would export it: scrollback rows in order, then the
    /// visible screen. Counting keys across both is what the native oracle does.
    /// </summary>
    private static string ReadFullBuffer(Hex1bTerminal terminal)
    {
        var text = new StringBuilder();
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
        {
            foreach (var cell in row.Cells)
            {
                text.Append(string.IsNullOrEmpty(cell.Character) ? " " : cell.Character);
            }

            text.Append('\n');
        }

        text.Append(terminal.GetScreenText());
        return text.ToString();
    }


    private static void AssertKeysExactlyOnce(string buffer, string commitId, int payloadRows, string trace)
    {
        var actual = Regex.Matches(buffer, Regex.Escape(commitId) + @":r(\d+)(?![0-9])")
            .Select(match => int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        CollectionAssert.AreEqual(
            Enumerable.Range(0, payloadRows).ToArray(), actual,
            $"payload keys must be retained exactly once in order\n{buffer}\ncommit trace:\n{trace}");
        Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape(commitId) + " BEGIN").Count, "BEGIN marker count");
        Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape(commitId) + " END").Count, "END marker count");
        for (var row = 0; row < payloadRows; row++)
        {
            Assert.AreEqual(
                1,
                Regex.Matches(buffer, Regex.Escape($"|{commitId}#r{row:00}|")).Count,
                $"right-hand payload sentinel for row {row} must survive reflow\n{buffer}");
        }
    }

    /// <summary>
    /// Immutable commit units mirroring the Janet driver's row grammar. <see cref="Gate"/>
    /// lets the test hold emission at a chosen unit index so a resize lands mid-commit.
    /// </summary>
    private sealed class FenceCommitSource(string commitId, int payloadRows) : FlowCommitSource
    {
        public Func<int, Task>? Gate { get; set; }

        public override int UnitCount => payloadRows + 2;

        public override async Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken)
        {
            if (Gate is { } gate)
            {
                await gate(index).ConfigureAwait(false);
            }

            var columns = Math.Max(1, width);
            var (key, text) = Materialize(index, columns);
            var surface = new Surface(columns, 1);
            surface.WriteText(0, 0, text);
            return new FlowCommitUnit(key, surface);
        }

        private (string Key, string Text) Materialize(int index, int columns)
        {
            if (index == 0)
            {
                return ($"{commitId}:begin", Fit($"<<<COMMIT {commitId} BEGIN rows={payloadRows:000} width={columns}>>>", columns));
            }

            if (index >= UnitCount - 1)
            {
                return ($"{commitId}:end", Fit($"<<<COMMIT {commitId} END rows={payloadRows:000}>>>", columns));
            }

            var row = index - 1;
            var left = $"{commitId}:r{row:00} payload row {row:000} ";
            var right = $"|{commitId}#r{row:00}|";
            return ($"r{row:00000}", Fit(left + new string('.', Math.Max(0, columns - left.Length - right.Length)) + right, columns));
        }

        private static string Fit(string text, int columns)
            => text.Length >= columns ? text[..columns] : text.PadRight(columns, '.');
    }
}
