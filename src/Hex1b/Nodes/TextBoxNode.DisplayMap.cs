using System.Text;
using Hex1b.Input;
using Hex1b.Theming;

namespace Hex1b;

/// <summary>
/// The display-map path of <see cref="TextBoxNode"/> (issue 62): rendering, hit-testing and vertical movement of a
/// text box whose graphemes are shown as stand-ins. Every column comes from <see cref="TextBoxDisplayLayout"/>; the
/// buffer is never changed here. Without a map none of this runs.
/// </summary>
public sealed partial class TextBoxNode
{
    private Func<string, string?>? _displayMap;
    private TextBoxDisplayLayout? _displayLayout;

    /// <summary>
    /// The grapheme display map from <see cref="Widgets.TextBoxWidget.DisplayMap(System.Func{string, string?})"/>, or null for the default rendering.
    /// </summary>
    internal Func<string, string?>? DisplayMap
    {
        get => _displayMap;
        set
        {
            if (Equals(_displayMap, value)) return; // delegate equality: same target and method keeps the layout
            _displayMap = value;
            _displayLayout = null;
            MarkDirty();
        }
    }

    /// <summary>The layout of the current buffer under <paramref name="map"/>, rebuilt only when the buffer, map or mode changed.</summary>
    private TextBoxDisplayLayout GetDisplayLayout(Func<string, string?> map)
    {
        var text = State.Text;
        if (_displayLayout is null || !_displayLayout.IsFor(text, map, IsMultiline))
            _displayLayout = TextBoxDisplayLayout.Build(text, map, IsMultiline);
        return _displayLayout;
    }

    /// <summary>Up/Down: the default char-column movement, or the display-column movement of rule 9 when mapped.</summary>
    private void MoveVertical(int direction, bool extend)
    {
        if (DisplayMap is not { } map)
        {
            if (direction < 0) State.MoveUp(extend);
            else State.MoveDown(extend);
            return;
        }

        var layout = GetDisplayLayout(map);
        var cursor = State.CursorPosition;
        var lineIndex = layout.LineIndexOf(cursor);
        var targetIndex = lineIndex + direction;
        if (targetIndex < 0 || targetIndex >= layout.Lines.Length) return;

        if (extend && !State.SelectionAnchor.HasValue)
            State.SelectionAnchor = cursor;
        else if (!extend)
            State.ClearSelection();

        var line = layout.Lines[lineIndex];
        State.PreferredDisplayColumn ??= line.Columns[line.IndexOf(cursor)];
        var target = layout.Lines[targetIndex];
        State.CursorPosition = TextBoxDisplayLayout.HitTest(target, 0, target.Units.Length, State.PreferredDisplayColumn.Value);
    }

    /// <summary>
    /// The units drawn from <paramref name="origin"/> in a viewport: whole units while they fit (rule 4), or the first
    /// unit alone, cut to the viewport, when even it does not fit (rule 5).
    /// </summary>
    private static (int End, bool Cut) DrawnUnits(TextBoxDisplayLayout.Line line, int origin, int limit, int viewportWidth)
    {
        if (viewportWidth <= 0) return (limit, false);
        var end = Math.Min(limit, TextBoxDisplayLayout.FitEnd(line, origin, viewportWidth));
        return end == origin && origin < limit ? (origin + 1, true) : (end, false);
    }

    /// <summary>Rule 4/6: the first unit drawn on a row, scrolled only for the caret's row when the caret reaches the edge.</summary>
    private static int RowOrigin(TextBoxDisplayLayout.Line line, TextBoxDisplayLayout.Row row, int cursor, bool scrolls, int viewportWidth)
    {
        if (!scrolls || viewportWidth <= 0) return row.First;
        var rowStart = line.OffsetOf(row.First);
        var rowEnd = line.OffsetOf(row.End);
        if (cursor < rowStart || cursor > rowEnd) return row.First;
        var caret = line.IndexOf(cursor);
        return line.Columns[caret] - line.Columns[row.First] >= viewportWidth
            ? TextBoxDisplayLayout.ScrollOrigin(line, row.First, caret, viewportWidth)
            : row.First;
    }

    private InputResult HandleMouseClickMapped(Func<string, string?> map, int localX, int localY)
    {
        var layout = GetDisplayLayout(map);
        var viewportWidth = Bounds.Width;
        int offset;
        if (!IsMultiline)
        {
            var line = layout.Lines[0];
            var origin = line.IndexOf(Math.Clamp(ScrollOffset, 0, State.Text.Length));
            var (end, _) = DrawnUnits(line, origin, line.Units.Length, viewportWidth);
            offset = TextBoxDisplayLayout.HitTest(line, origin, end, Math.Max(0, localX));
        }
        else
        {
            var rows = layout.GetRows(viewportWidth, IsWordWrap);
            var rowIndex = VerticalScrollOffset + localY;
            if (rowIndex >= rows.Length)
            {
                offset = State.Text.Length;
            }
            else
            {
                var row = rows[rowIndex];
                var line = layout.Lines[row.LineIndex];
                var origin = RowOrigin(line, row, State.CursorPosition, !IsWordWrap, viewportWidth);
                var (end, _) = DrawnUnits(line, origin, row.End, viewportWidth);
                offset = TextBoxDisplayLayout.HitTest(line, origin, end, Math.Max(0, localX));
            }
        }

        State.ClearSelection();
        State.CursorPosition = offset;
        State.ResetPreferredColumn();
        MarkDirty();
        return InputResult.Handled;
    }

    private void RenderMapped(Hex1bRenderContext context, Func<string, string?> map, string globalColors,
        string resetToGlobal, Hex1bColor fillBg, Hex1bColor selFg, Hex1bColor selBg,
        Hex1bColor predictionFg, Hex1bColor predictionBg)
    {
        var layout = GetDisplayLayout(map);
        var fillBgAnsi = fillBg.ToBackgroundAnsi();
        var selectionAnsi = $"{selFg.ToForegroundAnsi()}{selBg.ToBackgroundAnsi()}";
        var selStart = State.HasSelection ? State.SelectionStart : -1;
        var selEnd = State.HasSelection ? State.SelectionEnd : -1;
        var cursor = State.CursorPosition;
        var viewportWidth = Bounds.Width;

        if (!IsMultiline)
        {
            var line = layout.Lines[0];
            var caret = line.IndexOf(cursor);
            var origin = viewportWidth > 0 && line.Width > viewportWidth
                ? TextBoxDisplayLayout.ScrollOrigin(line, line.IndexOf(Math.Clamp(ScrollOffset, 0, State.Text.Length)), caret, viewportWidth)
                : 0;
            ScrollOffset = line.OffsetOf(origin);
            var (end, cut) = DrawnUnits(line, origin, line.Units.Length, viewportWidth);
            var (before, selected, after, width, anySelected) = Pieces(line, origin, end, cut, viewportWidth, selStart, selEnd);

            string? prediction = null;
            if (IsFocused && !string.IsNullOrEmpty(CurrentPrediction) && !State.HasSelection
                && cursor == State.Text.Length && end == line.Units.Length && viewportWidth - width > 0)
            {
                prediction = ShownPrediction(map, CurrentPrediction!, before + selected + after, viewportWidth - width);
            }
            var predictionWidth = prediction is null ? 0 : DisplayWidth.GetStringWidth(prediction);
            var padding = new string(' ', Math.Max(0, viewportWidth - width - predictionWidth));

            string output;
            if (IsFocused && anySelected)
            {
                output = $"{globalColors}{fillBgAnsi}{before}{selectionAnsi}{selected}{resetToGlobal}{fillBgAnsi}{after}{padding}{resetToGlobal}";
            }
            else if (IsFocused)
            {
                ScreenCursorX = Bounds.X + Math.Min(width, line.Columns[Math.Max(caret, origin)] - line.Columns[origin]);
                ScreenCursorY = Bounds.Y;
                output = prediction is null
                    ? $"{globalColors}{fillBgAnsi}{before}{selected}{after}{padding}{resetToGlobal}"
                    : $"{globalColors}{fillBgAnsi}{before}{selected}{after}{predictionFg.ToForegroundAnsi()}{predictionBg.ToBackgroundAnsi()}{prediction}{resetToGlobal}{fillBgAnsi}{padding}{resetToGlobal}";
            }
            else
            {
                output = $"{globalColors}{fillBgAnsi}{before}{selected}{after}{padding}{resetToGlobal}";
            }
            WriteRow(context, Bounds.Y, output);
            return;
        }

        var viewportHeight = Bounds.Height;
        if (viewportWidth <= 0 || viewportHeight <= 0) return;

        var rows = layout.GetRows(viewportWidth, IsWordWrap);
        var cursorRow = TextBoxDisplayLayout.RowIndexOf(layout, rows, cursor);
        VerticalScrollOffset = Math.Clamp(VerticalScrollOffset, 0, Math.Max(0, rows.Length - 1));
        if (cursorRow < VerticalScrollOffset)
            VerticalScrollOffset = cursorRow;
        else if (cursorRow >= VerticalScrollOffset + viewportHeight)
            VerticalScrollOffset = cursorRow - viewportHeight + 1;

        for (var screenRow = 0; screenRow < viewportHeight; screenRow++)
        {
            var screenY = Bounds.Y + screenRow;
            var rowIndex = VerticalScrollOffset + screenRow;
            if (rowIndex >= rows.Length)
            {
                WriteRow(context, screenY, $"{globalColors}{fillBgAnsi}{new string(' ', viewportWidth)}{resetToGlobal}");
                continue;
            }

            var row = rows[rowIndex];
            var line = layout.Lines[row.LineIndex];
            var rowStart = line.OffsetOf(row.First);
            var rowEnd = line.OffsetOf(row.End);
            var cursorOnRow = cursor >= rowStart && cursor <= rowEnd;
            var origin = RowOrigin(line, row, cursor, !IsWordWrap, viewportWidth);
            var (end, cut) = DrawnUnits(line, origin, row.End, viewportWidth);
            var (before, selected, after, width, anySelected) = Pieces(line, origin, end, cut, viewportWidth, selStart, selEnd);
            var padding = new string(' ', Math.Max(0, viewportWidth - width));

            string output;
            if (anySelected)
            {
                output = $"{globalColors}{fillBgAnsi}{before}{selectionAnsi}{selected}{resetToGlobal}{fillBgAnsi}{after}{padding}{resetToGlobal}";
            }
            else
            {
                if (IsFocused && cursorOnRow)
                {
                    ScreenCursorX = Bounds.X + Math.Min(width, line.Columns[Math.Max(line.IndexOf(cursor), origin)] - line.Columns[origin]);
                    ScreenCursorY = screenY;
                }
                output = $"{globalColors}{fillBgAnsi}{before}{after}{padding}{resetToGlobal}";
            }
            WriteRow(context, screenY, output);
        }
    }

    /// <summary>
    /// The shown text of units [origin, end) split around the selection (rule 10: a unit is selected when its range
    /// overlaps [selStart, selEnd)), and the drawn width.
    /// </summary>
    private static (string Before, string Selected, string After, int Width, bool AnySelected) Pieces(TextBoxDisplayLayout.Line line,
        int origin, int end, bool cut, int viewportWidth, int selStart, int selEnd)
    {
        var before = new StringBuilder();
        var selected = new StringBuilder();
        var after = new StringBuilder();
        var width = 0;
        var anySelected = false;
        for (var i = origin; i < end; i++)
        {
            var unit = line.Units[i];
            var shown = cut ? ClipToDisplayWidth(unit.Shown, viewportWidth) : unit.Shown;
            width += cut ? DisplayWidth.GetStringWidth(shown) : unit.Width;
            var isSelected = selStart >= 0 && unit.End > selStart && unit.Start < selEnd;
            anySelected |= isSelected;
            (selStart < 0 || unit.End <= selStart ? before : isSelected ? selected : after).Append(shown);
        }
        return (before.ToString(), selected.ToString(), after.ToString(), width, anySelected);
    }

    /// <summary>
    /// The prediction overlay under the same map and control replacement as the buffer, cut to <paramref name="cells"/>;
    /// null when its first grapheme would fuse with the last grapheme of the drawn text (rule 2b).
    /// </summary>
    private static string? ShownPrediction(Func<string, string?> map, string prediction, string drawn, int cells)
    {
        var shown = string.Concat(TextBoxDisplayLayout.Build(prediction, map, multiline: false).Lines[0].Units.Select(u => u.Shown));
        var tail = TextBoxDisplayLayout.LastGrapheme(drawn);
        if (tail.Length > 0 && shown.Length > 0
            && System.Globalization.StringInfo.GetNextTextElementLength(tail + shown) != tail.Length)
        {
            return null;
        }
        var clipped = ClipToDisplayWidth(shown, cells);
        return clipped.Length == 0 ? null : clipped;
    }

    private void WriteRow(Hex1bRenderContext context, int y, string output)
    {
        if (context.CurrentLayoutProvider != null)
            context.WriteClipped(Bounds.X, y, output);
        else
            context.Write(output);
    }
}
