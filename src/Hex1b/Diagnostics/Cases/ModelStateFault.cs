namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Declared faults a re-application can inject into its reconstructed state before comparing, to show the
/// comparison detects a change there. Each names the path it changes; a result with a fault is labelled as such.
/// </summary>
internal static class ModelStateFault
{
    /// <summary>The declared fault kinds.</summary>
    internal static readonly IReadOnlyList<string> Kinds =
    [
        "cell-text", "cell-style", "cursor", "mode", "title", "charset", "tab-stop", "pending-input", "history-row", "history-rows",
    ];

    /// <summary>
    /// Applies one fault. Returns the faulted state and the first path it changed, or null with the reason when
    /// the kind is unknown or the state has nothing it can change (a history fault without history).
    /// </summary>
    internal static DiagnosticModelState? Apply(DiagnosticModelState state, string kind, out string? path, out string? problem)
    {
        path = null;
        problem = null;
        switch (kind)
        {
            case "cell-text" when state.Screen.Count > 0 && state.Screen[0].Cells.Count > 0:
                path = "screen[0][0].text";
                return state with { Screen = WithCell(state.Screen, 0, 0, c => c with { Text = c.Text == "X" ? "Y" : "X" }) };
            case "cell-style" when state.Screen.Count > 0 && state.Screen[0].Cells.Count > 0:
            {
                path = "screen[0][0].style.attributes";
                var cell = state.Screen[0].Cells[0];
                var style = cell.Style >= 0 && cell.Style < state.Styles.Count ? state.Styles[cell.Style] : new DiagnosticModelStyle();
                var toggled = style with
                {
                    Attributes = style.Attributes.Contains("bold") ? [.. style.Attributes.Where(a => a != "bold")] : ["bold", .. style.Attributes],
                };
                return state with
                {
                    Styles = [.. state.Styles, toggled],
                    Screen = WithCell(state.Screen, 0, 0, c => c with { Style = state.Styles.Count }),
                };
            }
            case "cursor":
                path = "cursor.x";
                return state with { Cursor = state.Cursor with { X = state.Cursor.X == 0 ? 1 : state.Cursor.X - 1 } };
            case "mode":
            {
                path = "modes.wraparound";
                var modes = new Dictionary<string, bool>(state.Modes);
                modes["wraparound"] = !modes.GetValueOrDefault("wraparound");
                return state with { Modes = modes };
            }
            case "title":
                path = "titles.window";
                return state with { Titles = state.Titles with { Window = state.Titles.Window + "!" } };
            case "charset":
                path = "charsets.g0";
                return state with { Charsets = state.Charsets with { G0 = state.Charsets.G0 == "0" ? "B" : "0" } };
            case "tab-stop":
            {
                path = "tabStops.columns";
                var columns = state.TabStops.Columns.Contains(1) ? state.TabStops.Columns.Where(c => c != 1).ToList() : [.. state.TabStops.Columns, 1];
                columns.Sort();
                return state with { TabStops = state.TabStops with { Columns = columns } };
            }
            case "pending-input":
                path = "pendingInput.utf8";
                return state with { PendingInput = state.PendingInput with { Utf8 = state.PendingInput.Utf8.Length == 0 ? "5g==" : "" } };
            case "history-row" when state.History is { Rows.Count: > 0 } history:
                path = "history.rows[0].originalWidth";
                return state with
                {
                    History = history with { Rows = [history.Rows[0] with { OriginalWidth = history.Rows[0].OriginalWidth + 1 }, .. history.Rows.Skip(1)] },
                };
            case "history-rows" when state.History is { Rows.Count: > 0 } history:
                // Every retained row's width: one difference per row, however many rows are retained.
                path = "history.rows[0].originalWidth";
                return state with
                {
                    History = history with { Rows = [.. history.Rows.Select(r => r with { OriginalWidth = r.OriginalWidth + 1 })] },
                };
            case "history-row" or "history-rows":
                problem = $"fault '{kind}' needs retained history, and the reconstructed state has none.";
                return null;
            case "cell-text" or "cell-style":
                problem = $"fault '{kind}' needs a screen cell, and the reconstructed state has none.";
                return null;
            default:
                problem = $"unknown fault '{kind}'; declared faults are {string.Join(", ", Kinds)}.";
                return null;
        }
    }

    private static IReadOnlyList<DiagnosticModelRow> WithCell(IReadOnlyList<DiagnosticModelRow> rows, int row, int column,
        Func<DiagnosticModelCell, DiagnosticModelCell> change)
    {
        var cells = rows[row].Cells.ToArray();
        cells[column] = change(cells[column]);
        var copy = rows.ToArray();
        copy[row] = rows[row] with { Cells = cells };
        return copy;
    }
}
