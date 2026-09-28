using System.Globalization;
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
    internal static DiagnosticApplicationFrame Project(Hex1bNode? root, long frameId, int columns, int rows, bool wroteOutput)
    {
        var screen = new Rect(0, 0, columns, rows);
        return new DiagnosticApplicationFrame
        {
            FrameId = frameId,
            Columns = columns,
            Rows = rows,
            WroteOutput = wroteOutput,
            Root = root is null ? null : ProjectNode(root, screen),
        };
    }

    private static DiagnosticFrameNode ProjectNode(Hex1bNode node, Rect clip)
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
            Children = node.GetChildren().Select(child => ProjectNode(child, childClip)).ToArray(),
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

        return properties.Count > 0 ? properties : null;
    }

    private static string Invariant(int value) => value.ToString(CultureInfo.InvariantCulture);
}
