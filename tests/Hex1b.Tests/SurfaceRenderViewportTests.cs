using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class SurfaceRenderViewportTests
{
    [TestMethod]
    [DataRow(9999, true)]
    [DataRow(10000, true)]
    [DataRow(10001, true)]
    [DataRow(9999, false)]
    [DataRow(10000, false)]
    [DataRow(10001, false)]
    public void ScrollPanel_LastRowsBeyondSurfaceLimit_RemainVisible(int lineCount, bool caching)
    {
        var text = new TextBlockNode { Text = string.Join("\n", Enumerable.Range(0, lineCount).Select(i => $"{i:D5}")) };
        var panel = new ScrollPanelNode { Child = text, ShowScrollbar = false };
        var surface = new Surface(10, 7);
        var empty = surface[0, 0].Character;
        var context = new SurfaceRenderContext(surface) { CachingEnabled = caching };
        panel.Measure(Constraints.Tight(6, 3));
        panel.Arrange(new Rect(2, 2, 6, 3));
        panel.Render(context);
        Assert.AreEqual("0", surface[2, 2].Character);
        panel.SetOffset(lineCount - 3);
        panel.Arrange(new Rect(2, 2, 6, 3));
        surface.Clear();
        panel.Render(context);
        for (var row = 0; row < 3; row++)
        {
            var actual = string.Concat(Enumerable.Range(2, 5).Select(x => surface[x, row + 2].Character));
            Assert.AreEqual($"{lineCount - 3 + row:D5}", actual, $"Visible row {row}, caching={caching}");
        }
        Assert.AreEqual(empty, surface[2, 1].Character, "Content must not bleed above the viewport.");
        Assert.AreEqual(empty, surface[2, 5].Character, "Content must not bleed below the viewport.");
    }
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void ScrollPanel_WideChildHonorsNarrowAncestorAndSurfaceOrigin(bool caching)
    {
        var text = new TextBlockNode { Text = new string('x', 9996) + "TAIL!" };
        var panel = new ScrollPanelNode { Child = text, ShowScrollbar = false, Orientation = ScrollOrientation.Horizontal };
        var surface = new Surface(10, 7);
        var empty = surface[0, 0].Character;
        var context = new SurfaceRenderContext(surface, 100, 200)
        {
            CachingEnabled = caching,
            CurrentLayoutProvider = new RectLayoutProvider(new Rect(104, 202, 1, 1))
        };
        panel.Measure(Constraints.Tight(3, 1));
        panel.Arrange(new Rect(102, 202, 3, 1));
        panel.SetOffset(9998);
        panel.Arrange(new Rect(102, 202, 3, 1));
        panel.Render(context);
        Assert.AreEqual("!", surface[4, 2].Character);
        Assert.AreEqual(empty, surface[2, 2].Character);
        Assert.AreEqual(empty, surface[3, 2].Character);
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RenderChild_UnboundedExtentAtPositiveOrigin_RendersVisibleMarker(bool caching)
    {
        var child = new TextBlockNode { Text = "MARK" };
        child.Measure(new Constraints(0, int.MaxValue, 0, int.MaxValue));
        child.Arrange(new Rect(102, 202, int.MaxValue, int.MaxValue));
        var surface = new Surface(10, 7);
        var context = new SurfaceRenderContext(surface, 100, 200)
        {
            CachingEnabled = caching,
            CurrentLayoutProvider = new RectLayoutProvider(new Rect(100, 200, 100, 100))
        };
        context.RenderChild(child);
        Assert.AreEqual("MARK", string.Concat(Enumerable.Range(2, 4).Select(x => surface[x, 2].Character)));
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void RenderChild_OversizedOccluder_UsesVisibleSurfaceOrigin(bool caching)
    {
        var child = new TextBlockNode { Text = string.Join("\n", Enumerable.Repeat("ABCDE", 10001)) };
        child.Measure(new Constraints(0, 5, 0, int.MaxValue));
        child.Arrange(new Rect(102, -9798, 5, 10001));
        var surface = new Surface(10, 7);
        var registry = new Hex1b.Kgp.KgpImageRegistry();
        var context = new SurfaceRenderContext(surface, 100, 200)
        {
            CachingEnabled = caching,
            KgpRegistry = registry,
            CurrentLayoutProvider = new RectLayoutProvider(new Rect(103, 201, 2, 2))
        };
        context.RenderChild(child);
        Assert.AreEqual("B", surface[3, 1].Character);
        Assert.AreEqual(new Rect(103, 201, 2, 2), TestSeq.Single(registry.Occluders).Bounds);
    }

}
