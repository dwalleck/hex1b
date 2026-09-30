using System.Globalization;
using System.Text.Json;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Compares two <c>text-state/1</c> projections field by field: every cell of every screen and history row,
/// with styles resolved, and every other surface. It never concludes from counts alone; every difference is
/// counted per surface, and the first ones (up to the cap) are listed in the projection's order.
/// </summary>
internal static class ModelStateComparer
{
    internal const int DefaultMaxDifferences = 1_000;

    /// <summary>The surfaces a comparison covers, in the order differences are listed.</summary>
    internal static readonly IReadOnlyList<string> ComparedSurfaces =
    [
        "profile", "modelSequence", "width", "height", "activeBuffer", "screen", "savedMainScreen", "history", "cursor", "savedCursor",
        "alternateSavedCursor", "modes", "protectedMode", "margins", "tabStops", "charsets", "rendition", "titles", "activity",
        "commandMarks", "lastCommandAnchorId", "lastPrinted", "pendingGraphemeCombine", "pendingInput", "synchronizedUpdate", "unsupported",
    ];

    /// <summary>What the profile leaves out, and why (spec: state surface decision).</summary>
    internal static readonly IReadOnlyList<string> Exclusions =
    [
        "clock stamps: each cell's write time and each history row's timestamp",
        "write sequences: the order cells were written in",
        "text identities: row ids and the text generation, assigned when text is read (a command mark's position is compared as its buffer, row and column)",
        "caller-created text anchors and their views (browser custom markers)",
        "graphics: images, placements and Sixel registers are named unsupported, never compared",
    ];
    internal const int MaxMaxDifferences = 100_000;

    internal static DiagnosticModelStateComparison Compare(DiagnosticModelState recorded, DiagnosticModelState reapplied, int maxDifferences)
    {
        var d = new Differ(maxDifferences, recorded.Styles, reapplied.Styles);
        d.String("profile", "profile", recorded.Profile, reapplied.Profile);
        d.Number("modelSequence", "modelSequence", recorded.ModelSequence, reapplied.ModelSequence);
        d.Number("width", "width", recorded.Width, reapplied.Width);
        d.Number("height", "height", recorded.Height, reapplied.Height);
        d.String("activeBuffer", "activeBuffer", recorded.ActiveBuffer, reapplied.ActiveBuffer);
        d.Rows("screen", "screen", recorded.Screen, reapplied.Screen, history: false);
        d.Rows("savedMainScreen", "savedMainScreen", recorded.SavedMainScreen, reapplied.SavedMainScreen, history: false);
        d.History(recorded.History, reapplied.History);
        d.Cursor(recorded.Cursor, reapplied.Cursor);
        d.SavedCursor("savedCursor", recorded.SavedCursor, reapplied.SavedCursor);
        d.SavedCursor("alternateSavedCursor", recorded.AlternateSavedCursor, reapplied.AlternateSavedCursor);
        d.Modes(recorded.Modes, reapplied.Modes);
        d.String("protectedMode", "protectedMode", recorded.ProtectedMode, reapplied.ProtectedMode);
        d.Margins(recorded.Margins, reapplied.Margins);
        d.TabStops(recorded.TabStops, reapplied.TabStops);
        d.Charsets(recorded.Charsets, reapplied.Charsets);
        d.Style("rendition", "rendition", recorded.Rendition, reapplied.Rendition);
        d.Titles(recorded.Titles, reapplied.Titles);
        d.Activity(recorded.Activity, reapplied.Activity);
        d.CommandMarks(recorded.CommandMarks, reapplied.CommandMarks);
        d.Number("lastCommandAnchorId", "lastCommandAnchorId", recorded.LastCommandAnchorId, reapplied.LastCommandAnchorId);
        d.LastPrinted(recorded.LastPrinted, reapplied.LastPrinted);
        d.Bool("pendingGraphemeCombine", "pendingGraphemeCombine", recorded.PendingGraphemeCombine, reapplied.PendingGraphemeCombine);
        d.PendingInput(recorded.PendingInput, reapplied.PendingInput);
        d.Bool("synchronizedUpdate", "synchronizedUpdate.active", recorded.SynchronizedUpdate.Active, reapplied.SynchronizedUpdate.Active);
        d.Number("synchronizedUpdate", "synchronizedUpdate.startedAtSequence", recorded.SynchronizedUpdate.StartedAtSequence, reapplied.SynchronizedUpdate.StartedAtSequence);
        d.String("unsupported", "unsupported", Join(recorded.Unsupported), Join(reapplied.Unsupported));
        return d.Result();
    }

    private static string Join(IReadOnlyList<string> values) => string.Join(",", values);

    // A list of names as its JSON array, so a name containing the separator stays distinct.
    private static string Names(IReadOnlyList<string> values) =>
        "[" + string.Join(",", values.Select(v => JsonSerializer.Serialize(v, DiagnosticsJsonContext.Default.String))) + "]";

    private static string? Json(string? value) =>
        value is null ? null : JsonSerializer.Serialize(value, DiagnosticsJsonContext.Default.String);

    private sealed class Differ(int max, IReadOnlyList<DiagnosticModelStyle> recordedStyles, IReadOnlyList<DiagnosticModelStyle> reappliedStyles)
    {
        private readonly List<DiagnosticModelStateDifference> _differences = [];
        private readonly Dictionary<string, long> _bySurface = new(StringComparer.Ordinal);
        private readonly Dictionary<(int, int), bool> _sameStyle = [];
        private long _total;

        public DiagnosticModelStateComparison Result() => new()
        {
            Total = _total,
            BySurface = _bySurface,
            Truncated = _total > _differences.Count,
            Differences = _differences,
        };

        // Paths are formatted only for listed differences (no closure per cell); every difference is counted.
        private void Add(string surface, in Path path, string? recorded, string? reapplied)
        {
            _total++;
            _bySurface[surface] = _bySurface.GetValueOrDefault(surface) + 1;
            if (_differences.Count < max)
                _differences.Add(new DiagnosticModelStateDifference { Path = path.Format(), Recorded = recorded, Reapplied = reapplied });
        }

        // prefix, prefix.field, prefix[row].field, or prefix[row][column].field (-1 when absent).
        private readonly record struct Path(string Prefix, int Row = -1, int Column = -1, string? Field = null)
        {
            public string Format() => (Row, Column, Field) switch
            {
                (< 0, _, null) => Prefix,
                (< 0, _, _) => $"{Prefix}.{Field}",
                (_, < 0, _) => $"{Prefix}[{Row}].{Field}",
                _ => $"{Prefix}[{Row}][{Column}].{Field}",
            };
        }

        public void String(string surface, string path, string? recorded, string? reapplied)
        {
            if (!string.Equals(recorded, reapplied, StringComparison.Ordinal))
                Add(surface, new Path(path), Json(recorded), Json(reapplied));
        }

        public void Number(string surface, string path, long? recorded, long? reapplied)
        {
            if (recorded != reapplied)
                Add(surface, new Path(path), recorded?.ToString(CultureInfo.InvariantCulture), reapplied?.ToString(CultureInfo.InvariantCulture));
        }

        public void Bool(string surface, string path, bool? recorded, bool? reapplied)
        {
            if (recorded != reapplied)
                Add(surface, new Path(path), Flag(recorded), Flag(reapplied));
        }

        private static string? Flag(bool? value) => value is null ? null : value.Value ? "true" : "false";

        // Absent on one side only: one difference at the surface itself.
        private bool Presence<T>(string surface, string path, T? recorded, T? reapplied) where T : class
        {
            if (recorded is not null && reapplied is not null)
                return true;
            if (recorded is not null || reapplied is not null)
                Add(surface, new Path(path), recorded is null ? null : "present", reapplied is null ? null : "present");
            return false;
        }

        public void Rows(string surface, string prefix, IReadOnlyList<DiagnosticModelRow>? recorded, IReadOnlyList<DiagnosticModelRow>? reapplied,
            bool history)
        {
            if (!Presence(surface, prefix, recorded, reapplied))
                return;
            Number(surface, $"{prefix}.count", recorded!.Count, reapplied!.Count);
            for (var row = 0; row < Math.Min(recorded.Count, reapplied.Count); row++)
            {
                var r = recorded[row];
                var a = reapplied[row];
                if (history)
                {
                    if (r.Id != a.Id)
                        Add(surface, new Path(prefix, row, Field: "id"), Text(r.Id), Text(a.Id));
                    if (r.OriginalWidth != a.OriginalWidth)
                        Add(surface, new Path(prefix, row, Field: "originalWidth"), Text(r.OriginalWidth), Text(a.OriginalWidth));
                }
                if (r.Cells.Count != a.Cells.Count)
                    Add(surface, new Path(prefix, row, Field: "count"), Text(r.Cells.Count), Text(a.Cells.Count));
                for (var column = 0; column < Math.Min(r.Cells.Count, a.Cells.Count); column++)
                    Cell(surface, prefix, row, column, r.Cells[column], a.Cells[column]);
            }
        }

        private static string? Text(long? value) => value?.ToString(CultureInfo.InvariantCulture);

        private void Cell(string surface, string prefix, int row, int column, in DiagnosticModelCell recorded, in DiagnosticModelCell reapplied)
        {
            if (!string.Equals(recorded.Text, reapplied.Text, StringComparison.Ordinal))
                Add(surface, new Path(prefix, row, column, "text"), Json(recorded.Text), Json(reapplied.Text));
            if (recorded.WideWrapPadding != reapplied.WideWrapPadding)
                Add(surface, new Path(prefix, row, column, "wideWrapPadding"), Flag(recorded.WideWrapPadding), Flag(reapplied.WideWrapPadding));
            if (SameStyle(recorded.Style, reapplied.Style))
                return;
            // Rare (the styles differ), so this prefix is formatted here.
            Style(surface, new Path(prefix, row, column, "style").Format(), StyleAt(recordedStyles, recorded.Style), StyleAt(reappliedStyles, reapplied.Style));
        }


        private static DiagnosticModelStyle? StyleAt(IReadOnlyList<DiagnosticModelStyle> styles, int index) =>
            index >= 0 && index < styles.Count ? styles[index] : null;

        // Style tables are per projection, so indices are compared by the styles they name, once per pair.
        private bool SameStyle(int recorded, int reapplied)
        {
            if (_sameStyle.TryGetValue((recorded, reapplied), out var same))
                return same;
            var r = StyleAt(recordedStyles, recorded);
            var a = StyleAt(reappliedStyles, reapplied);
            same = r is not null && a is not null && SameStyle(r, a);
            _sameStyle[(recorded, reapplied)] = same;
            return same;
        }

        // Field by field: no joined key, so no separator or null/empty collision can make two styles equal.
        private static bool SameStyle(DiagnosticModelStyle recorded, DiagnosticModelStyle reapplied) =>
            recorded.Attributes.SequenceEqual(reapplied.Attributes, StringComparer.Ordinal)
            && string.Equals(recorded.Foreground, reapplied.Foreground, StringComparison.Ordinal)
            && string.Equals(recorded.Background, reapplied.Background, StringComparison.Ordinal)
            && string.Equals(recorded.UnderlineColor, reapplied.UnderlineColor, StringComparison.Ordinal)
            && string.Equals(recorded.UnderlineStyle, reapplied.UnderlineStyle, StringComparison.Ordinal)
            && string.Equals(recorded.HyperlinkUri, reapplied.HyperlinkUri, StringComparison.Ordinal)
            && string.Equals(recorded.HyperlinkParameters, reapplied.HyperlinkParameters, StringComparison.Ordinal);

        public void Style(string surface, string prefix, DiagnosticModelStyle? recorded, DiagnosticModelStyle? reapplied)
        {
            if (!Presence(surface, prefix, recorded, reapplied))
                return;
            if (!recorded!.Attributes.SequenceEqual(reapplied!.Attributes, StringComparer.Ordinal))
                Add(surface, new Path($"{prefix}.attributes"), Names(recorded.Attributes), Names(reapplied.Attributes));
            String(surface, $"{prefix}.foreground", recorded.Foreground, reapplied.Foreground);
            String(surface, $"{prefix}.background", recorded.Background, reapplied.Background);
            String(surface, $"{prefix}.underlineColor", recorded.UnderlineColor, reapplied.UnderlineColor);
            String(surface, $"{prefix}.underlineStyle", recorded.UnderlineStyle, reapplied.UnderlineStyle);
            String(surface, $"{prefix}.hyperlinkUri", recorded.HyperlinkUri, reapplied.HyperlinkUri);
            String(surface, $"{prefix}.hyperlinkParameters", recorded.HyperlinkParameters, reapplied.HyperlinkParameters);
        }

        public void History(DiagnosticModelHistory? recorded, DiagnosticModelHistory? reapplied)
        {
            if (!Presence("history", "history", recorded, reapplied))
                return;
            Number("history", "history.capacity", recorded!.Capacity, reapplied!.Capacity);
            Number("history", "history.nextRowId", recorded.NextRowId, reapplied.NextRowId);
            Rows("history", "history.rows", recorded.Rows, reapplied.Rows, history: true);
        }

        public void Cursor(DiagnosticModelCursor recorded, DiagnosticModelCursor reapplied)
        {
            Number("cursor", "cursor.x", recorded.X, reapplied.X);
            Number("cursor", "cursor.y", recorded.Y, reapplied.Y);
            Bool("cursor", "cursor.pendingWrap", recorded.PendingWrap, reapplied.PendingWrap);
            Bool("cursor", "cursor.visible", recorded.Visible, reapplied.Visible);
            Number("cursor", "cursor.shape", recorded.Shape, reapplied.Shape);
            Bool("cursor", "cursor.protected", recorded.Protected, reapplied.Protected);
        }

        public void SavedCursor(string surface, DiagnosticModelSavedCursor? recorded, DiagnosticModelSavedCursor? reapplied)
        {
            if (!Presence(surface, surface, recorded, reapplied))
                return;
            Number(surface, $"{surface}.x", recorded!.X, reapplied!.X);
            Number(surface, $"{surface}.y", recorded.Y, reapplied.Y);
            Bool(surface, $"{surface}.pendingWrap", recorded.PendingWrap, reapplied.PendingWrap);
            Bool(surface, $"{surface}.protected", recorded.Protected, reapplied.Protected);
        }

        public void Modes(IReadOnlyDictionary<string, bool> recorded, IReadOnlyDictionary<string, bool> reapplied)
        {
            foreach (var (name, value) in recorded)
                Bool("modes", $"modes.{name}", value, reapplied.TryGetValue(name, out var other) ? other : null);
            foreach (var (name, value) in reapplied)
            {
                if (!recorded.ContainsKey(name))
                    Bool("modes", $"modes.{name}", null, value);
            }
        }

        public void Margins(DiagnosticModelMargins recorded, DiagnosticModelMargins reapplied)
        {
            Number("margins", "margins.top", recorded.Top, reapplied.Top);
            Number("margins", "margins.bottom", recorded.Bottom, reapplied.Bottom);
            Number("margins", "margins.left", recorded.Left, reapplied.Left);
            Number("margins", "margins.right", recorded.Right, reapplied.Right);
        }

        public void TabStops(DiagnosticModelTabStops recorded, DiagnosticModelTabStops reapplied)
        {
            Number("tabStops", "tabStops.width", recorded.Width, reapplied.Width);
            String("tabStops", "tabStops.columns", string.Join(",", recorded.Columns), string.Join(",", reapplied.Columns));
        }

        public void Charsets(DiagnosticModelCharsets recorded, DiagnosticModelCharsets reapplied)
        {
            String("charsets", "charsets.g0", recorded.G0, reapplied.G0);
            String("charsets", "charsets.g1", recorded.G1, reapplied.G1);
            String("charsets", "charsets.g2", recorded.G2, reapplied.G2);
            String("charsets", "charsets.g3", recorded.G3, reapplied.G3);
            Number("charsets", "charsets.active", recorded.Active, reapplied.Active);
        }

        public void Titles(DiagnosticModelTitles recorded, DiagnosticModelTitles reapplied)
        {
            String("titles", "titles.window", recorded.Window, reapplied.Window);
            String("titles", "titles.icon", recorded.Icon, reapplied.Icon);
            Number("titles", "titles.stack.count", recorded.Stack.Count, reapplied.Stack.Count);
            for (var i = 0; i < Math.Min(recorded.Stack.Count, reapplied.Stack.Count); i++)
            {
                String("titles", $"titles.stack[{i}].window", recorded.Stack[i].Window, reapplied.Stack[i].Window);
                String("titles", $"titles.stack[{i}].icon", recorded.Stack[i].Icon, reapplied.Stack[i].Icon);
            }
        }

        public void Activity(DiagnosticModelActivity recorded, DiagnosticModelActivity reapplied)
        {
            String("activity", "activity.progressState", recorded.ProgressState, reapplied.ProgressState);
            Number("activity", "activity.progressPercentage", recorded.ProgressPercentage, reapplied.ProgressPercentage);
            String("activity", "activity.shellPhase", recorded.ShellPhase, reapplied.ShellPhase);
            Number("activity", "activity.lastExitCode", recorded.LastExitCode, reapplied.LastExitCode);
            String("activity", "activity.workingDirectoryUri", recorded.WorkingDirectoryUri, reapplied.WorkingDirectoryUri);
            String("activity", "activity.workingDirectoryHost", recorded.WorkingDirectoryHost, reapplied.WorkingDirectoryHost);
            String("activity", "activity.workingDirectoryPath", recorded.WorkingDirectoryPath, reapplied.WorkingDirectoryPath);
        }

        public void CommandMarks(IReadOnlyList<DiagnosticModelCommandMark> recorded, IReadOnlyList<DiagnosticModelCommandMark> reapplied)
        {
            Number("commandMarks", "commandMarks.count", recorded.Count, reapplied.Count);
            for (var i = 0; i < Math.Min(recorded.Count, reapplied.Count); i++)
            {
                String("commandMarks", $"commandMarks[{i}].anchor", recorded[i].Anchor, reapplied[i].Anchor);
                String("commandMarks", $"commandMarks[{i}].phase", recorded[i].Phase, reapplied[i].Phase);
                Number("commandMarks", $"commandMarks[{i}].exitCode", recorded[i].ExitCode, reapplied[i].ExitCode);
                String("commandMarks", $"commandMarks[{i}].rawParameters", recorded[i].RawParameters, reapplied[i].RawParameters);
                String("commandMarks", $"commandMarks[{i}].buffer", recorded[i].Buffer, reapplied[i].Buffer);
                Number("commandMarks", $"commandMarks[{i}].row", recorded[i].Row, reapplied[i].Row);
                Number("commandMarks", $"commandMarks[{i}].column", recorded[i].Column, reapplied[i].Column);
            }
        }

        public void LastPrinted(DiagnosticModelLastPrinted? recorded, DiagnosticModelLastPrinted? reapplied)
        {
            if (!Presence("lastPrinted", "lastPrinted", recorded, reapplied))
                return;
            Number("lastPrinted", "lastPrinted.x", recorded!.X, reapplied!.X);
            Number("lastPrinted", "lastPrinted.y", recorded.Y, reapplied.Y);
            Number("lastPrinted", "lastPrinted.width", recorded.Width, reapplied.Width);
            Cell("lastPrinted", "lastPrinted.cell", -1, -1, recorded.Cell, reapplied.Cell);
        }

        public void PendingInput(DiagnosticModelPendingInput recorded, DiagnosticModelPendingInput reapplied)
        {
            String("pendingInput", "pendingInput.escapePrefix", recorded.EscapePrefix, reapplied.EscapePrefix);
            String("pendingInput", "pendingInput.utf8", recorded.Utf8, reapplied.Utf8);
            Bool("pendingInput", "pendingInput.groundEscape", recorded.GroundEscape, reapplied.GroundEscape);
            Number("pendingInput", "pendingInput.framerUtf8Remaining", recorded.FramerUtf8Remaining, reapplied.FramerUtf8Remaining);
        }
    }
}
