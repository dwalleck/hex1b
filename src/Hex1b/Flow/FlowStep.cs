using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Handle returned by <see cref="Hex1bFlowContext.Step(Func{FlowStepContext, Task{Hex1bWidget}}, Action{Hex1bFlowStepOptions}?)"/>
/// that controls a running inline step. Use <see cref="Invalidate"/> to trigger re-renders from background
/// work, and <see cref="Complete()"/> or <see cref="CompleteAsync(CancellationToken)"/> to finish the step.
/// </summary>
public sealed class FlowStep
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile Hex1bApp? _app;
    private int _completed; // 0 = active, 1 = completed
    private FlowCommitCoordinator? _commitCoordinator;
    private FlowWidgetRenderer? _widgetRenderer;

    internal FlowStep(int terminalWidth, int terminalHeight, int stepHeight)
    {
        TerminalWidth = terminalWidth;
        TerminalHeight = terminalHeight;
        StepHeight = stepHeight;
    }

    /// <summary>
    /// Attaches the continuous-history coordinator created for this step by the
    /// runner. Called once, before the step's app starts running.
    /// </summary>
    internal void AttachCommitCoordinator(FlowCommitCoordinator coordinator)
        => _commitCoordinator = coordinator;

    internal void AttachWidgetRenderer(FlowWidgetRenderer renderer)
        => _widgetRenderer = renderer;

    /// <summary>
    /// Renders finalized widget content into a new surface without writing to the
    /// terminal or changing this step's live widget tree.
    /// </summary>
    /// <remarks>
    /// Uses the flow's theme and current terminal capabilities and the standard
    /// widget reconciliation, measurement, arrangement, and rendering pipeline.
    /// The surface has the requested width and the widget's measured height (at
    /// least one row). Widget-defined wrapping and clipping still apply; content
    /// whose measured height exceeds <paramref name="maxHeight"/> is rejected,
    /// never truncated by this method. Use content-sized widgets for history units.
    /// The framework cleans up the transient tree, including reconciled siblings
    /// when a later child fails. Widget code remains responsible for resources it
    /// allocates and abandons before exposing a node to reconciliation.
    /// <see cref="SurfaceWidget"/> is unsupported because its private widget-layer
    /// trees do not participate in transient-tree ownership; it is rejected before
    /// any layers are rendered.
    /// Cancellation is cooperative during reconciliation and checked between the
    /// synchronous layout/render phases. A returned surface belongs to the caller
    /// and can be supplied to a <see cref="FlowCommitUnit"/>.
    /// </remarks>
    /// <param name="widget">Immutable finalized presentation to render.</param>
    /// <param name="width">Positive width in terminal columns.</param>
    /// <param name="maxHeight">Positive maximum height in terminal rows.</param>
    /// <param name="cancellationToken">Cancels this materialization.</param>
    /// <returns>The complete rendered surface, including cell styles and hyperlinks.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="widget"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="InvalidOperationException">
    /// The step has completed, is not owned by a runner, or measurement returned
    /// an invalid negative dimension. Widget and cleanup failures also propagate.
    /// The renderer also refuses surfaces exceeding its 10,000-cell dimension limit.
    /// </exception>
    /// <exception cref="FlowWidgetBoundsException">
    /// Nonnegative measured content exceeds the requested width or maximum height.
    /// Measurement is constrained, so reported dimensions need not be natural size.
    /// No content is rendered for this refusal. Cleanup failures can wrap it.
    /// </exception>
    /// <exception cref="OverflowException">The surface cell count exceeds an integer.</exception>
    /// <exception cref="NotSupportedException">The tree contains a <see cref="SurfaceWidget"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller or flow was canceled.</exception>
    public Task<Surface> RenderWidgetAsync(
        Hex1bWidget widget,
        int width,
        int maxHeight,
        CancellationToken cancellationToken = default)
        => GetWidgetRenderer(widget, width, maxHeight).RenderAsync(widget, width, maxHeight, cancellationToken);

    /// <summary>Measures finalized content within the requested bounds without arranging or rendering it.</summary>
    /// <remarks>
    /// Uses the same reconciliation, capabilities, constrained measurement and
    /// transient-tree cleanup as <see cref="RenderWidgetAsync"/>. No surface is
    /// allocated and no render callback is invoked. Widget reconciliation and
    /// measurement may themselves allocate resources; cleanup failures propagate.
    /// Bounds and the renderer's hard dimension limit are checked identically to
    /// rendering. Reported width is measured content width, not surface width.
    /// </remarks>
    /// <example>
    /// <code>
    /// using System;
    /// using System.Threading.Tasks;
    /// using Hex1b;
    /// using Hex1b.Flow;
    /// using Hex1b.Widgets;
    ///
    /// string report = "";
    /// await using var terminal = Hex1bTerminal.CreateBuilder()
    ///     .WithHex1bFlow(async flow =&gt;
    ///     {
    ///         var step = flow.Step(context =&gt; Task.FromResult&lt;Hex1bWidget&gt;(context.Text("Measuring preview")));
    ///         await step.WaitForReadyAsync(flow.CancellationToken);
    ///         var content = new TextBlockWidget(new string('x', 81)).Wrap();
    ///         var size = await step.MeasureWidgetAsync(content, 40, 3, flow.CancellationToken);
    ///         report = $"Measured {size.Width} columns by {size.Height} rows.";
    ///         try
    ///         {
    ///             await step.MeasureWidgetAsync(content, 40, 2, flow.CancellationToken);
    ///         }
    ///         catch (FlowWidgetBoundsException bounds)
    ///         {
    ///             report += $" Preview exceeds {bounds.RequestedMaxHeight} rows; retain full content.";
    ///         }
    ///         await step.CompleteAsync(context =&gt; Task.FromResult&lt;Hex1bWidget&gt;(context.Text(report)));
    ///     })
    ///     .WithHeadless().WithDimensions(40, 10).Build();
    /// await terminal.RunAsync();
    /// Console.WriteLine(report);
    /// </code>
    /// </example>
    /// <param name="widget">Immutable content to measure.</param>
    /// <param name="width">Positive available width in columns.</param>
    /// <param name="maxHeight">Positive maximum height in rows.</param>
    /// <param name="cancellationToken">Cancels reconciliation and measurement.</param>
    /// <returns>Constrained measured dimensions; height is at least one row.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="widget"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A dimension is not positive.</exception>
    /// <exception cref="FlowWidgetBoundsException">Nonnegative measured content exceeds requested bounds.</exception>
    /// <exception cref="InvalidOperationException">The step is unavailable, measurement is invalid, or renderer dimension limits are exceeded.</exception>
    /// <exception cref="OverflowException">The equivalent surface cell count exceeds an integer.</exception>
    /// <exception cref="NotSupportedException">The tree contains a <see cref="SurfaceWidget"/>.</exception>
    /// <exception cref="OperationCanceledException">The caller or flow was canceled.</exception>
    public Task<Hex1b.Layout.Size> MeasureWidgetAsync(Hex1bWidget widget, int width, int maxHeight,
        CancellationToken cancellationToken = default)
        => GetWidgetRenderer(widget, width, maxHeight).MeasureAsync(widget, width, maxHeight, cancellationToken);

    private FlowWidgetRenderer GetWidgetRenderer(Hex1bWidget widget, int width, int maxHeight)
    {
        ArgumentNullException.ThrowIfNull(widget);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(width);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxHeight);
        if (Volatile.Read(ref _completed) != 0 || _tcs.Task.IsCompleted)
            throw new InvalidOperationException("Cannot materialize widgets for a completed flow step.");
        return _widgetRenderer ?? throw new InvalidOperationException("The flow step has no widget renderer.");
    }

    /// <summary>
    /// Gets the completed builder set by <see cref="Complete(Func{RootContext, Hex1bWidget})"/>
    /// or its async overloads, or null if the step exited without one.
    /// Always stored as an async builder; sync overloads wrap with
    /// <see cref="Task.FromResult{TResult}(TResult)"/> before assigning.
    /// </summary>
    internal Func<RootContext, Task<Hex1bWidget>>? CompletedBuilder { get; private set; }

    /// <summary>
    /// Width of the terminal in columns. Kept current across resizes so a
    /// finalized-content builder can pad rows to the width the commit will
    /// actually emit at.
    /// </summary>
    public int TerminalWidth { get; internal set; }

    /// <summary>
    /// Height of the terminal in rows.
    /// </summary>
    public int TerminalHeight { get; }

    /// <summary>
    /// Number of rows allocated to this step.
    /// </summary>
    public int StepHeight { get; internal set; }

    /// <summary>
    /// Sets the underlying app instance. Called by the runner after the app is created.
    /// </summary>
    internal void SetApp(Hex1bApp app) => _app = app;

    /// <summary>
    /// The live app instance while the step is running, or null before it starts.
    /// Used by diagnostics (widget-tree inspection) and by proof harnesses that
    /// verify the app is never restarted across a history commit.
    /// </summary>
    internal Hex1bApp? AppForDiagnostics => _app;

    /// <summary>
    /// Marks the task as completed. Called by the runner after cleanup.
    /// </summary>
    internal void SetCompleted() => _tcs.TrySetResult();

    /// <summary>
    /// Marks the task as faulted. Called by the runner on error.
    /// </summary>
    internal void SetFaulted(Exception ex) => _tcs.TrySetException(ex);

    /// <summary>
    /// Triggers a re-render of the step's widget tree.
    /// Safe to call from any thread.
    /// </summary>
    public void Invalidate() => _app?.Invalidate();

    /// <summary>Queues a short synchronous state transition on this step's application loop.</summary>
    /// <param name="callback">The synchronous callback; never supply async-void or block on frame-dependent work.</param>
    /// <param name="completion">For accepted work, its execution outcome; otherwise a completed placeholder.</param>
    /// <param name="cancellationToken">Cancels work before execution begins.</param>
    /// <returns>The admission outcome, including NotRunning before the app is attached or after it stops.</returns>
    /// <remarks>Uses <see cref="Hex1bApp.Dispatch"/>. Await completion outside step input/render handlers.
    /// Completion acknowledges callback execution, not frame emission or native presentation.</remarks>
    public Hex1bDispatchAdmission Dispatch(Action callback, out Task completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        var app = _app;
        if (app is not null) return app.Dispatch(callback, out completion, cancellationToken);
        completion = Task.CompletedTask;
        return Hex1bDispatchAdmission.NotRunning;
    }


    /// <summary>
    /// Completes the step without frozen output. The step region is cleared
    /// and the cursor advances past it.
    /// </summary>
    /// <remarks>
    /// This is a fire-and-forget call suitable for use in event handlers.
    /// Use <see cref="CompleteAsync(CancellationToken)"/> from the flow callback to also wait
    /// for cleanup to finish.
    /// </remarks>
    public void Complete()
    {
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;
        _app?.RequestStop();
    }

    /// <summary>
    /// Completes the step and renders the given widget as frozen terminal output.
    /// The widget is rendered once after the step ends, scrolling naturally into
    /// the scrollback buffer.
    /// </summary>
    /// <remarks>
    /// This is a fire-and-forget call suitable for use in event handlers.
    /// Use <see cref="CompleteAsync(Func{RootContext, Task{Hex1bWidget}}, CancellationToken)"/> from
    /// the flow callback to also wait for cleanup to finish.
    /// </remarks>
    /// <param name="builder">Async widget builder for the frozen output.</param>
    public void Complete(Func<RootContext, Task<Hex1bWidget>> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (Interlocked.CompareExchange(ref _completed, 1, 0) != 0)
            return;
        CompletedBuilder = builder;
        _app?.RequestStop();
    }

    /// <summary>
    /// Sync overload of <see cref="Complete(Func{RootContext, Task{Hex1bWidget}})"/>.
    /// Wraps the sync builder with <see cref="Task.FromResult{TResult}(TResult)"/>.
    /// </summary>
    /// <param name="builder">Sync widget builder for the frozen output.</param>
    public void Complete(Func<RootContext, Hex1bWidget> builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        Complete(ctx => Task.FromResult(builder(ctx)));
    }

    /// <summary>
    /// Completes the step without frozen output and waits for cleanup
    /// (yield widget rendering, cursor advancement) to finish.
    /// </summary>
    /// <param name="cancellationToken">Optional token to cancel the wait.</param>
    /// <returns>A task that completes when the step is fully cleaned up.</returns>
    public Task CompleteAsync(CancellationToken cancellationToken = default)
    {
        Complete();
        return WaitForCompletionAsync(cancellationToken);
    }

    /// <summary>
    /// Completes the step with frozen output and waits for cleanup
    /// (yield widget rendering, cursor advancement) to finish.
    /// </summary>
    /// <param name="builder">Async widget builder for the frozen output.</param>
    /// <param name="cancellationToken">Optional token to cancel the wait.</param>
    /// <returns>A task that completes when the step is fully cleaned up.</returns>
    public Task CompleteAsync(Func<RootContext, Task<Hex1bWidget>> builder, CancellationToken cancellationToken = default)
    {
        Complete(builder);
        return WaitForCompletionAsync(cancellationToken);
    }

    /// <summary>
    /// Sync overload of <see cref="CompleteAsync(Func{RootContext, Task{Hex1bWidget}}, CancellationToken)"/>.
    /// Wraps the sync builder with <see cref="Task.FromResult{TResult}(TResult)"/>.
    /// </summary>
    /// <param name="builder">Sync widget builder for the frozen output.</param>
    /// <param name="cancellationToken">Optional token to cancel the wait.</param>
    /// <returns>A task that completes when the step is fully cleaned up.</returns>
    public Task CompleteAsync(Func<RootContext, Hex1bWidget> builder, CancellationToken cancellationToken = default)
    {
        Complete(builder);
        return WaitForCompletionAsync(cancellationToken);
    }

    /// <summary>
    /// Waits for the step to finish, including yield widget rendering and
    /// cursor advancement. Use this after calling <see cref="Complete()"/> from
    /// the flow callback, or to wait for a step that is completed by user
    /// interaction (e.g., via <see cref="FlowStepContext.Step"/> in an event handler).
    /// </summary>
    /// <param name="cancellationToken">Optional token to cancel the wait.</param>
    /// <returns>A task that completes when the step is fully cleaned up.</returns>
    public Task WaitForCompletionAsync(CancellationToken cancellationToken = default)
    {
        return cancellationToken.CanBeCanceled
            ? _tcs.Task.WaitAsync(cancellationToken)
            : _tcs.Task;
    }

    /// <summary>
    /// True when this step can currently accept a history commit: no commit is
    /// outstanding and no earlier commit ended in an uncertain emission failure.
    /// </summary>
    /// <remarks>
    /// Once an emission failure has left the terminal stream uncertain, commits
    /// stay rejected for the lifetime of the step. Recovery is explicit; an
    /// uncertain batch is never replayed automatically.
    /// </remarks>
    public bool CanCommit => _commitCoordinator?.CanCommit ?? false;

    /// <summary>
    /// True when a previous commit failed after content may already have reached
    /// the terminal, so further history commitment is suspended.
    /// </summary>
    public bool IsCommitUncertain => _commitCoordinator?.IsUncertain ?? false;

    /// <summary>
    /// True while a history commit is admitted and running. Distinct from
    /// <see cref="IsCommitUncertain"/>: an in-flight commit is a transient state,
    /// not a suspension of further commitment.
    /// </summary>
    public bool IsCommitInFlight => _commitCoordinator?.IsCommitInFlight ?? false;

    /// <summary>
    /// Waits until the step's application has rendered and entered its
    /// input-capable lifecycle.
    /// </summary>
    /// <remarks>
    /// Readiness means the persistent app has produced at least one frame and is
    /// processing input. It is not a terminal-scanout or input-echo oracle: a
    /// rendered marker alone does not prove the host has displayed anything.
    /// </remarks>
    /// <returns>A task that completes when the step is ready for commitment.</returns>
    public Task WaitForReadyAsync(CancellationToken cancellationToken = default)
    {
        var coordinator = _commitCoordinator
            ?? throw new InvalidOperationException(
                "This step has no history commitment coordinator. It was created by a flow " +
                "runner without continuous-history support.");
        return coordinator.WaitForReadyAsync(cancellationToken);
    }

    /// <summary>
    /// Commits finalized presentation content to native terminal history while
    /// the step stays live, applying the next live layout as part of the same
    /// coordinated operation.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The application supplies immutable logical units through
    /// <see cref="FlowCommitSource"/>; each unit is emitted exactly once, in
    /// order, below the live region. The host owns scrolling and scrollback, and
    /// Hex1b never clears or replays it. The live step is not ended and the app
    /// is not restarted: the same application, node tree, and editor identity
    /// persist across the commit, with only the root layout builder replaced.
    /// </para>
    /// <para>
    /// Emission proceeds in bounded turns so input and resize processing get
    /// opportunities between them, and only units that have not been emitted are
    /// re-materialized if the terminal width changes mid-commit. The live region
    /// is not left out of the picture while that happens: each unit is written
    /// together with the region re-anchored and repainted below it, in one
    /// serialized terminal update bracketed by synchronized output, so the prompt
    /// stays on screen and an edit the app renders while the output pump is muted
    /// becomes visible in the same update rather than at the next turn.
    /// </para>
    /// <para>
    /// <see cref="FlowCommitResult.CompletedRows"/> counts physical rows that
    /// Hex1b handed to the terminal-side write path, and
    /// <see cref="FlowCommitResult.CompletedUnits"/> counts logical units. Both
    /// describe framework emission only — they are not evidence that the host
    /// terminal displayed them, retained them in scrollback, or made them
    /// durable.
    /// </para>
    /// <para>
    /// The next live layout is applied only when every submitted unit was handed
    /// off. A commit that stops with units still pending — cancellation, or a
    /// failure — retains the step's current live layout, because the pending
    /// content still owns the presentation and the caller decides what becomes of
    /// it.
    /// </para>
    /// <para>
    /// A cancelled commit leaves the step committable, so the following commit's
    /// <paramref name="nextLive"/> advances the layout — unless recovering the
    /// live region after the cancellation also failed, which is reported as
    /// <see cref="FlowCommitException"/> carrying the cancellation's partial
    /// progress and suspends further commitment. A failure with an emitted prefix
    /// suspends commitment the same way, and the step keeps the layout it had: no
    /// public entry point swaps a layout outside a commit, so a caller that has to
    /// advance the presentation after such a failure can only do so from inside
    /// its own still-running live application.
    /// </para>
    /// <para>
    /// Cancellation and partial commitment are reported as separate facts. A
    /// commit whose cancellation arrives only after the last unit was handed off
    /// reports <see cref="FlowCommitStatus.Emitted"/> with
    /// <see cref="FlowCommitResult.CancellationRequested"/> set, because the
    /// emission outcome — not the cancellation observation — is what
    /// <see cref="FlowCommitResult.Status"/> describes.
    /// Cancellation while waiting for readiness or preparing the source cancels
    /// the task without installing the next layout, unless recovery itself fails.
    /// Admission failures use <see cref="FlowCommitException"/> with zero hand-off
    /// counts; a missing safe boundary suspends further commitment.
    /// </para>
    /// <para>
    /// Await this from a background task, never from inside the step's own event
    /// handlers: the commit waits for frames produced by the app's render loop,
    /// so blocking that handler would deadlock.
    /// </para>
    /// <para>
    /// A second outstanding commit request is rejected with
    /// <see cref="InvalidOperationException"/>.
    /// </para>
    /// </remarks>
    /// <param name="finalized">Immutable finalized units, in commit order.</param>
    /// <param name="nextLive">Builder for the step's next live layout.</param>
    /// <param name="cancellationToken">Requests cancellation; completed hand-offs remain history.</param>
    /// <exception cref="FlowCommitException">
    /// Admission, source preparation, emission, or recovery failed. The exception
    /// reports the completed hand-offs, including zero when nothing was emitted.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// Cancellation was observed before source preparation completed and recovery succeeded.
    /// </exception>
    public Task<FlowCommitResult> CommitAsync(
        FlowCommitSource finalized,
        Func<FlowStepContext, Task<Hex1bWidget>> nextLive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(finalized);
        ArgumentNullException.ThrowIfNull(nextLive);
        var coordinator = RequireCoordinator();
        return coordinator.CommitAsync(finalized, nextLive, cancellationToken);
    }

    private FlowCommitCoordinator RequireCoordinator()
        => _commitCoordinator
            ?? throw new InvalidOperationException(
                "This step has no history commitment coordinator. It was created by a flow " +
                "runner without continuous-history support.");

    /// <summary>
    /// Requests that focus be moved to a node matching the predicate.
    /// </summary>
    public void RequestFocus(Func<Hex1bNode, bool> predicate) => _app?.RequestFocus(predicate);
}
