namespace Hex1b.Flow;

/// <summary>
/// Pure helpers for the flow runner's resize handler. Extracted so the
/// behavioural choice between the legacy "clear the whole visible area" path
/// and the soft-wrap "clear only the active-step region" path can be unit
/// tested without spinning up a real terminal pipeline.
/// </summary>
internal static class FlowResizeMath
{
    /// <summary>
    /// Clamps the desired step height to the terminal's available rows.
    /// </summary>
    /// <param name="maxHeight">Caller-supplied max height (from
    /// <see cref="Hex1bFlowStepOptions.MaxHeight"/>); when <c>null</c> the
    /// step is allowed to fill the terminal.</param>
    /// <param name="terminalHeight">New terminal height in rows.</param>
    /// <returns>Step height in rows, never less than 1.</returns>
    public static int ComputeStepHeight(int? maxHeight, int terminalHeight)
    {
        var stepHeight = Math.Min(maxHeight ?? terminalHeight, terminalHeight);
        return stepHeight < 1 ? 1 : stepHeight;
    }

    /// <summary>
    /// Selects the rows that the resize handler should clear before the
    /// active step's app re-renders into the new region.
    /// </summary>
    /// <param name="useSoftWrapTombstones">The
    /// <see cref="Hex1bFlowOptions.UseSoftWrapTombstones"/> flag.</param>
    /// <param name="terminalHeight">New terminal height in rows.</param>
    /// <param name="stepHeight">New step height in rows (see
    /// <see cref="ComputeStepHeight(int?, int)"/>).</param>
    /// <returns>
    /// A <c>(rowOrigin, height)</c> tuple suitable for
    /// <c>ClearRegion(rowOrigin, height)</c>. Under the legacy path this is
    /// always <c>(0, terminalHeight)</c> — every row in the visible area gets
    /// cleared because cell-positioned tombstones cannot survive a resize
    /// anyway. The soft-wrap path no longer calls into this helper at all:
    /// the runner owns the resize repaint there (clears the viewport, redraws
    /// every tracked tombstone at the new width via
    /// <c>SoftWrapEmitter</c>, then re-anchors the active step). The
    /// soft-wrap branch in this method is retained only for tests and
    /// possible future reuse — production code on the soft-wrap path
    /// bypasses it.
    /// </returns>
    public static (int RowOrigin, int Height) ComputeClearRegion(
        bool useSoftWrapTombstones,
        int terminalHeight,
        int stepHeight)
    {
        if (useSoftWrapTombstones)
        {
            var rowOrigin = Math.Max(0, terminalHeight - stepHeight);
            return (rowOrigin, stepHeight);
        }
        return (0, terminalHeight);
    }

    /// <summary>
    /// Computes the display row where the active step's top will sit, given
    /// the per-paragraph logical widths of every tombstone emitted above the
    /// active step and the current terminal width.
    /// </summary>
    /// <param name="initialRowOrigin">The row at which the very first
    /// tombstone (or active step, if none) starts. Captured at flow start
    /// and never changes for the life of the flow.</param>
    /// <param name="tombstoneParagraphWidths">For each tombstone, in
    /// emission order, the logical width (in cells) of each CR+LF-terminated
    /// paragraph it contains. Hard-newline-terminated paragraphs never merge
    /// across resize on any surveyed emulator, so we only need their widths
    /// to recompute reflow at any terminal width.</param>
    /// <param name="terminalWidth">Current terminal width in cells. Must be
    /// at least 1.</param>
    /// <returns>The 0-based display row where the active step begins after
    /// the host terminal reflows the tombstones above it at the given
    /// width.</returns>
    /// <remarks>
    /// This is the core primitive the resize handler relies on: it lets the
    /// runner answer "where should I move the cursor before erasing the
    /// active-step region?" without round-tripping a CPR query to the
    /// terminal. Its correctness is pinned by the feasibility tests in
    /// <c>FlowResizeRowOriginFeasibilityTests</c>, which drive the same
    /// inputs through a reference reflow simulator and assert agreement.
    /// </remarks>
    public static int ComputeRowOriginAtWidth(
        int initialRowOrigin,
        IReadOnlyList<IReadOnlyList<int>> tombstoneParagraphWidths,
        int terminalWidth)
    {
        if (terminalWidth < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(terminalWidth),
                terminalWidth, "terminalWidth must be at least 1");
        }

        var rows = initialRowOrigin;
        foreach (var tombstone in tombstoneParagraphWidths)
        {
            foreach (var paragraphWidth in tombstone)
            {
                if (paragraphWidth < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(tombstoneParagraphWidths), paragraphWidth,
                        "paragraph widths must be non-negative");
                }

                rows += Math.Max(1, (paragraphWidth + terminalWidth - 1) / terminalWidth);
            }
        }
        return rows;
    }

    /// <summary>
    /// Computes the row where new finalized content continues after the host
    /// terminal re-wrapped its buffer for a new geometry.
    /// </summary>
    /// <param name="initialRowOrigin">The row at which the flow's first
    /// paragraph started (see <see cref="ComputeRowOriginAtWidth"/>). Rows above
    /// it belong to earlier output and stay above the flow's content through a
    /// reflow, so they shift the screen row the content lands on.</param>
    /// <param name="paragraphWidths">Every paragraph the flow has emitted, in
    /// emission order, as logical (pre-wrap) widths in display cells — the same
    /// model <see cref="ComputeRowOriginAtWidth"/> consumes.</param>
    /// <param name="oldWidth">Terminal width the paragraphs were emitted at.</param>
    /// <param name="newWidth">Terminal width after the change.</param>
    /// <param name="newHeight">Terminal height after the change.</param>
    /// <param name="cursorScreenRow">The cursor's screen row before the change.</param>
    /// <param name="cursorColumn">The cursor's column before the change.</param>
    /// <param name="cursorBelowContent">True when the cursor sits on a row below
    /// the last content row (the live region's own top row) rather than on the
    /// last content row itself.</param>
    /// <returns>The screen row where the next content row belongs. This may be
    /// one past the last row when the content already reaches the bottom; the
    /// caller scrolls before writing.</returns>
    /// <remarks>
    /// <para>
    /// This mirrors the host reflow rule the reflow strategies in
    /// <c>Hex1b.Reflow</c> implement (VTE/Ghostty: the cursor's visual row is
    /// preserved and content above it is pushed into scrollback as needed).
    /// The computation stays in content coordinates, so it remains correct when
    /// the host scrolled rows the framework never asked for — the drift an
    /// absolute row counter cannot detect.
    /// </para>
    /// <para>
    /// The live region's own rows are deliberately not part of the model: they
    /// are rewritten on every turn, so treating the content as ending at its
    /// last committed row keeps the answer an upper bound — content is never
    /// written above the true tail.
    /// </para>
    /// </remarks>
    public static int ComputeAppendRowAfterReflow(
        int initialRowOrigin,
        IReadOnlyList<IReadOnlyList<int>> paragraphWidths,
        int oldWidth,
        int newWidth,
        int newHeight,
        int cursorScreenRow,
        int cursorColumn,
        bool cursorBelowContent)
    {
        if (oldWidth < 1) throw new ArgumentOutOfRangeException(nameof(oldWidth), oldWidth, "oldWidth must be at least 1");
        if (newWidth < 1) throw new ArgumentOutOfRangeException(nameof(newWidth), newWidth, "newWidth must be at least 1");
        if (newHeight < 1) throw new ArgumentOutOfRangeException(nameof(newHeight), newHeight, "newHeight must be at least 1");

        var contentRowsNew = 0;
        var rowsBeforeLast = 0;
        var lastParagraphRows = 0;
        var paragraphCount = 0;

        foreach (var tombstone in paragraphWidths)
        {
            foreach (var paragraphWidth in tombstone)
            {
                if (paragraphWidth < 0)
                {
                    throw new ArgumentOutOfRangeException(
                        nameof(paragraphWidths), paragraphWidth, "paragraph widths must be non-negative");
                }

                var rows = Math.Max(1, (paragraphWidth + newWidth - 1) / newWidth);
                rowsBeforeLast = contentRowsNew;
                contentRowsNew += rows;
                lastParagraphRows = rows;
                paragraphCount++;
            }
        }

        if (paragraphCount == 0)
        {
            return Math.Max(0, initialRowOrigin);
        }

        // Rows above the flow's first paragraph are unaffected by the reflow and
        // remain above the content, so they shift every content row.
        var leadRows = Math.Max(0, initialRowOrigin);

        int cursorRowInContent;
        if (cursorBelowContent)
        {
            // The cursor sits one row below the content (the live region's top),
            // which keeps that row part of the reflowed buffer.
            cursorRowInContent = contentRowsNew;
        }
        else
        {
            var rowInLastParagraph = Math.Clamp(cursorColumn / newWidth, 0, lastParagraphRows - 1);
            cursorRowInContent = rowsBeforeLast + rowInLastParagraph;
        }

        // The host anchors the cursor's visual row and scrolls everything above
        // it into scrollback as needed, so the content's start row follows from
        // the cursor's row rather than from the old absolute position.
        var cursorRowIncludingLead = leadRows + cursorRowInContent;
        var desiredCursorScreenRow = Math.Clamp(cursorScreenRow, 0, newHeight - 1);
        // Only reflowed content rows take part: the live region's own rows are
        // rewritten every turn and including them would widen the clamp enough
        // to move the anchor above the content's true tail.
        var contentRowCount = Math.Max(leadRows + contentRowsNew, cursorRowIncludingLead + 1);
        var maxScreenStart = Math.Max(0, contentRowCount - newHeight);
        var screenStart = Math.Clamp(leadRows + cursorRowInContent - desiredCursorScreenRow, 0, maxScreenStart);

        var lastContentRow = leadRows + contentRowsNew - 1 - screenStart;
        var cursorScreenRowAfter = cursorRowIncludingLead - screenStart;
        var appendRow = Math.Max(lastContentRow + 1, cursorBelowContent ? cursorScreenRowAfter : lastContentRow + 1);

        // Deliberately NOT clamped to newHeight - 1: when the content reaches
        // the bottom row the append position is one past it, and callers rely
        // on seeing that to scroll before writing. Clamping here would make a
        // write land on the content's last row.
        return Math.Max(0, appendRow);
    }
}
