using Hex1b.Documents;
using Hex1b.Layout;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class EditorCursorRevealTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReplacingParentDocumentAtSameVersionRecomputesWidth(bool replacementIsWide)
    {
        var before = new Hex1bDocument(replacementIsWide ? "short" : new string('x', 100));
        var after = new Hex1bDocument(replacementIsWide ? new string('x', 100) : "short");
        Assert.AreEqual(before.Version, after.Version);
        var node = new EditorNode { State = new(before) { IsReadOnly = true } };
        var bounds = new Rect(0, 0, 20, 4);
        node.Measure(new Constraints(0, 20, 0, 4));
        node.Arrange(bounds);
        node.State = new(after) { IsReadOnly = true };
        node.RevealCursor();
        node.Arrange(bounds);
        Assert.AreEqual(replacementIsWide ? 3 : 4, node.ViewportLines,
            "Horizontal scrollbar geometry must follow the new document, even when its version number is equal.");
    }

    [TestMethod]
    public void RevealDoesNotDismissAnExistingEditorOverlay()
    {
        var node = new EditorNode { State = new(new Hex1bDocument("read only")) { IsReadOnly = true } };
        var session = (IEditorSession)node;
        var overlay = new EditorOverlay("retained", new(1, 1), OverlayPlacement.Below, []);
        session.PushOverlay(overlay);
        node.RevealCursor();
        CollectionAssert.AreEqual(new[] { overlay }, session.ActiveOverlays.ToArray());
    }

    [TestMethod]
    [DataRow(20, 4)]
    [DataRow(80, 8)]
    public void RevealParentMovedCursorPreservesSelectionAndDoesNotPinLaterManualScroll(int width, int height)
    {
        var text = string.Join('\n', Enumerable.Range(1, 19).Select(i => $"line{i}")) + "\n" + new string('x', 100) + "界🚀";
        var state = new EditorState(new Hex1bDocument(text)) { IsReadOnly = true };
        var node = new EditorNode { State = state, IsFocused = true };
        var bounds = new Rect(0, 0, width, height);
        node.Measure(new Constraints(0, width, 0, height));
        node.Arrange(bounds);
        state.MoveToDocumentEnd(extend: true);
        var cursor = state.Cursor.Position;
        var anchor = state.Cursor.SelectionAnchor;

        node.RevealCursor();
        node.Arrange(bounds);

        Assert.IsTrue(node.ScrollOffset <= 20 && node.ScrollOffset + node.ViewportLines > 20,
            $"Last line is outside viewport {node.ScrollOffset}/{node.ViewportLines}");
        Assert.IsTrue(node.HorizontalScrollOffset > 0, "Long final line must be revealed horizontally.");
        Assert.AreEqual(cursor, state.Cursor.Position);
        Assert.AreEqual(anchor, state.Cursor.SelectionAnchor);
        Assert.AreEqual(text, state.Document.GetText());

        node.ScrollOffset = 1;
        node.HorizontalScrollOffset = 0;
        node.Arrange(bounds);
        Assert.AreEqual(1, node.ScrollOffset, "Reveal is a one-shot request, not sticky follow mode.");
        Assert.AreEqual(0, node.HorizontalScrollOffset);
        Assert.AreEqual(cursor, state.Cursor.Position);
        Assert.AreEqual(anchor, state.Cursor.SelectionAnchor);
    }
}
