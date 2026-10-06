using Hex1b.Input;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class TextBoxStateVersionTests
{
    [TestMethod]
    public void Version_SelectionRoundTrip_RecordsBothMutations()
    {
        var state = new TextBoxState("abc");
        var initial = state.Version;
        state.SelectAll();
        Assert.IsTrue(state.Version > initial);
        var selected = state.Version;
        state.ClearSelection();
        Assert.IsTrue(state.Version > selected);
        Assert.AreEqual("abc", state.Text);
        Assert.AreEqual(3, state.CursorPosition);
        Assert.IsNull(state.SelectionAnchor);
    }
    [TestMethod]
    [DataRow(Hex1bKey.Backspace)]
    [DataRow(Hex1bKey.Delete)]
    [DataRow(Hex1bKey.Enter)]
    [DataRow(Hex1bKey.X)]
    public void Version_LegacyEdit_AdvancesToken(Hex1bKey key)
    {
        var state = new TextBoxState("abc") { IsMultiline = true, CursorPosition = 1 };
        var initial = state.Version;
        state.HandleInput(new Hex1bKeyEvent(key, key == Hex1bKey.X ? 'x' : '\0', Hex1bModifiers.None));
        Assert.IsTrue(state.Version > initial);
    }

    [TestMethod]
    public void Version_NewlineHelper_AdvancesToken()
    {
        var state = new TextBoxState("abc");
        var initial = state.Version;
        state.InsertNewline();
        Assert.IsTrue(state.Version > initial);
        Assert.AreEqual("abc\n", state.Text);
        Assert.AreEqual(4, state.CursorPosition);
    }

    [TestMethod]
    public void Version_NoOpAssignmentsAndBlockedEdits_RetainToken()
    {
        var state = new TextBoxState("abc") { MaxLines = 1 };
        var initial = state.Version;
        state.Text = "abc";
        state.CursorPosition = 999;
        state.ClearSelection();
        state.InsertNewline();
        state.HandleInput(new Hex1bKeyEvent(Hex1bKey.Delete, '\0', Hex1bModifiers.None));
        Assert.AreEqual(initial, state.Version);
    }
}
