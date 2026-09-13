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
/// indices so a resize provably lands mid-commit, and the framework's own commit
/// trace is asserted to prove the commit observed it.
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

            // Gate at the last unit of a turn, so the resize is pending when the
            // commit reaches the next turn boundary and must re-flow for it.
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
                        terminal.ResizeWithWorkload(74, 19);
                        steps.Add("shrink@15");
                        break;
                    case 15 when scenario is "grow":
                        terminal.ResizeWithWorkload(138, 37);
                        steps.Add("grow@15");
                        break;
                    case 71 when scenario is "shrink-grow-shrink":
                        terminal.ResizeWithWorkload(138, 37);
                        steps.Add("grow@71");
                        break;
                    case 119 when scenario is "shrink-grow-shrink":
                        terminal.ResizeWithWorkload(74, 19);
                        steps.Add("shrink@119");
                        break;
                    default:
                        return;
                }

                // Let the workload resize event reach the runner before the
                // commit resumes, so the next turn boundary observes it.
                await Task.Delay(150);
                DumpStep(terminal, index);
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
            if (scenario != "grow")
            {
                Assert.IsTrue(trace.Contains("reflow when=", StringComparison.Ordinal) && trace.Contains("width=74", StringComparison.Ordinal), $"commit never observed the shrink:\n{trace}");
            }
            if (scenario != "shrink")
            {
                Assert.IsTrue(trace.Contains("reflow when=", StringComparison.Ordinal) && trace.Contains("width=138", StringComparison.Ordinal), $"commit never observed the grow:\n{trace}");
            }

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

    [TestMethod]
    public async Task FaultAfterThreeUnits_PreservesReportedPrefixAndNeverReplays()
    {
        var source = new FenceCommitSource(CommitId, PayloadRows);
        FlowCommitException? failure = null;
        var secondCommitThrew = false;
        var framesAfterFault = 0;

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
                    framesAfterFault++;
                }

                step.Complete();
            }, width: 121, height: 30);

            await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

            Assert.IsNotNull(failure, "expected the injected emission failure");
            Assert.AreEqual(3, failure!.CompletedUnits);
            Assert.IsTrue(failure.MayHavePartialRow, "an IOException after a partial row must be reported as uncertain");
            Assert.IsTrue(secondCommitThrew, "commitment must stay suspended after an uncertain emission failure");
            Assert.AreEqual(5, framesAfterFault);

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
    /// the sake of a test. The row-level check and the non-cancellable post-unit
    /// drain are therefore covered by inspection, not by this fence.
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
        Assert.IsTrue(terminal.GetScreenText().Length > 0, "the live region must still be present after cancellation");
    }

    // === harness =============================================================

    private static Hex1bTerminal CreateTerminal(Func<Hex1bFlowContext, Task> flowCallback, int width, int height)
        => Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flowCallback, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = TimeSpan.FromMilliseconds(80);
            })
            .WithHeadless()
            .WithDimensions(width, height)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000)
            .Build();

    private static Task<Hex1bWidget> BuildLive(FlowStepContext ctx)
        => Task.FromResult<Hex1bWidget>(
            ctx.VStack(v =>
            [
                v.Text(LiveMarker),
                v.Text("committed rows leave this region"),
                v.Text("prompt stays usable across commitment"),
            ]));

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

    /// <summary>Temporary diagnostic: capture the buffer state at each transition.</summary>
    private static void DumpStep(Hex1bTerminal terminal, int index)
    {
        var buffer = ReadFullBuffer(terminal);
        var counts = new Dictionary<int, int>();
        foreach (Match match in Regex.Matches(buffer, Regex.Escape(CommitId) + @":r(\d+)(?![0-9])"))
        {
            var i = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            counts[i] = counts.GetValueOrDefault(i) + 1;
        }

        var present = counts.Keys.OrderBy(i => i).ToArray();
        var missing = Enumerable.Range(0, PayloadRows).Where(i => !counts.ContainsKey(i)).ToArray();
        File.WriteAllText(
            Path.Combine(Path.GetTempPath(), $"fence-step-{index}.txt"),
            $"present={present.Length} missing={string.Join(",", missing)}\n\n{buffer}");
        Console.WriteLine($"fence step {index}: present={present.Length} missing={missing.Length}");
    }

    private static void AssertKeysExactlyOnce(string buffer, string commitId, int payloadRows, string trace)
    {
        var counts = new Dictionary<int, int>();
        foreach (Match match in Regex.Matches(buffer, Regex.Escape(commitId) + @":r(\d+)(?![0-9])"))
        {
            var index = int.Parse(match.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            counts[index] = counts.GetValueOrDefault(index) + 1;
        }

        var missing = Enumerable.Range(0, payloadRows).Where(i => !counts.ContainsKey(i)).ToArray();
        if (missing.Length > 0 || counts.Any(kv => kv.Value > 1))
        {
            var dump = Path.Combine(Path.GetTempPath(), "continuous-history-fence-buffer.txt");
            File.WriteAllText(dump, buffer);
            Console.WriteLine($"fence buffer dumped to {dump}");
        }
        var duplicated = counts.Where(kv => kv.Value > 1).Select(kv => kv.Key).OrderBy(i => i).ToArray();

        Assert.AreEqual(
            0,
            missing.Length,
            $"missing payload keys: {string.Join(",", missing.Take(40))}{(missing.Length > 40 ? "..." : string.Empty)}\ncommit trace:\n{trace}");
        Assert.AreEqual(
            0,
            duplicated.Length,
            $"duplicated payload keys: {string.Join(",", duplicated.Take(40))}{(duplicated.Length > 40 ? "..." : string.Empty)}\ncommit trace:\n{trace}");
        Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape(commitId) + " BEGIN").Count, "BEGIN marker count");
        Assert.AreEqual(1, Regex.Matches(buffer, Regex.Escape(commitId) + " END").Count, "END marker count");
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
