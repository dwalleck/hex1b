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
    /// <param name="cancellationToken">Cancels the commit between units.</param>
    /// <exception cref="FlowCommitException">
    /// Emission failed after content may have reached the terminal.
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
