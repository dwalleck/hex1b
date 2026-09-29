using System.Globalization;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Declared faults a re-application can inject into its reconstructed state before comparing, to show the
/// comparison detects a change there. A fault is <c>kind</c> or <c>kind:target</c>
/// (<c>cell-text:3/5</c>, <c>mode:wraparound</c>, <c>history-row:12</c>); each names the path it changes, and a
/// result with a fault is labelled as such.
/// </summary>
internal static class ModelStateFault
{
    /// <summary>The declared fault kinds.</summary>
    internal static readonly IReadOnlyList<string> Kinds =
    [
        "cell-text", "cell-style", "cursor", "mode", "title", "charset", "tab-stop", "pending-input", "history-row", "history-rows",
    ];

    // Kinds that take a target, and its form.
    private static readonly Dictionary<string, string> TargetForms = new(StringComparer.Ordinal)
    {
        ["cell-text"] = "<row>/<column>",
        ["cell-style"] = "<row>/<column>",
        ["mode"] = "<mode name>",
        ["history-row"] = "<row index>",
    };

    /// <summary>Why a fault's kind or target is not declared, or null (checked before anything is applied).</summary>
    internal static string? Validate(string fault)
    {
        var (kind, target) = Split(fault);
        if (!Kinds.Contains(kind))
            return $"Unknown fault '{fault}'; declared faults are {string.Join(", ", Kinds)}, as kind or kind:target.";
        if (target is null)
            return null;
        if (!TargetForms.TryGetValue(kind, out var form))
            return $"Fault '{kind}' takes no target.";
        var valid = form switch
        {
            "<row>/<column>" => TryCell(target, out _, out _),
            "<row index>" => int.TryParse(target, NumberStyles.None, CultureInfo.InvariantCulture, out _),
            _ => target.Length > 0,
        };
        return valid ? null : $"Fault '{fault}': the target must be {form}.";
    }

    /// <summary>
    /// Applies one fault. Returns the faulted state and the path it changed, or null with the reason when the
    /// state has nothing the fault can change (a history fault without history, a cell outside the screen).
    /// </summary>
    internal static DiagnosticModelState? Apply(DiagnosticModelState state, string fault, out string? path, out string? problem)
    {
        path = null;
        problem = null;
        var (kind, target) = Split(fault);
        switch (kind)
        {
            case "cell-text" or "cell-style":
            {
                var (row, column) = target is null ? (0, 0) : TryCell(target, out var r, out var c) ? (r, c) : (-1, -1);
                if (row < 0 || row >= state.Screen.Count || column >= state.Screen[row].Cells.Count)
                {
                    problem = $"fault '{fault}': the reconstructed screen has no cell {row},{column}.";
                    return null;
                }

                if (kind == "cell-text")
                {
                    path = $"screen[{row}][{column}].text";
                    return state with { Screen = WithCell(state.Screen, row, column, cell => cell with { Text = cell.Text == "X" ? "Y" : "X" }) };
                }

                path = $"screen[{row}][{column}].style.attributes";
                var existing = state.Screen[row].Cells[column];
                var style = existing.Style >= 0 && existing.Style < state.Styles.Count ? state.Styles[existing.Style] : new DiagnosticModelStyle();
                var toggled = style with
                {
                    Attributes = style.Attributes.Contains("bold") ? [.. style.Attributes.Where(a => a != "bold")] : ["bold", .. style.Attributes],
                };
                return state with
                {
                    Styles = [.. state.Styles, toggled],
                    Screen = WithCell(state.Screen, row, column, cell => cell with { Style = state.Styles.Count }),
                };
            }
            case "cursor":
                path = "cursor.x";
                return state with { Cursor = state.Cursor with { X = state.Cursor.X == 0 ? 1 : state.Cursor.X - 1 } };
            case "mode":
            {
                var name = target ?? "wraparound";
                if (!state.Modes.ContainsKey(name))
                {
                    problem = $"fault '{fault}': the projection has no mode '{name}'.";
                    return null;
                }
                path = $"modes.{name}";
                var modes = new Dictionary<string, bool>(state.Modes);
                modes[name] = !modes[name];
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
            case "history-row":
            {
                // A history row's text: its first cell's.
                var index = target is null ? 0 : int.Parse(target, NumberStyles.None, CultureInfo.InvariantCulture);
                if (state.History is not { } history || index >= history.Rows.Count || history.Rows[index].Cells.Count == 0)
                {
                    problem = $"fault '{fault}': the reconstructed state has no retained history row {index} with text.";
                    return null;
                }
                path = $"history.rows[{index}][0].text";
                return state with { History = history with { Rows = WithCell(history.Rows, index, 0, cell => cell with { Text = cell.Text == "X" ? "Y" : "X" }) } };
            }
            case "history-rows":
            {
                // Every retained row's width: one difference per row, however many rows are retained.
                if (state.History is not { Rows.Count: > 0 } history)
                {
                    problem = $"fault '{fault}': the reconstructed state has no retained history.";
                    return null;
                }
                path = "history.rows[0].originalWidth";
                return state with
                {
                    History = history with { Rows = [.. history.Rows.Select(r => r with { OriginalWidth = r.OriginalWidth + 1 })] },
                };
            }
            default:
                problem = Validate(fault) ?? $"Unknown fault '{fault}'.";
                return null;
        }
    }

    private static (string Kind, string? Target) Split(string fault)
    {
        var colon = fault.IndexOf(':');
        return colon < 0 ? (fault, null) : (fault[..colon], fault[(colon + 1)..]);
    }

    private static bool TryCell(string target, out int row, out int column)
    {
        row = column = -1;
        var comma = target.IndexOf('/');
        return comma > 0
            && int.TryParse(target.AsSpan(0, comma), NumberStyles.None, CultureInfo.InvariantCulture, out row)
            && int.TryParse(target.AsSpan(comma + 1), NumberStyles.None, CultureInfo.InvariantCulture, out column);
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
