using System.Runtime.ExceptionServices;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// One serialized terminal update: bytes are composed into a buffer and handed
/// to the terminal when the scope is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Synchronous by contract: nothing inside the scope may await, it must be
/// disposed on the thread that opened it, and an empty scope writes nothing.
/// </para>
/// <para>
/// The caller must keep the scope object rather than discarding it in a
/// <c>using</c> block, because <see cref="FlushCompleted"/> is the only place
/// that says whether the update was actually handed off — the update's own
/// failure cannot carry that fact.
/// </para>
/// <para>
/// Acceptance is not consumption: a completed hand-off means the adapter took
/// the bytes, never that the host terminal displayed or retained them.
/// </para>
/// </remarks>
internal interface IAtomicTerminalUpdate : IDisposable
{
    /// <summary>
    /// True once the adapter accepted the composed bytes.
    /// </summary>
    /// <remarks>
    /// False when the buffer was never handed off because the write failed. True
    /// for a scope that composed nothing: an empty scope writes nothing, so it
    /// has no hand-off to fail.
    /// </remarks>
    bool FlushCompleted { get; }
}

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
    /// Reads the current presentation geometry. Native presentations refresh
    /// this from the driver rather than the last event delivered to the app
    /// workload adapter.
    /// </summary>
    (int Width, int Height) ReadCurrentGeometry();

    /// <summary>
    /// Rechecks presentation geometry after an atomic update has acquired the
    /// step and terminal write locks, before any bytes are composed.
    /// </summary>
    (int Width, int Height, long ResizeVersion) ReadFreshGeometryForEmission();

    /// <summary>
    /// Rejects history commitment when this native presentation has no
    /// qualified host profile. Headless/custom adapters without a profile
    /// provider remain permitted.
    /// </summary>
    void EnsureHistoryCommitSupported();

    /// <summary>
    /// Acquires live-output ownership, cancels pending resize work, and observes
    /// the current boundary before any positioning write. Returns the prior mute
    /// state; on failure it restores that state itself.
    /// </summary>
    Task<bool> PrepareForCommitAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Writes <paramref name="text"/> at absolute terminal row
    /// <paramref name="row"/> under the terminal write lock, so positioning and
    /// content can never be split by another writer.
    /// </summary>
    void WriteTerminalAt(int row, string text);

    /// <summary>
    /// Opens a serialized terminal-update scope. Every write the commit issues
    /// through this handle inside the scope is composed into ONE terminal write,
    /// optionally bracketed by synchronized-output (DEC mode 2026) bytes. The
    /// write lock prevents framework writers from interleaving the unit's rows
    /// and its live-region repaint; host presentation atomicity is not inferred.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Synchronous by contract: nothing inside the scope may await, and it must
    /// be disposed on the thread that opened it. An empty scope writes nothing.
    /// </para>
    /// <para>
    /// The returned scope reports whether its hand-off actually happened, which
    /// the update's own failure cannot: <see cref="IAtomicTerminalUpdate.FlushCompleted"/>
    /// is set only once the adapter has accepted the composed bytes. A caller
    /// handling a failure must therefore keep the scope object — not just a
    /// <c>using</c> block — to tell "composed but never handed off" from
    /// "handed off". Acceptance is not host consumption.
    /// </para>
    /// </remarks>
    IAtomicTerminalUpdate BeginAtomicTerminalUpdate();

    /// <summary>
    /// Scrolls the viewport up by <paramref name="rows"/> rows by parking the
    /// cursor on the bottom row and emitting that many linefeeds, pushing the
    /// top rows into the host's scrollback. Never clears or replays the
    /// scrollback; the host owns it.
    /// </summary>
    void ScrollViewportUp(int rows);

    /// <summary>
    /// Emits the qualified host's commit-boundary marker at a committed row.
    /// Unqualified hosts receive no protocol bytes.
    /// </summary>
    void MarkCommittedRow(int row);

    /// <summary>
    /// Atomically moves the live step's region to <paramref name="rowOrigin"/>
    /// and resizes it to <paramref name="liveHeight"/>, then repaints the
    /// region as a single serialized pass: blank every row, paint
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
    /// Applies a terminal geometry discovered without a resize event and asks
    /// the live app to render at the corresponding step height.
    /// </summary>
    void ResizeLiveToTerminalGeometry(int width, int terminalHeight);

    /// <summary>
    /// Number of frames the live application has completed. Used to wait for a
    /// frame rendered after a builder swap instead of guessing.
    /// </summary>
    long FrameCount { get; }

    /// <summary>
    /// Monotonic counter of size changes this handle has observed, including a
    /// change that returns the terminal to the dimensions it had before (an ABA
    /// pair). A caller that samples it around an await can therefore tell that a
    /// resize happened even when comparing width and height afterwards cannot.
    /// </summary>
    long ResizeVersion { get; }

    /// <summary>
    /// Applies a new live layout builder to the still-running live
    /// application, preserving the same app and node identities. Returns
    /// without waiting; the caller observes <see cref="FrameCount"/>.
    /// </summary>
    void ApplyLiveLayout(Func<FlowStepContext, Task<Hex1bWidget>> builder);

    /// <summary>Whether the adapter can report an authoritative cursor position.</summary>
    bool SupportsCursorObservation { get; }

    /// <summary>
    /// Asks the host where its own cursor actually is, in screen rows, or returns
    /// <see langword="null"/> when the host cannot report it.
    /// </summary>
    /// <remarks>
    /// This is the authoritative post-reflow anchor, where the flow's own reflow
    /// model is only as good as the paragraphs it recorded: after the host has
    /// re-wrapped its buffer it owns where the content landed, and a scalar row
    /// the framework kept cannot see a change that moved that row and moved it
    /// back. A null answer means "unknown", never "row zero".
    /// </remarks>
    Task<int?> ObserveCursorRowAsync(CancellationToken cancellationToken);

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
    /// Waits until the live application has completed a frame newer than
    /// <paramref name="afterFrame"/> — a <see cref="FrameCount"/> baseline the
    /// caller captured before it changed what the app should render — bounded by
    /// <paramref name="timeout"/>.
    /// </summary>
    /// <remarks>
    /// The baseline is passed in rather than read here so that a frame which
    /// completes between the caller's change and this wait still satisfies it; a
    /// wait that read the count at entry could miss that frame and then block for
    /// the whole timeout waiting for one that already happened. Returns true as
    /// soon as <see cref="FrameCount"/> exceeds the baseline, and false when the
    /// wait times out or is cancelled.
    /// </remarks>
    Task<bool> WaitForLiveFrameAfterAsync(
        long afterFrame,
        TimeSpan timeout,
        CancellationToken cancellationToken);

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
/// The algorithm, per unit:
/// </para>
/// <list type="number">
///   <item>materialize the unit at the width in force (re-materializing it at the
///         settled width if the terminal changed since the last check);</item>
///   <item>in one serialized terminal update, bracketed by synchronized output:
///         scroll the viewport up if the unit does not fit below the append
///         cursor, pushing older content into the host's scrollback via bottom-row
///         linefeeds, append the unit's logical rows, then repaint the live
///         region at the new append position from the freshest live frame — so
///         the region is never left displaced, and an edit rendered while the
///         output pump was muted becomes visible in that same update;</item>
///   <item>count the unit as completed the moment its atomic update has been
///         handed to the write path, then wait for the terminal side to consume
///         the update, then yield at the turn boundary so queued input and
///         resize events are dispatched.</item>
/// </list>
/// <para>
/// The count comes first because the count is the commit's outcome: a
/// cancellation that lands during the post-emission wait must not be able to
/// turn an already emitted unit into a pending one. Cancellation is therefore
/// reported in two separate facts — how far the commit got
/// (<see cref="FlowCommitResult.Status"/>) and whether cancellation was observed
/// at all (<see cref="FlowCommitResult.CancellationRequested"/>) — and a
/// cancellation observed only after the last unit was handed off resolves as a
/// complete emission.
/// </para>
/// <para>
/// A failure reaches the caller as <see cref="FlowCommitException"/> with the
/// same hand-off facts, whatever raised it. When the failure left an emitted
/// prefix or a partly composed unit, the step is suspended rather than left
/// looking retryable, and the live region is re-anchored below everything the
/// failed commit may have written. Recovery failure never replaces the original
/// failure; it is reported alongside it.
/// </para>
/// <para>
/// The live layout follows the hand-off rule: the commit applies its next live
/// layout only when every submitted unit was handed off. A commit that stops
/// with units pending — cancellation, or a failure — retains the step's current
/// layout, because the pending content still owns the presentation and the
/// caller decides what to do with it.
/// </para>
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
    /// commit size. The live region is repainted per unit, not per turn — a turn
    /// boundary only decides where the commit yields.
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

    private Exception? _suspensionFailure;

    internal void Suspend(Exception failure)
    {
        _suspensionFailure = failure;
        _uncertain = true;
        _live.RecordCommitEvent($"suspended {failure.GetType().Name}: {failure.Message}");
    }

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

        // Native host capability is a synchronous precondition. Check it before
        // taking admission so an unqualified/unknown console never enters the
        // in-flight state or reaches source preparation.
        _live.EnsureHistoryCommitSupported();

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
                "History commitment is suspended: the previous failure left no safe append boundary.",
                completedUnits: 0,
                completedRows: 0,
                abortedRows: 0,
                innerException: _suspensionFailure);
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

        bool wasMuted;
        try
        {
            wasMuted = await _live.PrepareForCommitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested && !_uncertain)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new FlowCommitException(
                "History commitment could not establish a safe admission boundary.",
                0, 0, 0, innerException: ex);
        }
        var initialGeometry = _live.ReadCurrentGeometry();
        var committedWidth = initialGeometry.Width;
        var committedHeight = initialGeometry.Height;
        var committedVersion = _live.ResizeVersion;
        var totalUnits = 0;
        var sourcePrepared = false;

        var completedUnits = 0;
        var completedRows = 0;
        var rowKeys = new List<string?>();
        var appendRow = Math.Max(0, _live.RowOrigin);
        var emittedInTurn = 0;
        var faultThreshold = ReadPrototypeFailureThreshold();
        var emission = new UnitEmission();
        // Where the commit is when it fails. A unit's rows are composed into one
        // buffered update whose only hand-off to the write path is the scope's
        // dispose, so a failure raised after composition finished is a failed
        // hand-off (the composed rows may not have reached the adapter at all),
        // while a failure raised during composition leaves the composed rows to
        // the disposal that follows — which may itself fail. Recording which one
        // happened is what keeps the failure evidence from claiming a flush that
        // never took place.
        var phase = EmissionPhase.Preparing;
        // Whether the most recent atomic scope's one hand-off to the write path
        // was accepted. The failure path reports this, because a unit that was
        // composed without a hand-off may not have reached the adapter at all.
        var unitFlushed = false;
        // The surface of the unit in flight, so the failure paths can record the
        // rows it had already composed in the reflow model (see RecordComposedRows).
        Surface? unitSurface = null;

        // Retain every width seen for the pending unit: A -> B -> A must not
        // materialize (index, A) twice. Clear after hand-off, not after reflow.
        var materialized = new Dictionary<int, FlowCommitUnit>();

        async Task<FlowCommitUnit> MaterializeAsync(int index, int width, CancellationToken ct)
        {
            if (materialized.TryGetValue(width, out var cached))
            {
                Record($"materialize-reuse unit={index} width={width}");
                return cached;
            }

            var built = await source.UnitAsync(index, width, ct).ConfigureAwait(false);
            materialized.Add(width, built);
            return built;
        }

        var resumeOutput = true;
        var recoveryOffset = 0;

        try
        {
            totalUnits = await source.PrepareAsync(committedWidth, token).ConfigureAwait(false);
            sourcePrepared = true;
            if (totalUnits <= 0)
            {
                Record($"start empty width={committedWidth} liveOrigin={_live.RowOrigin}");
                return new FlowCommitResult(
                    0, 0, Array.Empty<string?>(), 0, events,
                    cancellationRequested: token.IsCancellationRequested,
                    abortedRows: 0, drainObserved: false, drainTimedOut: false);
            }

            rowKeys.Capacity = Math.Min(totalUnits, 256);
            Record(
                $"start units={totalUnits} width={committedWidth} " +
                $"liveOrigin={_live.RowOrigin} liveHeight={_live.LiveHeight} " +
                $"terminalHeight={_live.TerminalHeight}");
            while (completedUnits < totalUnits)
            {
                token.ThrowIfCancellationRequested();
                phase = EmissionPhase.Preparing;
                unitFlushed = false;
                recoveryOffset = 0;

                // Geometry is re-checked before every unit AND again right before
                // the write: the host re-wraps its buffer the moment it sees a
                // resize, while the resize event reaches the flow asynchronously,
                // so a unit written at the pre-resize row after the host has
                // already reflowed would land on committed content.
                async Task<bool> ReflowIfNeededAsync(string when)
                {
                    var (currentWidth, currentHeight) = _live.ReadCurrentGeometry();
                    var currentVersion = _live.ResizeVersion;
                    if (currentWidth == committedWidth && currentHeight == committedHeight
                        && currentVersion == committedVersion)
                    {
                        return false;
                    }

                    // Only a width change invalidates a pending unit's bytes, so
                    // only a width change re-prepares the source. A height-only
                    // change is geometry: nothing is re-prepared, and nothing is
                    // re-materialized at the unchanged width, which is the pair
                    // FlowCommitSource forbids re-requesting.
                    var widthChanged = currentWidth != committedWidth;
                    if (widthChanged)
                    {
                        await ReprepareForWidthChangeAsync(currentWidth).ConfigureAwait(false);
                        committedWidth = currentWidth;
                    }

                    if (!_live.SupportsCursorObservation)
                    {
                        throw new NotSupportedException(
                            "Resizing during history commitment requires cursor observation.");
                    }

                    appendRow = await ObserveBoundaryAsync(appendRow, 0, token).ConfigureAwait(false);
                    committedVersion = _live.ResizeVersion;
                    committedHeight = currentHeight;
                    Record(
                        $"reflow when={when} width={currentWidth} height={currentHeight} units={totalUnits} " +
                        $"appendRow={appendRow} resizeVersion={committedVersion} " +
                        $"geometryOnly={(widthChanged ? "false" : "true")}");
                    return widthChanged;
                }

                // Re-prepares the source for a new width and fails the commit if
                // its unit identity did not survive the change.
                async Task ReprepareForWidthChangeAsync(int width)
                {
                    var reprepared = await source.PrepareAsync(width, token).ConfigureAwait(false);
                    if (reprepared == totalUnits)
                    {
                        return;
                    }

                    // Unit identity must survive reflow. If the count changed,
                    // index i no longer means what it meant when the commit
                    // started, so continuing could silently duplicate or drop
                    // content — with units already emitted. Fail loudly, and let
                    // the failure path report the emitted prefix and suspend the
                    // step rather than leaving this looking like a retryable
                    // precondition violation.
                    throw new InvalidOperationException(
                        $"Commit source returned a different unit count at width {width} " +
                        $"({totalUnits} -> {reprepared}); logical units must be stable " +
                        "across widths so a resize cannot re-identify pending content.");
                }

                await ReflowIfNeededAsync("pre-unit");

                var width = committedWidth;

                if (emittedInTurn == 0)
                {
                    Record($"turn start unit={completedUnits} width={width}");
                }

                FlowCommitUnit unit;
                var materializationWidth = committedWidth;
                while (true)
                {
                    materializationWidth = committedWidth;
                    unit = await MaterializeAsync(completedUnits, materializationWidth, token)
                        .ConfigureAwait(false);
                    await ReflowIfNeededAsync("pre-write").ConfigureAwait(false);
                    appendRow = await ObserveBoundaryAsync(appendRow, 0, token).ConfigureAwait(false);

                    // Observation itself can span a resize. Reuse an existing
                    // materialization only when its width and the final geometry
                    // still agree; height-only changes never rebuild the unit.
                    if (materializationWidth == committedWidth
                        && committedWidth == _live.ReadCurrentGeometry().Width
                        && committedHeight == _live.ReadCurrentGeometry().Height
                        && committedVersion == _live.ResizeVersion)
                    {
                        break;
                    }
                }

                var unitHeight = Math.Max(1, unit.Surface.Height);
                unitSurface = unit.Surface;

                // Compose history plus live repaint as one serialized write.
                // Adapter acceptance is not a host-presentation acknowledgement.
                var scope = _live.BeginAtomicTerminalUpdate();
                var freshGeometry = _live.ReadFreshGeometryForEmission();
                if (freshGeometry.Width != materializationWidth
                    || freshGeometry.Height != committedHeight
                    || freshGeometry.ResizeVersion != committedVersion)
                {
                    // The native presentation can resize between the last
                    // asynchronous observation and this write. The scope is
                    // still empty, so release it and rebuild/re-observe rather
                    // than emitting bytes laid out for stale geometry.
                    scope.Dispose();
                    var widthChanged = freshGeometry.Width != committedWidth;
                    if (widthChanged)
                    {
                        await ReprepareForWidthChangeAsync(freshGeometry.Width).ConfigureAwait(false);
                        committedWidth = freshGeometry.Width;
                    }

                    committedHeight = freshGeometry.Height;
                    committedVersion = freshGeometry.ResizeVersion;
                    _live.ResizeLiveToTerminalGeometry(
                        freshGeometry.Width,
                        freshGeometry.Height);
                    appendRow = await ObserveBoundaryAsync(appendRow, 0, token)
                        .ConfigureAwait(false);
                    continue;
                }

                var wholeUnitComposed = false;
                Exception? compositionFailure = null;
                try
                {
                    phase = EmissionPhase.Composing;
                    EmitUnit(
                        Record, completedUnits, unit, ref appendRow,
                        faultThreshold, emission, token);
                    wholeUnitComposed = true;
                    appendRow = RepaintLiveRegion(appendRow, Record);
                    emission.NextRowOffset = 0;
                    phase = EmissionPhase.Handoff;
                }
                catch (Exception ex)
                {
                    compositionFailure = ex;
                }
                finally
                {
                    try
                    {
                        // A composition failure can leave a partial row in this
                        // scope. Repaint before disposing it so the partial
                        // history and the retained prompt reach the host as one
                        // presentable frame; recovery still re-anchors below the
                        // exact composed-row offset afterward.
                        if (compositionFailure is not null
                            && !wholeUnitComposed
                            && emission.RowsWritten > 0)
                        {
                            try
                            {
                                appendRow = RepaintLiveRegion(appendRow, Record);
                                emission.NextRowOffset = 0;
                            }
                            catch (Exception repaintEx)
                            {
                                Record(
                                    $"partial-repaint-failed unit={completedUnits} " +
                                    $"{repaintEx.GetType().Name}: {repaintEx.Message}");
                            }
                        }

                        scope.Dispose();
                    }
                    catch (Exception handoffEx)
                    {
                        if (compositionFailure is null)
                        {
                            compositionFailure = handoffEx;
                        }
                        else
                        {
                            Record(
                                $"scope-handoff-failed unit={completedUnits} " +
                                $"{handoffEx.GetType().Name}: {handoffEx.Message}");
                        }
                    }
                    finally
                    {
                        unitFlushed = scope.FlushCompleted;
                    }
                }

                if (unitFlushed && wholeUnitComposed)
                {
                    // Count accepted units even if their subsequent live repaint
                    // failed, and always before a cancellable drain.
                    recoveryOffset = emission.NextRowOffset;
                    emission.Reset();
                    materialized.Clear();
                    completedUnits++;
                    completedRows += unitHeight;
                    rowKeys.Add(unit.RowKey);
                    emittedInTurn++;
                    _live.RecordCommittedRows(unit.Surface);
                }

                if (compositionFailure is not null)
                {
                    ExceptionDispatchInfo.Capture(compositionFailure).Throw();
                }

                if (!unitFlushed)
                {
                    throw new IOException("The terminal did not accept the composed history update.");
                }

                var unitDrained = await WaitForTerminalConsumptionAsync(token).ConfigureAwait(false);
                if (!unitDrained)
                {
                    // Not fatal here: the commit's own outcome is reported by the
                    // final drain, so a per-unit timeout is evidence, not a
                    // failure.
                    Record(
                        $"drain-timeout unit={completedUnits - 1} " +
                        $"queueDepth={_terminal.OutputQueueDepth}");
                }

                if (emittedInTurn >= UnitsPerTurn && completedUnits < totalUnits)
                {
                    emittedInTurn = 0;
                    Record($"turn yield after unit {completedUnits} origin={appendRow} liveHeight={EffectiveLiveHeight()}");
                    await Task.Yield();
                }
            }

            phase = EmissionPhase.Finalizing;

            // Coordinated final step, in the only order that cannot replay a
            // frame laid out for the superseded origin: the pump stays muted
            // while the next live layout is applied and the app renders it, the
            // freshest surface is painted below the committed rows only after
            // that frame has arrived, and the pump resumes last. Every submitted
            // unit was handed off by the time the loop exits, which is exactly
            // the condition under which the next live layout is applied.
            //
            // The frame baseline is captured BEFORE the swap and the render
            // request, so a frame that completes in between still satisfies the
            // wait instead of being missed and stalling it. The layout builder is
            // free to trigger a resize of its own while it is applied, which is
            // why nothing is painted until its frame is in hand.
            var frameBeforeSwap = _live.FrameCount;
            _live.ApplyLiveLayout(nextLive);
            _live.RequestLiveFrame();
            var liveFrameObserved = await _live
                .WaitForLiveFrameAfterAsync(frameBeforeSwap, LiveFrameTimeout, token)
                .ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!liveFrameObserved)
            {
                throw new TimeoutException("The next live layout did not render before hand-off.");
            }

            appendRow = await ObserveBoundaryAsync(appendRow, 0, token).ConfigureAwait(false);
            // Reserve and paint only after a bounded geometry-stability check.
            // A host resize can land between the boundary observation and
            // reservation; preserve the adjusted append row from any scroll
            // rather than querying the cursor after EnsureRoom has parked it
            // on the bottom row.
            var finalLiveHeight = 0;
            var liveOrigin = 0;
            var finalWidth = 0;
            var finalReserved = false;
            for (var geometryAttempt = 0; geometryAttempt < 4; geometryAttempt++)
            {
                var geometryBefore = _live.ReadCurrentGeometry();
                var versionBefore = _live.ResizeVersion;
                finalLiveHeight = Math.Clamp(
                    Math.Max(1, _live.LiveHeight),
                    1,
                    Math.Max(1, geometryBefore.Height));
                liveOrigin = EnsureRoom(
                    appendRow,
                    finalLiveHeight,
                    Record,
                    "live-region",
                    geometryBefore.Height);
                var geometryAfter = _live.ReadCurrentGeometry();
                if (geometryBefore.Width != geometryAfter.Width
                    || geometryBefore.Height != geometryAfter.Height
                    || versionBefore != _live.ResizeVersion)
                {
                    // EnsureRoom may have scrolled before the host published
                    // the new geometry. Preserve its adjusted append row;
                    // the cursor now points at the scroll position, not the
                    // live boundary.
                    appendRow = Math.Max(0, liveOrigin);
                    continue;
                }

                finalWidth = Math.Max(1, geometryAfter.Width);
                finalLiveHeight = Math.Clamp(
                    finalLiveHeight, 1, Math.Max(1, geometryAfter.Height));
                finalReserved = true;
                break;
            }

            if (!finalReserved)
            {
                throw new InvalidOperationException(
                    "The terminal geometry kept changing while reserving the live region.");
            }

            var reanchorUpdate = _live.BeginAtomicTerminalUpdate();
            try
            {
                _live.ResizeLive(finalWidth, finalLiveHeight);
                _live.ReanchorLive(
                    liveOrigin,
                    finalLiveHeight,
                    _live.SnapshotLiveSurface() ?? EmptySurface(finalWidth));
            }
            finally
            {
                reanchorUpdate.Dispose();
            }

            // Drop every frame the app queued while muted — the region is painted
            // at the origin those frames are now laid out for, so none of them may
            // be replayed — and only then resume the pump, unless the caller had
            // muted it before this commit.
            var discarded = _live.DiscardQueuedLiveOutput();
            if (!wasMuted)
            {
                _live.SetLiveOutputMuted(false);
            }

            Record(
                $"emission-ack units={completedUnits} rows={completedRows} liveOrigin={liveOrigin} " +
                $"liveHeight={finalLiveHeight} width={finalWidth} " +
                $"discardedQueuedFrames={discarded} liveFrameObserved={liveFrameObserved}");
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Cancellation is not rollback: the completed prefix stays in
            // history and the aborted unit's rows stay where they were written.
            // Re-anchor below everything that may have been written before the
            // live pump resumes, so no later frame overwrites it.
            //
            // Only a prepared source with every unit handed off may install the
            // next layout. Before preparation returns, the pending count is unknown.
            var abortedRows = emission.RowsWritten;
            var pendingUnits = totalUnits - completedUnits;
            RecordComposedRows(unitSurface, abortedRows, Record, unitFlushed);
            Exception? recoveryFailure = null;
            try
            {
                await ReanchorAndResumeAsync(
                    appendRow,
                    Math.Max(recoveryOffset, emission.NextRowOffset),
                    sourcePrepared && pendingUnits == 0 ? nextLive : null,
                    Record,
                    sourcePrepared && pendingUnits == 0 ? "cancelled-all-handed-off" : "cancelled",
                    completedUnits,
                    abortedRows,
                    wasMuted).ConfigureAwait(false);
            }
            catch (Exception recoveryEx)
            {
                // The commit is cancelled, but its emitted prefix is history and
                // the live region was never moved below it. Letting the adapter
                // exception escape would leave the step looking safely retryable,
                // and a retry would duplicate the prefix.
                recoveryFailure = recoveryEx;
            }

            Record(
                $"cancelled after unit {completedUnits} abortedRows={abortedRows} " +
                $"pendingUnits={(sourcePrepared ? pendingUnits.ToString() : "unknown")}" +
                (recoveryFailure is null
                    ? string.Empty
                    : $" recoveryFailed={recoveryFailure.GetType().Name}: {recoveryFailure.Message}"));

            if (recoveryFailure is not null)
            {
                // Same policy as any other failure with potential emitted
                // progress: the step is suspended and the partial progress
                // travels with the failure, rather than the recovery failure
                // being reported as an unclassified error.
                Suspend(recoveryFailure);
                resumeOutput = false;
                throw new FlowCommitException(
                    $"History commitment was cancelled after {completedUnits} completed unit(s) " +
                    $"({completedRows} row(s))" +
                    (pendingUnits > 0 ? $", with {pendingUnits} unit(s) still pending" : string.Empty) +
                    (abortedRows > 0 ? $", {abortedRows} row(s) of the pending unit composed" : string.Empty) +
                    $"; live-region recovery also failed: {recoveryFailure.Message}",
                    completedUnits,
                    completedRows,
                    abortedRows,
                    innerException: recoveryFailure,
                    recoveryFailure: recoveryFailure);
            }

            if (!sourcePrepared)
            {
                // No source count or hand-off exists yet. Preserve the ordinary
                // pre-emission cancellation contract instead of inventing a result.
                throw;
            }

            // CompletedRows keeps its documented meaning (rows of fully emitted
            // units only). The aborted unit's rows are reserved on screen and
            // reported through the commit trace, not counted as completed.
            return new FlowCommitResult(
                completedUnits,
                completedRows,
                rowKeys,
                pendingUnits,
                events,
                cancellationRequested: true,
                abortedRows: abortedRows,
                drainObserved: false,
                drainTimedOut: false);
        }
        catch (Exception ex)
        {
            // Every failure in the emission region is reported through one
            // contract, whatever raised it. A raw exception escaping unchanged
            // would look like a precondition violation the caller may simply
            // retry, while the terminal may already hold emitted rows — above all
            // the unit-count change detected at a resize check, which can only be
            // reached with content already emitted.
            var abortedRows = emission.RowsWritten;
            var pendingUnits = totalUnits - completedUnits;
            // Potential emitted progress is what makes a retry unsafe: an emitted
            // prefix would be duplicated, and a partly composed unit may already
            // be on screen. With neither, nothing can be duplicated and the step
            // stays committable.
            var mayHaveEmitted = completedUnits > 0 || abortedRows > 0;
            if (mayHaveEmitted)
            {
                Suspend(ex);
            }

            // Whether this unit's one hand-off was accepted. Composed rows whose
            // hand-off failed may not have reached the adapter at all, so the
            // failure is reported with that uncertainty rather than as an exact
            // zero — the rows are still reserved and still never replayed.
            //
            // An empty scope reports a completed hand-off because it had nothing
            // to hand off, so the fact only says anything about rows this unit
            // actually composed: no composed rows must never be reported as a
            // hand-off that failed.
            var handoffMissing =
                abortedRows > 0
                && !unitFlushed
                && phase is EmissionPhase.Composing or EmissionPhase.Handoff;

            // The rows this unit composed are reserved on screen by the recovery
            // below, so the reflow model has to carry the same rows: a later
            // resize settle recomputes the region's origin from that model, and a
            // model ending above these rows would move the region on top of them.
            RecordComposedRows(unitSurface, abortedRows, Record, unitFlushed);

            Exception? recoveryFailure = null;
            try
            {
                if (handoffMissing)
                {
                    throw new IOException("The failed terminal hand-off left no safe repaint boundary.");
                }

                await ReanchorAndResumeAsync(
                    appendRow,
                    Math.Max(recoveryOffset, emission.NextRowOffset),
                    sourcePrepared && pendingUnits == 0 ? nextLive : null,
                    Record,
                    mayHaveEmitted ? "fault" : "fault-no-progress",
                    completedUnits,
                    abortedRows,
                    wasMuted).ConfigureAwait(false);
            }
            catch (Exception recoveryEx)
            {
                // Recovery failure must never replace or hide the failure that
                // caused it: the original exception is what the caller sees, with
                // the recovery failure reported next to it and traced here.
                recoveryFailure = recoveryEx;
                resumeOutput = false;
                Suspend(recoveryEx);
            }

            Record(
                $"fault after unit {completedUnits}: {ex.GetType().Name}: {ex.Message} " +
                $"abortedRows={abortedRows} pendingUnits={pendingUnits} phase={phase} " +
                $"handoffMissing={handoffMissing} suspended={mayHaveEmitted}" +
                (recoveryFailure is null
                    ? string.Empty
                    : $" recoveryFailed={recoveryFailure.GetType().Name}: {recoveryFailure.Message}"));

            throw new FlowCommitException(
                $"History emission failed after {completedUnits} completed unit(s) " +
                $"({completedRows} row(s))" +
                (pendingUnits > 0 ? $", with {pendingUnits} unit(s) still pending" : string.Empty) +
                (abortedRows > 0 ? $", {abortedRows} row(s) of the failing unit composed" : string.Empty) +
                (handoffMissing
                    ? ", before the update carrying them was accepted by the terminal"
                    : string.Empty) +
                $": {ex.Message}" +
                (recoveryFailure is null
                    ? string.Empty
                    : $" (live-region recovery also failed: {recoveryFailure.Message})"),
                completedUnits,
                completedRows,
                abortedRows,
                innerException: ex,
                recoveryFailure: recoveryFailure);
        }
        finally
        {
            if (resumeOutput && !wasMuted)
            {
                _live.SetLiveOutputMuted(false);
            }
        }

        // The final drain cannot change what was emitted: every unit was handed
        // off and the next live layout was applied before it ran. Cancellation
        // observed here is therefore reported alongside a complete emission, not
        // as a partial commit, and the two drain facts stay honest about what the
        // drain actually did.
        bool drained;
        try
        {
            drained = await WaitForTerminalConsumptionAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            // Every unit was handed off and the coordinated final step already
            // applied the next live layout, so the commit emitted everything it
            // was asked to; only the post-emission observation was cut short.
            Record(
                $"cancelled after unit {completedUnits} during final drain " +
                $"queueDepth={_terminal.OutputQueueDepth}");
            return new FlowCommitResult(
                completedUnits,
                completedRows,
                rowKeys,
                pendingUnits: 0,
                events,
                cancellationRequested: true,
                abortedRows: 0,
                drainObserved: false,
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
            pendingUnits: 0,
            events,
            cancellationRequested: token.IsCancellationRequested,
            abortedRows: 0,
            drainObserved: drained,
            drainTimedOut: !drained);
    }

    /// <summary>
    /// Repaints below the observed prefix while output remains muted. A partial
    /// commit retains the old builder; a fully handed-off commit installs nextLive.
    /// </summary>
    private async Task<int> ReanchorAndResumeAsync(
        int appendRow,
        int observedRowOffset,
        Func<FlowStepContext, Task<Hex1bWidget>>? nextLive,
        Action<string> record,
        string reason,
        int completedUnits,
        int abortedRows,
        bool wasMuted)
    {
        // Same order as the success path, and for the same reason: the pump stays
        // muted while the layout is applied (when the hand-off rule says it
        // applies) and the app renders it, and the region is painted from the
        // frame that actually arrived rather than from the pre-swap surface.
        var frameBeforeSwap = _live.FrameCount;
        if (nextLive is not null)
        {
            _live.ApplyLiveLayout(nextLive);
        }

        _live.RequestLiveFrame();
        var liveFrameObserved = await _live
            .WaitForLiveFrameAfterAsync(frameBeforeSwap, LiveFrameTimeout, _flowCancellationToken)
            .ConfigureAwait(false);
        _flowCancellationToken.ThrowIfCancellationRequested();
        if (!liveFrameObserved)
        {
            throw new TimeoutException("The live layout did not render during commitment recovery.");
        }

        appendRow = await ObserveBoundaryAsync(
            appendRow, observedRowOffset, _flowCancellationToken).ConfigureAwait(false);

        // The frame the app rendered may be a different height than the region
        // was, so the reservation is computed from a stable geometry snapshot.
        var liveHeight = 0;
        var liveOrigin = 0;
        var recoveryWidth = 0;
        var recoveryReserved = false;
        for (var geometryAttempt = 0; geometryAttempt < 4; geometryAttempt++)
        {
            var geometryBefore = _live.ReadCurrentGeometry();
            var versionBefore = _live.ResizeVersion;
            liveHeight = Math.Clamp(
                Math.Max(1, _live.LiveHeight),
                1,
                Math.Max(1, geometryBefore.Height));
            liveOrigin = EnsureRoom(
                appendRow,
                liveHeight,
                record,
                "recovery-live-region",
                geometryBefore.Height);
            var geometryAfter = _live.ReadCurrentGeometry();
            if (geometryBefore.Width != geometryAfter.Width
                || geometryBefore.Height != geometryAfter.Height
                || versionBefore != _live.ResizeVersion)
            {
                // EnsureRoom may have scrolled before the host published the
                // new geometry. Preserve that adjusted append row; querying
                // the cursor now would observe the bottom-row scroll cursor,
                // not the live boundary.
                appendRow = Math.Max(0, liveOrigin);
                continue;
            }

            recoveryWidth = Math.Max(1, geometryAfter.Width);
            liveHeight = Math.Clamp(
                liveHeight, 1, Math.Max(1, geometryAfter.Height));
            recoveryReserved = true;
            break;
        }

        if (!recoveryReserved)
        {
            throw new InvalidOperationException(
                "The terminal geometry kept changing while reserving the recovery region.");
        }

        var reanchorUpdate = _live.BeginAtomicTerminalUpdate();
        try
        {
            _live.ResizeLive(recoveryWidth, liveHeight);
            _live.ReanchorLive(
                liveOrigin,
                liveHeight,
                _live.SnapshotLiveSurface() ?? EmptySurface(recoveryWidth));
        }
        finally
        {
            reanchorUpdate.Dispose();
        }

        // Drop every frame the app queued while muted: the region's bookkeeping
        // just moved, so frames laid out for the superseded origin must never be
        // replayed. The pump is resumed only when this commit was the one that
        // muted it. The frame wait is bounded by the flow's own lifetime, so a
        // flow that is shutting down does not hold recovery open across the
        // whole timeout — and recovery never throws on cancellation, because it
        // runs while the commit is already unwinding an outcome.
        var discarded = _live.DiscardQueuedLiveOutput();
        if (!wasMuted)
        {
            _live.SetLiveOutputMuted(false);
        }

        record(
            $"recovery-reanchor reason={reason} units={completedUnits} abortedRows={abortedRows} " +
            $"liveOrigin={liveOrigin} liveHeight={liveHeight} width={recoveryWidth} " +
            $"discardedQueuedFrames={discarded} nextLiveApplied={nextLive is not null} " +
            $"liveFrameObserved={liveFrameObserved}");
        return liveOrigin;
    }

    /// <summary>
    /// Records the rows a stopped unit had already composed in the flow's reflow
    /// model, so the model carries exactly the rows the recovery reserves on
    /// screen.
    /// </summary>
    /// <remarks>
    /// The composed row count is exact. A row's written extent is not: the failing
    /// row may have been truncated or its hand-off rejected, and neither is
    /// observable from here, so the row's composed cells are recorded as the
    /// conservative upper bound. Recording less than that would end the model
    /// above rows that may be on screen, and a later resize settle would then
    /// anchor the live region over them.
    /// </remarks>
    private void RecordComposedRows(
        Surface? surface,
        int composedRows,
        Action<string> record,
        bool handoffAccepted)
    {
        if (!handoffAccepted || surface is null || composedRows <= 0)
        {
            return;
        }

        var rows = Math.Min(composedRows, Math.Max(1, surface.Height));
        if (rows >= surface.Height)
        {
            _live.RecordCommittedRows(surface);
        }
        else
        {
            var cropped = new Surface(surface.Width, rows);
            for (var row = 0; row < rows; row++)
            {
                for (var column = 0; column < surface.Width; column++)
                {
                    cropped.TrySetCell(column, row, surface.GetCell(column, row));
                }
            }

            _live.RecordCommittedRows(cropped);
        }

        record(
            $"partial-rows-recorded rows={rows} ofUnitRows={surface.Height} " +
            $"handoffAccepted={handoffAccepted} extentConservative={rows < surface.Height}");
    }

    /// <summary>
    /// The live region's height as it can actually exist in the terminal: never
    /// taller than the terminal, which is the clamp
    /// <see cref="ILiveStepHandle.ReanchorLive"/> applies when it paints.
    /// </summary>
    /// <remarks>
    /// The commit can observe a new terminal size before the step's own height
    /// bookkeeping catches up — the parent adapter reports the host's new
    /// geometry the moment the host reflows, while the resize event reaches the
    /// runner asynchronously. Reserving room for a region taller than the
    /// terminal would compute an origin the painting clamp rejects, leaving the
    /// append cursor above the screen and the next units stacked onto the bottom
    /// row; the native shrink leg is what caught that.
    /// </remarks>
    private int EffectiveLiveHeight()
        => Math.Clamp(Math.Max(1, _live.LiveHeight), 1, Math.Max(1, _live.TerminalHeight));

    /// <summary>
    /// Scrolls the viewport up until <paramref name="height"/> rows fit below
    /// <paramref name="appendRow"/>, returning the adjusted append row.
    /// </summary>
    private int EnsureRoom(
        int appendRow,
        int height,
        Action<string> record,
        string what,
        int? terminalHeightOverride = null)
    {
        var terminalHeight = Math.Max(1, terminalHeightOverride ?? _live.TerminalHeight);
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
        var liveHeight = EffectiveLiveHeight();
        var origin = EnsureRoom(appendRow, liveHeight, record, "live-region");
        var (width, _) = _live.ReadCurrentGeometry();
        _live.ReanchorLive(
            origin,
            liveHeight,
            _live.SnapshotLiveSurface() ?? EmptySurface(Math.Max(1, width)));
        return origin;
    }

    private async Task<int> ObserveBoundaryAsync(
        int fallbackRow, int rowOffset, CancellationToken cancellationToken)
    {
        if (!_live.SupportsCursorObservation)
        {
            return fallbackRow;
        }

        while (true)
        {
            var (width, height) = _live.ReadCurrentGeometry();
            var version = _live.ResizeVersion;
            var row = await _live.ObserveCursorRowAsync(cancellationToken).ConfigureAwait(false);
            if (row is null)
            {
                throw new IOException("The terminal did not report an authoritative history boundary.");
            }

            var after = _live.ReadCurrentGeometry();
            if (version == _live.ResizeVersion && width == after.Width && height == after.Height)
            {
                return Math.Max(0, row.Value + rowOffset);
            }
        }
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

        // Baseline 0 is exactly the readiness predicate: the guard above already
        // established that no frame has been rendered, so a fresh count sample
        // here would ask for a frame after a first frame that may have landed in
        // between, and would then time out on an app that rendered exactly once.
        var observed = await _live
            .WaitForLiveFrameAfterAsync(0, ReadinessTimeout, cancellationToken)
            .ConfigureAwait(false);
        if (!observed)
        {
            throw new InvalidOperationException(
                "The live step did not render a frame within the readiness window.");
        }
    }

    /// <summary>
    /// Emits one logical unit as contiguous logical rows, synchronously, inside
    /// the caller's atomic terminal update.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Row order per row is: position at the row, clear it, write the text, then
    /// terminate the line. Clearing before content means a row that fills the
    /// full width keeps its last cell, while a shorter row still removes
    /// whatever a previous frame left at the right margin.
    /// </para>
    /// <para>
    /// Synchronous by contract: the caller writes this and the live-region
    /// repaint as one atomic terminal update, and the drain wait that follows
    /// runs outside that scope. A fault or cancellation thrown from here leaves
    /// the rows already composed in that pending update; the scope's dispose is
    /// what hands it to the write path, so the recovery hand-off treats those
    /// rows as possibly-written rather than as flushed.
    /// </para>
    /// </remarks>
    private void EmitUnit(
        Action<string> record,
        int unitIndex,
        FlowCommitUnit unit,
        ref int unitRow,
        int faultThreshold,
        UnitEmission emission,
        CancellationToken cancellationToken)
    {
        var surface = unit.Surface;
        var height = Math.Max(1, surface.Height);
        var startRow = unitRow;

        for (var row = 0; row < height; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            unitRow = EnsureRoom(unitRow, 1, record, "unit");
            var rowText = SoftWrapEmitter.RenderRowText(surface, row);

            if (faultThreshold >= 0 && unitIndex == faultThreshold && row == 0)
            {
                // Deliberately compose a truncated row. Disposal still decides
                // whether those bytes were accepted by the adapter.
                var text = SoftWrapEmitter.OrderedRowPrefix + rowText;
                var partial = text.Length > 1 ? text[..^1] : text;
                _live.WriteTerminalAt(unitRow, partial);
                unitRow++;
                emission.RowsWritten++;
                emission.NextRowOffset = 1;
                throw new IOException(
                    $"injected history emission failure after {unitIndex} completed unit(s) " +
                    $"({FailAfterRowsVariable})");
            }

            if (row == height - 1)
            {
                // The marker is positioned on the final committed row and is
                // emitted before that row's bytes, matching the qualified
                // Ghostty native frame ordering.
                _live.MarkCommittedRow(unitRow);
            }

            // A hard LF clears a soft-wrap flag inherited from the former live
            // row; CUP and erasing its cells do not. At the bottom, EnsureRoom's
            // subsequent LF performs that reset while reserving the next row.
            var newline = unitRow < _live.TerminalHeight - 1;
            _live.WriteTerminalAt(
                unitRow,
                string.Concat(SoftWrapEmitter.OrderedRowPrefix, rowText, newline ? "\r\n" : string.Empty));
            unitRow++;
            emission.RowsWritten++;
            emission.NextRowOffset = newline ? 0 : 1;
        }

        record(
            $"unit {unitIndex} key={unit.RowKey ?? "-"} rows={height} " +
            $"at={startRow} width={surface.Width}");
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
    /// Where a commit was when it failed. A unit's rows are composed into one
    /// buffered atomic update, and that update's only hand-off to the write path
    /// is the scope's dispose — so a failure after composition finished is a
    /// failed hand-off, and a failure during composition leaves the composed rows
    /// to the disposal that follows.
    /// </summary>
    private enum EmissionPhase
    {
        /// <summary>Preparing or materializing the current unit; nothing of it composed yet.</summary>
        Preparing,

        /// <summary>Composing the current unit's rows and repaint into the pending update.</summary>
        Composing,

        /// <summary>The unit's bytes are in the buffer; the scope's dispose is handing them off.</summary>
        Handoff,

        /// <summary>Every unit was handed off; the commit is applying the live layout and repainting.</summary>
        Finalizing,
    }

    /// <summary>
    /// Per-unit emission progress: how many rows of the current unit were
    /// composed into its terminal update when a cancellation or failure aborted
    /// it, so the recovery hand-off can place the live region below them instead
    /// of painting over them.
    /// </summary>
    /// <remarks>
    /// This counts composed rows, not confirmed-flushed rows. Rows are composed
    /// into one buffered update and handed to the write path when the scope is
    /// disposed, so rows counted here have reached that update but not
    /// necessarily the adapter — the count is the conservative upper bound of
    /// what may be on screen, which is what the reservation needs.
    /// </remarks>
    private sealed class UnitEmission
    {
        /// <summary>Rows of the current unit already composed into its update.</summary>
        public int RowsWritten { get; set; }
        public int NextRowOffset { get; set; }

        /// <summary>Clears the tracker after each completed unit, so an aborted
        /// unit's row count never includes rows from earlier units.</summary>
        public void Reset()
        {
            RowsWritten = 0;
            NextRowOffset = 0;
        }
    }

    private static Surface EmptySurface(int width)
    {
        var surface = new Surface(width, 1);
        surface.TrySetCell(0, 0, SurfaceCells.Space(null, null));
        return surface;
    }
}
