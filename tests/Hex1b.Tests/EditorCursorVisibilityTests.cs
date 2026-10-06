using Hex1b.Documents;
using Hex1b.Layout;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class EditorCursorVisibilityTests
{
    [TestMethod]
    public async Task KeepCursorVisible_ResizeAddsScrollbar_RevealsUnchangedPrimaryCursorAndSelection()
    {
        var text = string.Join('\n', Enumerable.Range(1, 4).Select(i => $"row{i}-" + new string('x', 60) + "界🚀"));
        var state = new EditorState(new Hex1bDocument(text)) { IsReadOnly = true };
        state.MoveToDocumentEnd(extend: true);
        var cursor = state.Cursor.Position;
        var anchor = state.Cursor.SelectionAnchor;
        var node = (EditorNode)await new EditorWidget(state).KeepCursorVisible()
            .ReconcileAsync(null, ReconcileContext.CreateRoot());
        node.RevealCursor();
        node.Measure(new Constraints(0, 200, 0, 4));
        node.Arrange(new Rect(0, 0, 200, 4));
        Assert.AreEqual(4, node.ViewportLines);
        Assert.AreEqual(1, node.ScrollOffset);

        // Width change alone introduces a horizontal scrollbar and removes one text row.
        node.Measure(new Constraints(0, 40, 0, 4));
        node.Arrange(new Rect(0, 0, 40, 4));
        Assert.AreEqual(3, node.ViewportLines);
        Assert.AreEqual(2, node.ScrollOffset, "The last cursor line must remain inside the reduced viewport.");
        Assert.IsTrue(node.HorizontalScrollOffset > 0, "The unchanged last-column cursor must also be revealed horizontally.");
        Assert.AreEqual(cursor, state.Cursor.Position);
        Assert.AreEqual(anchor, state.Cursor.SelectionAnchor);
        Assert.AreEqual(text, state.Document.GetText());

        node.ScrollOffset = 1;
        node.HorizontalScrollOffset = 0;
        node.Arrange(new Rect(0, 0, 40, 4));
        Assert.AreEqual(2, node.ScrollOffset, "Enabled mode reveals on every layout, not only a resize.");
        Assert.IsTrue(node.HorizontalScrollOffset > 0);
        Assert.AreEqual(cursor, state.Cursor.Position);
        Assert.AreEqual(anchor, state.Cursor.SelectionAnchor);
    }
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public async Task KeepCursorVisible_DefaultExplicitFalseOrDisabled_PreservesManualScroll(int mode)
    {
        var text = string.Join('\n', Enumerable.Range(1, 10).Select(i => $"row{i}-" + new string('x', 100)));
        var state = new EditorState(new Hex1bDocument(text)) { IsReadOnly = true };
        state.MoveToDocumentEnd(extend: true);
        var cursor = state.Cursor.Position;
        var anchor = state.Cursor.SelectionAnchor;
        var context = ReconcileContext.CreateRoot();
        var widget = new EditorWidget(state);
        if (mode != 0) widget = widget.KeepCursorVisible(mode == 2);
        var node = (EditorNode)await widget.ReconcileAsync(null, context);
        var bounds = new Rect(0, 0, 74, 4);
        node.RevealCursor();
        node.Measure(new Constraints(0, 74, 0, 4));
        node.Arrange(bounds);
        Assert.AreEqual(8, node.ScrollOffset);
        Assert.IsTrue(node.HorizontalScrollOffset > 0);
        if (mode == 2)
        {
            var reconciled = await new EditorWidget(state).KeepCursorVisible(false).ReconcileAsync(node, context);
            Assert.AreSame(node, reconciled);
        }
        node.ScrollOffset = 2;
        node.HorizontalScrollOffset = 3;
        node.Arrange(bounds);
        node.Arrange(new Rect(0, 0, 60, 4));
        Assert.AreEqual(2, node.ScrollOffset, "Unrelated layout and resize must not override disabled manual scrolling.");
        Assert.AreEqual(3, node.HorizontalScrollOffset);
        Assert.AreEqual(cursor, state.Cursor.Position);
        Assert.AreEqual(anchor, state.Cursor.SelectionAnchor);
        Assert.AreEqual(text, state.Document.GetText());
    }
}
