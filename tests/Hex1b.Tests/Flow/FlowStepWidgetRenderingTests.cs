using System.Text;
using Hex1b.Composition;
using Hex1b.Documents;
using Hex1b.Flow;
using Hex1b.Layout;
using Hex1b.Markdown;
using Hex1b.Nodes;
using Hex1b.Reflow;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
public class FlowStepWidgetRenderingTests
{
    [TestMethod]
    public async Task RenderWidgetAsync_Markdown_PreservesThemeStylesCodeAndHyperlinks()
    {
        var heading = Hex1bColor.FromRgb(19, 211, 137);
        var inline = Hex1bColor.FromRgb(227, 103, 41);
        var theme = new Hex1bTheme("flow materialization")
            .Set(EditorTheme.ForegroundColor, heading)
            .Set(MarkdownTheme.InlineCodeForegroundColor, inline);
        await WithLiveStepAsync(async (step, terminal) =>
        {
            // Step readiness can precede the presentation pump's first frame.
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("live materialization prompt"),
                    TimeSpan.FromSeconds(5), "live prompt presented before materialization")
                .Build()
                .ApplyAsync(terminal, TestContext.Current.CancellationToken);
            var before = terminal.GetScreenText();
            var surface = await step.RenderWidgetAsync(new MarkdownWidget(
                "# Heading\n\n**bold** *italic* `inline` [site](https://example.test/docs)\n\n```csharp\nvar count = 3;\n```\n\n```diff\n-old\n+new\n```"), 80, 30);
            try
            {
                Assert.AreEqual(80, surface.Width);
                Assert.IsTrue(surface.Height < 30, "The surface must have content height, not the maximum allocation.");
                Assert.AreNotEqual(CellAttributes.None, FindCell(surface, "Heading").Attributes & CellAttributes.Bold);
                Assert.AreNotEqual(CellAttributes.None, FindCell(surface, "bold").Attributes & CellAttributes.Bold);
                Assert.AreNotEqual(CellAttributes.None, FindCell(surface, "italic").Attributes & CellAttributes.Italic);
                Assert.AreEqual(inline, FindCell(surface, "inline").Foreground);
                var link = FindCell(surface, "site").Hyperlink;
                Assert.IsNotNull(link);
                Assert.AreEqual("https://example.test/docs", link.Data.Uri);
                Assert.AreEqual(4, link.RefCount, "Only the four returned 'site' cells must own the link after transient cleanup.");
                StringAssert.Contains(Text(surface), "var count = 3;");
                Assert.AreEqual(heading, FindCell(surface, "var count").Foreground);
                StringAssert.Contains(Text(surface), "-old");
                StringAssert.Contains(Text(surface), "+new");
                Assert.AreEqual(before, terminal.GetScreenText(), "Materialization must not emit content to the live terminal.");
            }
            finally { surface.ClearAndReleaseTrackedObjects(); }
        }, theme);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_WrappedText_UsesRequestedWidthAndNaturalHeight()
    {
        var step = CreateStep();
        var widget = new TextBlockWidget("abcde fghij", TextOverflow.Wrap);
        var wide = await step.RenderWidgetAsync(widget, 11, 20);
        var narrow = await step.RenderWidgetAsync(widget, 5, 20);
        Assert.AreEqual(11, wide.Width);
        Assert.AreEqual(1, wide.Height);
        Assert.AreEqual("abcde fghij", Text(wide));
        Assert.AreEqual(5, narrow.Width);
        Assert.AreEqual(2, narrow.Height);
        Assert.AreEqual("abcde\nfghij", Text(narrow));
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenderWidgetAsync_ParentCapabilities_ReachMeasureAndRender(bool trueColor)
    {
        var capabilities = TerminalCapabilities.Modern with { SupportsTrueColor = trueColor };
        var probe = new ProbeNode();
        await WithLiveStepAsync(async (step, _) =>
        {
            var surface = await step.RenderWidgetAsync(new ProbeWidget(probe), 12, 3);
            Assert.AreEqual(trueColor, probe.MeasuredTrueColor);
            Assert.AreEqual(trueColor, probe.RenderedTrueColor);
            Assert.AreEqual("probe", Text(surface));
            Assert.AreEqual(1, probe.DisposeCount);
        }, capabilities: capabilities);
    }

    [TestMethod]
    [DataRow(0, 1)]
    [DataRow(-1, 1)]
    [DataRow(1, 0)]
    [DataRow(1, -1)]
    public void RenderWidgetAsync_InvalidDimensions_FailBeforeReconciliation(int width, int height)
    {
        var probe = new ProbeNode();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => CreateStep().RenderWidgetAsync(new ProbeWidget(probe), width, height));
        Assert.AreEqual(0, probe.MeasureCount);
        Assert.AreEqual(0, probe.DisposeCount);
    }

    [TestMethod]
    public void RenderWidgetAsync_NullWidgetAndCompletedStep_AreRejected()
    {
        var step = CreateStep();
        Assert.ThrowsExactly<ArgumentNullException>(() => step.RenderWidgetAsync(null!, 10, 10));
        step.Complete();
        Assert.ThrowsExactly<InvalidOperationException>(() => step.RenderWidgetAsync(new TextBlockWidget("done"), 10, 10));
    }

    [TestMethod]
    public async Task RenderWidgetAsync_OversizedContent_IsRejectedBeforeRenderAndCleanedUp()
    {
        var probe = new ProbeNode { NaturalHeight = 2 };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(new ProbeWidget(probe), 10, 1));
        StringAssert.Contains(error.Message, "height 1");
        Assert.AreEqual(1, probe.MeasureCount);
        Assert.AreEqual(0, probe.RenderCount);
        Assert.AreEqual(1, probe.DisposeCount);
        var textError = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(new TextBlockWidget("first\nsecond"), 10, 1));
        StringAssert.Contains(textError.Message, "height 1");
    }

    [TestMethod]
    public async Task RenderWidgetAsync_RendererDimensionLimit_IsExplicitRatherThanClipped()
    {
        var probe = new ProbeNode { NaturalHeight = 10_001 };
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(new VStackWidget([new ProbeWidget(probe)]), 1, 10_001));
        StringAssert.Contains(error.Message, "10000");
        Assert.AreEqual(0, probe.RenderCount);
        Assert.AreEqual(1, probe.DisposeCount);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_OpaqueSurfaceWidget_IsRefusedBeforeEnteringLayers()
    {
        var layerCalls = 0;
        var layerState = new DisposableState();
        var widget = new VStackWidget([new SurfaceWidget(ctx =>
        {
            layerCalls++;
            return [ctx.WidgetLayer(new StatefulWidget(layerState, new TextBlockWidget("opaque")))];
        }).Size(10, 1)]);
        var error = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            () => CreateStep().RenderWidgetAsync(widget, 10, 2));
        StringAssert.Contains(error.Message, "SurfaceWidget");
        Assert.AreEqual(0, layerCalls, "Refusal must precede the opaque layer's callbacks and resource acquisition.");
        Assert.AreEqual(0, layerState.DisposeCount, "The unentered layer remains outside transient ownership.");
    }

    [TestMethod]
    public async Task RenderWidgetAsync_RenderFailure_PropagatesAndCleansTransientState()
    {
        var expected = new InvalidOperationException("render failed deliberately");
        var probe = new ProbeNode { RenderFailure = expected };
        var state = new DisposableState();
        var widget = new StatefulWidget(state, new ProbeWidget(probe));
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(widget, 10, 2));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, probe.RenderCount);
        Assert.AreEqual(1, probe.DisposeCount);
        Assert.AreEqual(1, state.DisposeCount);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_CodeBlockEditor_DetachesDocumentAndDecorationProvider()
    {
        var document = new Hex1bDocument("code");
        var provider = new TrackingDecorationProvider();
        var widget = new CodeBlockEditorWidget(new EditorWidget(new EditorState(document) { IsReadOnly = true })
            .Decorations(provider));
        var surface = await CreateStep().RenderWidgetAsync(widget, 20, 1);
        Assert.AreEqual("code", Text(surface));
        Assert.AreEqual(1, provider.Activations);
        var node = provider.Node;
        Assert.IsNotNull(node);
        node.ClearDirty();
        document.Apply(new InsertOperation(DocumentOffset.Zero, "changed "));
        Assert.IsFalse(node.IsDirty, "The caller's document must not retain the transient editor through Changed.");
        Assert.AreEqual(1, provider.Deactivations);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_EditorCleanupFailure_StillReleasesOtherProviders()
    {
        var expected = new InvalidOperationException("provider cleanup failed deliberately");
        var document = new Hex1bDocument("code");
        var failing = new TrackingDecorationProvider { CleanupFailure = expected };
        var remaining = new TrackingDecorationProvider();
        var widget = new CodeBlockEditorWidget(new EditorWidget(new EditorState(document))
            .Decorations(failing).Decorations(remaining));
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(widget, 20, 1));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(1, failing.Deactivations);
        Assert.AreEqual(1, remaining.Deactivations);
        var node = remaining.Node;
        Assert.IsNotNull(node);
        node.ClearDirty();
        document.Apply(new InsertOperation(DocumentOffset.Zero, "changed "));
        Assert.IsFalse(node.IsDirty);
        node.ReleaseTransientResources();
        Assert.AreEqual(1, failing.Deactivations, "Cleanup must be idempotent after failure.");
        Assert.AreEqual(1, remaining.Deactivations);
    }

    [TestMethod]
    public void SurfaceComposite_HyperlinkOwnership_RetainsReplacesAndReleasesIndependently()
    {
        var store = new TrackedObjectStore();
        var firstLink = store.GetOrCreateHyperlink("https://example.test/first", "");
        var secondLink = store.GetOrCreateHyperlink("https://example.test/second", "");
        var first = new Surface(1, 1);
        var second = new Surface(1, 1);
        var target = new Surface(1, 1);
        first[0, 0] = new SurfaceCell("a", null, null, Hyperlink: firstLink);
        second[0, 0] = new SurfaceCell("b", null, null, Hyperlink: secondLink);
        target.Composite(first, 0, 0);
        Assert.AreEqual(2, firstLink.RefCount);
        target.Composite(first, 0, 0);
        Assert.AreEqual(2, firstLink.RefCount, "Compositing the same reference must be neutral.");
        target.Composite(second, 0, 0);
        Assert.AreEqual(1, firstLink.RefCount, "Replacing a link releases the old destination reference.");
        Assert.AreEqual(2, secondLink.RefCount);
        second.ClearAndReleaseTrackedObjects();
        Assert.AreEqual(1, secondLink.RefCount, "Source cleanup must leave the destination alive.");
        Assert.AreEqual("https://example.test/second", target[0, 0].Hyperlink!.Data.Uri);
        target.ClearAndReleaseTrackedObjects();
        first.ClearAndReleaseTrackedObjects();
        Assert.AreEqual(0, firstLink.RefCount);
        Assert.AreEqual(0, secondLink.RefCount);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_LaterSiblingReconciliationFailure_DisposesEarlierSibling()
    {
        var expected = new InvalidOperationException("later sibling failed deliberately");
        var probe = new ProbeNode();
        var state = new DisposableState();
        var widget = new VStackWidget([
            new StatefulWidget(state, new ProbeWidget(probe)),
            new ThrowingWidget(expected)
        ]);
        var actual = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => CreateStep().RenderWidgetAsync(widget, 10, 10));
        Assert.AreSame(expected, actual);
        Assert.AreEqual(0, probe.RenderCount);
        Assert.AreEqual(1, probe.DisposeCount);
        Assert.AreEqual(1, state.DisposeCount);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_PreCanceled_DoesNotReconcile()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<OperationCanceledException>(() => CreateStep().RenderWidgetAsync(
            new WaitingWidget(entered), 10, 10, cancellation.Token));
        Assert.IsFalse(entered.Task.IsCompleted);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task RenderWidgetAsync_CancellationDuringReconciliation_DisposesEarlierSibling(bool cancelFlow)
    {
        using var cancellation = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probe = new ProbeNode();
        var widget = new VStackWidget([new ProbeWidget(probe), new WaitingWidget(entered)]);
        var step = CreateStep(cancelFlow ? cancellation.Token : default);
        var render = step.RenderWidgetAsync(widget, 10, 10, cancelFlow ? default : cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => render.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.AreEqual(0, probe.RenderCount);
        Assert.AreEqual(1, probe.DisposeCount);
    }

    [TestMethod]
    public async Task RenderWidgetAsync_CommitSource_PreservesPersistentAppAndLiveState()
    {
        var liveState = new TextBoxState("canonical draft");
        Hex1bWidget BuildLive(FlowStepContext ctx) => ctx.VStack(v => [v.Text("live prompt"), v.TextBox().State(liveState)]);
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(ctx => Task.FromResult(BuildLive(ctx)));
                try
                {
                    await step.WaitForReadyAsync();
                    var app = step.AppForDiagnostics;
                    Assert.IsNotNull(app);
                    var source = new WidgetCommitSource(step);
                    var result = await step.CommitAsync(source, ctx => Task.FromResult(BuildLive(ctx)));
                    Assert.AreEqual(2, result.CompletedUnits);
                    CollectionAssert.AreEqual(new[] { "rich-0", "rich-1" }, result.RowKeys.ToArray());
                    Assert.AreSame(app, step.AppForDiagnostics, "The continuous-history consumer must retain the app instance.");
                    Assert.AreEqual("canonical draft", liveState.Text);
                    Assert.AreEqual(2, source.RequestedWidths.Count);
                    Assert.IsTrue(source.RequestedWidths.All(width => width == 80));
                }
                finally { await step.CompleteAsync(); }
            }, options => options.UseSoftWrapTombstones = true)
            .WithHeadless().WithDimensions(80, 24).WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(100).Build();
        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
        var transcript = new StringBuilder();
        foreach (var row in terminal.GetScrollbackRows(terminal.ScrollbackCount))
            transcript.AppendLine(string.Concat(row.Cells.Select(cell => cell.Character)));
        transcript.Append(terminal.GetScreenText());
        var text = transcript.ToString();
        Assert.AreEqual(1, text.Split("COMMITTED-ONE", StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, text.Split("COMMITTED-TWO", StringSplitOptions.None).Length - 1);
    }

    private static FlowStep CreateStep(CancellationToken flowCancellation = default)
    {
        var step = new FlowStep(80, 24, 2);
        step.AttachWidgetRenderer(new FlowWidgetRenderer(null, () => TerminalCapabilities.Modern, flowCancellation));
        return step;
    }

    private static async Task WithLiveStepAsync(
        Func<FlowStep, Hex1bTerminal, Task> action, Hex1bTheme? theme = null,
        TerminalCapabilities? capabilities = null)
    {
        Hex1bTerminal terminal = null!;
        using var terminalLifetime = terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(ctx => ctx.Text("live materialization prompt"));
                try
                {
                    await step.WaitForReadyAsync();
                    await action(step, terminal);
                }
                finally { await step.CompleteAsync(); }
            }, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.Theme = theme;
            })
            .WithHeadless(capabilities ?? TerminalCapabilities.Modern)
            .WithDimensions(80, 24).Build();
        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static string Text(Surface surface)
        => string.Join('\n', Enumerable.Range(0, surface.Height).Select(y => Row(surface, y).TrimEnd()));

    private static string Row(Surface surface, int y)
        => string.Concat(Enumerable.Range(0, surface.Width).Select(x =>
            surface[x, y] == SurfaceCells.Empty ? " " : surface[x, y].Character));

    private static SurfaceCell FindCell(Surface surface, string text)
    {
        for (var y = 0; y < surface.Height; y++)
        {
            var x = Row(surface, y).IndexOf(text, StringComparison.Ordinal);
            if (x >= 0) return surface[x, y];
        }
        Assert.Fail($"Missing {text} in rendered surface:\n{Text(surface)}");
        return default;
    }

    private sealed class ProbeNode : Hex1bNode, IDisposable
    {
        public int NaturalHeight { get; init; } = 1;
        public Exception? RenderFailure { get; init; }
        public int MeasureCount { get; private set; }
        public int RenderCount { get; private set; }
        public int DisposeCount { get; private set; }
        public bool MeasuredTrueColor { get; private set; }
        public bool RenderedTrueColor { get; private set; }
        protected override Size MeasureCore(Constraints constraints)
        {
            MeasureCount++;
            MeasuredTrueColor = TerminalCapabilities.SupportsTrueColor;
            return constraints.Constrain(new Size(5, NaturalHeight));
        }
        public override void Render(Hex1bRenderContext context)
        {
            RenderCount++;
            RenderedTrueColor = context.Capabilities.SupportsTrueColor;
            context.Write("probe");
            if (RenderFailure is not null) throw RenderFailure;
        }
        public void Dispose() => DisposeCount++;
    }

    private sealed record ProbeWidget(ProbeNode Node) : Hex1bWidget
    {
        internal override Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
            => Task.FromResult<Hex1bNode>(Node);
        internal override Type GetExpectedNodeType() => typeof(ProbeNode);
    }

    private sealed record StatefulWidget(DisposableState State, Hex1bWidget Child) : Hex1bWidget
    {
        protected override Hex1bWidget Build(CompositionContext ctx)
        {
            ctx.UseState(() => State);
            return Child;
        }
    }

    private sealed class DisposableState : IDisposable
    {
        public int DisposeCount { get; private set; }
        public void Dispose() => DisposeCount++;
    }

    private sealed class TrackingDecorationProvider : ITextDecorationProvider
    {
        public Exception? CleanupFailure { get; init; }
        public int Activations { get; private set; }
        public int Deactivations { get; private set; }
        public EditorNode? Node { get; private set; }
        public void Activate(IEditorSession session)
        {
            Activations++;
            Node = (EditorNode)session;
        }
        public void Deactivate()
        {
            Deactivations++;
            if (CleanupFailure is not null) throw CleanupFailure;
        }
        public IReadOnlyList<TextDecorationSpan> GetDecorations(int startLine, int endLine, IHex1bDocument document) => [];
    }

    // Uses the actual content-height wrapper used by Markdown code blocks, with
    // a caller-owned document so an escaped subscription is observable.
    private sealed record CodeBlockEditorWidget(EditorWidget Editor) : Hex1bWidget
    {
        internal override async Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            var node = new MarkdownCodeBlockNode { LineCount = 1 };
            node.EditorChild = await context.ReconcileChildAsync(null, Editor, node);
            return node;
        }
        internal override Type GetExpectedNodeType() => typeof(MarkdownCodeBlockNode);
    }

    private sealed record ThrowingWidget(Exception Failure) : Hex1bWidget
    {
        protected override Hex1bWidget Build(CompositionContext ctx) => throw Failure;
    }

    private sealed record WaitingWidget(TaskCompletionSource Entered) : Hex1bWidget
    {
        internal override async Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            Entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
            throw new InvalidOperationException("Canceled reconciliation must not return a node.");
        }
        internal override Type GetExpectedNodeType() => typeof(ProbeNode);
    }

    private sealed class WidgetCommitSource(FlowStep step) : FlowCommitSource
    {
        public override int UnitCount => 2;
        public List<int> RequestedWidths { get; } = [];
        public override async Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken)
        {
            RequestedWidths.Add(width);
            var widget = new MarkdownWidget(index == 0 ? "**COMMITTED-ONE**" : "*COMMITTED-TWO*");
            var surface = await step.RenderWidgetAsync(widget, width, 10, cancellationToken);
            return new FlowCommitUnit($"rich-{index}", surface);
        }
    }
}
