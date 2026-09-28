using System.Diagnostics;
using Hex1b.Diagnostics;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;

namespace Hex1b.Tests;

/// <summary>
/// Tests for diagnostic timing in published application frames and its text form.
/// </summary>
[TestClass]
public class DiagnosticTimingTests
{
    private static readonly ApplicationPassTimings NoPassCost = new(0, 0, 0);

    [TestMethod]
    public void NodeTiming_ConvertsTicksToMilliseconds()
    {
        var node = new TextBlockNode { Text = "test" };
        // Simulate 1ms of reconcile time (using Stopwatch frequency)
        node.DiagReconcileTicks = Stopwatch.Frequency / 1000; // 1ms
        node.DiagRenderTicks = Stopwatch.Frequency / 2000; // 0.5ms
        node.DiagLastRenderedTimestamp = Stopwatch.GetTimestamp() - Stopwatch.Frequency / 100; // 10ms ago

        var timing = Project(node, NoPassCost).Root!.Timing!;

        TestSeq.InRange(timing.ReconcileMs, 0.9, 1.1);
        TestSeq.InRange(timing.RenderMs, 0.4, 0.6);
        TestSeq.InRange(timing.LastRenderedMsAgo, 5, 15); // ~10ms ago with tolerance
    }

    [TestMethod]
    public void NodeTiming_ZeroTicks_ReturnsZerosAndNeverRendered()
    {
        var node = new ButtonNode { Label = "Click" };

        var timing = Project(node, NoPassCost).Root!.Timing;

        Assert.IsNotNull(timing, "timing-enabled frames report node timing even when a node cost nothing");
        Assert.AreEqual(0, timing.ReconcileMs);
        Assert.AreEqual(0, timing.RenderMs);
        Assert.AreEqual(-1, timing.LastRenderedMsAgo);
    }

    [TestMethod]
    public void NodeTiming_AbsentWhenTimingDisabled()
    {
        var node = new ButtonNode { Label = "Click" };
        node.DiagReconcileTicks = Stopwatch.Frequency / 1000;

        var frame = Project(node, timings: null);

        Assert.IsNull(frame.Timings);
        Assert.IsNull(frame.Root!.Timing);
    }

    [TestMethod]
    public void FrameTimings_ConvertPassTicksToMilliseconds()
    {
        var frequency = Stopwatch.Frequency;
        var frame = Project(new TextBlockNode { Text = "x" },
            new ApplicationPassTimings(frequency * 3 / 2000, frequency * 3 / 10000, frequency / 500));

        TestSeq.InRange(frame.Timings!.BuildMs, 1.49, 1.51);
        TestSeq.InRange(frame.Timings.ReconcileMs, 0.29, 0.31);
        TestSeq.InRange(frame.Timings.RenderMs, 1.99, 2.01);
    }

    [TestMethod]
    public void NodeTiming_ToString_FormatsCorrectly()
    {
        var timing = new DiagnosticNodeTiming
        {
            ReconcileMs = 0.15,
            RenderMs = 0.30,
            LastRenderedMsAgo = 12
        };

        var result = timing.ToString();

        Assert.Contains("reconcile=0.15ms", result);
        Assert.Contains("render=0.30ms", result);
        Assert.Contains("last=12ms ago", result);
    }

    [TestMethod]
    public void NodeTiming_ToString_OmitsZeroValues()
    {
        var timing = new DiagnosticNodeTiming
        {
            ReconcileMs = 0,
            RenderMs = 0.5,
            LastRenderedMsAgo = -1
        };

        var result = timing.ToString();

        Assert.DoesNotContain("reconcile", result);
        Assert.Contains("render=0.50ms", result);
        Assert.DoesNotContain("last=", result);
    }

    [TestMethod]
    public void DiagnosticRect_ToString_IncludesCornerCoordinates()
    {
        var rect = DiagnosticRect.FromRect(new Rect(5, 10, 20, 8));

        var str = rect.ToString();

        Assert.Contains("x=5", str);
        Assert.Contains("y=10", str);
        Assert.Contains("w=20", str);
        Assert.Contains("h=8", str);
        Assert.Contains("(5,10 → 25,18)", str);
    }

    [TestMethod]
    public void Hex1bNode_TimingFields_DefaultToZero()
    {
        var node = new TextBlockNode { Text = "test" };

        Assert.AreEqual(0, node.DiagReconcileTicks);
        Assert.AreEqual(0, node.DiagRenderTicks);
        Assert.AreEqual(0, node.DiagLastRenderedTimestamp);
    }

    private static DiagnosticApplicationFrame Project(Hex1bNode root, ApplicationPassTimings? timings) =>
        ApplicationFrameProjector.Project(root, new FocusRing(), "stub", frameId: 1, columns: 10, rows: 1, wroteOutput: true, timings);
}
