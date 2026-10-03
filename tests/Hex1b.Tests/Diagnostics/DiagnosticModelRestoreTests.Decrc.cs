using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    [TestMethod]
    [DynamicData(nameof(DecrcStrategiesAndScreens))]
    public void ModelRestore_DecrcAfterNarrowingErasesAndMarksAtBoundedCursor(string strategy, bool alternate)
    {
        using var original = FuzzModel(12, 4, null, strategy, modern: false);
        if (alternate)
            ApplyStep(original, "\u001b[1;2Hm\u001b[1;2H\u001b[?1049h");
        ApplyStep(original, "\u001b[3;9Hq\u001b7");
        original.Resize(5, 4);
        ApplyStep(original, "\u001b8");
        var restored = (X: original.CursorX, Y: original.CursorY);

        ApplyStep(original, "\u001b]133;A\u0007");
        var start = original.CaptureModelState();
        var mark = start.CommandMarks.Single();
        using var replica = FuzzModel(12, 4, null, strategy, modern: false);
        // Restore the real marked start first: the stale-column bug refused this projection.
        replica.RestoreModelState(start);
        Assert.IsTrue(restored.X >= 0 && restored.X < 5, $"{strategy}: DECRC left X outside the narrowed screen");
        Assert.IsTrue(restored.Y >= 0 && restored.Y < 4, $"{strategy}: DECRC left Y outside the screen");
        if (alternate || strategy is not ("foot" or "ghostty" or "vte"))
            Assert.AreEqual((4, 2), restored, "unchanged saved coordinates must restore at the right edge");

        Assert.AreEqual(alternate ? "alternate" : "main", mark.Buffer);
        Assert.AreEqual((int?)restored.X, mark.Column, "the mark must anchor at the bounded cursor, not the stale save");
        Assert.AreEqual((int?)(restored.Y + (alternate ? 0 : start.History?.Rows.Count ?? 0)), mark.Row);
        AssertDecrcModelsEqual(original, replica, "marked start");

        // Fill after the exact repro so ED has both retained and erased content to distinguish.
        var fill = string.Concat(Enumerable.Range(0, 4).Select(row => $"\u001b[{row + 1};1H{new string((char)('A' + row), 5)}"));
        foreach (var step in new[] { fill, "\u001b8\u001b[J" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            AssertDecrcModelsEqual(original, replica, "fill and repeated restore/erase");
        }
        using (var screen = original.CreateSnapshot())
        {
            for (var row = 0; row < 4; row++)
            {
                var kept = row < restored.Y ? 5 : row == restored.Y ? restored.X : 0;
                Assert.AreEqual(new string((char)('A' + row), kept).PadRight(5), screen.GetLine(row),
                    $"ED must erase from the restored cursor, retaining only the cells before it on row {row}");
            }
        }
        foreach (var step in new[] { "\u001b[H\u001b8Z", "RESIZE 12 4", "\u001b8!\u001b]133;B\u0007" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            AssertDecrcModelsEqual(original, replica, "later output and widening");
        }
        if (alternate)
        {
            ApplyStep(original, "\u001b[?1049l");
            ApplyStep(replica, "\u001b[?1049l");
            AssertDecrcModelsEqual(original, replica, "leave alternate");
            Assert.AreEqual((1, 0), (original.CursorX, original.CursorY), "alternate-screen exit must use its separate cursor save");
            using var main = original.CreateSnapshot();
            Assert.AreEqual("m", main.GetCell(1, 0).Character, "DECRC and alternate output must not overwrite the saved main screen");
        }
    }

    [TestMethod]
    [DynamicData(nameof(DecrcStrategiesAndSmallScreens))]
    public void ModelRestore_DecrcAfterWidthAndHeightShrinkWritesInsideScreen(string strategy, int width, int height)
    {
        using var original = FuzzModel(12, 4, null, strategy, modern: false);
        ApplyStep(original, "\u001b[4;9Hq\u001b7");
        original.Resize(width, height);
        ApplyStep(original, "\u001b8");
        var restored = (X: original.CursorX, Y: original.CursorY);
        Assert.IsTrue(restored.X >= 0 && restored.X < width, $"{strategy}: restored X outside width {width}");
        Assert.IsTrue(restored.Y >= 0 && restored.Y < height, $"{strategy}: restored Y outside height {height}");
        if (strategy is not ("foot" or "ghostty" or "vte"))
            Assert.AreEqual((width - 1, height - 1), restored, "both out-of-range saved coordinates must be bounded");
        using var replica = FuzzModel(12, 4, null, strategy, modern: false);
        replica.RestoreModelState(original.CaptureModelState());
        foreach (var step in new[] { "\u001b[H\u001b8Z", "\u001b]133;A\u0007" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            AssertDecrcModelsEqual(original, replica, "small-screen continuation");
        }
        using var screen = original.CreateSnapshot();
        Assert.AreEqual("Z", screen.GetCell(restored.X, restored.Y).Character, "output must occupy the restored in-bounds cell");
    }

    [TestMethod]
    [DataRow("no-provider")]
    [DataRow("none")]
    [DataRow("xterm")]
    [DataRow("iterm2")]
    public void ModelRestore_DecrcNarrowThenWideRetainsNonReflowingSave(string strategy)
    {
        using var original = FuzzModel(12, 4, null, strategy, modern: false);
        ApplyStep(original, "\u001b[3;9Hq\u001b7");
        original.Resize(5, 2);
        ApplyStep(original, "\u001b8");
        Assert.AreEqual((4, 1), (original.CursorX, original.CursorY));
        ApplyStep(original, "\u001b[H\u001b8");
        Assert.AreEqual((4, 1), (original.CursorX, original.CursorY), "a bounded restore must not overwrite the saved registers");
        using var replica = FuzzModel(12, 4, null, strategy, modern: false);
        replica.RestoreModelState(original.CaptureModelState());
        foreach (var step in new[] { "RESIZE 12 4", "\u001b8R" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            AssertDecrcModelsEqual(original, replica, "narrow-then-wide continuation");
        }
        Assert.AreEqual((10, 2), (original.CursorX, original.CursorY), "widening must restore the original save, then advance for R");
        using var screen = original.CreateSnapshot();
        Assert.AreEqual("R", screen.GetCell(9, 2).Character);
        Assert.AreEqual(" ", screen.GetCell(4, 1).Character, "R must not be written at the earlier clipped position");
    }

    [TestMethod]
    [DataRow("no-provider")]
    [DataRow("none")]
    [DataRow("xterm")]
    [DataRow("iterm2")]
    public void ModelRestore_DecrcAfterNarrowingPreservesPendingWrapAndProtection(string strategy)
    {
        using var original = FuzzModel(6, 4, null, strategy, modern: false);
        ApplyStep(original, "\u001b[2;6H\u001b[1\"qW\u001b7\u001b[0\"q\u001b[H");
        original.Resize(3, 4);
        ApplyStep(original, "\u001b8\u001b]133;A\u0007");
        Assert.AreEqual((2, 1), (original.CursorX, original.CursorY));
        Assert.IsTrue(original.PendingWrap, "DECRC must retain the saved deferred wrap even when X was clipped");
        Assert.IsTrue(original.CursorProtected, "DECRC must retain saved character protection");
        var start = original.CaptureModelState();
        Assert.AreEqual((int?)3, start.CommandMarks.Single().Column, "a pending-wrap mark belongs at the narrowed row's trailing boundary");
        using var replica = FuzzModel(6, 4, null, strategy, modern: false);
        replica.RestoreModelState(start);
        AssertDecrcModelsEqual(original, replica, "pending-wrap marked start");
        foreach (var step in new[] { "P", "\u001b[0\"qU\u001b[?2J" })
        {
            ApplyStep(original, step);
            ApplyStep(replica, step);
            AssertDecrcModelsEqual(original, replica, "wrap and selective erase");
        }
        using var screen = original.CreateSnapshot();
        Assert.AreEqual("P  ", screen.GetLine(2), "the saved wrap must put P on the next row and saved protection must spare P, not U");
        Assert.AreEqual("   ", screen.GetLine(1), "P must not overwrite the clipped edge before wrapping");
    }

    [TestMethod]
    [DataRow("foot")]
    [DataRow("ghostty")]
    [DataRow("vte")]
    public void ModelRestore_DecrcUsesProviderReflowedSavedPosition(string strategy)
    {
        using var original = FuzzModel(5, 5, 10, strategy, modern: false);
        ApplyStep(original, "ABCDEFGHIJ\u001b[2;1H\u001b7\u001b[3;1H");
        original.Resize(10, 5);
        using var replica = FuzzModel(5, 5, 10, strategy, modern: false);
        replica.RestoreModelState(original.CaptureModelState());
        ApplyStep(original, "\u001b8!");
        ApplyStep(replica, "\u001b8!");
        AssertDecrcModelsEqual(original, replica, "provider-mapped save");
        Assert.AreEqual((6, 0), (original.CursorX, original.CursorY), "the saved F position must map from (0,1) to (5,0), not the old coordinates");
        using var screen = original.CreateSnapshot();
        Assert.AreEqual("ABCDE!GHIJ", screen.GetLine(0));
    }

    public static IEnumerable<object[]> DecrcStrategiesAndScreens() =>
        from strategy in CaseConfiguration.StrategyIds.Append("no-provider")
        from alternate in new[] { false, true }
        select new object[] { strategy, alternate };

    public static IEnumerable<object[]> DecrcStrategiesAndSmallScreens() =>
        from strategy in CaseConfiguration.StrategyIds.Append("no-provider")
        from size in new[] { (Width: 1, Height: 1), (Width: 5, Height: 2) }
        select new object[] { strategy, size.Width, size.Height };

    private static void AssertDecrcModelsEqual(Hex1bTerminal original, Hex1bTerminal replica, string at)
    {
        var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{at}: " + string.Join("; ", differences.Take(5)));
        Assert.AreEqual(Digest(original), Digest(replica), $"{at}: public snapshots differ");
    }
}
