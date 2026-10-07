using System.Text;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Tests;

/// <summary>
/// Issue 62: an opt-in display map shows stand-ins for buffer graphemes while the buffer, events and selection keep
/// the original characters, and every on-screen computation uses the shown widths (rules in
/// .hex1b-textbox-display-map/design.md).
/// </summary>
[TestClass]
public class TextBoxDisplayMapTests
{
    /// <summary>The stand-ins these tests use: wider, same-width, made-visible and narrower than the original.</summary>
    private static string? Map(string grapheme) => grapheme switch
    {
        "\t" => "→",          // control (width 0 as raw) becomes 1 cell
        "\u202E" => "<RLO>",  // bidi override (Cf, invisible) becomes 5 cells
        "\u200B" => "·",      // zero-width space becomes 1 cell
        "\u00AD" => "",       // soft hyphen stays hidden (zero-width unit)
        "W" => "w",           // same width, different glyph
        _ => null,
    };

    [TestMethod]
    public async Task DisplayMap_InApp_ShowsStandInsAndSubmitsTheOriginalText()
    {
        const string original = "a\tb\u202Ec\u200Bd";
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 5).Build();
        string? submitted = null;
        string? changed = null;

        using var app = new Hex1bApp(
            ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v => [
                v.TextBox(original).DisplayMap(Map)
                    .OnTextChanged(e => changed = e.NewText)
                    .OnSubmit(e => submitted = e.Text)
            ])),
            new Hex1bAppOptions { WorkloadAdapter = workload });

        var runTask = app.RunAsync(TestContext.Current.CancellationToken);
        var snapshot = await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("a→b<RLO>c·d"), TimeSpan.FromSeconds(5), "stand-ins shown")
            .Capture("shown")
            .Type("e")
            .WaitUntil(s => s.ContainsText("a→b<RLO>c·de"), TimeSpan.FromSeconds(5), "typed after stand-ins")
            .Enter()
            .WaitUntil(_ => submitted is not null, TimeSpan.FromSeconds(5), "submitted")
            .Ctrl().Key(Hex1bKey.C)
            .Build()
            .ApplyWithCaptureAsync(terminal, TestContext.Current.CancellationToken);
        await runTask;

        Assert.IsTrue(snapshot.ContainsText("a→b<RLO>c·d"));
        Assert.AreEqual(original + "e", changed);
        Assert.AreEqual(original + "e", submitted);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────────────────────────────────────

    private static TextBoxNode Node(string text, bool multiline = false, bool wrap = false, bool focused = true,
        Func<string, string?>? map = null)
    {
        var node = new TextBoxNode { IsMultiline = multiline, IsWordWrap = wrap, IsFocused = focused, Text = text };
        node.State.IsMultiline = multiline;
        node.DisplayMap = map ?? Map;
        return node;
    }

    /// <summary>Lays the node out at <paramref name="width"/> x <paramref name="height"/> and renders it clipped to its bounds.</summary>
    private static Surface Render(TextBoxNode node, int width, int height = 1)
    {
        node.Measure(new Constraints(0, width, 0, height));
        node.Arrange(new Rect(0, 0, width, height));
        var surface = new Surface(width, height);
        var context = new SurfaceRenderContext(surface, Hex1bThemes.Default)
        {
            CurrentLayoutProvider = new RectLayoutProvider(node.Bounds),
        };
        node.Render(context);
        return surface;
    }

    /// <summary>The graphemes drawn on row <paramref name="y"/> (a wide grapheme once), unwritten cells as '?'.</summary>
    private static string Row(Surface surface, int y)
    {
        var row = new StringBuilder();
        for (var x = 0; x < surface.Width; x++)
        {
            var cell = surface.GetCell(x, y);
            if (cell.IsContinuation) continue;
            row.Append(cell.Character == "\uE000" ? "?" : cell.Character);
        }
        return row.ToString();
    }

    private static Task Key(TextBoxNode node, Hex1bKey key, Hex1bModifiers modifiers = Hex1bModifiers.None)
        => InputRouter.RouteInputToNodeAsync(node, new Hex1bKeyEvent(key, '\0', modifiers), null, null,
            TestContext.Current.CancellationToken);

    private static Hex1bMouseEvent LeftClick(int x, int y)
        => new(MouseButton.Left, MouseAction.Down, x, y, Hex1bModifiers.None);

    // ── Rules 1-4: units, shown text, widths, single-line caret and scroll ───────────────────────────────────────

    [TestMethod]
    public void SingleLine_WiderStandIn_MeasuresAndPlacesTheCaretByShownCells()
    {
        var node = Node("a\u202Eb\t");
        node.State.CursorPosition = 2; // after the override

        Assert.AreEqual(8, node.Measure(new Constraints(0, 20, 0, 1)).Width); // a + <RLO> + b + →
        var surface = Render(node, 20);

        Assert.AreEqual("a<RLO>b→" + new string(' ', 12), Row(surface, 0));
        Assert.AreEqual(6, node.ScreenCursorX);
        Assert.AreEqual("a\u202Eb\t", node.Text);
    }

    [TestMethod]
    public void SingleLine_ControlsTheMapLeavesOrReturns_AreDrawnAsReplacementCharacters()
    {
        var node = Node("x\u0001y\u009B", map: g => g == "x" ? "\u001b[31m" : null);

        var surface = Render(node, 10);

        Assert.AreEqual("\uFFFD[31m\uFFFDy\uFFFD  ", Row(surface, 0));
        Assert.AreEqual(8, node.Measure(new Constraints(0, 20, 0, 1)).Width);
    }

    [TestMethod]
    public async Task SingleLine_WideCombiningAndZeroWidthGraphemes_MoveAsOneUnitByTheirShownWidth()
    {
        // 中 (2 cells), e + U+0301 (one grapheme, 1 cell), U+200B shown as · (1 cell), z.
        var node = Node("中e\u0301\u200Bz");
        node.State.CursorPosition = 5;
        var expected = new (int Offset, int Column)[] { (5, 5), (4, 4), (3, 3), (1, 2), (0, 0) };

        foreach (var (offset, column) in expected)
        {
            var surface = Render(node, 10);
            Assert.AreEqual(offset, node.State.CursorPosition);
            Assert.AreEqual(column, node.ScreenCursorX, $"caret column at offset {offset}");
            Assert.AreEqual("中e\u0301·z" + new string(' ', 5), Row(surface, 0));
            await Key(node, Hex1bKey.LeftArrow);
        }
    }

    [TestMethod]
    public void SingleLine_EmptyStandIn_TakesNoCellAndBothSidesShareTheColumn()
    {
        var node = Node("a\u00ADb");

        node.State.CursorPosition = 1;
        var surface = Render(node, 5);
        Assert.AreEqual("ab   ", Row(surface, 0));
        Assert.AreEqual(1, node.ScreenCursorX);

        node.State.CursorPosition = 2;
        Render(node, 5);
        Assert.AreEqual(1, node.ScreenCursorX);
    }

    [TestMethod]
    public void SingleLine_WiderThanViewport_ScrollsByWholeUnitsAndNeverDrawsAPartialOne()
    {
        // Columns: a0 b1 c2 <RLO>3..7 d8 e9 f10, width 11, viewport 6.
        var node = Node("abc\u202Edef");

        node.State.CursorPosition = 0;
        var surface = Render(node, 6);
        Assert.AreEqual("abc   ", Row(surface, 0), "the stand-in does not fit whole, so it is not drawn");
        Assert.AreEqual(0, node.ScreenCursorX);

        node.State.CursorPosition = 4; // caret column 8: the origin moves to the stand-in (column 3)
        surface = Render(node, 6);
        Assert.AreEqual("<RLO>d", Row(surface, 0));
        Assert.AreEqual(5, node.ScreenCursorX);

        node.State.CursorPosition = 7; // caret column 11: origin d (column 8)
        surface = Render(node, 6);
        Assert.AreEqual("def   ", Row(surface, 0));
        Assert.AreEqual(3, node.ScreenCursorX);
    }

    [TestMethod]
    public async Task SingleLine_PredictionFollowsTheShownTextAndIsClippedToTheRemainingCells()
    {
        var node = Node("\t");
        node.State.CursorPosition = 1;
        node.Predictor = (_, _) => Task.FromResult<string?>("zz");

        await InputRouter.RouteInputToNodeAsync(node, new Hex1bKeyEvent(Hex1bKey.A, 'a', Hex1bModifiers.None), null, null,
            TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (node.CurrentPrediction is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.AreEqual("zz", node.CurrentPrediction);
        Assert.AreEqual("→azz      ", Row(Render(node, 10), 0));
        Assert.AreEqual(2, node.ScreenCursorX);
        Assert.AreEqual("→az", Row(Render(node, 3), 0));
        Assert.AreEqual("\ta", node.Text);
    }

    [TestMethod]
    public async Task SingleLine_PredictionGoesThroughTheMapAndControlReplacement()
    {
        var node = Node("");
        node.Predictor = (_, _) => Task.FromResult<string?>("\t\u001b]0;x\u0007");

        await InputRouter.RouteInputToNodeAsync(node, new Hex1bKeyEvent(Hex1bKey.A, 'a', Hex1bModifiers.None), null, null,
            TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (node.CurrentPrediction is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.AreEqual("a→\uFFFD]0;x\uFFFD  ", Row(Render(node, 10), 0));
    }

    // ── Rule 2b: a stand-in never joins its neighbours into a grapheme of another width ──────────────────────────

    [TestMethod]
    public void EmptyStandInBetweenRegionalIndicators_IsShownAsAReplacementCharacter()
    {
        // Shown as one string "🇺" + "" + "🇸" would be a single 2-cell flag; the layout would count 4 cells.
        var node = Node("\U0001F1FA\u00AD\U0001F1F8");
        node.State.CursorPosition = node.Text.Length;

        Assert.AreEqual(5, node.Measure(new Constraints(0, 20, 0, 1)).Width);
        var surface = Render(node, 8);

        Assert.AreEqual("\U0001F1FA\uFFFD\U0001F1F8   ", Row(surface, 0));
        Assert.AreEqual(5, node.ScreenCursorX);
        Assert.AreEqual("\U0001F1FA\u00AD\U0001F1F8", node.Text);
    }

    [TestMethod]
    public void EmptyStandInBeforeAJoiner_DoesNotLetTheNeighboursFormAnEmojiSequence()
    {
        // 👨, soft hyphen (shown ""), ZWJ (its own grapheme after the control), 👩: joined they would be one 2-cell
        // family. The empty stand-in shows as U+FFFD; the lone ZWJ would then fuse with it, so it shows as U+FFFD too.
        var node = Node("\U0001F468\u00AD\u200D\U0001F469");
        node.State.CursorPosition = node.Text.Length;

        Assert.AreEqual(6, node.Measure(new Constraints(0, 20, 0, 1)).Width);
        var surface = Render(node, 8);
        Assert.AreEqual("\U0001F468\uFFFD\uFFFD\U0001F469  ", Row(surface, 0));
        Assert.AreEqual(6, node.ScreenCursorX);
    }

    [TestMethod]
    public void JoinRepair_ShiftedRegionalIndicatorPairing_IsCaught()
    {
        // 🇺, soft hyphen (""), then the buffer's own pair 🇸🇪 and a lone 🇫. Joined, 🇺 would pair with 🇸 and 🇪
        // with 🇫 (4 cells) while the units count 6; the empty stand-in shows as U+FFFD instead.
        var node = Node("\U0001F1FA\u00AD\U0001F1F8\U0001F1EA\U0001F1EB");
        node.State.CursorPosition = node.Text.Length;

        Assert.AreEqual(7, node.Measure(new Constraints(0, 20, 0, 1)).Width);
        var surface = Render(node, 8);
        Assert.AreEqual("\U0001F1FA\uFFFD\U0001F1F8\U0001F1EA\U0001F1EB ", Row(surface, 0));
        Assert.AreEqual(7, node.ScreenCursorX);
    }

    [TestMethod]
    public void JoinRepair_ReplacesOnlyAUnitAtTheJoin()
    {
        // "w" (stand-in for W), a control (U+FFFD), then U+0903, a spacing mark that would fuse with the U+FFFD before
        // it. Only the spacing mark is replaced; the earlier stand-in keeps its glyph.
        var node = Node("W\u0001\u0903");

        Assert.AreEqual(3, node.Measure(new Constraints(0, 20, 0, 1)).Width);
        Assert.AreEqual("w\uFFFD\uFFFD  ", Row(Render(node, 5), 0));
    }

    [TestMethod]
    public void JoinRepair_StaysLinearOnAdversarialInput()
    {
        // 20,000 stand-ins followed by 20,000 control + spacing-mark pairs: each pair fuses at its own join.
        var text = new string('W', 20_000) + string.Concat(Enumerable.Repeat("\u0001\u0903", 20_000));
        var node = Node(text, focused: false);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var width = node.Measure(new Constraints(0, int.MaxValue, 0, 1)).Width;
        watch.Stop();

        Assert.AreEqual(60_000, width);
        Assert.AreEqual(new string('w', 10), Row(Render(node, 10), 0));
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds, $"layout took {watch.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public void JoinRepair_PrependBeforeEmptyStandIns_ReplacesThePrependOnce()
    {
        // U+0600 (prepend) fuses with whatever follows, U+FFFD included, so the hidden soft hyphens between it and "b"
        // stay hidden and the prepend itself is shown as U+FFFD; a 20,000-char prepend run is one unit, replaced once.
        Assert.AreEqual("\uFFFDb   ", Row(Render(Node("\u0600\u00AD\u00ADb", focused: false), 5), 0));

        var node = Node(new string('\u0600', 20_000) + new string('\u00AD', 20_000) + "b", focused: false);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var width = node.Measure(new Constraints(0, int.MaxValue, 0, 1)).Width;
        watch.Stop();
        Assert.AreEqual(2, width);
        Assert.IsLessThan(5_000, watch.ElapsedMilliseconds, $"layout took {watch.ElapsedMilliseconds} ms");
    }

    [TestMethod]
    public async Task SingleLine_PredictionThatWouldFuseWithTheText_IsNotShown()
    {
        var node = Node("");
        node.Predictor = (_, _) => Task.FromResult<string?>("\u0301z");

        await InputRouter.RouteInputToNodeAsync(node, new Hex1bKeyEvent(Hex1bKey.A, 'a', Hex1bModifiers.None), null, null,
            TestContext.Current.CancellationToken);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (node.CurrentPrediction is null && DateTime.UtcNow < deadline)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        Assert.AreEqual("\u0301z", node.CurrentPrediction);
        Assert.AreEqual("a     ", Row(Render(node, 6), 0));
    }

    // ── Rule 10 and the buffer: selection, deletion ──────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task SingleLine_ShiftRightOverAStandIn_SelectsTheOriginalAndHighlightsItsCells()
    {
        var node = Node("a\u202Eb");
        node.State.CursorPosition = 1;

        await Key(node, Hex1bKey.RightArrow, Hex1bModifiers.Shift);
        var surface = Render(node, 10);

        Assert.AreEqual("\u202E", node.State.SelectedText);
        var selection = Hex1bThemes.Default.Get(TextBoxTheme.SelectionBackgroundColor);
        var highlighted = Enumerable.Range(0, 10).Where(x => Equals(surface.GetCell(x, 0).Background, selection)).ToArray();
        CollectionAssert.AreEqual(new[] { 1, 2, 3, 4, 5 }, highlighted);
    }

    [TestMethod]
    public void SingleLine_AnchorInsideAUnit_HighlightsEveryUnitTheSelectionOverlaps()
    {
        var node = Node("\U0001F600b");
        node.State.SelectionAnchor = 1; // between the surrogates of 😀
        node.State.CursorPosition = 3;

        var surface = Render(node, 6);

        var selection = Hex1bThemes.Default.Get(TextBoxTheme.SelectionBackgroundColor);
        var highlighted = Enumerable.Range(0, 6).Where(x => Equals(surface.GetCell(x, 0).Background, selection)).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 1, 2 }, highlighted);
    }

    [TestMethod]
    public async Task SingleLine_BackspaceAfterAStandIn_DeletesOnlyTheOriginalCharacter()
    {
        var node = Node("a\u202Eb");
        node.State.CursorPosition = 2;

        await Key(node, Hex1bKey.Backspace);

        Assert.AreEqual("ab", node.Text);
        Assert.AreEqual(1, node.State.CursorPosition);
        Assert.AreEqual("ab", Row(Render(node, 2), 0));
    }

    // ── Rule 8: hit-testing ──────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void SingleLine_ClickOnAStandInOrWideGrapheme_LandsOnTheNearerBoundary()
    {
        var node = Node("a\u202Eb中z");
        Render(node, 20);
        // Columns: a0, <RLO>1..5 (midpoint 3.5), b6, 中7..8 (midpoint 8), z9.
        var expected = new (int X, int Offset)[] { (2, 1), (3, 1), (4, 2), (5, 2), (6, 2), (7, 3), (8, 4), (9, 4), (15, 5) };

        foreach (var (x, offset) in expected)
        {
            node.HandleMouseClick(x, 0, LeftClick(x, 0));
            Assert.AreEqual(offset, node.State.CursorPosition, $"click at column {x}");
        }
    }

    [TestMethod]
    public void SingleLine_ClickWhileScrolled_UsesTheDrawnOrigin()
    {
        var node = Node("abc\u202Edef");
        node.State.CursorPosition = 4;
        Render(node, 6); // "<RLO>d", origin column 3

        node.HandleMouseClick(1, 0, LeftClick(1, 0));
        Assert.AreEqual(3, node.State.CursorPosition, "left half of the stand-in");
        node.HandleMouseClick(5, 0, LeftClick(5, 0));
        Assert.AreEqual(4, node.State.CursorPosition, "left half of d");
    }

    // ── Rule 5: word wrap ────────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public void WordWrap_StandInAtTheWrapBoundary_MovesWholeToTheNextRow()
    {
        var node = Node("abcd\u202Eef", multiline: true, wrap: true);
        node.State.CursorPosition = 4; // the soft-wrap boundary before the stand-in

        Assert.AreEqual(3, node.Measure(new Constraints(0, 6, 0, 10)).Height);
        var surface = Render(node, 6, 3);

        Assert.AreEqual("abcd  ", Row(surface, 0));
        Assert.AreEqual("<RLO>e", Row(surface, 1));
        Assert.AreEqual("f     ", Row(surface, 2));
        Assert.AreEqual((0, 1), (node.ScreenCursorX, node.ScreenCursorY), "rule 7: the continuation row's start");
    }

    [TestMethod]
    public void WordWrap_BreaksAfterTheLastSpaceInTheRow()
    {
        var node = Node("ab cdef", multiline: true, wrap: true, focused: false);

        var surface = Render(node, 5, 2);

        Assert.AreEqual("ab   ", Row(surface, 0));
        Assert.AreEqual("cdef ", Row(surface, 1));
    }

    [TestMethod]
    public void WordWrap_StandInWiderThanTheViewport_SitsAloneAndIsCut()
    {
        var node = Node("ab\u202Ecd", multiline: true, wrap: true, focused: false);

        Assert.AreEqual(3, node.Measure(new Constraints(0, 4, 0, 10)).Height);
        var surface = Render(node, 4, 3);

        Assert.AreEqual("ab  ", Row(surface, 0));
        Assert.AreEqual("<RLO", Row(surface, 1));
        Assert.AreEqual("cd  ", Row(surface, 2));
    }

    [TestMethod]
    public void WordWrap_CaretAfterACutStandIn_StaysOnePastTheLastCell()
    {
        var node = Node("ab\u202E", multiline: true, wrap: true);
        node.State.CursorPosition = 3;

        Render(node, 4, 2);

        Assert.AreEqual((4, 1), (node.ScreenCursorX, node.ScreenCursorY));
    }

    [TestMethod]
    public void WordWrap_SelectionAcrossRows_HighlightsTheShownCellsOfEverySelectedUnit()
    {
        // Rows "abcd", "<RLO>e", "f", "xy"; the selection covers "cd", the override, "ef", the newline and "x".
        var node = Node("abcd\u202Eef\nxy", multiline: true, wrap: true);
        node.State.SelectionAnchor = 2;
        node.State.CursorPosition = 9;

        var surface = Render(node, 6, 4);

        var selection = Hex1bThemes.Default.Get(TextBoxTheme.SelectionBackgroundColor);
        int[] Highlighted(int y) => Enumerable.Range(0, 6).Where(x => Equals(surface.GetCell(x, y).Background, selection)).ToArray();
        CollectionAssert.AreEqual(new[] { 2, 3 }, Highlighted(0));
        CollectionAssert.AreEqual(new[] { 0, 1, 2, 3, 4, 5 }, Highlighted(1));
        CollectionAssert.AreEqual(new[] { 0 }, Highlighted(2));
        CollectionAssert.AreEqual(new[] { 0 }, Highlighted(3));
        Assert.AreEqual(-1, node.ScreenCursorX, "no caret while the caret row shows a selection");
    }

    [TestMethod]
    public void WordWrap_ScrolledVertically_RendersAndHitTestsTheVisibleRows()
    {
        var node = Node("abcd\u202Eef\nxy", multiline: true, wrap: true);
        node.State.CursorPosition = node.Text.Length; // row 3 of 4 in a 2-row viewport

        var surface = Render(node, 6, 2);

        Assert.AreEqual("f     ", Row(surface, 0));
        Assert.AreEqual("xy    ", Row(surface, 1));
        Assert.AreEqual((2, 1), (node.ScreenCursorX, node.ScreenCursorY));
        node.HandleMouseClick(0, 0, LeftClick(0, 0));
        Assert.AreEqual(6, node.State.CursorPosition);
    }

    [TestMethod]
    public void WordWrap_ClickOnAWrappedRow_MapsToTheSourceOffset()
    {
        var node = Node("abcd\u202Eef", multiline: true, wrap: true);
        Render(node, 6, 3);

        node.HandleMouseClick(3, 1, LeftClick(3, 1)); // right half of <RLO> on the continuation row
        Assert.AreEqual(5, node.State.CursorPosition);
        node.HandleMouseClick(0, 2, LeftClick(0, 2));
        Assert.AreEqual(6, node.State.CursorPosition);
        node.HandleMouseClick(2, 1, LeftClick(2, 1)); // left half
        Assert.AreEqual(4, node.State.CursorPosition);
    }

    // ── Rules 6 and 9: no-wrap scrolling, Up/Down ────────────────────────────────────────────────────────────────

    [TestMethod]
    public void NoWrap_TheCaretRowScrollsByCellsAndOtherRowsStartAtColumnZero()
    {
        var node = Node("\u202E\u202Ex\nabcdefgh", multiline: true);
        node.State.CursorPosition = 3; // end of line 0: caret column 11

        var surface = Render(node, 6, 2);

        Assert.AreEqual("x     ", Row(surface, 0));
        Assert.AreEqual("abcdef", Row(surface, 1));
        Assert.AreEqual((1, 0), (node.ScreenCursorX, node.ScreenCursorY));
    }

    [TestMethod]
    public async Task Multiline_UpAndDown_KeepTheDisplayColumnAcrossStandIns()
    {
        // Line 0 "<RLO>ab" (offsets 0-3), line 1 "abcdefgh" (4-12), line 2 "xy" (13-15).
        var node = Node("\u202Eab\nabcdefgh\nxy", multiline: true);
        node.State.CursorPosition = 2; // after "<RLO>a": display column 6

        await Key(node, Hex1bKey.DownArrow);
        Assert.AreEqual(10, node.State.CursorPosition, "column 6 of line 1 is before g");
        await Key(node, Hex1bKey.DownArrow);
        Assert.AreEqual(15, node.State.CursorPosition, "line 2 is shorter: its end");
        await Key(node, Hex1bKey.UpArrow);
        Assert.AreEqual(10, node.State.CursorPosition, "the preferred display column is kept");
        await Key(node, Hex1bKey.UpArrow);
        Assert.AreEqual(2, node.State.CursorPosition);

        await Key(node, Hex1bKey.DownArrow, Hex1bModifiers.Shift);
        Assert.AreEqual("b\nabcdef", node.State.SelectedText);
    }

    // ── API ──────────────────────────────────────────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Reconcile_RoutesTheMapAndAWidgetWithoutItRestoresTheDefaultPath()
    {
        var context = ReconcileContext.CreateRoot();
        context.IsNew = true;
        var node = (TextBoxNode)await new TextBoxWidget("a\tb").DisplayMap(Map).ReconcileAsync(null, context);
        Assert.AreEqual("a→b", Row(Render(node, 3), 0));

        context.IsNew = false;
        await new TextBoxWidget("a\tb").ReconcileAsync(node, context);
        Assert.IsNull(node.DisplayMap);
        Assert.ThrowsExactly<ArgumentNullException>(() => new TextBoxWidget().DisplayMap(null!));
    }
}
