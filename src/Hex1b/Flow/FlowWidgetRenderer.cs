using System.Runtime.ExceptionServices;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>Owns one-shot widget trees independently of the flow's live app.</summary>
internal sealed class FlowWidgetRenderer(
    Hex1bTheme? theme,
    Func<TerminalCapabilities> capabilities,
    CancellationToken flowCancellation)
{
    public async Task<Surface> RenderAsync(
        Hex1bWidget widget, int width, int maxHeight, CancellationToken cancellationToken)
        => (await MaterializeAsync(widget, width, maxHeight, render: true, cancellationToken)).Surface!;

    public async Task<Size> MeasureAsync(
        Hex1bWidget widget, int width, int maxHeight, CancellationToken cancellationToken)
        => (await MaterializeAsync(widget, width, maxHeight, render: false, cancellationToken)).Size;

    private async Task<(Size Size, Surface? Surface)> MaterializeAsync(
        Hex1bWidget widget, int width, int maxHeight, bool render, CancellationToken cancellationToken)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(flowCancellation, cancellationToken);
        var token = lifetime.Token;
        var nodes = new HashSet<Hex1bNode>(ReferenceEqualityComparer.Instance);
        Surface? surface = null;
        Size size = default;
        Exception? failure = null;
        try
        {
            token.ThrowIfCancellationRequested();
            var context = ReconcileContext.CreateRoot(cancellationToken: token);
            context.ObserveTransientNode = node => nodes.Add(node);
            // A private anchor exposes partial StatePanel subtrees to cleanup even
            // when their builders fail before returning their child nodes.
            var anchor = new StatePanelNode();
            var node = await context.ReconcileChildAsync(null, widget, anchor)
                ?? throw new InvalidOperationException("The widget did not reconcile to a node.");
            token.ThrowIfCancellationRequested();
            if (ExpandNodes(nodes).Any(candidate => candidate is SurfaceNode))
                throw new NotSupportedException("SurfaceWidget has opaque widget-layer trees whose lifetime cannot be owned by this materializer.");
            var terminalCapabilities = capabilities();
            node.SetTerminalCapabilities(terminalCapabilities);
            // One extra row is enough to detect an oversized unit even when a
            // container constrains its reported height. Do not allocate that row.
            var measureHeight = maxHeight == int.MaxValue ? int.MaxValue : maxHeight + 1;
            var measured = node.Measure(new Constraints(0, width, 0, measureHeight));
            if (measured.Height < 0 || measured.Width < 0)
                throw new InvalidOperationException("Widget measurement returned a negative dimension.");
            if (measured.Height > maxHeight || measured.Width > width)
                throw new FlowWidgetBoundsException(measured, width, maxHeight);
            var height = Math.Max(1, measured.Height);
            // Retain the existing width restriction. Height is bounded by the
            // caller's maxHeight; child clipping supports the complete measured tail.
            if (width > 10_000)
                throw new InvalidOperationException("Widget width exceeds the renderer's 10000-cell width limit.");
            _ = checked(width * height);
            token.ThrowIfCancellationRequested();
            size = new Size(measured.Width, height);
            if (render) surface = RenderNode(node, width, height, theme, terminalCapabilities, transient: true);
            token.ThrowIfCancellationRequested();
        }
        catch (Exception ex)
        {
            failure = ex;
        }
        finally
        {
            // Stop work scoped to reconciliation before releasing node state.
            try { lifetime.Cancel(); }
            catch (Exception ex) { failure = Combine(failure, ex); }
            foreach (var node in ExpandNodes(nodes).AsEnumerable().Reverse())
            {
                try
                {
                    node.CachedSurface?.ClearAndReleaseTrackedObjects();
                    if (!ReferenceEquals(node.CachedSurface, node.RenderBuffer))
                        node.RenderBuffer?.ClearAndReleaseTrackedObjects();
                    node.CachedSurface = null;
                    node.RenderBuffer = null;
                    if (node is EditorNode editor) editor.ReleaseTransientResources();
                    if (node is TextBoxNode textBox) textBox.ClearPrediction();
                    if (node is TerminalNode terminal)
                    {
                        terminal.Unbind();
                        terminal.AttachCopyModeHelper(null);
                    }
                    if (node is StatePanelNode statePanel) statePanel.DisposeAllState();
                    if (node is Hex1bCompositeNode composite) composite.DisposeAllState();
                    if (node is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
                    else if (node is IDisposable disposable) disposable.Dispose();
                }
                catch (Exception ex) { failure = Combine(failure, ex); }
            }
        }

        if (failure is not null)
        {
            surface?.ClearAndReleaseTrackedObjects();
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
        return (size, surface);
    }

    // Shared with legacy tombstones. Their measurement and fallback policy stays
    // in the runner; only the actual arrange/render pipeline is extracted here.
    internal static Surface RenderNode(
        Hex1bNode node, int width, int height, Hex1bTheme? theme,
        TerminalCapabilities capabilities, bool transient = false)
    {
        var surface = new Surface(width, height);
        try
        {
            node.Arrange(new Rect(0, 0, width, height));
            var context = new SurfaceRenderContext(surface, theme)
            {
                // A one-shot tree gains nothing from retained caches. A zero-
                // retention pool releases temporary clipping surfaces on failure.
                CachingEnabled = !transient,
                SurfacePool = transient ? new SurfacePool(0, 0) : null
            };
            context.SetCapabilities(capabilities);
            node.Render(context);
            return surface;
        }
        catch
        {
            surface.ClearAndReleaseTrackedObjects();
            throw;
        }
    }

    private static List<Hex1bNode> ExpandNodes(HashSet<Hex1bNode> nodes)
    {
        var all = nodes.ToList();
        for (var index = 0; index < all.Count; index++)
        {
            var node = all[index];
            foreach (var child in node.GetChildren())
                if (nodes.Add(child)) all.Add(child);
            if (node is StatePanelNode panel)
                foreach (var child in panel.NestedStatePanels.Values)
                    if (nodes.Add(child)) all.Add(child);
        }
        return all;
    }

    private static Exception Combine(Exception? first, Exception next)
        => first is null ? next : new AggregateException(first, next);
}
