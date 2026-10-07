using System.Runtime.InteropServices;
using Hex1b.Diagnostics;
using Hex1b.Theming;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    /// <summary>
    /// Restores a <c>text-state/3</c> projection into this model: the inverse of the model-state projection
    /// for the active text buffer, the retained history (each row with its cells, original width and identity, and the
    /// next identity; rows are stamped with this model's clock) and, on the alternate screen, the saved main screen.
    /// Only the case reapplier calls
    /// this, on a terminal whose pumps never started. The geometry is set through the model's own resize (an empty
    /// model, so nothing reflows). A state on the alternate screen is entered through the model's own entry: the main
    /// screen's cells are written at the saved screen's geometry, the model enters the alternate screen (which saves
    /// them, and selects the alternate screen for its graphics and text coordinates), then resizes to the active
    /// geometry. Every other projected field is then written in one hold of the model lock. Restored hyperlinks are this model's own
    /// tracked objects, and an open synchronized update is re-entered on this model's clock. Identity, clock and absolute
    /// write-order fields keep this model's values; fresh write sequences preserve the projected global equality classes.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The state holds something this restore cannot represent (malformed pending input, an unsupported surface, a history
    /// this model's configured scrollback cannot hold as recorded, or malformed titles or command marks), or the
    /// model has already applied output.
    /// </exception>
    internal void RestoreModelState(DiagnosticModelState state)
    {
        if (UnrestorableField(state) is { } field)
            throw new InvalidOperationException($"The state cannot be restored: it holds {field}.");
        Dictionary<int, long> writeClasses;
        lock (_bufferLock)
        {
            if (_modelSequence != 0 || (_scrollbackBuffer?.Count ?? 0) != 0)
                throw new InvalidOperationException("Only a model that has applied nothing can be restored.");
            if (HistoryProblem(state.History, state.Styles, _scrollbackBuffer) is { } historyProblem)
                throw new InvalidOperationException($"The state cannot be restored: its history {historyProblem}");
            if (WriteClassesProblem(state, out writeClasses) is { } classProblem)
                throw new InvalidOperationException($"The state cannot be restored: it holds {classProblem}.");
            if (state.CommandMarks.Count > _commandMarkHistoryCapacity)
                throw new InvalidOperationException(
                    $"The state cannot be restored: its {state.CommandMarks.Count} command marks exceed the configured capacity of {_commandMarkHistoryCapacity}.");
            // Marks last: their positions are checked against rows already validated above.
            if (CommandMarksProblem(state) is { } marksProblem)
                throw new InvalidOperationException($"The state cannot be restored: it holds {marksProblem}.");
        }

        var restoredDcs = PrepareDcsRestore(state.PendingInput);
        try
        {
            var cells = new TerminalCell[state.Styles.Count];
            var built = new bool[state.Styles.Count];
            if (state.SavedMainScreen is { } savedMain)
            {
                var savedHeight = savedMain.Count;
                var savedWidth = savedHeight == 0 ? 0 : savedMain[0].Cells.Count;
                if (savedWidth != _width || savedHeight != _height)
                    Resize(savedWidth, savedHeight);
                lock (_bufferLock)
                {
                    RestoreScreenUnsafe(savedMain, state.Styles, cells, built, writeClasses);
                    DoEnterAlternateScreen();
                }
            }

            if (state.Width != _width || state.Height != _height)
                Resize(state.Width, state.Height);

            lock (_bufferLock)
            {
                // The resizes of an empty model move no text into history; the model is still otherwise fresh. The
                // retained rows are restored in order with their identities, and the next identity continues past
                // cleared or evicted rows.
                _scrollbackBuffer?.Clear();
                if (state.History is { } history && _scrollbackBuffer is { } scrollback)
                {
                    var rows = new (TerminalCell[] Cells, int OriginalWidth, long RowId)[history.Rows.Count];
                    for (var row = 0; row < rows.Length; row++)
                    {
                        var projected = history.Rows[row];
                        var rowCells = new TerminalCell[projected.Cells.Count];
                        var run = 0;
                        for (var column = 0; column < rowCells.Length; column++)
                            rowCells[column] = RestoreCell(projected.Cells[column], state.Styles, cells, built,
                                RestoredSequenceUnsafe(projected.Cells[column], UnwrittenAt(projected.Unwritten, column, ref run), writeClasses));
                        rows[row] = (rowCells, projected.OriginalWidth ?? rowCells.Length, projected.Id ?? 0);
                    }
                    scrollback.RestoreRows(rows, history.NextRowId, _timeProvider.GetUtcNow());
                }
                RestoreScreenUnsafe(state.Screen, state.Styles, cells, built, writeClasses);

                _cursorX = state.Cursor.X;
                _cursorY = state.Cursor.Y;
                _pendingWrap = state.Cursor.PendingWrap;
                _cursorVisible = state.Cursor.Visible;
                _cursorShape = state.Cursor.Shape;
                _cursorProtected = state.Cursor.Protected;
                _cursorSaved = state.SavedCursor is not null;
                if (state.SavedCursor is { } saved)
                {
                    _savedCursorX = saved.X;
                    _savedCursorY = saved.Y;
                    _savedPendingWrap = saved.PendingWrap;
                    _savedCursorProtected = saved.Protected ?? false;
                }
                _alternateScreenSavedCursorX = state.AlternateSavedCursor.X;
                _alternateScreenSavedCursorY = state.AlternateSavedCursor.Y;
                _alternateScreenSavedPendingWrap = state.AlternateSavedCursor.PendingWrap;

                foreach (var (name, value) in state.Modes)
                    RestoreMode(name, value);
                _protectedMode = ParseName<ProtectedMode>(state.ProtectedMode);
                _scrollTop = state.Margins.Top;
                _scrollBottom = state.Margins.Bottom;
                _marginLeft = state.Margins.Left;
                _marginRight = state.Margins.Right;
                _tabStops = new bool[state.TabStops.Width];
                foreach (var column in state.TabStops.Columns)
                    _tabStops[column] = true;
                _charsetG0 = state.Charsets.G0[0];
                _charsetG1 = state.Charsets.G1[0];
                _charsetG2 = state.Charsets.G2[0];
                _charsetG3 = state.Charsets.G3[0];
                _activeCharsetSlot = state.Charsets.Active;

                var rendition = state.Rendition;
                _currentForeground = ParseColor(rendition.Foreground);
                _currentBackground = ParseColor(rendition.Background);
                _currentAttributes = ParseAttributes(rendition.Attributes);
                _currentUnderlineColor = ParseColor(rendition.UnderlineColor);
                _currentUnderlineStyle = ParseName<UnderlineStyle>(rendition.UnderlineStyle);
                _currentHyperlink?.Release();
                _currentHyperlink = rendition.HyperlinkUri is { } uri
                    ? _trackedObjects.GetOrCreateHyperlink(uri, rendition.HyperlinkParameters ?? "")
                    : null;

                var activity = state.Activity;
                SetActivityState(new TerminalActivityState(
                    new TerminalProgress(ParseName<TerminalProgressState>(activity.ProgressState), activity.ProgressPercentage),
                    new TerminalShellIntegration(ParseName<TerminalShellIntegrationPhase>(activity.ShellPhase), activity.LastExitCode),
                    new TerminalWorkingDirectory(activity.WorkingDirectoryUri, activity.WorkingDirectoryHost, activity.WorkingDirectoryPath)));

                // Titles as projected (already normalized); the projection lists the stack top first.
                _windowTitle = state.Titles.Window;
                _iconName = state.Titles.Icon;
                _titleStack.Clear();
                for (var i = state.Titles.Stack.Count - 1; i >= 0; i--)
                    _titleStack.Push((state.Titles.Stack[i].Window, state.Titles.Stack[i].Icon));
                // Marks last: their positions are in the screens and history restored above.
                RestoreCommandMarksUnsafe(state.CommandMarks, state.LastCommandAnchorId);

                _hasLastPrintedCell = state.LastPrinted is not null;
                if (state.LastPrinted is { } last)
                {
                    _lastPrintedCellX = last.X;
                    _lastPrintedCellY = last.Y;
                    _lastPrintedCellWidth = last.Width;
                    // The original holds the cell as a copy, not a counted reference: the restored one does too. Its write
                    // identity is its same-write buffer cell's restored one (shared with that cell's whole class), otherwise
                    // its own fresh one, never zero (which never-written and erased cells hold): later modifiers attach to
                    // it exactly when they attached in the original (issue 60, decision D1).
                    var cell = RestoreCell(last.Cell, state.Styles, cells, built, last.SameWrite switch
                    {
                        { Buffer: "history" } at => _scrollbackBuffer!.GetEntryAt(at.Row).Row.Cells[at.Column].Sequence,
                        { Buffer: "savedMainScreen" } at => _savedMainScreenBuffer![at.Row, at.Column].Sequence,
                        { } at => _screenBuffer[at.Row, at.Column].Sequence,
                        null => ++_writeSequence,
                    });
                    cell.TrackedHyperlink?.Release();
                    _lastPrintedCell = cell;
                }
                _pendingGraphemeCombine = state.PendingGraphemeCombine;

                _modelSequence = state.ModelSequence;
                // Install the preflighted parser without replaying checkpoint bytes as another application.
                RestorePendingInputUnsafe(state.PendingInput, restoredDcs);
                restoredDcs = null;
                CommitOutputContinuationUnsafe();
                if (state.SynchronizedUpdate.Active)
                {
                    // Re-entered on this model's clock, so the timeout fires only when that clock reaches it.
                    SetSynchronizedOutputMode(true);
                    _synchronizedOutputStartedSequence = state.SynchronizedUpdate.StartedAtSequence ?? state.ModelSequence;
                }
            }
        }
        finally
        {
            restoredDcs?.Dispose();
        }
    }

    // Why a recorded history cannot be restored into this model's configured scrollback, or null: its presence and
    // capacity must be the configuration's, each row whole at a width of at least 1, with as many cells as that width
    // (every push lays a row out at the width it records; a resize has no upper width, so neither has a row), and its
    // identities a ring the buffer can hold as recorded.
    private static string? HistoryProblem(DiagnosticModelHistory? history, IReadOnlyList<DiagnosticModelStyle>? styles, ScrollbackBuffer? scrollback)
    {
        if (history is null)
            return scrollback is null ? null : "is absent, but the model is configured with a scrollback.";
        if (scrollback is null)
            return "is present, but the model is configured without a scrollback.";
        if (history.Capacity != scrollback.Capacity)
            return $"capacity {history.Capacity} is not the configured {scrollback.Capacity}.";
        if (history.Rows is null)
            return "has no rows.";
        foreach (var row in history.Rows)
        {
            if (row is null || row.Id is null || row.OriginalWidth is not { } width || row.Cells is null)
                return "has a row without its identity, original width or cells.";
            if (width < 1)
                return $"has a row of original width {width}, below 1.";
            if (row.Cells.Count != width)
                return $"has a row of {row.Cells.Count} cells at original width {width}.";
        }
        if (RowsProblem(history.Rows, "history", styles) is { } rowsProblem)
            return $"has {rowsProblem}.";
        return ScrollbackBuffer.RestoreProblem(history.Rows.Count, history.Rows.Select(row => row.Id!.Value), history.NextRowId, scrollback.Capacity);
    }

    // Writes projected rows into the active screen buffer, which has their geometry.
    private void RestoreScreenUnsafe(IReadOnlyList<DiagnosticModelRow> rows, IReadOnlyList<DiagnosticModelStyle> styles,
        TerminalCell[] cells, bool[] built, Dictionary<int, long> writeClasses)
    {
        for (var row = 0; row < rows.Count; row++)
        {
            var projected = rows[row];
            var run = 0;
            for (var column = 0; column < projected.Cells.Count; column++)
                SetCell(row, column, RestoreCell(projected.Cells[column], styles, cells, built,
                    RestoredSequenceUnsafe(projected.Cells[column], UnwrittenAt(projected.Unwritten, column, ref run), writeClasses)),
                    damageSixel: false);
        }
    }

    // The first field of a state this restore cannot represent, or null. The case's start policy
    // decides what a start checkpoint may hold; this is the restore's own precondition.
    private static string? UnrestorableField(DiagnosticModelState state)
    {
        if (state.Profile != DiagnosticCaseCheckpointProfiles.TextState)
            return $"profile '{state.Profile}'";

        if ((state.SavedMainScreen is not null) != (state.ActiveBuffer == "alternate") || state.ActiveBuffer is not ("main" or "alternate"))
            return $"an active buffer '{state.ActiveBuffer}' that does not match its saved main screen";
        var historyLast = state.History?.Rows is { Count: > 0 } historyRows ? historyRows[^1] : null;
        if (RowsProblem(state.Screen, "a screen", state.Styles, state.ActiveBuffer == "main" ? historyLast : null) is { } screenProblem)
            return screenProblem;
        if (state.SavedMainScreen is not null && RowsProblem(state.SavedMainScreen, "a saved main screen", state.Styles, historyLast) is { } savedProblem)
            return savedProblem;
        if (state.SavedMainScreen is { Count: > 0 } saved && saved.Any(row => row.Cells.Count != saved[0].Cells.Count))
            return "a saved main screen whose rows do not share one width";
        if (state.Titles is null || state.Titles.Window is null || state.Titles.Icon is null || state.Titles.Stack is null
            || state.Titles.Stack.Any(entry => entry?.Window is null || entry.Icon is null))
            return "titles with a missing field";
        if (state.CommandMarks is null)
            return "command marks: missing";
        if (PendingInputProblem(state.PendingInput) is { } pendingProblem)
            return pendingProblem;
        if (state.Unsupported.Count > 0)
            return $"unsupported surfaces ({string.Join(", ", state.Unsupported)})";
        if (state.Screen.Count != state.Height || state.Screen.Any(row => row.Cells.Count != state.Width))
            return "a screen whose rows do not match its geometry";
        // A copy that is the same write as a buffer cell needs that cell to exist and to be written.
        if (state.LastPrinted?.SameWrite is { } same)
        {
            var rows = same.Buffer switch
            {
                "history" => state.History?.Rows,
                "savedMainScreen" => state.SavedMainScreen,
                "screen" => state.Screen,
                _ => null,
            };
            // This runs before the rows themselves are validated: a missing row or cell list refuses here, never throws.
            var lastRun = 0;
            if (rows is null || same.Row < 0 || same.Row >= rows.Count || rows[same.Row]?.Cells is not { } cells
                || same.Column < 0 || same.Column >= cells.Count || UnwrittenAt(rows[same.Row].Unwritten, same.Column, ref lastRun))
                return "a last-printed same-write cell that is not a written buffer cell";
        }
        return null;
    }

    // Count classes across every buffer before model mutation. Negative values count one or at least two members;
    // each is replaced by one fresh sequence on its first restored occurrence, even if saved main is written first.
    private static string? WriteClassesProblem(DiagnosticModelState state, out Dictionary<int, long> classes)
    {
        var counted = new Dictionary<int, long>();
        classes = counted;
        Count(state.History?.Rows);
        Count(state.SavedMainScreen);
        Count(state.Screen);
        if (state.LastPrinted is { Cell.WriteClass: not 0 })
            return "a write class on the last-printed cell, which is not a buffer cell";
        foreach (var (label, count) in classes)
            if (count == -1)
                return $"write class {label} with only one buffer cell";
        return null;

        void Count(IReadOnlyList<DiagnosticModelRow>? rows)
        {
            if (rows is null)
                return;
            foreach (var row in rows)
                foreach (var cell in row.Cells)
                    if (cell.WriteClass > 0)
                    {
                        ref var count = ref CollectionsMarshal.GetValueRefOrAddDefault(counted, cell.WriteClass, out var present);
                        count = present ? -2 : -1;
                    }
        }
    }

    // Never-written cells retain zero. Every singleton gets its own value, and every repeated class one shared value.
    // Advancing the model's counter as values are assigned keeps all subsequent output above every restored sequence.
    private long RestoredSequenceUnsafe(DiagnosticModelCell cell, bool unwritten, Dictionary<int, long> classes)
    {
        if (unwritten)
            return 0;
        if (cell.WriteClass == 0)
            return ++_writeSequence;
        ref var sequence = ref CollectionsMarshal.GetValueRefOrAddDefault(classes, cell.WriteClass, out _);
        if (sequence <= 0)
            sequence = ++_writeSequence;
        return sequence;
    }

    // Read validated runs in one pass without a per-row mask allocation.
    private static bool UnwrittenAt(IReadOnlyList<int>? runs, int column, ref int run)
    {
        if (runs is null)
            return false;
        while (run + 1 < runs.Count && column >= (long)runs[run] + runs[run + 1])
            run += 2;
        return run + 1 < runs.Count && column >= runs[run];
    }

    // Why projected rows cannot be restored, or null: runs are ordered start/count pairs within the row; classes are
    // nonnegative and absent on unwritten cells; continuation exactly matches an empty cell's equality with its eligible
    // predecessor (left, or the preceding row's last cell across a soft wrap). Equality can also exist without a visible
    // continuation: class discovery is not gated by text, adjacency or soft wraps.
    private static string? RowsProblem(IReadOnlyList<DiagnosticModelRow>? rows, string what,
        IReadOnlyList<DiagnosticModelStyle>? styles, DiagnosticModelRow? before = null)
    {
        if (rows is null)
            return $"{what}: missing";
        for (var row = 0; row < rows.Count; row++)
        {
            if (rows[row]?.Cells is not { } cells)
                return $"{what} row {row}: missing";
            if (rows[row].Unwritten is { } runs)
            {
                var end = 0;
                if (runs.Count == 0 || runs.Count % 2 != 0)
                    return $"{what} row {row}: never-written runs that are not start, count pairs";
                for (var i = 0; i < runs.Count; i += 2)
                {
                    if (runs[i] < end || runs[i + 1] < 1 || (long)runs[i] + runs[i + 1] > cells.Count)
                        return $"{what} row {row}: a never-written run at {runs[i]} that is out of order or outside the row";
                    end = runs[i] + runs[i + 1];
                }
            }
            var run = 0;
            // The row before in reading order is eligible only across a soft wrap.
            var previous = row > 0 ? rows[row - 1] : before;
            DiagnosticModelCell? leftCell = previous?.Cells is { Count: > 0 } previousCells && SoftWraps(previous, styles)
                ? previousCells[^1] : null;
            var leftUnwritten = leftCell is not null && previous!.Unwritten is { Count: >= 2 } previousRuns
                && previousRuns[^2] >= 0 && previousRuns[^1] > 0
                && (long)previousRuns[^2] + previousRuns[^1] == previous.Cells.Count;
            for (var column = 0; column < cells.Count; column++)
            {
                var cell = cells[column];
                var unwritten = UnwrittenAt(rows[row].Unwritten, column, ref run);
                if (cell.WriteClass < 0 || unwritten && cell.WriteClass != 0)
                    return $"{what} row {row}: an invalid write class {cell.WriteClass} at column {column}";
                var shares = leftCell is { } left && (unwritten && leftUnwritten
                    || !unwritten && !leftUnwritten && cell.WriteClass > 0 && cell.WriteClass == left.WriteClass);
                var continues = cell.Text is { Length: 0 } && shares;
                if (cell.Continues != continues)
                    return $"{what} row {row}: a continuation at column {column} that continues nothing or contradicts its write class";
                leftCell = cell;
                leftUnwritten = unwritten;
            }
        }
        return null;
    }

    // Whether a projected row soft-wraps: its last cell's style carries the soft-wrap attribute. A style index outside the
    // table is not a soft wrap.
    private static bool SoftWraps(DiagnosticModelRow row, IReadOnlyList<DiagnosticModelStyle>? styles) =>
        row.Cells is { Count: > 0 } cells && styles is not null && cells[^1] is { } last && (uint)last.Style < (uint)styles.Count
        && styles[last.Style]?.Attributes?.Contains("soft-wrap") == true;

    private TerminalCell RestoreCell(DiagnosticModelCell projected, IReadOnlyList<DiagnosticModelStyle> styles,
        TerminalCell[] cells, bool[] built, long sequence = 0)
    {
        if (!built[projected.Style])
        {
            var style = styles[projected.Style];
            cells[projected.Style] = new TerminalCell(" ", ParseColor(style.Foreground), ParseColor(style.Background),
                ParseAttributes(style.Attributes), UnderlineColor: ParseColor(style.UnderlineColor),
                UnderlineStyle: ParseName<UnderlineStyle>(style.UnderlineStyle));
            built[projected.Style] = true;
        }

        var template = styles[projected.Style];
        var hyperlink = template.HyperlinkUri is { } uri ? _trackedObjects.GetOrCreateHyperlink(uri, template.HyperlinkParameters ?? "") : null;
        return cells[projected.Style] with
        {
            Character = projected.Text,
            TrackedHyperlink = hyperlink,
            IsWideWrapPadding = projected.WideWrapPadding,
            Sequence = sequence,
        };
    }

    private void RestoreMode(string name, bool value)
    {
        switch (name)
        {
            case "application-cursor-keys": _appCursorKeysMode = value; break;
            case "application-keypad": _appKeypadMode = value; break;
            case "bracketed-paste": _bracketedPasteMode = value; break;
            case "focus-event-reporting": _focusEventReporting = value; break;
            case "grapheme-clusters": _graphemeClusterMode = value; break;
            case "insert": _insertMode = value; break;
            case "left-right-margins": _declrmm = value; break;
            case "mouse-encoding-sgr": _mouseEncodingSgr = value; break;
            case "mouse-encoding-urxvt": _mouseEncodingUrxvt = value; break;
            case "mouse-encoding-utf8": _mouseEncodingUtf8 = value; break;
            case "mouse-protocol-any": _mouseProtocolAny = value; break;
            case "mouse-protocol-button": _mouseProtocolButton = value; break;
            case "mouse-protocol-highlight": _mouseProtocolHighlight = value; break;
            case "mouse-protocol-normal": _mouseProtocolNormal = value; break;
            case "mouse-protocol-x10": _mouseProtocolX10 = value; break;
            case "newline": _newlineMode = value; break;
            case "origin": _originMode = value; break;
            case "reverse-wrap": _reverseWrapMode = value; break;
            case "reverse-wrap-extended": _reverseWrapExtendedMode = value; break;
            case "sixel-cursor-to-right": _sixelCursorToRightMode = value; break;
            case "sixel-scrolling": _sixelScrollingMode = value; break;
            case "wraparound": _wraparoundMode = value; break;
            default: throw new InvalidOperationException($"The state names an unknown mode '{name}'.");
        }
    }

    // The inverse of ColorName: "default", "rgb:#rrggbb", or "<kind>:<index>:#rrggbb".
    private static Hex1bColor? ParseColor(string? name)
    {
        if (name is null)
            return null;
        if (name == "default")
            return Hex1bColor.Default;
        var parts = name.Split(':');
        var rgb = parts[^1];
        if (rgb.Length != 7 || rgb[0] != '#')
            throw new InvalidOperationException($"The state names an unknown colour '{name}'.");
        var r = Convert.ToByte(rgb.Substring(1, 2), 16);
        var g = Convert.ToByte(rgb.Substring(3, 2), 16);
        var b = Convert.ToByte(rgb.Substring(5, 2), 16);
        if (parts.Length == 2 && parts[0] == "rgb")
            return Hex1bColor.FromRgb(r, g, b);
        if (parts.Length != 3)
            throw new InvalidOperationException($"The state names an unknown colour '{name}'.");
        var index = byte.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture);
        // The same index bounds the HMP1 row codec enforces for these kinds.
        return ParseName<Hex1bColorKind>(parts[0]) switch
        {
            Hex1bColorKind.Standard when index < 8 => Hex1bColor.FromStandard(index, r, g, b),
            Hex1bColorKind.Bright when index < 8 => Hex1bColor.FromBright(index, r, g, b),
            Hex1bColorKind.Indexed => Hex1bColor.FromIndexed(index, r, g, b),
            _ => throw new InvalidOperationException($"The state names an unknown colour '{name}'."),
        };
    }

    // The inverse of AttributeName.
    private static CellAttributes ParseAttributes(IReadOnlyList<string> names)
    {
        var attributes = CellAttributes.None;
        foreach (var name in names)
        {
            attributes |= name switch
            {
                "bold" => CellAttributes.Bold,
                "dim" => CellAttributes.Dim,
                "italic" => CellAttributes.Italic,
                "underline" => CellAttributes.Underline,
                "blink" => CellAttributes.Blink,
                "reverse" => CellAttributes.Reverse,
                "hidden" => CellAttributes.Hidden,
                "strikethrough" => CellAttributes.Strikethrough,
                "overline" => CellAttributes.Overline,
                "soft-wrap" => CellAttributes.SoftWrap,
                "protected" => CellAttributes.Protected,
                _ when name.StartsWith("bit-", StringComparison.Ordinal)
                    && int.TryParse(name.AsSpan(4), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var bit)
                    && bit is >= 0 and < 16 => (CellAttributes)(1 << bit),
                _ => throw new InvalidOperationException($"The state names an unknown attribute '{name}'."),
            };
        }
        return attributes;
    }

    // The inverse of ModelStateName: a kebab-case contract name back to its enum value. Only a name the projection
    // itself writes is accepted (the value must name back to it), so numbers and combined names are refused.
    private static T ParseName<T>(string name) where T : struct, Enum =>
        TryParseName<T>(name, out var value)
            ? value
            : throw new InvalidOperationException($"The state names an unknown {typeof(T).Name} '{name}'.");

    // The names the projection writes for an enum, and only those.
    private static bool TryParseName<T>(string? name, out T value) where T : struct, Enum
    {
        value = default;
        return name is not null && Enum.TryParse(name.Replace("-", "", StringComparison.Ordinal), ignoreCase: true, out value)
            && Enum.IsDefined(value) && ModelStateName(value.ToString()) == name;
    }
}
