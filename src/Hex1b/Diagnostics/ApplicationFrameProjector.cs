using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
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
    // Timings are null when diagnostic timing is off, which also leaves node timings out.
    internal static DiagnosticApplicationFrame Project(Hex1bNode? root, FocusRing focusRing, long frameId,
        int columns, int rows, bool wroteOutput, ApplicationPassTimings? timings)
    {
        var screen = new Rect(0, 0, columns, rows);
        long? now = timings is null ? null : Stopwatch.GetTimestamp();
        var focusables = focusRing.Focusables;
        var focusedIndex = IndexOfFocused(focusables);
        return new DiagnosticApplicationFrame
        {
            FrameId = frameId,
            Columns = columns,
            Rows = rows,
            WroteOutput = wroteOutput,
            Root = root is null ? null : ProjectNode(root, screen, now),
            Popups = ProjectPopups(FindFirst<ZStackNode>(root)),
            Focus = ProjectFocus(focusables, focusedIndex, focusRing.LastHitTestDebug),
            FocusedEditor = focusedIndex < 0 ? null : ProjectEditor(focusables[focusedIndex]),
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

    private static DiagnosticFrameNode ProjectNode(Hex1bNode node, Rect clip, long? now)
    {
        var bounds = node.Bounds;
        var visible = Intersect(bounds, clip);

        DiagnosticRect? ownClip = null;
        string? clipMode = null;
        var childClip = clip;
        if (node is ILayoutProvider provider)
        {
            ownClip = DiagnosticRect.FromRect(provider.ClipRect);
            clipMode = provider.ClipMode == ClipMode.Clip ? "clip" : "overflow";
            if (provider.ClipMode == ClipMode.Clip)
                childClip = Intersect(clip, provider.ClipRect);
        }

        return new DiagnosticFrameNode
        {
            Type = node.GetType().Name,
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
            Timing = now is { } at ? TimingOf(node, at) : null,
            Children = Frozen(node.GetChildren().Select(child => ProjectNode(child, childClip, now))),
        };
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

    // Only the focused editor is projected (spec Q4). Its text is copied here because a later
    // capture may be authorized to read it; the engine withholds it otherwise.
    private static DiagnosticEditorState? ProjectEditor(Hex1bNode focused)
    {
        switch (focused)
        {
            case TextBoxNode textBox:
            {
                var state = textBox.State;
                DiagnosticCaret At(int offset)
                {
                    var (line, column) = state.OffsetToLineColumn(offset);
                    return new DiagnosticCaret { Offset = offset, Line = line, Column = column };
                }

                return new DiagnosticEditorState
                {
                    Kind = "text-box",
                    Bounds = DiagnosticRect.FromRect(textBox.Bounds),
                    Length = state.Text.Length,
                    LineCount = state.GetLineCount(),
                    Carets = Array.AsReadOnly(new[] { At(state.CursorPosition) }),
                    Selections = state.HasSelection
                        ? Array.AsReadOnly(new[] { new DiagnosticSelection { Start = At(state.SelectionStart), End = At(state.SelectionEnd) } })
                        : [],
                    Text = state.Text,
                };
            }
            case EditorNode editor when editor.State is { } state:
            {
                var document = state.Document;
                DiagnosticCaret At(int offset)
                {
                    // Document positions are 1-based; the contract is 0-based.
                    var position = document.OffsetToPosition(new Documents.DocumentOffset(offset));
                    return new DiagnosticCaret { Offset = offset, Line = position.Line - 1, Column = position.Column - 1 };
                }

                var carets = new List<DiagnosticCaret>(state.Cursors.Count);
                var selections = new List<DiagnosticSelection>();
                foreach (var cursor in state.Cursors)
                {
                    carets.Add(At(cursor.Position));
                    if (cursor.HasSelection)
                        selections.Add(new DiagnosticSelection { Start = At(cursor.SelectionStart), End = At(cursor.SelectionEnd) });
                }

                return new DiagnosticEditorState
                {
                    Kind = "editor",
                    Bounds = DiagnosticRect.FromRect(editor.Bounds),
                    Length = document.Length,
                    LineCount = document.LineCount,
                    Carets = carets.AsReadOnly(),
                    Selections = selections.AsReadOnly(),
                    Text = document.GetText(),
                };
            }
            default:
                return null;
        }
    }

    private static T? FindFirst<T>(Hex1bNode? node) where T : Hex1bNode
    {
        if (node is null)
            return null;
        if (node is T found)
            return found;
        foreach (var child in node.GetChildren())
        {
            if (FindFirst<T>(child) is { } descendant)
                return descendant;
        }

        return null;
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

    // Rendered text of non-editor nodes. Editors report interaction metadata instead.
    private static string? TextOf(Hex1bNode node) => node switch
    {
        TextBlockNode textBlock => textBlock.Text,
        ButtonNode button => button.Label,
        MenuItemNode menuItem => menuItem.Label,
        _ => null,
    };

    // Explicit, type-specific projections; no reflection over node objects.
    private static IReadOnlyDictionary<string, string>? PropertiesOf(Hex1bNode node)
    {
        var properties = new Dictionary<string, string>();
        switch (node)
        {
            case ListNode list:
                properties["itemCount"] = Invariant(list.Items?.Count ?? 0);
                properties["selectedIndex"] = Invariant(list.FocusedIndex);
                break;
            case PickerNode picker:
                properties["selectedIndex"] = Invariant(picker.SelectedIndex);
                if (picker.SelectedText is { } selectedText)
                    properties["selectedText"] = selectedText;
                break;
            case AnchoredNode anchored:
                if (anchored.AnchorNode is { } anchor)
                {
                    properties["anchorNodeType"] = anchor.GetType().Name;
                    properties["anchorBounds"] = DiagnosticRect.FromRect(anchor.Bounds).ToString();
                }
                properties["isAnchorStale"] = anchored.IsAnchorStale ? "true" : "false";
                properties["position"] = anchored.Position.ToString();
                break;
            case BackdropNode backdrop:
                properties["style"] = backdrop.Style.ToString();
                properties["hasClickAwayHandler"] =
                    backdrop.ClickAwayHandler != null || backdrop.ClickAwayEventHandler != null ? "true" : "false";
                break;
            case NotificationPanelNode notificationPanel:
                properties["isDrawerExpanded"] = notificationPanel.IsDrawerExpanded ? "true" : "false";
                properties["notificationCount"] = Invariant(notificationPanel.Notifications?.Count ?? 0);
                break;
        }

        return properties.Count > 0 ? new ReadOnlyDictionary<string, string>(properties) : null;
    }

    // Read-only wrappers, so an in-process caller cannot mutate a frame other captures share.
    private static ReadOnlyCollection<T> Frozen<T>(IEnumerable<T> items) => Array.AsReadOnly(items.ToArray());

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
