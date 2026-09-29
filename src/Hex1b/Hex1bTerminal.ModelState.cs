using Hex1b.Diagnostics;
using Hex1b.Sixel;
using Hex1b.Theming;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    /// <summary>
    /// How <see cref="CaptureModelState"/> covers each instance field of the terminal and of the
    /// model's value types: <c>projected</c>, an exclusion with its reason, or <c>unsupported:</c> a
    /// surface the projection names when it holds state. The coverage census compares this map
    /// with the compiled fields, so a new field fails it until it is classified.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ModelStateFieldCoverage { get; } = BuildModelStateFieldCoverage();

    /// <summary>
    /// Reads the model's full text state (profile <c>text-state/1</c>) in one hold of the model lock.
    /// It never changes the model; lazily assigned text identities are left unassigned.
    /// </summary>
    internal DiagnosticModelState CaptureModelState()
    {
        lock (_bufferLock)
        {
            var styles = new ModelStyleTable();
            var screen = ProjectScreen(_screenBuffer, styles);
            var savedMain = _savedMainScreenBuffer is { } main ? ProjectScreen(main, styles) : null;

            DiagnosticModelHistory? history = null;
            if (_scrollbackBuffer is { } scrollback)
            {
                var entries = scrollback.GetEntries(scrollback.Count);
                var rows = new DiagnosticModelRow[entries.Length];
                for (var i = 0; i < entries.Length; i++)
                {
                    var row = entries[i].Row;
                    var cells = new DiagnosticModelCell[row.Cells.Length];
                    for (var column = 0; column < cells.Length; column++)
                        cells[column] = ProjectCell(row.Cells[column], styles);
                    rows[i] = new DiagnosticModelRow { Cells = cells, Id = entries[i].RowId, OriginalWidth = row.OriginalWidth };
                }
                history = new DiagnosticModelHistory { Capacity = scrollback.Capacity, NextRowId = scrollback.NextRowId, Rows = rows };
            }

            var tabColumns = new List<int>();
            for (var column = 0; column < _tabStops.Length; column++)
            {
                if (_tabStops[column])
                    tabColumns.Add(column);
            }

            var titleStack = new List<DiagnosticModelTitleEntry>(_titleStack.Count);
            foreach (var (title, icon) in _titleStack)
                titleStack.Add(new DiagnosticModelTitleEntry { Window = title, Icon = icon });

            var marks = new DiagnosticModelCommandMark[_commandMarks.Count];
            for (var i = 0; i < marks.Length; i++)
            {
                var mark = _commandMarks[i];
                marks[i] = new DiagnosticModelCommandMark
                {
                    Anchor = _commandAnchors[mark].Id,
                    Phase = ModelStateName(mark.Phase.ToString()),
                    ExitCode = mark.ExitCode,
                    RawParameters = mark.RawParameters,
                };
            }

            var rendition = new TerminalCell(" ", _currentForeground, _currentBackground, _currentAttributes,
                TrackedHyperlink: _currentHyperlink, UnderlineColor: _currentUnderlineColor, UnderlineStyle: _currentUnderlineStyle);
            var lastPrinted = _hasLastPrintedCell
                ? new DiagnosticModelLastPrinted
                {
                    X = _lastPrintedCellX,
                    Y = _lastPrintedCellY,
                    Width = _lastPrintedCellWidth,
                    Cell = ProjectCell(_lastPrintedCell, styles),
                }
                : null;

            var unsupported = new List<string>(2);
            if (_dcsByteStreamParser.IsInDcs)
                unsupported.Add("dcs-continuation");
            if (HoldsGraphicsState())
                unsupported.Add("graphics");
            unsupported.Sort(StringComparer.Ordinal);

            var activity = _activityState;
            return new DiagnosticModelState
            {
                ModelSequence = _modelSequence,
                Width = _width,
                Height = _height,
                ActiveBuffer = _inAlternateScreen ? "alternate" : "main",
                Screen = screen,
                SavedMainScreen = savedMain,
                History = history,
                NextCellSequence = _writeSequence,
                Cursor = new DiagnosticModelCursor
                {
                    X = _cursorX,
                    Y = _cursorY,
                    PendingWrap = _pendingWrap,
                    Visible = _cursorVisible,
                    Shape = _cursorShape,
                    Protected = _cursorProtected,
                },
                SavedCursor = _cursorSaved
                    ? new DiagnosticModelSavedCursor
                    {
                        X = _savedCursorX,
                        Y = _savedCursorY,
                        PendingWrap = _savedPendingWrap,
                        Protected = _savedCursorProtected,
                    }
                    : null,
                AlternateSavedCursor = new DiagnosticModelSavedCursor
                {
                    X = _alternateScreenSavedCursorX,
                    Y = _alternateScreenSavedCursorY,
                    PendingWrap = _alternateScreenSavedPendingWrap,
                },
                Modes = ProjectModes(),
                ProtectedMode = ModelStateName(_protectedMode.ToString()),
                Margins = new DiagnosticModelMargins { Top = _scrollTop, Bottom = _scrollBottom, Left = _marginLeft, Right = _marginRight },
                TabStops = new DiagnosticModelTabStops { Width = _tabStops.Length, Columns = tabColumns },
                Charsets = new DiagnosticModelCharsets
                {
                    G0 = _charsetG0.ToString(),
                    G1 = _charsetG1.ToString(),
                    G2 = _charsetG2.ToString(),
                    G3 = _charsetG3.ToString(),
                    Active = _activeCharsetSlot,
                },
                Rendition = ProjectStyle(rendition),
                Titles = new DiagnosticModelTitles { Window = _windowTitle, Icon = _iconName, Stack = titleStack },
                Activity = new DiagnosticModelActivity
                {
                    ProgressState = ModelStateName(activity.Progress.State.ToString()),
                    ProgressPercentage = activity.Progress.Percentage,
                    ShellPhase = ModelStateName(activity.ShellIntegration.Phase.ToString()),
                    LastExitCode = activity.ShellIntegration.LastExitCode,
                    WorkingDirectoryUri = activity.WorkingDirectory.Uri,
                    WorkingDirectoryHost = activity.WorkingDirectory.Host,
                    WorkingDirectoryPath = activity.WorkingDirectory.Path,
                },
                CommandMarks = marks,
                LastCommandAnchorId = _nextCommandAnchorId,
                LastPrinted = lastPrinted,
                PendingGraphemeCombine = _pendingGraphemeCombine,
                PendingInput = new DiagnosticModelPendingInput
                {
                    EscapePrefix = _incompleteSequenceBuffer,
                    Utf8 = Convert.ToBase64String(_pendingUtf8Output, 0, _pendingUtf8OutputLength),
                    GroundEscape = _dcsByteStreamParser.HasPendingGroundEscape,
                    FramerUtf8Remaining = _dcsByteStreamParser.PendingUtf8ContinuationBytes,
                },
                SynchronizedUpdate = new DiagnosticSynchronizedUpdate
                {
                    Active = _synchronizedOutputCompletion is not null,
                    StartedAtSequence = _synchronizedOutputCompletion is not null ? _synchronizedOutputStartedSequence : null,
                },
                Styles = styles.Styles,
                Unsupported = unsupported,
            };
        }
    }

    // Resident images, placements, a used placement counter, or Sixel registers that differ from
    // their defaults all change later output, and the text profile represents none of them.
    private bool HoldsGraphicsState()
    {
        if (_kgpGraphicsState.HasResidentState || _sixelGraphicsState.HasResidentState || _sixelPlacementSequence != 0)
            return true;
        var defaults = new SixelColorRegisters(_sixelColorRegisters.Policy);
        for (var register = 0; register < defaults.Count; register++)
        {
            if (_sixelColorRegisters.Get(register) != defaults.Get(register))
                return true;
        }
        return false;
    }

    private Dictionary<string, bool> ProjectModes() => new()
    {
        ["application-cursor-keys"] = _appCursorKeysMode,
        ["application-keypad"] = _appKeypadMode,
        ["bracketed-paste"] = _bracketedPasteMode,
        ["focus-event-reporting"] = _focusEventReporting,
        ["grapheme-clusters"] = _graphemeClusterMode,
        ["insert"] = _insertMode,
        ["left-right-margins"] = _declrmm,
        ["mouse-encoding-sgr"] = _mouseEncodingSgr,
        ["mouse-encoding-urxvt"] = _mouseEncodingUrxvt,
        ["mouse-encoding-utf8"] = _mouseEncodingUtf8,
        ["mouse-protocol-any"] = _mouseProtocolAny,
        ["mouse-protocol-button"] = _mouseProtocolButton,
        ["mouse-protocol-highlight"] = _mouseProtocolHighlight,
        ["mouse-protocol-normal"] = _mouseProtocolNormal,
        ["mouse-protocol-x10"] = _mouseProtocolX10,
        ["newline"] = _newlineMode,
        ["origin"] = _originMode,
        ["reverse-wrap"] = _reverseWrapMode,
        ["reverse-wrap-extended"] = _reverseWrapExtendedMode,
        ["sixel-cursor-to-right"] = _sixelCursorToRightMode,
        ["sixel-scrolling"] = _sixelScrollingMode,
        ["wraparound"] = _wraparoundMode,
    };

    private static DiagnosticModelRow[] ProjectScreen(TerminalCell[,] buffer, ModelStyleTable styles)
    {
        var height = buffer.GetLength(0);
        var width = buffer.GetLength(1);
        var rows = new DiagnosticModelRow[height];
        for (var row = 0; row < height; row++)
        {
            var cells = new DiagnosticModelCell[width];
            for (var column = 0; column < width; column++)
                cells[column] = ProjectCell(buffer[row, column], styles);
            rows[row] = new DiagnosticModelRow { Cells = cells };
        }
        return rows;
    }

    private static DiagnosticModelCell ProjectCell(in TerminalCell cell, ModelStyleTable styles) => new()
    {
        Text = cell.Character,
        Style = styles.IndexOf(cell),
        Sequence = cell.Sequence,
        WideWrapPadding = cell.IsWideWrapPadding,
    };

    private static DiagnosticModelStyle ProjectStyle(in TerminalCell cell)
    {
        var attributes = new List<string>();
        var bits = (ushort)cell.Attributes;
        for (var bit = 0; bit < 16; bit++)
        {
            if ((bits & (1 << bit)) != 0)
                attributes.Add(AttributeName((CellAttributes)(1 << bit), bit));
        }
        var hyperlink = cell.TrackedHyperlink?.Data;
        return new DiagnosticModelStyle
        {
            Attributes = attributes,
            Foreground = ColorName(cell.Foreground),
            Background = ColorName(cell.Background),
            UnderlineColor = ColorName(cell.UnderlineColor),
            UnderlineStyle = ModelStateName(cell.UnderlineStyle.ToString()),
            HyperlinkUri = hyperlink?.Uri,
            HyperlinkParameters = hyperlink?.Parameters,
        };
    }

    private static string AttributeName(CellAttributes attribute, int bit) => attribute switch
    {
        CellAttributes.Bold => "bold",
        CellAttributes.Dim => "dim",
        CellAttributes.Italic => "italic",
        CellAttributes.Underline => "underline",
        CellAttributes.Blink => "blink",
        CellAttributes.Reverse => "reverse",
        CellAttributes.Hidden => "hidden",
        CellAttributes.Strikethrough => "strikethrough",
        CellAttributes.Overline => "overline",
        CellAttributes.SoftWrap => "soft-wrap",
        CellAttributes.Protected => "protected",
        _ => $"bit-{bit}",
    };

    private static string? ColorName(Hex1bColor? color)
    {
        if (color is not { } value)
            return null;
        if (value.IsDefault)
            return "default";
        var rgb = $"#{value.R:x2}{value.G:x2}{value.B:x2}";
        return value.Kind == Hex1bColorKind.Rgb
            ? $"rgb:{rgb}"
            : $"{ModelStateName(value.Kind.ToString())}:{value.AnsiIndex}:{rgb}";
    }

    // PascalCase enum names as kebab-case contract names.
    private static string ModelStateName(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 4);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0)
                builder.Append('-');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }

    // Interns the styles cells use. Consecutive cells usually share a style, so the last one is
    // checked before the table.
    private sealed class ModelStyleTable
    {
        private readonly Dictionary<ModelStyleKey, int> _indexes = [];
        private ModelStyleKey _lastKey;
        private int _lastIndex = -1;

        public List<DiagnosticModelStyle> Styles { get; } = [];

        public int IndexOf(in TerminalCell cell)
        {
            var hyperlink = cell.TrackedHyperlink?.Data;
            var key = new ModelStyleKey(cell.Attributes, PackColor(cell.Foreground), PackColor(cell.Background),
                PackColor(cell.UnderlineColor), cell.UnderlineStyle, hyperlink?.Uri, hyperlink?.Parameters);
            if (_lastIndex >= 0 && key.Equals(_lastKey))
                return _lastIndex;
            if (!_indexes.TryGetValue(key, out var index))
            {
                index = Styles.Count;
                Styles.Add(ProjectStyle(cell));
                _indexes.Add(key, index);
            }
            _lastKey = key;
            _lastIndex = index;
            return index;
        }
    }

    // Every field of a color packed into one value, so style keys compare without boxing.
    private static ulong PackColor(Hex1bColor? color) => color is not { } value
        ? 0
        : 1UL << 48 | (value.IsDefault ? 1UL : 0) << 47 | (ulong)value.Kind << 32 | (ulong)value.AnsiIndex << 24
          | (ulong)value.R << 16 | (ulong)value.G << 8 | value.B;

    private readonly record struct ModelStyleKey(
        CellAttributes Attributes,
        ulong Foreground,
        ulong Background,
        ulong UnderlineColor,
        UnderlineStyle UnderlineStyle,
        string? HyperlinkUri,
        string? HyperlinkParameters);

    private static Dictionary<string, string> BuildModelStateFieldCoverage()
    {
        const string Projected = "projected";
        const string Clock = "clock: a wall-clock or timer reading";
        const string Identity = "identity: differs between otherwise identical models, or is assigned lazily when text is read";
        const string Configuration = "configuration: recorded in the case manifest, fixed after construction";
        const string Infrastructure = "infrastructure: pumps, adapters, locks, callbacks, diagnostics or caches, not model state";
        const string InputPath = "input-path: state of the input direction, not of the output model";
        const string Anchors = "identity: caller-created text anchors and their views; command marks are projected";
        const string Graphics = "unsupported:graphics";
        const string Dcs = "unsupported:dcs-continuation";

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        void Set(string value, params string[] names)
        {
            foreach (var name in names)
                map.Add(name, value);
        }

        Set(Projected,
            "_width", "_height", "_modelSequence", "_inAlternateScreen", "_screenBuffer", "_savedMainScreenBuffer",
            "_scrollbackBuffer", "_writeSequence", "_cursorX", "_cursorY", "_pendingWrap", "_cursorVisible", "_cursorShape",
            "_cursorProtected", "_cursorSaved", "_savedCursorX", "_savedCursorY", "_savedPendingWrap", "_savedCursorProtected",
            "_alternateScreenSavedCursorX", "_alternateScreenSavedCursorY", "_alternateScreenSavedPendingWrap",
            "_appCursorKeysMode", "_appKeypadMode", "_bracketedPasteMode", "_focusEventReporting", "_graphemeClusterMode",
            "_insertMode", "_declrmm", "_mouseEncodingSgr", "_mouseEncodingUrxvt", "_mouseEncodingUtf8", "_mouseProtocolAny",
            "_mouseProtocolButton", "_mouseProtocolHighlight", "_mouseProtocolNormal", "_mouseProtocolX10", "_newlineMode",
            "_originMode", "_reverseWrapMode", "_reverseWrapExtendedMode", "_sixelCursorToRightMode", "_sixelScrollingMode",
            "_wraparoundMode", "_protectedMode", "_scrollTop", "_scrollBottom", "_marginLeft", "_marginRight", "_tabStops",
            "_charsetG0", "_charsetG1", "_charsetG2", "_charsetG3", "_activeCharsetSlot", "_currentAttributes",
            "_currentForeground", "_currentBackground", "_currentUnderlineColor", "_currentUnderlineStyle", "_currentHyperlink",
            "_windowTitle", "_iconName", "_titleStack", "_activityState", "_commandMarks", "_nextCommandAnchorId",
            "_hasLastPrintedCell", "_lastPrintedCell", "_lastPrintedCellX", "_lastPrintedCellY", "_lastPrintedCellWidth",
            "_pendingGraphemeCombine", "_incompleteSequenceBuffer", "_pendingUtf8Output", "_pendingUtf8OutputLength",
            "_synchronizedOutputCompletion", "_synchronizedOutputStartedSequence");
        // The decoder's held bytes are the ones _pendingUtf8Output tracks; the byte framer's pending
        // escape and UTF-8 count are projected, and a DCS in progress is named unsupported.
        Set(Projected, "_utf8Decoder");
        Set(Dcs, "_dcsByteStreamParser");
        Set(Graphics, "_sixelGraphicsState", "_kgpGraphicsState", "_sixelColorRegisters", "_sixelPlacementSequence");
        Set(Clock, "_sessionStart", "_synchronizedOutputStarted", "_synchronizedOutputTimer", "_escapeFlushTimer",
            "_kgpAnimationTimer");
        Set(Identity, "<DiagnosticSessionId>k__BackingField", "_textGeneration", "_nextTextRowId", "_textScreenRowIds",
            "_textHistoryRowIds", "_savedMainTextRowIds", "_savedMainTextHistoryRowIds", "_commandAnchors",
            "_textAnchorReflowPending", "_textAnchorRetentionChanged", "_bufferGeometryVersion");
        Set(Anchors, "_textAnchors", "_historyTextAnchors", "_customAnchorViews", "_markerDetailsViews");
        Set(Configuration, "_commandMarkHistoryCapacity", "_customMarkerLimit", "_escapeTimeout", "_caseConfiguration",
            "_sixelCellMetricsOverride");
        Set(InputPath, "_activePasteContext", "_inBracketedPaste", "_incompleteInputSequenceBuffer", "_inputUtf8Decoder");
        Set(Infrastructure,
            "_bufferLock", "_captures", "_captureSequence", "_captureApplicationDepth", "_diagnosticCase", "_disposedCase",
            "_caseIngress", "_caseIngressPending", "_disposeCts", "_disposed", "_hmp1OutputStateLock", "_hmp1State",
            "_deferHmp1ReplayCallbacks", "<Hmp1ReplayActivityState>k__BackingField",
            "<Hmp1ReplayCommandMarkState>k__BackingField", "<Hmp1ReplayScrollbackState>k__BackingField",
            "_inputProcessingTask", "_outputProcessingTask", "_metrics", "_nativeDelivery", "<InputMilestones>k__BackingField",
            "_presentation", "_presentationFilters", "_presentationInputChannel", "_presentationOwnsResize", "_pumpFaultTcs",
            "_runCallback", "_scrollbackCallback", "_timeProvider", "_workload", "_workloadFilters",
            "_workloadInputWriteLock", "_outputBytesRead", "_trackedObjects", "_currentGraphicsImpacts",
            "_activityRestoreBaseline", "_activityRestoreDepth", "_selectedHistoryCount",
            "ActivityStateChanged", "CommandMarkAdded", "IconNameChanged", "PresentationInvalidated", "ProgressChanged",
            "ShellIntegrationChanged", "WindowTitleChanged", "WorkingDirectoryChanged");

        // The model's value types.
        Set(Projected, "TerminalCell.<Character>k__BackingField", "TerminalCell.<Foreground>k__BackingField",
            "TerminalCell.<Background>k__BackingField", "TerminalCell.<Attributes>k__BackingField",
            "TerminalCell.<Sequence>k__BackingField", "TerminalCell.<TrackedHyperlink>k__BackingField",
            "TerminalCell.<UnderlineColor>k__BackingField", "TerminalCell.<UnderlineStyle>k__BackingField",
            "TerminalCell.<IsWideWrapPadding>k__BackingField",
            "ScrollbackRow.<Cells>k__BackingField", "ScrollbackRow.<OriginalWidth>k__BackingField",
            "ScrollbackBuffer._rows", "ScrollbackBuffer._rowIds", "ScrollbackBuffer._head", "ScrollbackBuffer._count",
            "ScrollbackBuffer._nextRowId", "ScrollbackBuffer.<Capacity>k__BackingField",
            "TerminalCommandMark.<Phase>k__BackingField", "TerminalCommandMark.<ExitCode>k__BackingField",
            "TerminalCommandMark.<RawParameters>k__BackingField",
            "TerminalActivityState.<Progress>k__BackingField", "TerminalActivityState.<ShellIntegration>k__BackingField",
            "TerminalActivityState.<WorkingDirectory>k__BackingField");
        Set(Clock, "TerminalCell.<WrittenAt>k__BackingField", "ScrollbackRow.<Timestamp>k__BackingField");
        Set(Identity, "TerminalCommandMark.<TextGeneration>k__BackingField", "TerminalCommandMark.<TextRowId>k__BackingField");
        Set(Infrastructure, "ScrollbackBuffer._rowPruned", "TerminalCommandMark._parameters");
        return map;
    }
}
