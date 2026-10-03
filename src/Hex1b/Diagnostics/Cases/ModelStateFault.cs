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
        // Continuation a start restores (ticket 09): each toggles its field, so it differs even from a default.
        "pending-wrap", "last-printed", "rendition", "margins", "saved-cursor", "pending-grapheme", "activity", "synchronized-update",
        // Titles and command marks a start restores (ticket 11): the top saved title, and the first placed mark's column.
        "title-stack", "command-mark",
        // Pending input a start restores (ticket 12): each holder dropped, refused when the start holds none.
        "pending-escape", "pending-ground-escape", "pending-framer",
        // Non-Sixel DCS continuation (ticket 25): content and substate omissions are independently declared.
        "dcs-bytes", "dcs-state",
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
            case "title-stack":
                if (state.Titles.Stack.Count == 0)
                {
                    problem = $"fault '{fault}': the reconstructed state has no saved title.";
                    return null;
                }
                path = "titles.stack[0].window";
                return state with
                {
                    Titles = state.Titles with { Stack = [state.Titles.Stack[0] with { Window = state.Titles.Stack[0].Window + "!" }, .. state.Titles.Stack.Skip(1)] },
                };
            case "command-mark":
            {
                var index = state.CommandMarks.ToList().FindIndex(mark => mark.Column is not null);
                if (index < 0)
                {
                    problem = $"fault '{fault}': the reconstructed state has no command mark with a position.";
                    return null;
                }
                var mark = state.CommandMarks[index];
                path = $"commandMarks[{index}].column";
                var marks = state.CommandMarks.ToArray();
                marks[index] = mark with { Column = mark.Column == 0 ? 1 : mark.Column - 1 };
                return state with { CommandMarks = marks };
            }
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
                if (state.PendingInput.Utf8.Length == 0)
                {
                    problem = $"fault '{fault}': the reconstructed state holds no unfinished UTF-8 scalar.";
                    return null;
                }
                path = "pendingInput.utf8";
                return state with { PendingInput = state.PendingInput with { Utf8 = "" } };
            case "pending-escape":
                if (state.PendingInput.EscapePrefix.Length == 0)
                {
                    problem = $"fault '{fault}': the reconstructed state holds no unfinished escape sequence.";
                    return null;
                }
                path = "pendingInput.escapePrefix";
                return state with { PendingInput = state.PendingInput with { EscapePrefix = "" } };
            case "pending-ground-escape":
                if (!state.PendingInput.GroundEscape)
                {
                    problem = $"fault '{fault}': the reconstructed state holds no ESC before a possible DCS.";
                    return null;
                }
                path = "pendingInput.groundEscape";
                return state with { PendingInput = state.PendingInput with { GroundEscape = false } };
            case "pending-framer":
                if (state.PendingInput.FramerUtf8Remaining == 0)
                {
                    problem = $"fault '{fault}': the reconstructed state expects no UTF-8 continuation bytes.";
                    return null;
                }
                path = "pendingInput.framerUtf8Remaining";
                return state with { PendingInput = state.PendingInput with { FramerUtf8Remaining = 0 } };
            case "dcs-bytes":
                if (state.PendingInput.Dcs is not { RetainedBytes.Length: > 0 } dcsBytes)
                {
                    problem = $"fault '{fault}': the reconstructed state holds no retained DCS content bytes.";
                    return null;
                }
                path = "pendingInput.dcs.retainedBytes";
                return state with { PendingInput = state.PendingInput with { Dcs = dcsBytes with { RetainedBytes = "" } } };
            case "dcs-state":
                if (state.PendingInput.Dcs is not { State.Length: > 0 } dcsState)
                {
                    problem = $"fault '{fault}': the reconstructed state holds no DCS parser substate.";
                    return null;
                }
                path = "pendingInput.dcs.state";
                return state with { PendingInput = state.PendingInput with { Dcs = dcsState with { State = "" } } };
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
            case "pending-wrap":
                path = "cursor.pendingWrap";
                return state with { Cursor = state.Cursor with { PendingWrap = !state.Cursor.PendingWrap } };
            case "last-printed":
                if (state.LastPrinted is { } last)
                {
                    path = "lastPrinted.x";
                    return state with { LastPrinted = last with { X = last.X == 0 ? 1 : last.X - 1 } };
                }
                path = "lastPrinted";
                return state with { LastPrinted = new DiagnosticModelLastPrinted { Width = 1, Cell = new DiagnosticModelCell { Text = "X" } } };
            case "rendition":
            {
                path = "rendition.attributes";
                var attributes = state.Rendition.Attributes;
                return state with
                {
                    Rendition = state.Rendition with
                    {
                        Attributes = attributes.Contains("bold") ? [.. attributes.Where(a => a != "bold")] : ["bold", .. attributes],
                    },
                };
            }
            case "margins":
                path = "margins.top";
                return state with { Margins = state.Margins with { Top = state.Margins.Top == 0 ? 1 : 0 } };
            case "saved-cursor":
                if (state.SavedCursor is { } saved)
                {
                    path = "savedCursor.x";
                    return state with { SavedCursor = saved with { X = saved.X == 0 ? 1 : saved.X - 1 } };
                }
                path = "savedCursor";
                return state with { SavedCursor = new DiagnosticModelSavedCursor { X = 1, Protected = false } };
            case "pending-grapheme":
                path = "pendingGraphemeCombine";
                return state with { PendingGraphemeCombine = !state.PendingGraphemeCombine };
            case "activity":
                path = "activity.progressPercentage";
                return state with { Activity = state.Activity with { ProgressPercentage = state.Activity.ProgressPercentage is null ? 50 : null } };
            case "synchronized-update":
                path = "synchronizedUpdate.active";
                return state with
                {
                    SynchronizedUpdate = new DiagnosticSynchronizedUpdate
                    {
                        Active = !state.SynchronizedUpdate.Active,
                        StartedAtSequence = state.SynchronizedUpdate.StartedAtSequence,
                    },
                };
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
