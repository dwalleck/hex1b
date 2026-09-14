using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hex1b;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Reflow;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Recovery and accounting fences for
/// <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// These are narrow contract fences driven over the real
/// <see cref="Hex1bFlowRunner"/> with a synthetic parent adapter, not
/// retention fences: each one pins a fact the caller branches on that the
/// broader retention suite cannot observe deterministically.
/// </para>
/// <list type="bullet">
///   <item>a failure raised by the source itself, after a prefix was emitted,
///         is reported as an emitted prefix and suspends the step rather than
///         escaping as a retryable-looking exception;</item>
///   <item>a height-only resize is geometry: nothing is re-prepared and the
///         pending unit is not materialized twice for the same
///         <c>(index, width)</c> pair, while a width change still
///         re-materializes it for the new pair;</item>
///   <item>a unit is counted as completed before the post-emission wait, so a
///         cancellation that lands in that wait cannot turn an emitted unit
///         back into a pending one;</item>
///   <item>cancellation observed after the last unit was handed off reports a
///         complete emission with cancellation, not a partial commit, and
///         applies the next live layout;</item>
///   <item>recovery with units still pending retains the step's current live
///         layout instead of applying the commit's next one.</item>
/// </list>
/// <para>
/// The adapter's output queue is settable here, which the retention harness has
/// no need for: the post-emission wait only waits when the queue is non-empty,
/// so a cancellation that must land inside that wait needs the queue held open.
/// </para>
/// </remarks>
[DoNotParallelize]
[TestClass]
public class FlowCommitRecoveryContractTests
{
    private const string LiveMarker = "RECOVERY-LIVE-PROMPT";
    private const string NextLiveMarker = "RECOVERY-NEXT-LIVE-PROMPT";
    private const int UnitCount = 12;
    private const int QueueHoldMs = 150;

    [TestMethod]
    public async Task SourceFailureAfterCompletedPrefix_ReportsThePrefixAndSuspendsTheStep()
    {
        const int FailingUnit = 5;
        var adapter = new ControllableDrainParentAdapter(width: 100, height: 30);
        var source = new ContractSource(UnitCount) { ThrowAtIndex = FailingUnit };
        FlowCommitException? failure = null;
        FlowCommitException? secondCommitFailure = null;

        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            try
            {
                await step.CommitAsync(source, NextLive);
            }
            catch (FlowCommitException ex)
            {
                failure = ex;
            }

            // An emitted prefix must never be replayed, so the step has to refuse
            // the next commit rather than let the caller retry into duplicates.
            try
            {
                await step.CommitAsync(new ContractSource(UnitCount), NextLive);
            }
            catch (FlowCommitException ex)
            {
                secondCommitFailure = ex;
            }

            await CompleteStepAsync(step);
        });

        Assert.IsNotNull(failure, "a source failure after a prefix must surface as FlowCommitException");
        Assert.AreEqual(FailingUnit, failure!.CompletedUnits);
        Assert.AreEqual(FailingUnit, failure.CompletedRows);
        Assert.AreEqual(0, failure.AbortedRows, "the failing unit composed nothing before it threw");
        Assert.IsFalse(failure.MayHavePartialRow);
        Assert.IsInstanceOfType<InvalidOperationException>(
            failure.InnerException,
            "the original failure must be preserved, not replaced by the recovery");
        Assert.IsNotNull(secondCommitFailure, "an emitted prefix must suspend further commitment");
        Assert.AreEqual(0, secondCommitFailure!.CompletedUnits);

        var writes = adapter.AllWrites;
        Assert.IsTrue(
            writes.Contains($"UNIT-{FailingUnit - 1:00}", StringComparison.Ordinal),
            "the fully emitted prefix must have reached the write path");
        Assert.IsFalse(
            writes.Contains($"UNIT-{FailingUnit:00}", StringComparison.Ordinal),
            "the failing unit must not have been written");
        Assert.IsFalse(
            writes.Contains(NextLiveMarker, StringComparison.Ordinal),
            "a failure with units pending must retain the previous live layout");
        Assert.IsTrue(
            writes.Contains(LiveMarker, StringComparison.Ordinal),
            "recovery must leave the step's current live layout on screen");
    }

    [TestMethod]
    public async Task HeightOnlyResize_IsGeometryOnlyAndNeverReMaterializesTheSamePair()
    {
        const int ResizeAtUnit = 4;
        const int OriginalWidth = 100;
        var source = new ContractSource(UnitCount) { RefuseDuplicatePairs = true };
        FlowCommitResult? result = null;
        string? buffer = null;
        var resized = false;
        Hex1bTerminal terminal = null!;

        var trace = await RunWithTraceAsync(async () =>
        {
            using var lifetime = terminal = CreateTerminal(async flow =>
            {
                var step = flow.Step(Live, options =>
                {
                    options.MinHeight = 8;
                    options.MaxHeight = 8;
                });
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(source, NextLive);
                    await Task.Delay(200);
                    buffer = ReadFullBuffer(terminal);
                }
                finally
                {
                    step.Complete();
                }
            }, width: OriginalWidth, height: 30);

            source.Gate = async index =>
            {
                if (index == ResizeAtUnit && !resized)
                {
                    resized = true;
                    // Height only: every pending unit's bytes are unchanged, so the
                    // commit must neither re-prepare the source nor re-materialize
                    // the pending unit at the width it was already built for.
                    terminal.ResizeWithWorkload(OriginalWidth, 20);
                    await Task.Delay(150);
                }
            };

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        });

        Assert.IsTrue(resized, "the gate must have changed the terminal height mid-commit");
        Assert.IsNotNull(result);
        Assert.AreEqual(UnitCount, result!.CompletedUnits);
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(1, source.PrepareCalls, "a height-only change must not re-prepare the source");
        Assert.AreEqual(0, source.DuplicatePairs, "no (index, width) pair may be materialized twice");
        Assert.AreEqual(UnitCount, source.Materializations);
        Assert.IsTrue(
            trace.Contains("geometryOnly=true", StringComparison.Ordinal),
            $"the commit must have observed the height change as geometry:\n{trace}");
        Assert.IsNotNull(buffer, "the commit must have completed so the buffer can be read");
        AssertEachUnitMarkerExactlyOnce(buffer!, "after a height-only resize mid-commit");
    }

    [TestMethod]
    public async Task WidthChange_StillReMaterializesThePendingUnitAtTheNewWidth()
    {
        const int ResizeAtUnit = 4;
        const int OriginalWidth = 100;
        const int NarrowerWidth = 80;
        var source = new ContractSource(UnitCount) { RefuseDuplicatePairs = true };
        FlowCommitResult? result = null;
        string? buffer = null;
        var resized = false;
        Hex1bTerminal terminal = null!;

        var trace = await RunWithTraceAsync(async () =>
        {
            using var lifetime = terminal = CreateTerminal(async flow =>
            {
                var step = flow.Step(Live, options =>
                {
                    options.MinHeight = 8;
                    options.MaxHeight = 8;
                });
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(source, NextLive);
                    await Task.Delay(200);
                    buffer = ReadFullBuffer(terminal);
                }
                finally
                {
                    step.Complete();
                }
            }, width: OriginalWidth, height: 30);

            source.Gate = async index =>
            {
                if (index == ResizeAtUnit && !resized)
                {
                    resized = true;
                    terminal.ResizeWithWorkload(NarrowerWidth, 30);
                    await Task.Delay(150);
                }
            };

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        });

        Assert.IsTrue(resized, "the gate must have changed the terminal width mid-commit");
        Assert.IsNotNull(result);
        Assert.AreEqual(UnitCount, result!.CompletedUnits);
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(2, source.PrepareCalls, "a width change must re-prepare the source");
        Assert.AreEqual(0, source.DuplicatePairs, "the pending unit must be re-materialized at the NEW width");
        Assert.AreEqual(UnitCount + 1, source.Materializations, "only the pending unit is re-materialized");
        var pairs = source.Pairs;
        Assert.IsTrue(
            pairs.Contains((ResizeAtUnit, OriginalWidth)),
            "the pending unit must have been built at the pre-resize width first");
        Assert.IsTrue(
            pairs.Contains((ResizeAtUnit, NarrowerWidth)),
            "the pending unit must be re-materialized at the width that is now in force");
        Assert.IsTrue(
            trace.Contains("geometryOnly=false", StringComparison.Ordinal),
            $"the commit must have observed a width reflow:\n{trace}");
        Assert.IsNotNull(buffer, "the commit must have completed so the buffer can be read");
        AssertEachUnitMarkerExactlyOnce(buffer!, "after a width change mid-commit");
    }

    /// <summary>
    /// The pending width moves A → B → back to A while units are still being
    /// materialized. The middle width is built as its own pair, and the return to
    /// A must not rebuild the original pair: whichever direction the width moves
    /// in, no <c>(index, width)</c> pair is materialized twice.
    /// </summary>
    /// <remarks>
    /// The source refuses a repeated pair outright, so a duplicate is not merely
    /// counted here — it fails the commit. The explicit pair assertions then name
    /// what the oscillation actually built, and the bounded materialization count
    /// keeps the fence honest about how much re-materializing an ABA move may cost.
    /// </remarks>
    [TestMethod]
    public async Task PendingWidthMovesAToBAndBack_MaterializesEachPairAtMostOnce()
    {
        const int NarrowAtUnit = 4;
        const int OriginalWidth = 100;
        const int MiddleWidth = 80;
        var source = new ContractSource(UnitCount) { RefuseDuplicatePairs = true };
        FlowCommitResult? result = null;
        string? buffer = null;
        var narrowed = false;
        var restored = false;
        Hex1bTerminal terminal = null!;

        await RunWithTraceAsync(async () =>
        {
            using var lifetime = terminal = CreateTerminal(async flow =>
            {
                var step = flow.Step(Live, options =>
                {
                    options.MinHeight = 8;
                    options.MaxHeight = 8;
                });
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(source, NextLive);
                    await Task.Delay(200);
                    buffer = ReadFullBuffer(terminal);
                }
                finally
                {
                    step.Complete();
                }
            }, width: OriginalWidth, height: 30);

            source.Gate = async index =>
            {
                if (index == NarrowAtUnit && !narrowed)
                {
                    narrowed = true;
                    terminal.ResizeWithWorkload(MiddleWidth, 30);
                    await Task.Delay(150);
                }
                else if (index == NarrowAtUnit && !restored)
                {
                    restored = true;
                    terminal.ResizeWithWorkload(OriginalWidth, 30);
                    await Task.Delay(150);
                }
            };

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));
        });

        Assert.IsTrue(
            narrowed && restored,
            "the gate must have moved the width to the middle value and then back");
        Assert.IsNotNull(result);
        Assert.AreEqual(UnitCount, result!.CompletedUnits);
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(0, source.DuplicatePairs, "the source must never be asked for a pair twice");

        var pairs = source.Pairs;
        Assert.AreEqual(
            1,
            pairs.Count(pair => pair == (NarrowAtUnit, OriginalWidth)),
            "the unit that was pending when the width moved must not be rebuilt at the original width after it returns");
        Assert.AreEqual(1, pairs.Count(pair => pair == (NarrowAtUnit, MiddleWidth)),
            "the same pending unit must also have been built at the middle width");
        Assert.AreEqual(UnitCount + 1, source.Materializations,
            "returning to A must reuse its cached unit rather than materialize it again");

        Assert.IsNotNull(buffer, "the commit must have completed so the buffer can be read");
        AssertEachUnitMarkerExactlyOnce(buffer!, "after an A -> B -> A width move mid-commit");
    }

    [TestMethod]
    public async Task CancelDuringPostUnitDrain_CountsTheHandedOffUnitBeforeTheWait()
    {
        const int CancelAtUnit = 4;
        var adapter = new ControllableDrainParentAdapter(width: 100, height: 30);
        var source = new ContractSource(UnitCount);
        using var cancellation = new CancellationTokenSource();
        FlowCommitResult? result = null;

        source.Gate = index =>
        {
            if (index == CancelAtUnit)
            {
                // Hold the output queue open so the wait that follows this unit's
                // emission is still running when the token is cancelled, then let
                // the adapter drain again so only the commit's own wait sees the
                // cancellation.
                adapter.SetQueueDepth(1);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(QueueHoldMs).ConfigureAwait(false);
                    cancellation.Cancel();
                    adapter.SetQueueDepth(0);
                });
            }

            return Task.CompletedTask;
        };

        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            result = await step.CommitAsync(source, NextLive, cancellation.Token);
            await CompleteStepAsync(step);
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(FlowCommitStatus.Cancelled, result!.Status);
        Assert.IsTrue(result.Cancelled);
        Assert.IsTrue(result.CancellationRequested);
        // The unit emitted before the cancelled wait is complete: the count
        // happens before the wait precisely so this cannot drift.
        Assert.AreEqual(CancelAtUnit + 1, result.CompletedUnits);
        Assert.AreEqual(CancelAtUnit + 1, result.CompletedRows);
        Assert.AreEqual(CancelAtUnit + 1, result.RowKeys.Count);
        Assert.AreEqual(0, result.AbortedRows);
        Assert.IsFalse(result.EmissionDrainObserved, "the queue was never observed empty");
        Assert.IsFalse(result.EmissionDrainTimedOut, "the wait was cut short, not timed out");

        var writes = adapter.AllWrites;
        Assert.IsTrue(
            writes.Contains($"UNIT-{CancelAtUnit:00}", StringComparison.Ordinal),
            "the unit counted as completed must have reached the write path");
        Assert.IsFalse(
            writes.Contains($"UNIT-{CancelAtUnit + 1:00}", StringComparison.Ordinal),
            "the unit after the cancelled wait must stay pending");
        Assert.IsFalse(
            writes.Contains(NextLiveMarker, StringComparison.Ordinal),
            "cancellation with units pending must retain the previous live layout");
        Assert.IsTrue(
            writes.Contains(LiveMarker, StringComparison.Ordinal),
            "recovery must leave the step's current live layout on screen");
    }

    [TestMethod]
    public async Task CancelAfterTheLastUnitIsHandedOff_ReportsEmittedAndAppliesTheNextLiveLayout()
    {
        var adapter = new ControllableDrainParentAdapter(width: 100, height: 30);
        var source = new ContractSource(UnitCount);
        using var cancellation = new CancellationTokenSource();
        FlowCommitResult? result = null;

        source.Gate = index =>
        {
            if (index == UnitCount - 1)
            {
                // The last unit's wait is the post-emission wait: cancelling
                // inside it must not turn a finished emission into a partial one.
                adapter.SetQueueDepth(1);
                _ = Task.Run(async () =>
                {
                    await Task.Delay(QueueHoldMs).ConfigureAwait(false);
                    cancellation.Cancel();
                    adapter.SetQueueDepth(0);
                });
            }

            return Task.CompletedTask;
        };

        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            result = await step.CommitAsync(source, NextLive, cancellation.Token);
            await CompleteStepAsync(step);
        });

        Assert.IsNotNull(result);
        Assert.AreEqual(FlowCommitStatus.Emitted, result!.Status);
        Assert.IsFalse(result.Cancelled, "a complete emission is not a partial commit");
        Assert.IsTrue(result.CancellationRequested, "cancellation was observed, just after the last unit");
        Assert.AreEqual(UnitCount, result.CompletedUnits);
        Assert.AreEqual(UnitCount, result.RowKeys.Count);
        Assert.AreEqual(0, result.AbortedRows);
        Assert.IsFalse(result.EmissionDrainObserved);
        Assert.IsFalse(result.EmissionDrainTimedOut);

        Assert.IsTrue(
            adapter.AllWrites.Contains($"UNIT-{UnitCount - 1:00}", StringComparison.Ordinal),
            "every unit must have reached the write path");
        await WaitForWriteAsync(
            adapter,
            NextLiveMarker,
            "the next live layout must be applied once every unit was handed off");
    }

    [TestMethod]
    public async Task CancellationDuringPreparation_RetainsLiveContentAndRemainsCommittable()
    {
        using var cancellation = new CancellationTokenSource();
        var adapter = new ControllableDrainParentAdapter(width: 100, height: 30);
        var source = new ContractSource(UnitCount)
        {
            Preparation = token =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(token);
            },
        };

        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            var cancelled = false;
            try
            {
                await step.CommitAsync(source, NextLive, cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                cancelled = true;
            }

            Assert.IsTrue(cancelled, "an unknown source count must not be reported as Emitted");
            Assert.IsFalse(adapter.AllWrites.Contains(NextLiveMarker, StringComparison.Ordinal),
                "preparation cancellation must not replace pending live content");
            var retry = await step.CommitAsync(new ContractSource(1), Live);
            Assert.AreEqual(1, retry.CompletedUnits, "nothing was handed off before preparation cancelled");
            await CompleteStepAsync(step);
        });
    }

    [TestMethod]
    public async Task FailureDuringPreparation_RetainsLiveContentAndReportsNoHandoff()
    {
        var adapter = new ControllableDrainParentAdapter(width: 100, height: 30);
        var source = new ContractSource(UnitCount)
        {
            Preparation = _ => Task.FromException(new InvalidOperationException("source preparation failed")),
        };

        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            FlowCommitException? failure = null;
            try
            {
                await step.CommitAsync(source, NextLive);
            }
            catch (FlowCommitException ex)
            {
                failure = ex;
            }

            Assert.IsNotNull(failure);
            Assert.AreEqual(0, failure.CompletedUnits);
            Assert.AreEqual(0, failure.CompletedRows);
            Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            Assert.IsFalse(adapter.AllWrites.Contains(NextLiveMarker, StringComparison.Ordinal),
                "an unknown source count must not mean every unit was handed off");
            var retry = await step.CommitAsync(new ContractSource(1), Live);
            Assert.AreEqual(1, retry.CompletedUnits);
            await CompleteStepAsync(step);
        });
    }

    [TestMethod]
    public async Task NonObservingPresentation_CommitsWithoutSuspendingOrFreezingLiveOutput()
    {
        FlowCommitResult? result = null;
        var buffer = string.Empty;
        Hex1bTerminal terminal = null!;
        using var lifetime = terminal = CreateTerminal(async flow =>
        {
            var step = await StartStepAsync(flow);
            result = await step.CommitAsync(new ContractSource(UnitCount), NextLive);
            for (var attempt = 0; attempt < 100; attempt++)
            {
                buffer = ReadFullBuffer(terminal);
                if (buffer.Contains(NextLiveMarker, StringComparison.Ordinal))
                {
                    break;
                }
                await Task.Delay(10);
            }
            step.Complete();
        }, 100, 30, new TerminalWidgetHandle(100, 30));

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(20));
        Assert.IsNotNull(result);
        Assert.AreEqual(UnitCount, result.CompletedUnits);
        AssertEachUnitMarkerExactlyOnce(buffer, "non-observing presentation");
        Assert.IsTrue(buffer.Contains(NextLiveMarker, StringComparison.Ordinal));
    }

    [TestMethod]
    public async Task AdmissionObservationFailure_ReportsZeroHandoffAndSuspendsCommitment()
    {
        var adapter = new MissingCursorParentAdapter();
        await RunFlowAsync(adapter, async flow =>
        {
            var step = await StartStepAsync(flow);
            for (var attempt = 0; attempt < 2; attempt++)
            {
                FlowCommitException? failure = null;
                try
                {
                    await step.CommitAsync(new ContractSource(UnitCount), NextLive);
                }
                catch (FlowCommitException ex)
                {
                    failure = ex;
                }

                Assert.IsNotNull(failure, "admission failure and subsequent suspension share the typed contract");
                Assert.AreEqual(0, failure.CompletedUnits);
                Assert.AreEqual(0, failure.CompletedRows);
                Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
            }
            Assert.IsFalse(adapter.AllWrites.Contains("UNIT-00", StringComparison.Ordinal));
            await CompleteStepAsync(step);
        });
    }

    // === harness =============================================================

    private static async Task<FlowStep> StartStepAsync(Hex1bFlowContext flow)
    {
        await flow.ShowAsync(ctx => ctx.Text("RECOVERY-HEADER")).ConfigureAwait(false);
        return flow.Step(Live, options =>
        {
            options.MinHeight = 8;
            options.MaxHeight = 8;
        });
    }

    private static async Task CompleteStepAsync(FlowStep step)
    {
        // Let the app render whatever layout the commit left behind before the
        // step ends, so the retained or applied layout is observable in the
        // writes.
        await Task.Delay(250).ConfigureAwait(false);
        step.Invalidate();
        await Task.Delay(250).ConfigureAwait(false);
        step.Complete();
    }

    /// <summary>
    /// Real headless terminal over the flow runner, with the re-wrapping model the
    /// retained native runs exercised.
    /// </summary>
    /// <remarks>
    /// The geometry fences need it because a resized commitment is only accepted
    /// when the adapter can report where the host's own cursor is: a synthetic
    /// adapter has no such observation, and the commit legitimately refuses to
    /// re-anchor a resize on it. The drain and cancellation fences do not resize,
    /// so they keep the synthetic adapter that lets them hold the output queue.
    /// </remarks>
    private static Hex1bTerminal CreateTerminal(
        Func<Hex1bFlowContext, Task> flowCallback,
        int width,
        int height,
        IHex1bTerminalPresentationAdapter? presentation = null)
    {
        var builder = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flowCallback, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = TimeSpan.FromMilliseconds(80);
            })
            .WithHeadless()
            .WithDimensions(width, height)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000);
        if (presentation is not null)
        {
            builder.WithPresentation(presentation);
        }
        return builder.Build();
    }

    /// <summary>Full buffer as the host would export it: scrollback, then screen.</summary>
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

    /// <summary>
    /// Every emitted unit's row marker must be in the host's own buffer exactly
    /// once, so a resize that dropped or duplicated emitted content fails even
    /// when the framework's unit accounting still looks right.
    /// </summary>
    private static void AssertEachUnitMarkerExactlyOnce(string buffer, string when)
    {
        for (var index = 0; index < UnitCount; index++)
        {
            var marker = $"UNIT-{index:00}";
            var count = Regex.Matches(buffer, Regex.Escape(marker)).Count;
            Assert.AreEqual(
                1,
                count,
                $"[{when}] unit {index} must appear exactly once in the committed buffer (found {count})");
        }
    }

    private static Task RunFlowAsync(
        ControllableDrainParentAdapter adapter,
        Func<Hex1bFlowContext, Task> flowCallback)
        => new Hex1bFlowRunner(
                flowCallback: flowCallback,
                options: new Hex1bFlowOptions
                {
                    UseSoftWrapTombstones = true,
                    InitialCursorRow = 0,
                    ResizeSettleDelay = TimeSpan.FromMilliseconds(60),
                },
                parentAdapter: adapter)
            .RunAsync(CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(60));

    private static async Task<string> RunWithTraceAsync(Func<Task> body)
    {
        var path = Path.Combine(Path.GetTempPath(), $"hex1b-recovery-{Guid.NewGuid():N}.jsonl");
        Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", path);
        try
        {
            await body().ConfigureAwait(false);
            return File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        }
        finally
        {
            Environment.SetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS", null);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private static async Task WaitForWriteAsync(
        ControllableDrainParentAdapter adapter,
        string text,
        string because)
    {
        for (var attempt = 0; attempt < 40; attempt++)
        {
            if (adapter.AllWrites.Contains(text, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(25).ConfigureAwait(false);
        }

        Assert.Fail($"{because}; '{text}' never reached the parent adapter");
    }

    private static Task<Hex1bWidget> Live(FlowStepContext ctx)
        => Task.FromResult<Hex1bWidget>(ctx.VStack(v =>
        [
            v.Text(LiveMarker),
            v.Text("committed rows leave this region"),
        ]));

    private static Task<Hex1bWidget> NextLive(FlowStepContext ctx)
        => Task.FromResult<Hex1bWidget>(ctx.VStack(v =>
        [
            v.Text(NextLiveMarker),
            v.Text("committed rows leave this region"),
        ]));

    /// <summary>
    /// Commit source over a fixed unit count, one single-row unit per index. It
    /// refuses a second materialization of the same <c>(index, width)</c> pair —
    /// the source-side half of the at-most-once contract — and can raise a
    /// non-IOException failure for a chosen unit after a prefix.
    /// </summary>
    private sealed class ContractSource(int unitCount) : FlowCommitSource
    {
        private readonly HashSet<(int Index, int Width)> _pairs = [];
        private readonly object _pairLock = new();

        public int PrepareCalls { get; private set; }

        public int Materializations { get; private set; }

        public int DuplicatePairs { get; private set; }

        public bool RefuseDuplicatePairs { get; set; }

        public int ThrowAtIndex { get; set; } = -1;

        public Func<int, Task>? Gate { get; set; }

        public Func<CancellationToken, Task>? Preparation { get; set; }

        public (int Index, int Width)[] Pairs
        {
            get
            {
                lock (_pairLock)
                {
                    return [.. _pairs];
                }
            }
        }

        public override int UnitCount => unitCount;

        public override async Task<int> PrepareAsync(int width, CancellationToken cancellationToken)
        {
            PrepareCalls++;
            if (Preparation is not null)
            {
                await Preparation(cancellationToken);
            }
            return unitCount;
        }

        public override async Task<FlowCommitUnit> UnitAsync(
            int index,
            int width,
            CancellationToken cancellationToken)
        {
            if (Gate is { } gate)
            {
                await gate(index).ConfigureAwait(false);
            }

            lock (_pairLock)
            {
                if (!_pairs.Add((index, width)))
                {
                    // Hand-off the fact before refusing: a regression that
                    // re-materializes a pair must be count-visible even though the
                    // refusal faults the commit.
                    DuplicatePairs++;
                    if (RefuseDuplicatePairs)
                    {
                        throw new InvalidOperationException(
                            $"unit {index} was already materialized at width {width}");
                    }
                }
            }

            Materializations++;
            if (index == ThrowAtIndex)
            {
                throw new InvalidOperationException(
                    $"contract source failed unit {index} after a completed prefix");
            }

            var columns = Math.Max(1, width);
            var surface = new Surface(columns, 1);
            surface.WriteText(0, 0, RowText(index, columns));
            return new FlowCommitUnit($"u{index:00}", surface);
        }

        private static string RowText(int index, int columns)
        {
            var text = $"UNIT-{index:00} " + new string('.', Math.Max(0, columns - 12));
            return text.Length >= columns ? text[..columns] : text.PadRight(columns, '.');
        }
    }

    private sealed class MissingCursorParentAdapter() : ControllableDrainParentAdapter(100, 30), ICursorPositionSource
    {
        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken cancellationToken = default)
            => Task.FromResult<(int Column, int Row)?>(null);
    }

    /// <summary>
    /// Parent adapter for the recovery fences: records every write, lets a test
    /// change the geometry without dispatching a resize event, and lets a test
    /// hold the shared output queue non-empty so the commit's post-emission wait
    /// genuinely waits.
    /// </summary>
    internal class ControllableDrainParentAdapter : IHex1bAppTerminalWorkloadAdapter
    {
        private readonly Channel<Hex1bEvent> _inputChannel = Channel.CreateUnbounded<Hex1bEvent>();
        private readonly ConcurrentQueue<string> _writes = new();
        private int _width;
        private int _height;
        private int _queueDepth;

        public ControllableDrainParentAdapter(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public int Width => _width;

        public int Height => _height;

        public TerminalCapabilities Capabilities { get; } = TerminalCapabilities.Modern;

        public ChannelReader<Hex1bEvent> InputEvents => _inputChannel.Reader;

        public int OutputQueueDepth => Volatile.Read(ref _queueDepth);

        public event Action? Disconnected;

        public string AllWrites => string.Concat(_writes.ToArray());

        public void SetSize(int width, int height)
        {
            _width = width;
            _height = height;
        }

        public void SetQueueDepth(int depth) => Volatile.Write(ref _queueDepth, depth);

        public void Write(string text) => _writes.Enqueue(text);

        public void Write(ReadOnlySpan<byte> data) => _writes.Enqueue(Encoding.UTF8.GetString(data));

        public void Flush()
        {
        }

        public void EnterTuiMode()
        {
        }

        public void ExitTuiMode()
        {
        }

        public void Clear() => _writes.Enqueue("\x1b[2J");

        public void SetCursorPosition(int left, int top) =>
            _writes.Enqueue($"\x1b[{top + 1};{left + 1}H");

        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
        {
            _width = width;
            _height = height;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            _inputChannel.Writer.TryComplete();
            Disconnected?.Invoke();
            return ValueTask.CompletedTask;
        }

        public ValueTask PushEventAsync(Hex1bEvent evt) => _inputChannel.Writer.WriteAsync(evt);
    }
}
