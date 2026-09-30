using Hex1b.Diagnostics;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // A command mark's position in the model-state projection, and its restore as a text anchor. One coordinate space
    // serves both: the buffer (main or alternate), a row, and the anchor's raw column. Main rows are the retained history
    // rows (oldest first) followed by the main screen's rows (the saved main screen while the alternate screen is
    // active); alternate rows are the alternate screen's. A mark whose anchor has lost its row (awaiting collection)
    // has no row and no column. Projection never resolves or collects anchors, so taking it changes nothing.

    /// <summary>The retained command marks in order, each with its position. Must hold <c>_bufferLock</c>.</summary>
    private DiagnosticModelCommandMark[] ProjectCommandMarksUnsafe()
    {
        var marks = new DiagnosticModelCommandMark[_commandMarks.Count];
        if (marks.Length == 0)
            return marks;

        var historyCount = _scrollbackBuffer?.Count ?? 0;
        var mainScreen = RowIndex(_inAlternateScreen ? _savedMainTextRowIds ?? [] : _textScreenRowIds);
        var alternateScreen = RowIndex(_inAlternateScreen ? _textScreenRowIds : []);
        Dictionary<long, int>? historyRows = null;
        for (var i = 0; i < marks.Length; i++)
        {
            var mark = _commandMarks[i];
            var anchor = _commandAnchors[mark];
            int? row = null;
            if (anchor.RowId is long id)
            {
                if (anchor.Alternate)
                    row = alternateScreen.TryGetValue(id, out var screenRow) ? screenRow : null;
                else if (mainScreen.TryGetValue(id, out var mainRow))
                    row = historyCount + mainRow;
                else
                    row = (historyRows ??= HistoryRowsByTextRowIdUnsafe()).TryGetValue(id, out var historyRow) ? historyRow : null;
            }
            marks[i] = new DiagnosticModelCommandMark
            {
                Anchor = anchor.Id,
                Phase = ModelStateName(mark.Phase.ToString()),
                ExitCode = mark.ExitCode,
                RawParameters = mark.RawParameters,
                Buffer = anchor.Alternate ? "alternate" : "main",
                Row = row,
                Column = row is null ? null : anchor.Column,
            };
        }
        return marks;
    }

    // Each screen row's index, keyed by its text row id (row ids are unique).
    private static Dictionary<long, int> RowIndex(long[] rowIds)
    {
        var result = new Dictionary<long, int>(rowIds.Length);
        for (var row = 0; row < rowIds.Length; row++)
            result.TryAdd(rowIds[row], row);
        return result;
    }

    // The ring index of each retained history row that has a text row id (assigned when its text was read or a mark
    // was placed on it), keyed by that id.
    private Dictionary<long, int> HistoryRowsByTextRowIdUnsafe()
    {
        var result = new Dictionary<long, int>();
        if (_scrollbackBuffer is not { Count: > 0 } history)
            return result;
        var textRowIds = _inAlternateScreen ? _savedMainTextHistoryRowIds : _textHistoryRowIds;
        if (textRowIds is not { Count: > 0 })
            return result;
        for (var row = 0; row < history.Count; row++)
        {
            if (textRowIds.TryGetValue(history.GetEntryAt(row).RowId, out var id))
                result[id] = row;
        }
        return result;
    }

    /// <summary>
    /// Why a projection's command marks cannot be restored, or null. They must name known phases and anchors
    /// <c>command:&lt;n&gt;</c> ascending and no later than the last anchor id, and each position must be within its
    /// buffer's rows and its row's width (a mark whose text is gone has neither row nor column).
    /// </summary>
    private static string? CommandMarksProblem(DiagnosticModelState state)
    {
        if (state.CommandMarks is not { } marks)
            return "command marks: missing";
        if (state.LastCommandAnchorId is < 0 or long.MaxValue)
            return $"a last command anchor id {state.LastCommandAnchorId} that is not 0 to {long.MaxValue - 1}";
        var historyRows = state.History?.Rows ?? [];
        var mainScreen = state.ActiveBuffer == "alternate" ? state.SavedMainScreen ?? [] : state.Screen;
        long previous = 0;
        for (var i = 0; i < marks.Count; i++)
        {
            var mark = marks[i];
            var at = $"command mark {i}";
            if (mark is null)
                return $"{at}: missing";
            if (!TryParseCommandAnchor(mark.Anchor, out var number) || number <= previous || number > state.LastCommandAnchorId)
                return $"{at}: anchor '{mark.Anchor}' is not command:<n> ascending to at most {state.LastCommandAnchorId}";
            previous = number;
            if (!TryParseName<TerminalShellIntegrationPhase>(mark.Phase, out _))
                return $"{at}: unknown phase '{mark.Phase}'";
            if (mark.Buffer is not ("main" or "alternate"))
                return $"{at}: unknown buffer '{mark.Buffer}'";
            if (mark.Row is null != mark.Column is null)
                return $"{at}: a position needs both its row and its column";
            if (mark.Row is not int row || mark.Column is not int column)
                continue;
            int? width;
            // Rows are validated before marks (history by HistoryProblem, screens by UnrestorableField); a missing row
            // still counts as outside its buffer.
            if (mark.Buffer == "alternate")
                width = state.ActiveBuffer == "alternate" && row >= 0 && row < state.Screen.Count ? state.Screen[row]?.Cells?.Count : null;
            else if (row >= 0 && row < historyRows.Count)
                width = historyRows[row]?.Cells?.Count;
            else
                width = row >= historyRows.Count && row - historyRows.Count < mainScreen.Count ? mainScreen[row - historyRows.Count]?.Cells?.Count : null;
            if (width is not int rowWidth)
                return $"{at}: row {row} is outside the {mark.Buffer} buffer";
            if (column < 0 || column > rowWidth)
                return $"{at}: column {column} is outside row {row}'s width {rowWidth}";
        }
        return null;
    }

    /// <summary>
    /// Restores command marks as text anchors at their positions, in order, continuing the anchor numbering; last,
    /// after the screens and history. A history row gains a text row id when it has none. Must hold <c>_bufferLock</c>.
    /// </summary>
    private void RestoreCommandMarksUnsafe(IReadOnlyList<DiagnosticModelCommandMark> marks, long lastCommandAnchorId)
    {
        if (marks.Count > 0)
            EnsureTextRows();
        foreach (var projected in marks)
        {
            TryParseName<TerminalShellIntegrationPhase>(projected.Phase, out var phase);
            var alternate = projected.Buffer == "alternate";
            long? rowId = null;
            var inHistory = false;
            if (projected.Row is int row)
            {
                var historyCount = _scrollbackBuffer?.Count ?? 0;
                if (alternate)
                    rowId = _textScreenRowIds[row];
                else if (row >= historyCount)
                    rowId = (_inAlternateScreen ? _savedMainTextRowIds! : _textScreenRowIds)[row - historyCount];
                else
                {
                    var textRowIds = _inAlternateScreen ? _savedMainTextHistoryRowIds! : _textHistoryRowIds;
                    var historyId = _scrollbackBuffer!.GetEntryAt(row).RowId;
                    if (!textRowIds.TryGetValue(historyId, out var id))
                        textRowIds[historyId] = id = checked(++_nextTextRowId);
                    rowId = id;
                    inHistory = true;
                }
            }

            var mark = new TerminalCommandMark(phase, projected.ExitCode, projected.RawParameters, _textGeneration, rowId ?? 0);
            var anchor = new TerminalTextAnchor(projected.Anchor, alternate, rowId ?? 0, projected.Column ?? 0);
            if (rowId is null)
            {
                // Its text is gone: the next collection removes it, as it does the original's.
                anchor.RowId = null;
                InvalidateTextAnchorRetention();
            }
            _textAnchors.Add(anchor);
            if (inHistory)
                _historyTextAnchors.Add(anchor);
            _commandAnchors.Add(mark, anchor);
            _commandMarks.Add(mark);
        }
        _nextCommandAnchorId = lastCommandAnchorId;
    }

    private static bool TryParseCommandAnchor(string? anchor, out long number)
    {
        number = 0;
        return anchor is not null && anchor.StartsWith("command:", StringComparison.Ordinal)
            && long.TryParse(anchor.AsSpan(8), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out number)
            && number > 0;
    }
}
