using System.Text;
using Hex1b.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ModelRestore_WriteClassesPreserveHistoryAndScreens(bool alternate)
    {
        // Separated halves need equality even without a visible continuation. On alternate, the saved main is
        // installed before history, and classes can span all three buffers without continuing across their edges.
        var state = WriteClassState(alternate);
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var captured = replica.CaptureModelState();
        var main = alternate ? captured.SavedMainScreen! : captured.Screen;
        Assert.AreEqual(1, captured.History!.Rows[0].Cells[0].WriteClass);
        Assert.AreEqual(2, captured.History.Rows[0].Cells[2].WriteClass);
        Assert.AreEqual((3, 3, 1, 2), (main[0].Cells[0].WriteClass, main[0].Cells[1].WriteClass,
            main[0].Cells[2].WriteClass, main[1].Cells[1].WriteClass), "classes must follow history then main, not numeric labels");
        Assert.IsFalse(main[0].Cells[2].Continues, "the separated half must not acquire an adjacent continuation");
        var history = replica.GetScrollbackRows(replica.ScrollbackCount).Single().Cells;
        var mainCells = WriteClassBuffer(replica, alternate ? "_savedMainScreenBuffer" : "_screenBuffer");
        Assert.AreEqual(history[0].Sequence, mainCells[0, 2].Sequence, "history and main lost their shared glyph");
        Assert.AreEqual(history[2].Sequence, mainCells[1, 1].Sequence, "a second class was merged or split");
        Assert.AreNotEqual(history[0].Sequence, history[2].Sequence, "distinct classes were merged");
        Assert.AreEqual(mainCells[0, 0].Sequence, mainCells[0, 1].Sequence, "adjacent halves must also retain equality");
        Assert.AreEqual(0L, mainCells[1, 5].Sequence, "never-written cells must remain zero");
        if (alternate)
        {
            var screen = WriteClassBuffer(replica, "_screenBuffer");
            Assert.AreEqual(history[0].Sequence, screen[0, 0].Sequence, "alternate was assigned a separate class domain");
            Assert.IsFalse(captured.Screen[0].Cells[0].Continues, "alternate must not continue the saved main or history");
            Assert.AreEqual((4, 4), (captured.Screen[0].Cells[2].WriteClass, captured.Screen[0].Cells[3].WriteClass));
            Assert.AreNotEqual(screen[0, 2].Sequence, mainCells[0, 0].Sequence, "screen-local classes collided across buffers");
        }

        var second = Detached(new FakeTimeProvider());
        second.RestoreModelState(captured);
        Assert.IsEmpty(JsonDifferences(Json(captured), Json(second.CaptureModelState())),
            "a canonical projection must survive another restore unchanged");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ModelRestore_NewWritesCannotJoinRestoredClasses(bool alternate)
    {
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(WriteClassState(alternate));
        var assigned = WriteClassSequences(replica).ToHashSet();
        var maximum = assigned.Max();
        replica.ApplyRecordedOutput(Encoding.UTF8.GetBytes("\u001b[2;4H新"));
        var screen = WriteClassBuffer(replica, "_screenBuffer");
        Assert.IsGreaterThan(maximum, screen[1, 3].Sequence, "later writes must advance past every class and singleton");
        Assert.AreEqual(screen[1, 3].Sequence, screen[1, 4].Sequence, "a newly written wide glyph must share its own sequence");
        Assert.IsFalse(assigned.Contains(screen[1, 3].Sequence), "a new glyph accidentally joined an old equality class");
        Assert.AreEqual(0L, screen[1, 5].Sequence, "writing the glyph changed the neighboring never-written cell");
        var projected = replica.CaptureModelState();
        Assert.AreEqual(projected.Screen[1].Cells[3].WriteClass, projected.Screen[1].Cells[4].WriteClass);
        Assert.IsGreaterThan(0, projected.Screen[1].Cells[3].WriteClass);
    }

    [TestMethod]
    public void Projection_WriteClassesAreNotAbsoluteWriteOrderOrLastPrintedIdentity()
    {
        var singleton = Detached(new FakeTimeProvider());
        singleton.ApplyRecordedOutput("X"u8);
        var singletonState = singleton.CaptureModelState();
        Assert.AreEqual(0, singletonState.Screen[0].Cells[0].WriteClass,
            "the copied last-printed cell must not turn a buffer singleton into a repeated class");

        var model = Detached(new FakeTimeProvider());
        model.ApplyRecordedOutput(Encoding.UTF8.GetBytes("X漢"));
        var state = model.CaptureModelState();
        Assert.AreEqual((0, 1, 1, 0), (state.Screen[0].Cells[0].WriteClass, state.Screen[0].Cells[1].WriteClass,
            state.Screen[0].Cells[2].WriteClass, state.Screen[0].Cells[3].WriteClass));
        var json = Json(state);
        var cells = json.GetProperty("screen")[0].GetProperty("cells");
        Assert.IsFalse(cells[0].TryGetProperty("q", out _), "singletons must omit q");
        Assert.AreEqual(1, cells[1].GetProperty("q").GetInt32());
        Assert.AreEqual(1, cells[2].GetProperty("q").GetInt32());
        Assert.IsFalse(cells[3].TryGetProperty("q", out _), "never-written cells must omit q");
        Assert.IsFalse(json.GetProperty("lastPrinted").GetProperty("cell").TryGetProperty("q", out _),
            "lastPrinted is a copy, not a buffer-cell equality member");

        // Relabel the actual write sequences while preserving equality and zero. The complete projection, not
        // merely q, must remain identical: neither numeric order nor the last-printed copy may leak into the artifact.
        var buffer = WriteClassBuffer(model, "_screenBuffer");
        for (var row = 0; row < buffer.GetLength(0); row++)
            for (var column = 0; column < buffer.GetLength(1); column++)
                if (buffer[row, column].Sequence != 0)
                    buffer[row, column] = buffer[row, column] with { Sequence = 9_000_000_000_000L - buffer[row, column].Sequence };
        Assert.IsEmpty(JsonDifferences(json, Json(model.CaptureModelState())), "absolute sequences leaked into the projection");
    }

    [TestMethod]
    [DataRow("negative screen class")]
    [DataRow("negative history class")]
    [DataRow("negative saved-main class")]
    [DataRow("class on unwritten screen")]
    [DataRow("class on unwritten history")]
    [DataRow("class on unwritten saved-main")]
    [DataRow("singleton class")]
    [DataRow("class on last printed")]
    [DataRow("negative class on last printed")]
    [DataRow("continuation with another class")]
    [DataRow("continuation without a class")]
    [DataRow("missing continuation")]
    [DataRow("continuation over a hard row boundary")]
    [DataRow("continuation over alternate buffer boundary")]
    [DataRow("missing unwritten continuation")]
    public void ModelRestore_RefusesMalformedWriteClassesBeforeAnyMutation(string shape)
    {
        var state = WriteClassState(alternate: true);
        var screen = state.Screen.ToArray();
        var savedMain = state.SavedMainScreen!.ToArray();
        var history = state.History!;
        var historyRows = history.Rows.ToArray();
        switch (shape)
        {
            case "negative screen class":
                screen[0] = WriteClassCellAt(screen[0], 1, cell => cell with { WriteClass = -1 });
                break;
            case "negative history class":
                historyRows[0] = WriteClassCellAt(historyRows[0], 1, cell => cell with { WriteClass = -1 });
                break;
            case "negative saved-main class":
                savedMain[0] = WriteClassCellAt(savedMain[0], 3, cell => cell with { WriteClass = -1 });
                break;
            case "class on unwritten screen":
                screen[1] = WriteClassCellAt(screen[1], 0, cell => cell with { WriteClass = 91 });
                break;
            case "class on unwritten history":
                historyRows[0] = WriteClassCellAt(historyRows[0], 1, cell => cell with { WriteClass = 91 }) with { Unwritten = [1, 1] };
                break;
            case "class on unwritten saved-main":
                savedMain[1] = WriteClassCellAt(savedMain[1], 5, cell => cell with { WriteClass = 91 });
                break;
            case "singleton class":
                screen[0] = WriteClassCellAt(screen[0], 1, cell => cell with { WriteClass = 123 });
                break;
            case "class on last printed":
            case "negative class on last printed":
                state = state with
                {
                    LastPrinted = new DiagnosticModelLastPrinted
                    {
                        X = 1,
                        Y = 0,
                        Width = 1,
                        Cell = screen[0].Cells[1] with { WriteClass = shape.StartsWith("negative", StringComparison.Ordinal) ? -1 : 91 },
                    }
                };
                break;
            case "continuation with another class":
                screen[0] = WriteClassCellAt(screen[0], 3, cell => cell with { WriteClass = 91 });
                break;
            case "continuation without a class":
                screen[0] = WriteClassCellAt(screen[0], 3, cell => cell with { WriteClass = 0 });
                break;
            case "missing continuation":
                screen[0] = WriteClassCellAt(screen[0], 3, cell => cell with { Continues = false });
                break;
            case "continuation over a hard row boundary":
                screen[0] = WriteClassCellAt(screen[0], 5, cell => cell with { WriteClass = 91 }) with { Unwritten = null };
                screen[1] = WriteClassCellAt(screen[1], 0, cell => cell with { Text = "", WriteClass = 91, Continues = true }) with { Unwritten = [1, 5] };
                break;
            case "continuation over alternate buffer boundary":
                screen[0] = WriteClassCellAt(screen[0], 0, cell => cell with { Continues = true });
                break;
            case "missing unwritten continuation":
                screen[1] = WriteClassCellAt(screen[1], 1, cell => cell with { Text = "" });
                break;
        }
        var forged = state with { Screen = screen, SavedMainScreen = savedMain, History = history with { Rows = historyRows } };
        var replica = Detached(new FakeTimeProvider());
        var before = Json(replica.CaptureModelState());
        var writeSequence = (long)PrivateField(replica, "_writeSequence");
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged));
        StringAssert.Contains(error.Message, shape.Contains("continuation", StringComparison.Ordinal) ? "continuation" : "write class", shape);
        Assert.IsEmpty(JsonDifferences(before, Json(replica.CaptureModelState())), $"{shape}: a refusal changed projected state");
        Assert.AreEqual(writeSequence, (long)PrivateField(replica, "_writeSequence"), $"{shape}: a refusal consumed write sequences");
        Assert.AreEqual(0, Store(replica).HyperlinkCount, $"{shape}: a refusal acquired tracked objects");
    }

    private static DiagnosticModelState WriteClassState(bool alternate)
    {
        var model = Detached(new FakeTimeProvider());
        model.Resize(6, 2);
        var baseline = model.CaptureModelState();
        DiagnosticModelCell Cell(string text, int label = 0, bool continues = false) =>
            new() { Text = text, WriteClass = label, Continues = continues };
        DiagnosticModelRow Row(params DiagnosticModelCell[] cells) => new() { Cells = cells };
        var history = Row(Cell("漢", 91), Cell("h"), Cell("字", 42), Cell("h"), Cell("h"), Cell("h"))
            with
        { Id = 1, OriginalWidth = 6 };
        DiagnosticModelRow[] main =
        [
            Row(Cell("界", int.MaxValue), Cell("", int.MaxValue, true), Cell("", 91), Cell("s"), Cell(" "), Cell(" ")),
            Row(Cell("s"), Cell("", 42), Cell(" "), Cell(" "), Cell(" "), Cell(" ")) with { Unwritten = [5, 1] },
        ];
        DiagnosticModelRow[] alt =
        [
            Row(Cell("", 91), Cell("a"), Cell("龍", 7), Cell("", 7, true), Cell(" "), Cell(" ")) with { Unwritten = [5, 1] },
            Row(Cell(" "), Cell(" "), Cell(" "), Cell(" "), Cell(" "), Cell(" ")) with { Unwritten = [0, 6] },
        ];
        return baseline with
        {
            ActiveBuffer = alternate ? "alternate" : "main",
            Screen = alternate ? alt : main,
            SavedMainScreen = alternate ? main : null,
            History = baseline.History! with { NextRowId = 2, Rows = [history] },
        };
    }

    private static DiagnosticModelRow WriteClassCellAt(DiagnosticModelRow row, int column, Func<DiagnosticModelCell, DiagnosticModelCell> change) =>
        row with { Cells = row.Cells.Select((cell, index) => index == column ? change(cell) : cell).ToArray() };

    private static TerminalCell[,] WriteClassBuffer(Hex1bTerminal terminal, string name) =>
        (TerminalCell[,])PrivateField(terminal, name);

    private static IEnumerable<long> WriteClassSequences(Hex1bTerminal terminal)
    {
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
            foreach (var cell in row.Cells)
                yield return cell.Sequence;
        foreach (var cell in WriteClassBuffer(terminal, "_screenBuffer"))
            yield return cell.Sequence;
        if (PrivateField(terminal, "_savedMainScreenBuffer") is TerminalCell[,] main)
            foreach (var cell in main)
                yield return cell.Sequence;
    }
}
