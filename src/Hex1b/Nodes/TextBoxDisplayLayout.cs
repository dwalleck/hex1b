using System.Globalization;

namespace Hex1b;

/// <summary>
/// The on-screen layout of a TextBox buffer under a display map (issue 62): every grapheme of a line is one unit,
/// shown as the map's stand-in (or itself) with C0/DEL/C1 controls replaced by U+FFFD, and measured by the shown text.
/// Columns, wrap rows, scroll origins and hit-tests are all computed from these units, so the caret, the selection
/// cells and a click agree with what is drawn. Offsets are buffer (UTF-16) offsets; unit boundaries are the only
/// cursor positions the layout produces.
/// </summary>
internal sealed class TextBoxDisplayLayout
{
    /// <summary>One grapheme of the buffer: its source range, its shown text and that text's width in cells.</summary>
    internal readonly record struct Unit(int Start, int End, string Shown, int Width, bool IsSpace);

    /// <summary>One document line (without its newline) and the running column before each unit.</summary>
    internal sealed class Line
    {
        public Line(int start, int end, Unit[] units)
        {
            Start = start;
            End = end;
            Units = units;
            Columns = new int[units.Length + 1];
            for (var i = 0; i < units.Length; i++)
                Columns[i + 1] = Columns[i] + units[i].Width;
        }

        public int Start { get; }
        public int End { get; }
        public Unit[] Units { get; }

        /// <summary>Columns[i] is the column where unit i starts; Columns[Units.Length] is the line's width.</summary>
        public int[] Columns { get; }

        public int Width => Columns[^1];

        /// <summary>Source offset of the boundary before unit <paramref name="index"/> (the line end past the last).</summary>
        public int OffsetOf(int index) => index < Units.Length ? Units[index].Start : End;

        /// <summary>The number of units that end at or before <paramref name="offset"/>: the boundary the offset shows at.</summary>
        public int IndexOf(int offset)
        {
            int lo = 0, hi = Units.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (Units[mid].End <= offset) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }
    }

    /// <summary>A visual row: units [First, End) of line <see cref="LineIndex"/>.</summary>
    internal readonly record struct Row(int LineIndex, int First, int End);

    // Rows for the last two (width, wrap) requests: measure and render can ask for different widths each frame.
    private (Row[] Rows, int Width, bool Wrap)? _rowsA;
    private (Row[] Rows, int Width, bool Wrap)? _rowsB;

    private TextBoxDisplayLayout(string text, Func<string, string?> map, bool multiline, Line[] lines)
    {
        Text = text;
        Map = map;
        Multiline = multiline;
        Lines = lines;
    }

    public string Text { get; }
    public Func<string, string?> Map { get; }
    public bool Multiline { get; }
    public Line[] Lines { get; }

    /// <summary>Whether this layout was built for this buffer instance, an equal map delegate and this mode.</summary>
    public bool IsFor(string text, Func<string, string?> map, bool multiline)
        => ReferenceEquals(Text, text) && Map.Equals(map) && Multiline == multiline;

    /// <summary>Lays out <paramref name="text"/> in one pass: lines at '\n' (multiline only), units per grapheme.</summary>
    public static TextBoxDisplayLayout Build(string text, Func<string, string?> map, bool multiline)
    {
        var lines = new List<Line>();
        var start = 0;
        while (true)
        {
            var end = multiline ? text.IndexOf('\n', start) : -1;
            if (end < 0) end = text.Length;
            lines.Add(new Line(start, end, BuildUnits(text, start, end, map)));
            if (end >= text.Length) break;
            start = end + 1;
        }
        return new TextBoxDisplayLayout(text, map, multiline, [.. lines]);
    }

    private static Unit[] BuildUnits(string text, int start, int end, Func<string, string?> map)
    {
        if (end == start) return [];
        var units = new List<Unit>();
        var changed = new List<bool>();
        var enumerator = StringInfo.GetTextElementEnumerator(text[start..end]);
        while (enumerator.MoveNext())
        {
            var grapheme = (string)enumerator.Current;
            var shown = Sanitize(map(grapheme) ?? grapheme);
            var unitStart = start + enumerator.ElementIndex;
            units.Add(new Unit(unitStart, unitStart + grapheme.Length, shown, DisplayWidth.GetStringWidth(shown), grapheme == " "));
            changed.Add(!string.Equals(shown, grapheme, StringComparison.Ordinal));
        }
        SeparateJoins(units, changed);
        return [.. units];
    }

    /// <summary>
    /// Rule 2b: a row's shown units are drawn as one string, so every unit must start a grapheme of that string, as it
    /// does in the buffer. Where a join fuses (an empty stand-in between two regional indicators, a stand-in before a
    /// ZWJ or a spacing mark, a prepend character before a stand-in), one unit at the join is shown as U+FFFD: the
    /// changed unit on its right, else the empty stand-in just before it when U+FFFD would break after the tail, else
    /// the tail's own unit, else the right unit; then the check resumes from the replaced unit. Every choice is local to the join and replaces a unit not
    /// yet shown as U+FFFD, so the pass is linear. Joins between unchanged units with no changed unit between them are
    /// not checked: the buffer's own segmentation already breaks there, with the same pairing.
    /// </summary>
    private static void SeparateJoins(List<Unit> units, List<bool> changed)
    {
        // tails[i]: the last grapheme of the shown text through unit i ("" before the first non-empty unit);
        // owner[i]: the unit that grapheme lies in (-1 if none); dirty[i]: the next join must be checked.
        var tails = new string[units.Count];
        var owner = new int[units.Count];
        var dirty = new bool[units.Count];
        var i = 0;
        while (i < units.Count)
        {
            var tail = i > 0 ? tails[i - 1] : "";
            var last = i > 0 ? owner[i - 1] : -1;
            var tailDirty = i > 0 && dirty[i - 1];
            var unit = units[i];
            if (unit.Shown.Length == 0)
            {
                (tails[i], owner[i], dirty[i]) = (tail, last, tailDirty || changed[i]);
                i++;
                continue;
            }
            if (tail.Length == 0 || (!tailDirty && !changed[i])
                || StringInfo.GetNextTextElementLength(tail + unit.Shown) == tail.Length)
            {
                (tails[i], owner[i], dirty[i]) = (changed[i] ? LastGrapheme(unit.Shown) : unit.Shown, i, changed[i]);
                i++;
                continue;
            }

            int target;
            if (changed[i] && unit.Shown != Replacement) target = i;
            else if (i - 1 > last && StringInfo.GetNextTextElementLength(tail + Replacement) == tail.Length) target = i - 1;
            else if (units[last].Shown != Replacement) target = last;
            else target = i;
            if (units[target].Shown == Replacement)
            {
                // Unreachable: two U+FFFD always break, and an empty gap unit is never U+FFFD. Accept, never loop.
                (tails[i], owner[i], dirty[i]) = (LastGrapheme(unit.Shown), i, true);
                i++;
                continue;
            }
            units[target] = units[target] with { Shown = Replacement, Width = 1 };
            changed[target] = true;
            i = target; // tails before target are unchanged; re-check from the replaced unit
        }
    }

    private const string Replacement = "\uFFFD";

    /// <summary>The last grapheme of <paramref name="text"/> (the text itself when it is one grapheme or empty).</summary>
    internal static string LastGrapheme(string text)
    {
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        var last = text;
        while (enumerator.MoveNext())
            last = (string)enumerator.Current;
        return last;
    }

    /// <summary>Rule 2: no C0 control, DEL or C1 control reaches the terminal from a mapped TextBox.</summary>
    private static string Sanitize(string shown)
    {
        foreach (var c in shown)
        {
            if (IsControl(c))
                return string.Create(shown.Length, shown, static (span, source) =>
                {
                    for (var i = 0; i < source.Length; i++)
                        span[i] = IsControl(source[i]) ? '\uFFFD' : source[i];
                });
        }
        return shown;
    }

    private static bool IsControl(char c) => c < ' ' || (c >= '\u007F' && c <= '\u009F');

    /// <summary>The index of the line containing <paramref name="offset"/> (a newline belongs to the line it ends).</summary>
    public int LineIndexOf(int offset)
    {
        int lo = 0, hi = Lines.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (Lines[mid].Start <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// The visual rows for a viewport (rule 5 when <paramref name="wrap"/>, else one row per line), cached per width.
    /// </summary>
    public Row[] GetRows(int viewportWidth, bool wrap)
    {
        if (_rowsA is { } a && a.Width == viewportWidth && a.Wrap == wrap) return a.Rows;
        if (_rowsB is { } b && b.Width == viewportWidth && b.Wrap == wrap) return b.Rows;
        var rows = new List<Row>(Lines.Length);
        for (var lineIndex = 0; lineIndex < Lines.Length; lineIndex++)
        {
            var line = Lines[lineIndex];
            if (!wrap || viewportWidth <= 0 || line.Width <= viewportWidth)
            {
                rows.Add(new Row(lineIndex, 0, line.Units.Length));
                continue;
            }
            var first = 0;
            while (first < line.Units.Length)
            {
                var end = FitEnd(line, first, viewportWidth);
                if (end < line.Units.Length)
                {
                    if (end == first)
                    {
                        end = first + 1; // a unit wider than the viewport sits alone and is clipped
                    }
                    else
                    {
                        for (var k = end - 1; k > first; k--)
                        {
                            if (line.Units[k].IsSpace)
                            {
                                end = k + 1;
                                break;
                            }
                        }
                    }
                }
                rows.Add(new Row(lineIndex, first, end));
                first = end;
            }
        }
        Row[] result = [.. rows];
        _rowsB = _rowsA;
        _rowsA = (result, viewportWidth, wrap);
        return result;
    }

    /// <summary>The end of the longest run of whole units from <paramref name="first"/> that fits in <paramref name="width"/> cells.</summary>
    public static int FitEnd(Line line, int first, int width)
    {
        var limit = line.Columns[first] + width;
        int lo = first, hi = line.Units.Length;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (line.Columns[mid] <= limit) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>The source offset where row <paramref name="row"/> starts.</summary>
    public int RowStart(Row row) => Lines[row.LineIndex].OffsetOf(row.First);

    /// <summary>The source offset where row <paramref name="row"/> ends.</summary>
    public int RowEnd(Row row) => Lines[row.LineIndex].OffsetOf(row.End);

    /// <summary>Rule 7: the last row whose start offset is at or before <paramref name="offset"/>.</summary>
    public static int RowIndexOf(TextBoxDisplayLayout layout, Row[] rows, int offset)
    {
        int lo = 0, hi = rows.Length - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (layout.RowStart(rows[mid]) <= offset) lo = mid;
            else hi = mid - 1;
        }
        return lo;
    }

    /// <summary>
    /// Rule 4's scroll origin: the unit index, at or after <paramref name="origin"/> moved the least, from which the
    /// caret at unit <paramref name="caret"/> is within <c>viewport − 1</c> columns.
    /// </summary>
    public static int ScrollOrigin(Line line, int origin, int caret, int viewportWidth)
    {
        if (caret < origin) return caret;
        var minimum = line.Columns[caret] - (viewportWidth - 1);
        if (line.Columns[origin] >= minimum) return origin;
        int lo = origin, hi = caret;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (line.Columns[mid] >= minimum) hi = mid;
            else lo = mid + 1;
        }
        return lo;
    }

    /// <summary>
    /// Rule 8: the boundary a click at <paramref name="column"/> (relative to unit <paramref name="first"/>) selects
    /// among units [first, end).
    /// </summary>
    public static int HitTest(Line line, int first, int end, int column)
    {
        var origin = line.Columns[first];
        for (var i = first; i < end; i++)
        {
            var left = line.Columns[i] - origin;
            var width = line.Units[i].Width;
            if (column < left + width)
                return column < left + width / 2.0 ? line.Units[i].Start : line.Units[i].End;
        }
        return line.OffsetOf(end);
    }
}
