using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    // Issue 60, decision D1. A later modifier (a combining mark, a ZWJ continuation, VS15/VS16) attaches to the last
    // printed glyph only while the active cell at its position is the same write as the last-printed copy
    // (Hex1bTerminal.UpdateLastGrapheme). Rows move, so that can stop and start again: the projection records a buffer
    // cell of the copy's write (lastPrinted.sameWrite) and the restore gives the copy that write's restored identity, so a
    // restored model attaches modifiers exactly as the original does, in both directions: to the glyph still (or again)
    // there, and never to a blank or to a different write that looks the same.
    [TestMethod]
    [DataRow("combining mark on the glyph in place", "abe", "\u0301", true, "e\u0301")]
    [DataRow("zwj continuation on the glyph in place", "\u001b[2;1H\U0001F469\u200D", "\U0001F4BB!", true, "\U0001F469\u200D\U0001F4BB")]
    [DataRow("combining mark after the glyph scrolled up a row", "\u001b[10;1Ha\r\n", "\u0301", false, " ")]
    [DataRow("zwj after the glyph scrolled up a row", "\u001b[10;1Ha\r\n", "\u200D", false, " ")]
    [DataRow("combining mark after the glyph was erased", "a\u001b[2K", "\u0301", false, " ")]
    [DataRow("combining mark after an identical different write moved in", "\u001b[1;1Ha\u001b[2;1Ha\u001b[1;1H\u001b[L", "\u0301", false, "a")]
    [DataRow("combining mark after the glyph moved away and back", "\u001b[1;1Ha\u001b[1;1H\u001b[L", "\u001b[M\u0301", false, "a\u0301")]
    [DataRow("combining mark after the glyph scrolled into history", "\u001b[1;1Ha\u001b[10;1H\r\n", "\u0301", false, " ")]
    [DataRow("combining mark after returning from the alternate screen", "\u001b[1;1Ha\u001b[?1049h", "\u001b[?1049l\u0301", false, "a\u0301")]
    public void ModelRestore_LastPrintedIdentityAttachesModifiersAsTheOriginal(string shape, string leave, string reveal, bool inPlace, string expected)
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(leave));
        var state = original.CaptureModelState();
        Assert.IsNotNull(state.LastPrinted, $"{shape}: fixture: nothing was printed");
        var (x, y) = (state.LastPrinted.X, state.LastPrinted.Y);
        Assert.AreEqual(inPlace, state.LastPrinted.SameWrite is { Buffer: "screen" } at && at.Row == y && at.Column == x,
            $"{shape}: the projected same-write cell");
        var lastPrinted = Json(state).GetProperty("lastPrinted");
        Assert.IsFalse(lastPrinted.GetProperty("cell").TryGetProperty("q", out _), $"{shape}: lastPrinted.cell joined a write class");

        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var start = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(start, $"{shape}: the restored start differs: " + string.Join("; ", start.Take(5)));

        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes(reveal));
        Assert.AreEqual(expected, original.CaptureModelState().Screen[y].Cells[x].Text, $"{shape}: fixture: the original's glyph after the modifier");
        var after = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(after, $"{shape}: after the modifier the restored model differs: " + string.Join("; ", after.Take(5)));
        Assert.AreEqual(Digest(original), Digest(replica), $"{shape}: the public views differ after the modifier");
    }

    [TestMethod]
    [DataRow("a never-written screen cell", "screen", 0, 5)]
    [DataRow("a column outside the screen", "screen", 0, 40)]
    [DataRow("a row outside the screen", "screen", 10, 0)]
    [DataRow("an empty history", "history", 0, 0)]
    [DataRow("a saved main screen that is absent", "savedMainScreen", 0, 0)]
    [DataRow("an unknown buffer", "alternate", 0, 0)]
    public void ModelRestore_RefusesALastPrintedSameWriteWithoutAWrittenCell(string shape, string buffer, int row, int column)
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput("a"u8);
        var state = original.CaptureModelState();
        Assert.IsNotNull(state.LastPrinted!.SameWrite, "fixture: the last printed glyph has no same-write cell");
        var malformed = state with
        {
            LastPrinted = state.LastPrinted with { SameWrite = new DiagnosticModelCellLocation { Buffer = buffer, Row = row, Column = column } },
        };
        var replica = Detached(new FakeTimeProvider());
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(malformed), shape);
        StringAssert.Contains(refused.Message, "last-printed", shape);
    }

    // Review r2 finding 1: the location is checked before the history rows themselves, so a missing row it names must be
    // refused, not dereferenced.
    [TestMethod]
    public void ModelRestore_RefusesALastPrintedSameWriteOnAMissingHistoryRow()
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput("\u001b[1;1Ha\u001b[10;1H\r\n"u8);
        var state = original.CaptureModelState();
        Assert.IsTrue(state.LastPrinted?.SameWrite is { Buffer: "history" }, $"fixture: the glyph did not scroll into history: lastPrinted {state.LastPrinted}, sameWrite {state.LastPrinted?.SameWrite}, history rows {state.History?.Rows.Count}");
        var rows = state.History!.Rows.ToList();
        rows[state.LastPrinted!.SameWrite!.Row] = null!;
        var malformed = state with { History = state.History with { Rows = rows } };
        var replica = Detached(new FakeTimeProvider());
        var refused = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(malformed));
        StringAssert.Contains(refused.Message, "last-printed");
    }

    [TestMethod]
    public void Compare_LastPrintedSameWriteDiffersAtItsPath()
    {
        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput("abc"u8);
        var recorded = model.CaptureModelState();
        var moved = recorded with { LastPrinted = recorded.LastPrinted! with { SameWrite = recorded.LastPrinted.SameWrite! with { Column = 1 } } };
        var absent = recorded with { LastPrinted = recorded.LastPrinted with { SameWrite = null } };
        Assert.AreEqual("lastPrinted.sameWrite.column", Paths(recorded, moved));
        Assert.AreEqual("lastPrinted.sameWrite", Paths(recorded, absent));

        static string Paths(DiagnosticModelState a, DiagnosticModelState b) =>
            string.Join(",", ModelStateComparer.Compare(a, b, ModelStateComparer.DefaultMaxDifferences).Differences.Select(d => d.Path));
    }
}
