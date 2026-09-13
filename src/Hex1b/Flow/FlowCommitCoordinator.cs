using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Live-step operations the continuous-history committer needs from the flow
/// runner. Implemented by <see cref="Hex1bFlowRunner"/>; never exposed publicly.
/// </summary>
internal interface ILiveStepHandle
{
    /// <summary>Current terminal width in columns.</summary>
    int TerminalWidth { get; }

    /// <summary>Current terminal height in rows.</summary>
    int TerminalHeight { get; }

    /// <summary>Row where the live step's region currently starts.</summary>
    int RowOrigin { get; }

    /// <summary>Rows currently allocated to the live step's region.</summary>
    int LiveHeight { get; }

    /// <summary>
    /// The live application's most recently rendered surface, or null before
    /// its first frame. Used to repaint the live image during a commit without
    /// re-rendering the widget tree.
    /// </summary>
    Surface? SnapshotLiveSurface();

    /// <summary>
    /// Writes <paramref name="text"/> at absolute terminal row
    /// <paramref name="row"/> under the terminal write lock, so positioning and
    /// content can never be split by another writer.
    /// </summary>
    void WriteTerminalAt(int row, string text);

    /// <summary>
    /// Scrolls the viewport up by <paramref name="rows"/> rows by parking the
    /// cursor on the bottom row and emitting that many linefeeds, pushing the
    /// top rows into the host's scrollback. Never clears or replays the
    /// scrollback; the host owns it.
    /// </summary>
    void ScrollViewportUp(int rows);

    /// <summary>
    /// Atomically moves the live step's region to <paramref name="rowOrigin"/>
    /// and resizes it to <paramref name="liveHeight"/> rows, then repaints the
    /// region as a single serialized pass: blank every row of the region, paint
    /// <paramref name="liveSurface"/>, and leave the cursor at the region's
    /// top-left.
    /// </summary>
    /// <remarks>
    /// Blank-all-rows-then-paint ordering matters. A row whose new content is
    /// shorter than its previous content must not leave residue at the right
    /// margin, a region that shrinks must not leave orphaned rows, and the
    /// paint's own clear-before-content handling must not be undone by a later
    /// clear.
    /// </remarks>
    void ReanchorLive(int rowOrigin, int liveHeight, Surface liveSurface);

    /// <summary>Forwards a resize to the live step's adapter.</summary>
    void ResizeLive(int width, int liveHeight);

    /// <summary>
    /// Number of frames the live application has completed. Used to wait for a
    /// frame rendered after a builder swap instead of guessing.
    /// </summary>
    long FrameCount { get; }

    /// <summary>
    /// Applies a new live layout builder to the still-running live
    /// application, preserving the same app and node identities. Returns
    /// without waiting; the caller observes <see cref="FrameCount"/>.
    /// </summary>
    void ApplyLiveLayout(Func<FlowStepContext, Task<Hex1bWidget>> builder);

    /// <summary>
    /// Computes where the next content row belongs after the host terminal
    /// re-wrapped its buffer for a new geometry, from the flow's own reflow
    /// model rather than from a row counter a host scroll can invalidate.
    /// </summary>
    int ComputeAppendRowAfterReflow(
        int newWidth,
        int newHeight,
        int cursorScreenRow,
        int cursorColumn,
        bool cursorBelowContent);


    /// <summary>
    /// Records the logical paragraph widths of the rows a commit just emitted,
    /// so a later soft-wrap resize can recompute where the live region belongs
    /// after the host reflows the committed content.
    /// </summary>
    void RecordCommittedRows(Surface surface);

    /// <summary>
    /// Discards frames the live app queued while its output was muted, so no
    /// frame laid out for a superseded origin is replayed after unmute.
    /// </summary>
    /// <returns>The number of queued frames discarded.</returns>
    int DiscardQueuedLiveOutput();

    /// <summary>
    /// Asks the live application to render a frame now.
    /// </summary>
    void RequestLiveFrame();

    /// <summary>
    /// Mutes or unmutes the pump that forwards the live application's frames to
    /// the terminal, returning the previous muted state. While muted the live
    /// app keeps rendering into its adapter but nothing reaches the terminal.
    /// </summary>
    bool SetLiveOutputMuted(bool muted);

    /// <summary>
    /// Waits until the live application completes a frame newer than the
    /// current one, bounded by <paramref name="timeout"/>.
    /// </summary>
    Task<bool> WaitForNextLiveFrameAsync(TimeSpan timeout, CancellationToken cancellationToken);

    /// <summary>Records a commit event for the driver's structured evidence log.</summary>
    void RecordCommitEvent(string message);
}

/// <summary>
/// Coordinates uninterrupted native-history commitment over a live inline step.
/// Small, deep, and internal: the public surface is
/// <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// The algorithm, per bounded turn of at most
/// <see cref="UnitsPerTurn"/> units:
/// </para>
/// <list type="number">
///   <item>materialize each not-yet-emitted unit at the width in force when its
///         turn begins (so pending content is always at the current width);</item>
///   <item>scroll the viewport up if the unit does not fit below the append
///         cursor, pushing older content into the host's scrollback via bottom-row
///         linefeeds, then append the unit's logical rows below the live
///         region;</item>
///   <item>repaint the live region at the new append position, so the region is
///         continuously rewritten rather than left blank while a large batch is
///         committed;</item>
///   <item>yield so queued input and resize events are dispatched.</item>
/// </list>
/// <para>
/// Progress is counted in logical units, never in terminal rows: a unit that
/// soft-wraps on a narrow terminal is still exactly one unit, so a mid-commit
/// width change can never duplicate or drop content by skipping rows. Unit
/// indices are stable across widths.
/// </para>
/// <para>
/// The live step is never ended and the app is never restarted. The same
/// <see cref="Hex1bApp"/> instance keeps running; only its root layout builder
/// changes, and no stale-origin frame is replayed after unmute.
/// </para>
/// <para>
/// Threading: <see cref="CommitAsync"/> must be awaited from a background task,
/// never from inside the live app's own event handlers. The coordinator waits
/// for frames produced by that app, so blocking its handler would deadlock.
/// </para>
/// </remarks>
internal sealed class FlowCommitCoordinator
{
    /// <summary>
    /// Maximum units emitted per turn. Bounds the work done between
    /// opportunities for input and resize processing; not a bound on total
    /// commit size.
    /// </summary>
    internal const int UnitsPerTurn = 8;

    /// <summary>How long to wait for the terminal side to consume emitted bytes.</summary>
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    /// <summary>How long to wait for the first live frame.</summary>
    private static readonly TimeSpan ReadinessTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long to wait for the live repaint frame after a commit.</summary>
    private static readonly TimeSpan LiveFrameTimeout = TimeSpan.FromSeconds(5);

    private const string FailAfterRowsVariable = "HEX1B_PROTOTYPE_FAIL_AFTER_ROWS";

    /// <summary>
    /// Prototype fault injection, configured by
    /// <c>HEX1B_PROTOTYPE_FAIL_AFTER_ROWS</c>. Read per history commit, so a
    /// harness can arm and disarm it around a single run; unset the variable to
    /// stop injecting. The failure is raised only on the history-commit path.
    /// </summary>
    private static int ReadPrototypeFailureThreshold()
        => int.TryParse(Environment.GetEnvironmentVariable(FailAfterRowsVariable), out var configured)
            && configured > 0
                ? configured
                : -1;

    private readonly ILiveStepHandle _live;
    private readonly IHex1bAppTerminalWorkloadAdapter _terminal;
    private readonly CancellationToken _flowCancellationToken;

    private int _admission;
    private volatile bool _uncertain;
    private volatile bool _inFlight;
    private long _nextCommitId;

    public FlowCommitCoordinator(
        ILiveStepHandle live,
        IHex1bAppTerminalWorkloadAdapter terminal,
        CancellationToken flowCancellationToken)
    {
        _live = live;
        _terminal = terminal;
        _flowCancellationToken = flowCancellationToken;
    }

    /// <summary>
    /// True when no commit is outstanding and no previous commit ended in an
    /// uncertain emission failure.
    /// </summary>
    public bool CanCommit => !_uncertain && Volatile.Read(ref _admission) == 0;

    /// <summary>
    /// True when a previous commit failed after content may already have reached
    /// the terminal. Distinct from <see cref="IsCommitInFlight"/>, which is a
    /// transient state, not a suspension.
    /// </summary>
    public bool IsUncertain => _uncertain;

    /// <summary>
    /// True while a commit is admitted and running. The runner's resize handler
    /// consults this so it does not reposition the live region out from under an
    /// in-flight commit.
    /// </summary>
    public bool IsCommitInFlight => _inFlight;

    /// <remarks>
    /// Admission is taken before the first await, so a second outstanding
    /// request throws <see cref="InvalidOperationException"/> synchronously
    /// rather than surfacing through the returned task.
    /// </remarks>
    public Task<FlowCommitResult> CommitAsync(
        FlowCommitSource source,
        Func<FlowStepContext, Task<Hex1bWidget>> nextLive,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(nextLive);

        if (Interlocked.CompareExchange(ref _admission, 1, 0) != 0)
        {
            throw new FlowCommitAdmissionException(
                "A commit is already outstanding. Wait for it to complete before requesting " +
                "another one; history commitment is admitted one request at a time.");
        }

        _inFlight = true;
        return CommitAndReleaseAsync(source, nextLive, cancellationToken);
    }

    private async Task<FlowCommitResult> CommitAndReleaseAsync(
        FlowCommitSource source,
        Func<FlowStepContext, Task<Hex1bWidget>> nextLive,
        CancellationToken cancellationToken)
    {
        try
        {
            return await CommitCoreAsync(source, nextLive, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _inFlight = false;
            Volatile.Write(ref _admission, 0);
        }
    }

    private async Task<FlowCommitResult> CommitCoreAsync(
        FlowCommitSource source,
        Func<FlowStepContext, Task<Hex1bWidget>> nextLive,
        CancellationToken cancellationToken)
    {
        if (_uncertain)
        {
            throw new FlowCommitException(
                "History commitment is suspended for this step: a previous commit failed after " +
                "emission may already have reached the terminal, and it is never replayed.",
                completedUnits: 0,
                completedRows: 0,
                mayHavePartialRow: false,
                innerException: null);
        }

        var commitId = Interlocked.Increment(ref _nextCommitId);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _flowCancellationToken);
        var token = linked.Token;
        var events = 0;

        void Record(string message)
        {
            events++;
            _live.RecordCommitEvent($"commit {commitId} {message}");
        }

        await WaitForReadyAsync(token).ConfigureAwait(false);

        // The authoritative unit count comes from PrepareAsync, which is also
        // where a render-backed source materializes for the first width. Unit
        // count is stable across widths, so re-preparing after a resize cannot
        // change a source's identity mid-commit.
        var committedWidth = Math.Max(1, _terminal.Width);
        var committedHeight = Math.Max(1, _live.TerminalHeight);
        var totalUnits = await source.PrepareAsync(committedWidth, token).ConfigureAwait(false);
        if (totalUnits <= 0)
        {
            Record(
                $"start empty width={committedWidth} liveOrigin={_live.RowOrigin} " +
                $"liveHeight={_live.LiveHeight}");
            return new FlowCommitResult(
                0, 0, rowKeys: Array.Empty<string?>(), FlowCommitStatus.Emitted, events,
                cancelled: false, abortedRows: 0, drainTimedOut: false);
        }

        Record(
            $"start units={totalUnits} width={committedWidth} " +
            $"liveOrigin={_live.RowOrigin} liveHeight={_live.LiveHeight} " +
            $"terminalHeight={_live.TerminalHeight}");

        var completedUnits = 0;
        var completedRows = 0;
        var rowKeys = new List<string?>(Math.Min(totalUnits, 256));
        // Where the terminal's own cursor sits, in screen rows, and whether it
        // is below the last content row (the live region's top row). The host
        // preserves this visual row when it re-wraps the buffer, so it is what
        // the post-reflow anchor is derived from.
        var cursorRow = Math.Max(0, _live.RowOrigin);
        var cursorColumn = 0;
        var cursorBelowContent = true;
        var appendRow = _live.ComputeAppendRowAfterReflow(
            committedWidth, Math.Max(1, _live.TerminalHeight), cursorRow, cursorColumn, cursorBelowContent);
        var emittedInTurn = 0;
        var faultThreshold = ReadPrototypeFailureThreshold();
        var emission = new UnitEmission();

        // The live app stops painting while history is appended: its frames are
        // discarded so a frame laid out for a superseded origin can never race
        // the repositioning.
        var wasMuted = _live.SetLiveOutputMuted(true);

        try
        {
            while (completedUnits < totalUnits)
            {
                token.ThrowIfCancellationRequested();

                // Geometry is re-checked before every unit AND again right before
                // the write: the host re-wraps its buffer the moment it sees a
                // resize, while the resize event reaches the flow asynchronously,
                // so a unit written at the pre-resize row after the host has
                // already reflowed would land on committed content.
                async Task<bool> ReflowIfNeededAsync(string when)
                {
                    var currentWidth = Math.Max(1, _terminal.Width);
                    var currentHeight = Math.Max(1, _live.TerminalHeight);
                    if (currentWidth == committedWidth && currentHeight == committedHeight)
                    {
                        return false;
                    }

                    // Reflow: only not-yet-emitted units are re-materialized, at
                    // the new width. Unit indices are stable across widths, so
                    // nothing is duplicated or skipped. Content already emitted
                    // stays where the host put it.
                    var reprepared = await source.PrepareAsync(currentWidth, token)
                        .ConfigureAwait(false);
                    if (reprepared != totalUnits)
                    {
                        // Unit identity must survive reflow. If the count
                        // changed, index i no longer means what it meant when the
                        // commit started, so continuing could silently duplicate
                        // or drop content. Fail loudly.
                        throw new InvalidOperationException(
                            $"Commit source returned a different unit count at width {currentWidth} " +
                            $"({totalUnits} -> {reprepared}); logical units must be stable " +
                            "across widths so a resize cannot re-identify pending content.");
                    }

                    // The host re-wrapped its whole buffer, so absolute rows no
                    // longer identify where the flow's content ends. Re-derive
                    // the append position from the flow's own reflow model:
                    // committed paragraph widths plus the cursor's visual row,
                    // which is what the host preserved.
                    appendRow = _live.ComputeAppendRowAfterReflow(
                        currentWidth, currentHeight, cursorRow, cursorColumn, cursorBelowContent);
                    committedWidth = currentWidth;
                    committedHeight = currentHeight;
                    Record(
                        $"reflow when={when} width={currentWidth} height={currentHeight} units={totalUnits} " +
                        $"appendRow={appendRow} cursorRow={cursorRow} cursorBelowContent={cursorBelowContent}");
                    return true;
                }

                await ReflowIfNeededAsync("pre-unit");

                var width = committedWidth;

                if (emittedInTurn == 0)
                {
                    Record($"turn start unit={completedUnits} width={width}");
                }

                var unit = await source.UnitAsync(completedUnits, committedWidth, token)
                    .ConfigureAwait(false);

                // The host may have reflowed while the unit was being built, so
                // re-check and re-materialize at the settled width before writing.
                if (await ReflowIfNeededAsync("pre-write").ConfigureAwait(false))
                {
                    unit = await source.UnitAsync(completedUnits, committedWidth, token)
                        .ConfigureAwait(false);
                }

                var unitHeight = Math.Max(1, unit.Surface.Height);

                // Proven geometry discipline (same as the tombstone path):
                // scroll first so the whole unit fits below the cursor, then
                // append. Without this, a full-height append clamps onto the
                // last row and destroys already-emitted history.
                appendRow = EnsureRoom(appendRow, unitHeight, Record, "unit");
                if (_live.TerminalHeight > 0)
                {
                    // EnsureRoom's scrolls park the host cursor on the bottom row.
                    cursorRow = Math.Max(0, _live.TerminalHeight - 1);
                    cursorColumn = 0;
                    cursorBelowContent = true;
                }

                emission.Reset();
                await EmitUnitAsync(
                    Record,
                    completedUnits,
                    unit,
                    appendRow,
                    faultThreshold,
                    emission,
                    token).ConfigureAwait(false);
                cursorRow = emission.LastRow;
                cursorColumn = emission.LastColumn;
                cursorBelowContent = false;

                completedUnits++;
                completedRows += unitHeight;
                // Always record a slot, including for units submitted without a
                // key, so RowKeys stays index-aligned with unit order and
                // RowKeys.Count == CompletedUnits.
                rowKeys.Add(unit.RowKey);
                appendRow += unitHeight;
                emittedInTurn++;

                if (emittedInTurn >= UnitsPerTurn && completedUnits < totalUnits)
                {
                    emittedInTurn = 0;
                    // Repaint the live region at every turn boundary, below the
                    // appended content, so the region is continuously rewritten
                    // instead of being left blank across a large batch. Whether
                    // the host has displayed the repaint is not claimed here.
                    appendRow = RepaintLiveRegion(appendRow, Record);
                    cursorRow = appendRow;
                    cursorColumn = 0;
                    cursorBelowContent = true;
                    Record($"turn yield after unit {completedUnits}");
                    await WaitForTerminalConsumptionAsync(token).ConfigureAwait(false);
                    await Task.Yield();
                }
            }

            // Coordinated final step: reserve room for the live region, apply
            // the next live layout to the still-running app, and repaint the
            // region below the committed content before unmuting, so no
            // stale-origin frame is ever replayed.
            var liveOrigin = EnsureRoom(
                appendRow, Math.Max(1, _live.LiveHeight), Record, "live-region");
            var snapshot = _live.SnapshotLiveSurface() ?? EmptySurface(Math.Max(1, _terminal.Width));
            _live.ApplyLiveLayout(nextLive);
            _live.ReanchorLive(liveOrigin, _live.LiveHeight, snapshot);
            _live.ResizeLive(Math.Max(1, _terminal.Width), _live.LiveHeight);

            // Resume the live output pump, then ask for a frame and wait for it.
            // Ordering matters: a frame rendered while muted would be discarded,
            // so the unmute happens before the request, and the wait is what
            // makes "nextLive applied" an observed fact rather than an
            // assumption. It is bounded, and a timeout is recorded explicitly.
            // Drop every frame the app queued while muted, then resume: the
            // region's bookkeeping was already moved to the new origin, so the
            // frames the app renders from here on are laid out for it, and no
            // frame laid out for the superseded origin can be replayed.
            var discarded = _live.DiscardQueuedLiveOutput();
            if (!wasMuted)
            {
                _live.SetLiveOutputMuted(false);
            }
            _live.RequestLiveFrame();
            var liveFrameObserved = await _live
                .WaitForNextLiveFrameAsync(LiveFrameTimeout, token)
                .ConfigureAwait(false);
            Record(
                $"emission-ack units={completedUnits} rows={completedRows} liveOrigin={liveOrigin} " +
                $"liveHeight={_live.LiveHeight} width={_terminal.Width} " +
                $"discardedQueuedFrames={discarded} liveFrameObserved={liveFrameObserved}");
        }
        catch (FlowCommitException)
        {
            throw;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is not rollback: the completed prefix stays in
            // history and the aborted unit's rows stay where they were written.
            // Re-anchor below everything that may have been written before the
            // live pump resumes, so no later frame overwrites it.
            var abortedRows = emission.RowsWritten;
            var recoveryRow = appendRow + abortedRows;
            await ReanchorAndResumeAsync(
                recoveryRow,
                nextLive,
                Record,
                "cancelled",
                completedUnits,
                abortedRows).ConfigureAwait(false);
            Record($"cancelled after unit {completedUnits} abortedRows={abortedRows}");
            // CompletedRows keeps its documented meaning (rows of fully emitted
            // units only). The aborted unit's rows are reserved on screen and
            // reported through the commit trace, not counted as completed.
            return new FlowCommitResult(
                completedUnits,
                completedRows,
                rowKeys,
                FlowCommitStatus.Cancelled,
                events,
                cancelled: true,
                abortedRows: abortedRows,
                drainTimedOut: false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            _uncertain = true;
            // The failure left rows in an unknown state. Move the live region
            // below everything that may have been written — including the
            // aborted unit's partial rows — and resume there, so the completed
            // prefix and the uncertain bytes are never painted over by the next
            // live frame, and nothing is replayed.
            var abortedRows = emission.RowsWritten;
            var recoveryRow = appendRow + abortedRows;
            await ReanchorAndResumeAsync(
                recoveryRow,
                nextLive,
                Record,
                "fault",
                completedUnits,
                abortedRows).ConfigureAwait(false);
            Record($"fault after unit {completedUnits}: {ex.GetType().Name}: {ex.Message} abortedRows={abortedRows}");
            throw new FlowCommitException(
                $"History emission failed after {completedUnits} completed unit(s) " +
                $"({completedRows} row(s)): {ex.Message}",
                completedUnits,
                completedRows,
                mayHavePartialRow: ex is IOException,
                innerException: ex);
        }
        finally
        {
            if (!wasMuted)
            {
                _live.SetLiveOutputMuted(false);
            }
        }

        // The final drain is part of the emission outcome, so a cancellation
        // here must surface as a cancelled commit rather than escaping as an
        // unrelated failure the caller would have to treat as uncertain.
        bool drained;
        try
        {
            drained = await WaitForTerminalConsumptionAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            Record($"cancelled after unit {completedUnits} during final drain");
            return new FlowCommitResult(
                completedUnits,
                completedRows,
                rowKeys,
                FlowCommitStatus.Cancelled,
                events,
                cancelled: true,
                abortedRows: 0,
                drainTimedOut: false);
        }

        if (!drained)
        {
            // The bounded drain expired with the adapter's output queue still
            // holding bytes. Emission is not withdrawn — the units were handed
            // to the write path — but the result must not imply the terminal
            // side consumed them.
            Record(
                $"drain-timeout units={completedUnits} rows={completedRows} " +
                $"queueDepth={_terminal.OutputQueueDepth}");
        }

        return new FlowCommitResult(
            completedUnits,
            completedRows,
            rowKeys,
            FlowCommitStatus.Emitted,
            events,
            cancelled: false,
            abortedRows: 0,
            drainTimedOut: !drained);
    }

    /// <summary>
    /// Moves the live region below everything the commit may have written and
    /// resumes the live pump there. Used by the success path and by the fault
    /// and cancellation paths, so a resumed live frame can never paint over
    /// committed or uncertain rows.
    /// </summary>
    /// <returns>The live region's new origin.</returns>
    private async Task<int> ReanchorAndResumeAsync(
        int appendRow,
        Func<FlowStepContext, Task<Hex1bWidget>> nextLive,
        Action<string> record,
        string reason,
        int completedUnits,
        int abortedRows)
    {
        var liveHeight = Math.Max(1, _live.LiveHeight);
        var liveOrigin = EnsureRoom(appendRow, liveHeight, record, "recovery-live-region");
        var snapshot = _live.SnapshotLiveSurface() ?? EmptySurface(Math.Max(1, _terminal.Width));
        _live.ApplyLiveLayout(nextLive);
        _live.ReanchorLive(liveOrigin, liveHeight, snapshot);
        _live.ResizeLive(Math.Max(1, _terminal.Width), liveHeight);

        // Drop every frame the app queued while muted: the region's bookkeeping
        // just moved, so frames laid out for the superseded origin must never be
        // replayed.
        var discarded = _live.DiscardQueuedLiveOutput();
        _live.SetLiveOutputMuted(false);
        _live.RequestLiveFrame();
        var liveFrameObserved = await _live
            .WaitForNextLiveFrameAsync(LiveFrameTimeout, CancellationToken.None)
            .ConfigureAwait(false);
        record(
            $"recovery-reanchor reason={reason} units={completedUnits} abortedRows={abortedRows} " +
            $"liveOrigin={liveOrigin} liveHeight={liveHeight} width={_terminal.Width} " +
            $"discardedQueuedFrames={discarded} liveFrameObserved={liveFrameObserved}");
        return liveOrigin;
    }

    /// <summary>
    /// Scrolls the viewport up until <paramref name="height"/> rows fit below
    /// <paramref name="appendRow"/>, returning the adjusted append row.
    /// </summary>
    private int EnsureRoom(int appendRow, int height, Action<string> record, string what)
    {
        var terminalHeight = Math.Max(1, _live.TerminalHeight);
        var overflow = (appendRow + height) - terminalHeight;
        if (overflow > 0)
        {
            _live.ScrollViewportUp(overflow);
            appendRow -= overflow;
            record(
                $"scroll {what} rows={overflow} appendRow={appendRow} " +
                $"terminalHeight={terminalHeight} height={height} " +
                $"terminalWidth={_live.TerminalWidth} liveHeight={_live.LiveHeight}");
        }
        return appendRow;
    }

    /// <summary>
    /// Reserves room for the live region and repaints it below the committed
    /// content, returning the append row for subsequent content.
    /// </summary>
    private int RepaintLiveRegion(int appendRow, Action<string> record)
    {
        var liveHeight = Math.Max(1, _live.LiveHeight);
        var origin = EnsureRoom(appendRow, liveHeight, record, "live-region");
        _live.ReanchorLive(
            origin,
            liveHeight,
            _live.SnapshotLiveSurface() ?? EmptySurface(Math.Max(1, _terminal.Width)));
        return origin;
    }

    /// <summary>
    /// Waits until the live step's application has rendered and is in its
    /// input-capable lifecycle. Public face of readiness for
    /// <see cref="FlowStep.WaitForReadyAsync(CancellationToken)"/>.
    /// </summary>
    public Task WaitForReadyAsync(CancellationToken cancellationToken)
        => WaitForReadyCoreAsync(cancellationToken);

    private async Task WaitForReadyCoreAsync(CancellationToken cancellationToken)
    {
        // Readiness = the persistent app has rendered at least one frame and is
        // in its input-capable lifecycle. A rendered marker alone is not used as
        // an input oracle; this waits for an actual frame.
        if (_live.FrameCount > 0)
        {
            return;
        }

        var observed = await _live
            .WaitForNextLiveFrameAsync(ReadinessTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!observed)
        {
            throw new InvalidOperationException(
                "The live step did not render a frame within the readiness window.");
        }
    }

    /// <summary>
    /// Emits one logical unit as contiguous logical rows, then waits for the
    /// terminal side to consume it.
    /// </summary>
    /// <remarks>
    /// Row order per row is: position at the row, clear it, write the text, then
    /// terminate the line. Clearing before content means a row that fills the
    /// full width keeps its last cell, while a shorter row still removes
    /// whatever a previous frame left at the right margin.
    /// </remarks>
    private async Task EmitUnitAsync(
        Action<string> record,
        int unitIndex,
        FlowCommitUnit unit,
        int unitRow,
        int faultThreshold,
        UnitEmission emission,
        CancellationToken cancellationToken)
    {
        var surface = unit.Surface;
        var height = Math.Max(1, surface.Height);

        for (var row = 0; row < height; row++)
        {
            // Cancellation is observed between rows, never after the unit's last
            // row: once every row has been handed to the terminal the unit is
            // complete, so cancelling there must not report it as aborted.
            cancellationToken.ThrowIfCancellationRequested();

            var text = SoftWrapEmitter.OrderedRowPrefix
                + SoftWrapEmitter.RenderRowText(surface, row);

            if (faultThreshold >= 0 && unitIndex == faultThreshold && row == 0)
            {
                // Prototype fault fixture: this unit is written partially and
                // then abandoned, so the host stream genuinely contains a
                // truncated line — exactly the uncertainty being reported.
                var partial = text.Length > 1 ? text[..^1] : text;
                _live.WriteTerminalAt(unitRow, partial);

                // The bytes reached the host, so the recovery hand-off must
                // treat this row as written and never paint over it.
                emission.RowsWritten++;
                emission.LastRow = unitRow;
                emission.LastColumn = SoftWrapEmitter.RenderRowText(surface, row).Length;
                throw new IOException(
                    $"injected history emission failure after {unitIndex} completed unit(s) " +
                    $"({FailAfterRowsVariable})");
            }

            _live.WriteTerminalAt(
                unitRow + row,
                row == height - 1 ? text : text + "\r\n");
            emission.RowsWritten++;
            emission.LastRow = unitRow + row;
            emission.LastColumn = SoftWrapEmitter.RenderRowText(surface, row).Length;
        }

        // Track the committed rows as paragraphs so a later resize settle can
        // recompute the live region's origin with this content included.
        _live.RecordCommittedRows(surface);

        record(
            $"unit {unitIndex} key={unit.RowKey ?? "-"} rows={height} " +
            $"at={unitRow} width={surface.Width}");

        // The unit is fully written and recorded; waiting for the terminal side
        // is deliberately not cancellable, so a cancellation cannot land after
        // the last row but before the caller counts the unit as completed.
        await WaitForTerminalConsumptionAsync(CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// Waits until the terminal-side output pump has drained what the commit
    /// handed to the adapter.
    /// </summary>
    /// <remarks>
    /// This is a framework-emission acknowledgement, not host scanout, and it
    /// makes no claim about visibility or durability. If the queue does not
    /// drain within the bounded window the commit still proceeds and reports
    /// completion, so a stalled terminal cannot deadlock the step.
    /// </remarks>
    private async Task<bool> WaitForTerminalConsumptionAsync(CancellationToken cancellationToken)
    {
        var deadline = Environment.TickCount64 + (long)DrainTimeout.TotalMilliseconds;
        while (_terminal.OutputQueueDepth > 0 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(2, cancellationToken).ConfigureAwait(false);
        }

        // True only when the queue actually drained. A caller must not turn a
        // timeout into a claim of confirmed emission.
        return _terminal.OutputQueueDepth <= 0;
    }

    /// <summary>
    /// Per-unit emission progress. The recovery hand-off needs to know which
    /// rows may already carry bytes when a unit aborts, so it can place the live
    /// region below them instead of painting over them.
    /// </summary>
    private sealed class UnitEmission
    {
        /// <summary>Rows of the current unit already handed to the terminal.</summary>
        public int RowsWritten { get; set; }

        /// <summary>Screen row of the last row the unit wrote.</summary>
        public int LastRow { get; set; }

        /// <summary>Column the last row's text ended at.</summary>
        public int LastColumn { get; set; }

        /// <summary>Clears the tracker before each unit, so an aborted unit's
        /// row count never includes rows from earlier units.</summary>
        public void Reset()
        {
            RowsWritten = 0;
            LastRow = 0;
            LastColumn = 0;
        }
    }

    private static Surface EmptySurface(int width)
    {
        var surface = new Surface(width, 1);
        surface.TrySetCell(0, 0, SurfaceCells.Space(null, null));
        return surface;
    }
}
