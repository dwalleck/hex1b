using Hex1b.Flow;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Tokens;

namespace Hex1b.Tests.Flow;

[TestClass]
public class SoftWrapEmitterHyperlinkTests
{
    [TestMethod]
    public void RenderRowText_HyperlinkTransitions_PreserveTargetsParametersAndUnlinkedGaps()
    {
        var firstStore = new TrackedObjectStore();
        var secondStore = new TrackedObjectStore();
        var surface = new Surface(9, 1);
        surface[0, 0] = new SurfaceCell("A", null, null,
            Hyperlink: firstStore.GetOrCreateHyperlink("https://example.test/one", "id=one"));
        surface[1, 0] = new SurfaceCell("B", null, null,
            Hyperlink: secondStore.GetOrCreateHyperlink("https://example.test/one", "id=one"));
        surface[2, 0] = new SurfaceCell("C", null, null,
            Hyperlink: firstStore.GetOrCreateHyperlink("https://example.test/two", "id=one"));
        surface[3, 0] = new SurfaceCell("D", null, null,
            Hyperlink: firstStore.GetOrCreateHyperlink("https://example.test/two", "id=two"));
        // Column four is an unwritten gap. It must be emitted without a link.
        surface.WriteText(5, 0, "E");
        try
        {
            var output = SoftWrapEmitter.RenderRowText(surface, 0);
            Assert.AreEqual("\x1b[0m\x1b]8;id=one;https://example.test/one\x1b\\AB"
                + "\x1b]8;id=one;https://example.test/two\x1b\\C"
                + "\x1b]8;id=two;https://example.test/two\x1b\\D"
                + "\x1b]8;;\x1b\\ E", output);
            Assert.AreEqual(1, surface[0, 0].Hyperlink!.RefCount,
                "Emission borrows metadata; the surface retains its existing ownership.");
            Assert.AreEqual(1, surface[1, 0].Hyperlink!.RefCount);
        }
        finally { surface.ClearAndReleaseTrackedObjects(); }
    }

    [TestMethod]
    public void RenderRowText_LinkedWideGraphemeStyleAndTrailingSpace_PreserveTheLinkSpan()
    {
        var store = new TrackedObjectStore();
        var surface = new Surface(6, 1);
        surface.WriteText(0, 0, "漢 ", attributes: CellAttributes.Bold);
        foreach (var column in new[] { 0, 1, 2 })
        {
            surface[column, 0] = surface[column, 0] with
            {
                Hyperlink = store.GetOrCreateHyperlink("https://example.test/wide", "")
            };
        }
        try
        {
            Assert.AreEqual("\x1b[0;1m\x1b]8;;https://example.test/wide\x1b\\漢 "
                + "\x1b]8;;\x1b\\\x1b[0m", SoftWrapEmitter.RenderRowText(surface, 0));
            Assert.AreEqual(3, surface[0, 0].Hyperlink!.RefCount);
        }
        finally { surface.ClearAndReleaseTrackedObjects(); }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void Format_HyperlinkedRow_ClosesBeforeClearNextRowAndLaterWrites(bool ordered)
    {
        var store = new TrackedObjectStore();
        var surface = new Surface(8, 2);
        surface[0, 0] = new SurfaceCell("L", null, null,
            Hyperlink: store.GetOrCreateHyperlink("https://example.test/row", "id=row"));
        surface.WriteText(0, 1, "prompt");
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 4).Build();
        try
        {
            var output = ordered ? SoftWrapEmitter.FormatOrdered(surface) : SoftWrapEmitter.Format(surface);
            StringAssert.Contains(output, ordered
                ? "L\x1b]8;;\x1b\\\r\n"
                : "L\x1b]8;;\x1b\\\x1b[K\r\n");
            terminal.ApplyTokens(AnsiTokenizer.Tokenize(output + "\r\nnext"));
            using var snapshot = terminal.CreateSnapshot();
            Assert.AreEqual("L", snapshot.GetCell(0, 0).Character);
            Assert.IsNotNull(snapshot.GetCell(0, 0).HyperlinkData);
            Assert.AreEqual("https://example.test/row", snapshot.GetCell(0, 0).HyperlinkData!.Uri);
            Assert.AreEqual("id=row", snapshot.GetCell(0, 0).HyperlinkData!.Parameters);
            Assert.AreEqual("p", snapshot.GetCell(0, 1).Character);
            Assert.AreEqual("n", snapshot.GetCell(0, 2).Character);
            for (var y = 0; y < 3; y++)
            for (var x = y == 0 ? 1 : 0; x < 8; x++)
                Assert.IsNull(snapshot.GetCell(x, y).HyperlinkData, $"Unexpected link at {x},{y}");
        }
        finally { surface.ClearAndReleaseTrackedObjects(); }
    }

    [TestMethod]
    public void FormatOrdered_HyperlinkedRightMargin_PreservesLastCellAndWrapBoundary()
    {
        var store = new TrackedObjectStore();
        var surface = new Surface(4, 1);
        surface.WriteText(0, 0, "ABCD");
        surface[3, 0] = surface[3, 0] with
        {
            Hyperlink = store.GetOrCreateHyperlink("https://example.test/edge", "")
        };
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(4, 3).Build();
        try
        {
            terminal.ApplyTokens(AnsiTokenizer.Tokenize(SoftWrapEmitter.FormatOrdered(surface) + "\r\nP"));
            using var snapshot = terminal.CreateSnapshot();
            Assert.AreEqual("D", snapshot.GetCell(3, 0).Character);
            Assert.IsNotNull(snapshot.GetCell(3, 0).HyperlinkData);
            Assert.AreEqual("https://example.test/edge", snapshot.GetCell(3, 0).HyperlinkData!.Uri);
            Assert.AreEqual("P", snapshot.GetCell(0, 1).Character);
            Assert.IsNull(snapshot.GetCell(0, 1).HyperlinkData);
        }
        finally { surface.ClearAndReleaseTrackedObjects(); }
    }
}
