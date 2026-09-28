using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b.Diagnostics;

/// <summary>
/// Projects a completed application pass into an immutable diagnostic frame. Runs on the
/// application loop at the end of a pass, so every value it reads belongs to that pass; the
/// result holds copies only, never live nodes or states.
/// </summary>
internal static class ApplicationFrameProjector
{
    /// <summary>
    /// Counts projections made in the current async flow while a test has set a counter; lets a
    /// test prove an app projects nothing without diagnostics, isolated from parallel tests.
    /// </summary>
    internal static readonly AsyncLocal<StrongBox<int>?> ProjectionsForTesting = new();

    // Timings are null when diagnostic timing is off, which also leaves node timings out.
    internal static DiagnosticApplicationFrame Project(Hex1bNode? root, FocusRing focusRing, string applicationInstanceId,
        long frameId, int columns, int rows, bool wroteOutput, ApplicationPassTimings? timings)
    {
        if (ProjectionsForTesting.Value is { } counter)
            Interlocked.Increment(ref counter.Value);

        var focusables = focusRing.Focusables;
        var focusedIndex = IndexOfFocused(focusables);
        var walk = new Walk(new Rect(0, 0, columns, rows), timings is null ? null : Stopwatch.GetTimestamp(),
            focusedIndex < 0 ? null : focusables[focusedIndex]);
        var projectedRoot = root is null ? null : walk.Node(root, clipChain: null);
        return new DiagnosticApplicationFrame
        {
            ApplicationInstanceId = applicationInstanceId,
            FrameId = frameId,
            Columns = columns,
            Rows = rows,
            WroteOutput = wroteOutput,
            Root = projectedRoot,
            Popups = ProjectPopups(walk.PopupHost),
            Focus = ProjectFocus(focusables, focusedIndex, focusRing.LastHitTestDebug),
            FocusedEditor = walk.FocusedEditor ?? (walk.Focused is { } focused ? EditorOf(focused, includeText: true) : null),
            Timings = timings is { } pass
                ? new DiagnosticFrameTimings
                {
                    BuildMs = Milliseconds(pass.BuildTicks),
                    ReconcileMs = Milliseconds(pass.ReconcileTicks),
                    RenderMs = Milliseconds(pass.RenderTicks),
                }
                : null,
        };
    }

    // One tree walk: nodes with their effective clip, the popup host, and the focused editor.
    private sealed class Walk(Rect screen, long? now, Hex1bNode? focused)
    {
        public Hex1bNode? Focused => focused;

        public ZStackNode? PopupHost { get; private set; }

        public DiagnosticEditorState? FocusedEditor { get; private set; }

        public DiagnosticFrameNode Node(Hex1bNode node, ILayoutProvider? clipChain)
        {
            if (PopupHost is null && node is ZStackNode host)
                PopupHost = host;

            var bounds = node.Bounds;
            var clip = EffectiveClip(clipChain, screen);
            var visible = Intersect(bounds, clip);

            DiagnosticRect? ownClip = null;
            string? clipMode = null;
            var childChain = clipChain;
            if (node is ILayoutProvider provider)
            {
                ownClip = DiagnosticRect.FromRect(provider.ClipRect);
                clipMode = provider.ClipMode == ClipMode.Clip ? "clip" : "overflow";
                childChain = new SnapshotClip(provider.ClipRect, provider.ClipMode, clipChain);
            }

            // Editors: metadata on the node; the focused editor's text only on the frame.
            DiagnosticEditorState? editor = null;
            if (node is TextBoxNode or EditorNode)
            {
                var described = EditorOf(node, includeText: ReferenceEquals(node, focused));
                if (ReferenceEquals(node, focused))
                    FocusedEditor = described;
                editor = described is { Text: not null } ? described with { Text = null } : described;
            }

            var children = new List<DiagnosticFrameNode>();
            foreach (var child in node.GetChildren())
            {
                // Splitter and drag-bar panes render their children under a pane clip.
                var paneChain = node is IChildLayoutProvider panes && panes.GetChildLayoutProvider(child) is { } pane
                    ? new SnapshotClip(pane.ClipRect, pane.ClipMode, childChain)
                    : childChain;
                children.Add(Node(child, paneChain));
            }

            return new DiagnosticFrameNode
            {
                Type = node.GetType().Name,
                WidgetType = node.ReconcileSourceWidget is { } widget ? FriendlyName(widget.GetType()) : null,
                Bounds = DiagnosticRect.FromRect(bounds),
                HitTestBounds = DiagnosticRect.FromRect(node.HitTestBounds),
                ContentBounds = DiagnosticRect.FromRect(node.ContentBounds),
                VisibleBounds = DiagnosticRect.FromRect(visible),
                ClipState = ClipStateOf(bounds, visible, clip),
                ClipRect = ownClip,
                ClipMode = clipMode,
                IsFocusable = node.IsFocusable,
                IsFocused = node.IsFocused,
                Text = TextOf(node),
                Properties = PropertiesOf(node),
                Editor = editor,
                Timing = now is { } at ? TimingOf(node, at) : null,
                Children = children.AsReadOnly(),
            };
        }
    }

    // The renderer's rule (LayoutProviderHelper): an Overflow provider whose parent is absent or
    // also Overflow draws unclipped; otherwise drawing is clipped to every enclosing provider.
    private static Rect EffectiveClip(ILayoutProvider? chain, Rect screen)
    {
        if (chain is null)
            return screen;
        if (chain.ClipMode == ClipMode.Overflow && chain.ParentLayoutProvider is null or { ClipMode: ClipMode.Overflow })
            return screen;
        return Intersect(screen, LayoutProviderHelper.GetEffectiveClipRect(chain));
    }

    // An immutable copy of one provider in the render-time clip chain; live providers link their
    // parents only while rendering.
    private sealed class SnapshotClip(Rect clipRect, ClipMode clipMode, ILayoutProvider? parent) : ILayoutProvider
    {
        public ILayoutProvider? ParentLayoutProvider
        {
            get => parent;
            set => throw new NotSupportedException("A projected clip chain is immutable.");
        }

        public Rect ClipRect => clipRect;

        public ClipMode ClipMode => clipMode;

        public bool ShouldRenderAt(int x, int y) => LayoutProviderHelper.ShouldRenderAt(this, x, y);

        public (int adjustedX, string clippedText) ClipString(int x, int y, string text) =>
            LayoutProviderHelper.ClipString(this, x, y, text);
    }

    private static DiagnosticNodeTiming TimingOf(Hex1bNode node, long now) => new()
    {
        ReconcileMs = Milliseconds(node.DiagReconcileTicks),
        RenderMs = Milliseconds(node.DiagRenderTicks),
        LastRenderedMsAgo = node.DiagLastRenderedTimestamp > 0 ? Milliseconds(now - node.DiagLastRenderedTimestamp) : -1,
    };

    private static IReadOnlyList<DiagnosticFramePopup> ProjectPopups(ZStackNode? host)
    {
        if (host is null)
            return [];

        var entries = host.Popups.Entries;
        var popups = new DiagnosticFramePopup[entries.Count];
        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var anchored = entry.ContentNode as AnchoredNode;
            var anchor = anchored?.AnchorNode;
            popups[i] = new DiagnosticFramePopup
            {
                Index = i,
                ContentType = entry.ContentNode?.GetType().Name ?? "null",
                ContentBounds = entry.ContentNode is { } content ? DiagnosticRect.FromRect(content.ContentBounds) : null,
                IsBarrier = entry.IsBarrier,
                IsAnchored = entry.AnchorNode != null,
                FocusRestoreNodeType = entry.FocusRestoreNode?.GetType().Name,
                AnchorNodeType = anchor?.GetType().Name,
                AnchorBounds = anchor is null ? null : DiagnosticRect.FromRect(anchor.Bounds),
                AnchorIsStale = anchored?.IsAnchorStale,
                AnchorPosition = anchored?.Position.ToString(),
            };
        }

        return Array.AsReadOnly(popups);
    }

    private static DiagnosticFrameFocus ProjectFocus(IReadOnlyList<Hex1bNode> focusables, int focusedIndex, string? lastHitTest)
    {
        var entries = new DiagnosticFocusable[focusables.Count];
        for (var i = 0; i < focusables.Count; i++)
        {
            var node = focusables[i];
            entries[i] = new DiagnosticFocusable
            {
                Index = i,
                Type = node.GetType().Name,
                Bounds = DiagnosticRect.FromRect(node.Bounds),
                HitTestBounds = DiagnosticRect.FromRect(node.HitTestBounds),
                IsFocused = i == focusedIndex,
            };
        }

        return new DiagnosticFrameFocus
        {
            CurrentIndex = focusedIndex,
            FocusedNodeType = focusedIndex < 0 ? null : focusables[focusedIndex].GetType().Name,
            LastHitTest = lastHitTest,
            Focusables = Array.AsReadOnly(entries),
        };
    }

    private static int IndexOfFocused(IReadOnlyList<Hex1bNode> focusables)
    {
        for (var i = 0; i < focusables.Count; i++)
        {
            if (focusables[i].IsFocused)
                return i;
        }

        return -1;
    }

    // Editor metadata from one snapshot of the text and cursors. A document can be edited off the
    // app loop, so every length, line and caret is derived from that snapshot, and cursor offsets
    // the snapshot no longer contains are clamped, as the editor itself clamps them.
    private static DiagnosticEditorState? EditorOf(Hex1bNode node, bool includeText) => node switch
    {
        TextBoxNode textBox => Describe("text-box", textBox.Bounds, textBox.State.Text,
            [(textBox.State.CursorPosition, textBox.State.SelectionAnchor)], includeText),
        EditorNode { State: { } state } editor => Describe("editor", editor.Bounds, state.Document.GetText(),
            SnapshotCursors(state.Cursors), includeText),
        _ => null,
    };

    private static IReadOnlyList<(int Position, int? Anchor)> SnapshotCursors(CursorSet cursors)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var snapshot = new List<(int, int?)>(cursors.Count);
                foreach (var cursor in cursors)
                    snapshot.Add((cursor.Position.Value, cursor.SelectionAnchor?.Value));
                return snapshot;
            }
            catch (Exception error) when (attempt < 2 && error is InvalidOperationException or ArgumentOutOfRangeException)
            {
                // The cursor set changed while it was being copied; copy it again.
            }
        }
    }

    private static DiagnosticEditorState Describe(string kind, Rect bounds, string text,
        IReadOnlyList<(int Position, int? Anchor)> cursors, bool includeText)
    {
        var lineStarts = new List<int> { 0 };
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] == '\n')
                lineStarts.Add(i + 1);
        }

        DiagnosticCaret At(int offset)
        {
            offset = Math.Clamp(offset, 0, text.Length);
            var line = lineStarts.BinarySearch(offset);
            if (line < 0)
                line = ~line - 1;
            return new DiagnosticCaret { Offset = offset, Line = line, Column = offset - lineStarts[line] };
        }

        var carets = new DiagnosticCaret[cursors.Count];
        var selections = new List<DiagnosticSelection>();
        for (var i = 0; i < cursors.Count; i++)
        {
            var (position, anchor) = cursors[i];
            carets[i] = At(position);
            if (anchor is { } from && At(from).Offset != carets[i].Offset)
            {
                var (start, end) = from < position ? (from, position) : (position, from);
                selections.Add(new DiagnosticSelection { Start = At(start), End = At(end) });
            }
        }

        return new DiagnosticEditorState
        {
            Kind = kind,
            Bounds = DiagnosticRect.FromRect(bounds),
            Length = text.Length,
            LineCount = lineStarts.Count,
            Carets = Array.AsReadOnly(carets),
            Selections = selections.AsReadOnly(),
            Text = includeText ? text : null,
        };
    }

    private static DiagnosticClipState ClipStateOf(Rect bounds, Rect visible, Rect clip)
    {
        if (bounds.Width <= 0 || bounds.Height <= 0)
            return clip.Contains(bounds.X, bounds.Y) ? DiagnosticClipState.Visible : DiagnosticClipState.FullyClipped;
        if (visible.Width <= 0 || visible.Height <= 0)
            return DiagnosticClipState.FullyClipped;
        return visible.Width == bounds.Width && visible.Height == bounds.Height
            ? DiagnosticClipState.Visible
            : DiagnosticClipState.PartiallyClipped;
    }

    private static Rect Intersect(Rect a, Rect b)
    {
        var x = Math.Max(a.X, b.X);
        var y = Math.Max(a.Y, b.Y);
        var right = Math.Min(a.Right, b.Right);
        var bottom = Math.Min(a.Bottom, b.Bottom);
        return right > x && bottom > y ? new Rect(x, y, right - x, bottom - y) : new Rect(x, y, 0, 0);
    }

    // Rendered text of non-editor nodes (spec Q8). Editors report metadata instead.
    private static string? TextOf(Hex1bNode node) => node switch
    {
        TextBlockNode textBlock => textBlock.Text,
        ButtonNode button => button.Label,
        MenuItemNode menuItem => menuItem.Label,
        CheckboxNode checkbox => checkbox.Label,
        HyperlinkNode hyperlink => hyperlink.Text,
        TabItemNode tabItem => tabItem.Title,
        TreeItemNode treeItem => treeItem.Label,
        FigletTextNode figlet => figlet.Text,
        NotificationCardNode card => card.Body is { } body ? $"{card.Title}\n{body}" : card.Title,
        WindowNode window => window.Title,
        BorderNode border => border.Title,
        _ => null,
    };

    // Explicit, type-specific projections; no reflection over node objects. Allocates only for
    // the node types that have properties.
    private static IReadOnlyDictionary<string, string>? PropertiesOf(Hex1bNode node)
    {
        Dictionary<string, string>? properties = null;
        switch (node)
        {
            case ListNode list:
                properties = new()
                {
                    ["itemCount"] = Invariant(list.Items?.Count ?? 0),
                    ["selectedIndex"] = Invariant(list.FocusedIndex),
                };
                if (list.FocusedItem is { } selectedItem)
                    properties["selectedText"] = selectedItem;
                break;
            case PickerNode picker:
                properties = new() { ["selectedIndex"] = Invariant(picker.SelectedIndex) };
                if (picker.SelectedText is { } selectedText)
                    properties["selectedText"] = selectedText;
                break;
            case AnchoredNode anchored:
                properties = new()
                {
                    ["isAnchorStale"] = anchored.IsAnchorStale ? "true" : "false",
                    ["position"] = anchored.Position.ToString(),
                };
                if (anchored.AnchorNode is { } anchor)
                {
                    properties["anchorNodeType"] = anchor.GetType().Name;
                    properties["anchorBounds"] = DiagnosticRect.FromRect(anchor.Bounds).ToString();
                }
                break;
            case BackdropNode backdrop:
                properties = new()
                {
                    ["style"] = backdrop.Style.ToString(),
                    ["hasClickAwayHandler"] =
                        backdrop.ClickAwayHandler != null || backdrop.ClickAwayEventHandler != null ? "true" : "false",
                };
                break;
            case NotificationPanelNode notificationPanel:
                properties = new()
                {
                    ["isDrawerExpanded"] = notificationPanel.IsDrawerExpanded ? "true" : "false",
                    ["notificationCount"] = Invariant(notificationPanel.Notifications?.Count ?? 0),
                };
                break;
        }

        return properties is null ? null : new ReadOnlyDictionary<string, string>(properties);
    }

    // ListWidget`1[String] reads as ListWidget<String>.
    private static string FriendlyName(Type type)
    {
        if (!type.IsGenericType)
            return type.Name;
        var name = type.Name;
        var tick = name.IndexOf('`');
        return $"{(tick < 0 ? name : name[..tick])}<{string.Join(", ", type.GetGenericArguments().Select(FriendlyName))}>";
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
