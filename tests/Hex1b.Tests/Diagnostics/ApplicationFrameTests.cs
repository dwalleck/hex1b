using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A diagnostics-enabled Hex1b app publishes an immutable projection after every completed
/// render pass, and the shared engine returns the latest one without driving the app loop.
/// </summary>
[TestClass]
public class ApplicationFrameTests
{
    [TestMethod]
    public async Task PublishesEveryCompletedPass()
    {
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget($"count {counter}"));

        for (var i = 0; i < 10; i++)
        {
            var changed = await harness.NextPassAsync(() => counter++);
            var withChange = harness.Capture();
            Assert.AreEqual(changed, withChange.Frame!.FrameId, "frame identity is not the completed-pass count");
            Assert.IsTrue(withChange.Frame.WroteOutput, $"pass {changed} changed text but reports no output");
            Assert.AreEqual(changed, withChange.Identity!.ApplicationFrame);

            var unchanged = await harness.NextPassAsync(change: null);
            var withoutChange = harness.Capture();
            Assert.AreEqual(unchanged, withoutChange.Frame!.FrameId, $"frame {unchanged} not published; latest is {withoutChange.Frame.FrameId}");
            Assert.IsFalse(withoutChange.Frame.WroteOutput, $"pass {unchanged} changed nothing but reports output");
        }
    }

    [TestMethod]
    public async Task CaptureNeverAdvancesFrames()
    {
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget("idle"));
        await harness.WaitForQuietAsync();
        var before = harness.App.FrameCount;

        for (var i = 0; i < 100; i++)
            Assert.AreEqual(DiagnosticOutcome.Captured, harness.Capture().Outcome);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.AreEqual(before, harness.App.FrameCount, $"passes advanced {before} -> {harness.App.FrameCount}");
        Assert.AreEqual(before, harness.Capture().Frame!.FrameId);
    }

    [TestMethod]
    public async Task NoProjectionWithoutDiagnostics()
    {
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget($"n {counter}"), publish: false);
        for (var i = 0; i < 10; i++)
            await harness.NextPassAsync(() => counter++);

        var source = (IApplicationFrameSource)harness.App;
        Assert.IsNull(source.LatestFrame, $"an app without diagnostics projected frame {source.LatestFrame?.FrameId}");
        var result = harness.Capture();
        Assert.AreEqual(DiagnosticOutcome.Unavailable, result.Outcome);
        Assert.AreEqual("application-frame-publication-disabled", result.Problem!.Code);
    }

    [TestMethod]
    public async Task UnavailableReasonsAreDistinct()
    {
        // Publication enabled but the first pass is still building: no frame yet.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        using var cts = new CancellationTokenSource();
        using var app = new Hex1bApp(async _ => { await gate.Task; return new TextBlockWidget("late"); },
            new Hex1bAppOptions { WorkloadAdapter = workload });
        var run = app.RunAsync(cts.Token);
        for (var i = 0; i < 100 && workload.ApplicationFrameSource is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var notYet = new TerminalDiagnostics(terminal, "gated").CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
        gate.SetResult();
        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        await using var disabled = await AppHarness.StartAsync(_ => new TextBlockWidget("off"), publish: false);
        var off = disabled.Capture();

        var rawWorkload = new Hex1bAppWorkloadAdapter();
        await using var raw = Hex1bTerminal.CreateBuilder().WithWorkload(rawWorkload).WithHeadless().WithDimensions(20, 3).Build();
        var noApp = new TerminalDiagnostics(raw, "raw").CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());

        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-application-frame-yet"), (notYet.Outcome, notYet.Problem!.Code));
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "application-frame-publication-disabled"), (off.Outcome, off.Problem!.Code));
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-application-layer"), (noApp.Outcome, noApp.Problem!.Code));
    }

    [TestMethod]
    public async Task Clipping_VisibleBoundsFollowEnclosingClipRegions()
    {
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            // Inner area is rows 1-2. row-1 wraps onto rows 2-3, straddling the clip edge.
            new BorderWidget(new VStackWidget(
            [
                new TextBlockWidget("row-0"),
                new TextBlockWidget($"row-1 {new string('w', 60)}", TextOverflow.Wrap),
                .. Enumerable.Range(2, 8).Select(i => (Hex1bWidget)new TextBlockWidget($"row-{i}")),
            ])).FixedHeight(4),
        ]), columns: 40, rows: 10);

        var frame = harness.Capture().Frame!;
        var nodes = new List<(DiagnosticFrameNode Node, Rect Expected)>();
        Walk(frame.Root!, new Rect(0, 0, frame.Columns, frame.Rows), nodes);

        foreach (var (node, expected) in nodes)
        {
            var visible = node.VisibleBounds;
            Assert.AreEqual(Describe(expected), $"{visible.X},{visible.Y},{visible.Width},{visible.Height}",
                $"{node.Type} visible {Describe(visible)} != expected {Describe(expected)} for bounds {Describe(node.Bounds)}");
            var expectedState = expected.Width <= 0 || expected.Height <= 0 ? DiagnosticClipState.FullyClipped
                : expected.Width == node.Bounds.Width && expected.Height == node.Bounds.Height ? DiagnosticClipState.Visible
                : DiagnosticClipState.PartiallyClipped;
            if (node.Bounds.Width > 0 && node.Bounds.Height > 0)
                Assert.AreEqual(expectedState, node.ClipState, $"{node.Type} at {Describe(node.Bounds)}");
        }

        var rows = nodes.Where(n => n.Node.Text?.StartsWith("row-", StringComparison.Ordinal) == true).Select(n => n.Node).ToList();
        Assert.IsTrue(rows.Any(r => r.ClipState == DiagnosticClipState.PartiallyClipped), "no row was partially clipped by the border");
        Assert.IsTrue(rows.Any(r => r.ClipState == DiagnosticClipState.FullyClipped), "no row was clipped away by the border");
        var border = nodes.Single(n => n.Node.Type == nameof(BorderNode)).Node;
        Assert.AreEqual("clip", border.ClipMode);
        Assert.IsNotNull(border.ClipRect);
    }

    [TestMethod]
    public async Task ProjectionFailure_ReportsTheFailedFrameAndRecovers()
    {
        var armed = new StrongBox<bool>();
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            new TextBlockWidget($"n {counter}"),
            new ArmableProjectionFailureWidget(armed),
        ]));

        armed.Value = true;
        var failedPass = await harness.NextPassAsync(() => counter++);
        var failed = harness.Capture();
        armed.Value = false;

        Assert.AreEqual(DiagnosticOutcome.Failed, failed.Outcome,
            $"captured frame {failed.Frame?.FrameId} while frame {failedPass} failed");
        Assert.AreEqual("application-frame-projection-failed", failed.Problem!.Code);
        StringAssert.Contains(failed.Problem.Message, $"frame {failedPass}");
        await harness.WaitForTextAsync($"n {counter}");

        var recovered = await harness.NextPassAsync(() => counter++);
        var result = harness.Capture();
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.AreEqual(recovered, result.Frame!.FrameId);
    }

    // === Helpers ===

    private static void Walk(DiagnosticFrameNode node, Rect clip, List<(DiagnosticFrameNode, Rect)> into)
    {
        var bounds = new Rect(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
        into.Add((node, Overlap(bounds, clip)));
        var childClip = node.ClipMode == "clip" && node.ClipRect is { } own
            ? Overlap(clip, new Rect(own.X, own.Y, own.Width, own.Height))
            : clip;
        foreach (var child in node.Children)
            Walk(child, childClip, into);
    }

    // Independent rectangle arithmetic for the oracle.
    private static Rect Overlap(Rect a, Rect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : new Rect(left, top, 0, 0);
    }

    private static string Describe(Rect r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private static string Describe(DiagnosticRect r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    internal sealed class StrongBox<T>
    {
        public T? Value { get; set; }
    }

    internal sealed record ArmableProjectionFailureWidget(StrongBox<bool> Armed) : Hex1bWidget
    {
        internal override Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            var node = existingNode as ArmableProjectionFailureNode ?? new ArmableProjectionFailureNode();
            node.Armed = Armed;
            return Task.FromResult<Hex1bNode>(node);
        }

        internal override Type GetExpectedNodeType() => typeof(ArmableProjectionFailureNode);
    }

    // Rendering never reads HitTestBounds, so arming it makes only the diagnostic projection fail.
    internal sealed class ArmableProjectionFailureNode : Hex1bNode
    {
        public StrongBox<bool> Armed { get; set; } = new();

        public override Rect HitTestBounds =>
            Armed.Value ? throw new InvalidOperationException("armed projection failure") : Bounds;

        protected override Size MeasureCore(Constraints constraints) => constraints.Constrain(new Size(1, 1));

        public override void Render(Hex1bRenderContext context)
        {
        }
    }

    internal sealed class AppHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;

        private AppHarness(Hex1bTerminal terminal, Hex1bApp app)
        {
            Terminal = terminal;
            App = app;
            Diagnostics = new TerminalDiagnostics(terminal, "frames");
        }

        public Hex1bTerminal Terminal { get; }
        public Hex1bApp App { get; }
        public TerminalDiagnostics Diagnostics { get; }

        public static async Task<AppHarness> StartAsync(Func<RootContext, Hex1bWidget> build, bool publish = true,
            int columns = 40, int rows = 6)
        {
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = publish };
            var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(columns, rows).Build();
            var app = new Hex1bApp(build, new Hex1bAppOptions { WorkloadAdapter = workload });
            var harness = new AppHarness(terminal, app);
            harness._run = app.RunAsync(harness._cts.Token);
            await harness.WaitForPassAsync(1);
            return harness;
        }

        public DiagnosticApplicationFrameResult Capture(params DiagnosticAuthorization[] authorizations) =>
            Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest { Authorizations = authorizations });

        public async Task<long> NextPassAsync(Action? change)
        {
            var target = App.FrameCount + 1;
            change?.Invoke();
            App.Invalidate();
            await WaitForPassAsync(target);
            return App.FrameCount;
        }

        public async Task WaitForPassAsync(long pass)
        {
            for (var i = 0; i < 500 && App.FrameCount < pass; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.IsTrue(App.FrameCount >= pass, $"pass {pass} never completed (at {App.FrameCount})");
        }

        public async Task WaitForQuietAsync()
        {
            var last = -1L;
            while (App.FrameCount != last)
            {
                last = App.FrameCount;
                await Task.Delay(150, TestContext.Current.CancellationToken);
            }
        }

        public Task WaitForTextAsync(string text) => new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText(text), TimeSpan.FromSeconds(5), $"'{text}' rendered")
            .Build().ApplyAsync(Terminal, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            App.Dispose();
            await Terminal.DisposeAsync();
            _cts.Dispose();
        }
    }
}
