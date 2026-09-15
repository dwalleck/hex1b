using System.Text;
using System.Text.RegularExpressions;
using Hex1b;
using Hex1b.Flow;
using Hex1b.Reflow;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Regression fence for resize ownership across a continuous-history commit.
/// </summary>
/// <remarks>
/// A resize must retain the history above the live region, render edits before
/// delayed cleanup, and relinquish cleanup ownership when a commit is admitted.
/// <para>
/// Both cases are reduced from the retained angleC probes. The oracle is the
/// terminal model's full buffer (scrollback + screen) — the same export the
/// native runs use — and every payload key plus both markers must be present
/// exactly once afterwards.
/// </para>
/// </remarks>
[DoNotParallelize]
[TestClass]
public class FlowCommitResizeOwnershipTests
{
    private const int PayloadRows = 40;
    private const string CommitId = "rz-k0001";
    private const string LiveMarker = "RZ-LIVE-PROMPT";

    [TestMethod]
    public async Task PostCommitResize_RetainsCommittedRowsAndMarkers()
    {
        FlowCommitResult? result = null;
        string? before = null;
        string? after = null;

        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            var step = flow.Step(BuildLive, options =>
            {
                options.MinHeight = 14;
                options.MaxHeight = 14;
            });
            await step.WaitForReadyAsync();
            try
            {
                result = await step.CommitAsync(new RowCommitSource(CommitId, PayloadRows), BuildLive);

                // The commit has repainted the live region below its rows.
                await Task.Delay(300);
                before = ReadFullBuffer(terminal);

                // Shrink: the host re-wraps the committed rows and the settle
                // pass must repaint only the live region.
                _ = terminal.ResizeWithWorkloadAsync(100, 30);
                await Task.Delay(600);
                after = ReadFullBuffer(terminal);
            }
            finally
            {
                step.Complete();
            }
        }, width: 121, height: 30);

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsNotNull(result, "the commit must complete");
        Assert.AreEqual(PayloadRows + 2, result!.CompletedUnits);

        // Before the resize is asserted too: if the commit itself were already
        // lossy the "after" failure would be misattributed to the resize.
        AssertCommittedExactlyOnce(before!, "before resize");
        AssertCommittedExactlyOnce(after!, "after post-commit resize");
        Assert.IsTrue(
            after!.Contains(LiveMarker, StringComparison.Ordinal),
            $"the live region must be repainted after the post-commit resize\n{after}");
    }

    /// <summary>
    /// Height-only resize after the commit: the width — and therefore the
    /// reflowed row the settle pass computes — is unchanged, so the only thing
    /// that can move the live rectangle is the origin the commit re-anchored.
    /// A scratch origin captured when the step started makes the union clear
    /// span every committed row between the two rectangles.
    /// </summary>
    [TestMethod]
    public async Task PostCommitHeightOnlyResize_RetainsCommittedRowsAndMarkers()
    {
        FlowCommitResult? result = null;
        string? before = null;
        string? after = null;

        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            var step = flow.Step(BuildLive, options =>
            {
                options.MinHeight = 14;
                options.MaxHeight = 14;
            });
            await step.WaitForReadyAsync();
            try
            {
                result = await step.CommitAsync(new RowCommitSource(CommitId, PayloadRows), BuildLive);
                await Task.Delay(300);
                before = ReadFullBuffer(terminal);

                // Same width, shorter viewport: the host does not re-wrap, and
                // the settled row origin is derived from the authoritative one.
                _ = terminal.ResizeWithWorkloadAsync(121, 24);
                await Task.Delay(600);
                after = ReadFullBuffer(terminal);
            }
            finally
            {
                step.Complete();
            }
        }, width: 121, height: 30);

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(60));

        Assert.IsNotNull(result, "the commit must complete");
        Assert.AreEqual(PayloadRows + 2, result!.CompletedUnits);
        AssertCommittedExactlyOnce(before!, "before height-only resize");
        AssertCommittedExactlyOnce(after!, "after post-commit height-only resize");
        Assert.IsTrue(
            after!.Contains(LiveMarker, StringComparison.Ordinal),
            $"the live region must be repainted after the post-commit height-only resize\n{after}");
    }

    [TestMethod]
    public async Task DuringResizeBurst_UpdatedPromptRendersBeforeSettleAndHistorySurvives()
    {
        var liveText = LiveMarker;
        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            Task<Hex1bWidget> Live(FlowStepContext ctx) =>
                Task.FromResult<Hex1bWidget>(ctx.Text(liveText));
            var step = flow.Step(Live, options => options.MaxHeight = 14);
            await step.WaitForReadyAsync();
            try
            {
                var committed = await step.CommitAsync(
                    new RowCommitSource(CommitId, PayloadRows), Live);
                Assert.AreEqual(PayloadRows + 2, committed.CompletedUnits);

                // The deliberately long cleanup delay cannot supply these
                // changed frames. This is a liveness fence, not native timing proof.
                foreach (var width in new[] { 100, 85, 121 })
                {
                    _ = terminal.ResizeWithWorkloadAsync(width, 30);
                    liveText = $"{LiveMarker}-EDIT-{width}";
                    step.Invalidate();
                    Assert.IsTrue(
                        await WaitForScreenTextAsync(terminal, liveText, TimeSpan.FromSeconds(2)),
                        $"edited prompt must render during the resize burst\n{ScreenText(terminal)}");
                    AssertCommittedExactlyOnce(ReadFullBuffer(terminal), $"resize to {width}");
                    Assert.AreEqual(
                        1, Regex.Matches(ScreenText(terminal), Regex.Escape(liveText)).Count,
                        "resize must not duplicate the mutable prompt");
                }
            }
            finally
            {
                step.Complete();
            }
        }, width: 121, height: 30, settleDelay: TimeSpan.FromSeconds(10));

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    [TestMethod]
    public async Task CommitDuringResizeCleanup_PreservesCommittedRowsAndPrompt()
    {
        var settleDelay = TimeSpan.FromSeconds(2);
        var source = new RowCommitSource(CommitId, PayloadRows) { DelayMs = 60 };
        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = CreateTerminal(async flow =>
        {
            Task<Hex1bWidget> Live(FlowStepContext ctx) =>
                Task.FromResult<Hex1bWidget>(ctx.Text($"{LiveMarker}-WIDTH-{ctx.Step.TerminalWidth}"));
            var step = flow.Step(Live, options => options.MaxHeight = 14);
            await step.WaitForReadyAsync();
            try
            {
                var resizeStarted = System.Diagnostics.Stopwatch.GetTimestamp();
                _ = terminal.ResizeWithWorkloadAsync(100, 30);
                Assert.IsTrue(
                    await WaitForScreenTextAsync(terminal, $"{LiveMarker}-WIDTH-100", TimeSpan.FromSeconds(1)),
                    "the resized live frame must be visible before committing");
                Assert.IsTrue(
                    System.Diagnostics.Stopwatch.GetElapsedTime(resizeStarted) < settleDelay,
                    "commit admission must precede the pending cleanup deadline");

                var committed = await step.CommitAsync(source, Live);
                Assert.AreEqual(PayloadRows + 2, committed.CompletedUnits);
                AssertCommittedExactlyOnce(ReadFullBuffer(terminal), "commit across pending resize cleanup");
                Assert.Contains($"{LiveMarker}-WIDTH-100", ScreenText(terminal));
            }
            finally
            {
                step.Complete();
            }
        }, width: 121, height: 30, settleDelay: settleDelay);

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    // === harness =============================================================

    private static Hex1bTerminal CreateTerminal(
        Func<Hex1bFlowContext, Task> flowCallback,
        int width,
        int height,
        TimeSpan? settleDelay = null)
        => Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flowCallback, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = settleDelay ?? TimeSpan.FromMilliseconds(80);
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

    private static string ScreenText(Hex1bTerminal terminal) => terminal.GetScreenText();

    private static async Task<bool> WaitForScreenTextAsync(
        Hex1bTerminal terminal,
        string text,
        TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            if (ScreenText(terminal).Contains(text, StringComparison.Ordinal))
            {
                return true;
            }

            await Task.Delay(10).ConfigureAwait(false);
        }

        return ScreenText(terminal).Contains(text, StringComparison.Ordinal);
    }

    private static void AssertCommittedExactlyOnce(string buffer, string when)
    {
        var missing = new List<int>();
        var duplicated = new List<int>();
        for (var row = 0; row < PayloadRows; row++)
        {
            var count = Regex.Matches(buffer, Regex.Escape($"{CommitId}:r{row:00} ")).Count;
            if (count == 0)
            {
                missing.Add(row);
            }
            else if (count > 1)
            {
                duplicated.Add(row);
            }
        }

        Assert.AreEqual(
            0,
            missing.Count,
            $"[{when}] missing payload keys: {Summarize(missing)}\n{buffer}");
        Assert.AreEqual(
            0,
            duplicated.Count,
            $"[{when}] duplicated payload keys: {Summarize(duplicated)}\n{buffer}");
        Assert.AreEqual(
            1,
            Regex.Matches(buffer, Regex.Escape($"COMMIT {CommitId} BEGIN")).Count,
            $"[{when}] BEGIN marker count\n{buffer}");
        Assert.AreEqual(
            1,
            Regex.Matches(buffer, Regex.Escape($"COMMIT {CommitId} END")).Count,
            $"[{when}] END marker count\n{buffer}");
    }

    private static string Summarize(List<int> rows)
        => rows.Count == 0
            ? "(none)"
            : string.Join(",", rows.Take(40)) + (rows.Count > 40 ? "..." : string.Empty);

    /// <summary>
    /// Immutable commit units mirroring the angleC repro: a BEGIN unit, one unit
    /// per payload row carrying the stable key <c>{commitId}:r{index:00}</c>, and
    /// an END unit. <see cref="DelayMs"/> bounds how long the commit outlasts a
    /// settle debounce window.
    /// </summary>
    private sealed class RowCommitSource(string commitId, int payloadRows) : FlowCommitSource
    {
        public int DelayMs { get; set; }

        public override int UnitCount => payloadRows + 2;

        public override async Task<FlowCommitUnit> UnitAsync(
            int index,
            int width,
            CancellationToken cancellationToken)
        {
            if (DelayMs > 0)
            {
                await Task.Delay(DelayMs, cancellationToken).ConfigureAwait(false);
            }

            var columns = Math.Max(1, width);
            string key;
            string text;
            if (index == 0)
            {
                key = $"{commitId}:begin";
                text = $"<<<COMMIT {commitId} BEGIN width={columns}>>>";
            }
            else if (index >= UnitCount - 1)
            {
                key = $"{commitId}:end";
                text = $"<<<COMMIT {commitId} END>>>";
            }
            else
            {
                var row = index - 1;
                key = $"r{row:00}";
                text = $"{commitId}:r{row:00} payload row {row:000} ";
            }

            var surface = new Surface(columns, 1);
            surface.WriteText(0, 0, text.Length > columns ? text[..columns] : text);
            return new FlowCommitUnit(key, surface);
        }
    }
}
