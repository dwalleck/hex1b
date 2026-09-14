using System.Text;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Orchestrates a flow — consuming sequential steps (steps and full-screen apps)
/// and managing the visual stack of yield widgets in the normal terminal buffer.
/// </summary>
internal sealed class Hex1bFlowRunner
{
    private const string GhosttyCommitBoundaryMark = "\x1b]133;C\x07";
    private const string GhosttyLivePromptMark = "\x1b]133;P;k=i\x07";
    private readonly Func<Hex1bFlowContext, Task> _flowCallback;
    private readonly Hex1bFlowOptions _options;
    private readonly IHex1bAppTerminalWorkloadAdapter _parentAdapter;

    /// <summary>
    /// Current cursor row in the terminal buffer (0-based, relative to terminal top).
    /// Tracks where the next yield widget or step should be rendered. After a
    /// soft-wrap tombstone is emitted, this becomes the row immediately past
    /// the last emitted line (capped to the bottom of the viewport when the
    /// terminal had to scroll). After a resize on the soft-wrap path, this is
    /// re-anchored to the top of the bottom-aligned active-step region so the
    /// next tombstone (when the active step completes) lands directly in the
    /// step's place.
    /// </summary>
    private int _cursorRow;
    private long _resizeVersion;
    private int _lastGeometryWidth = int.MinValue;
    private int _lastGeometryHeight = int.MinValue;
    private readonly object _geometrySync = new();
    private long _anchorGeneration;

    /// <summary>
    /// The row at which the very first tombstone (or the active step, if no
    /// tombstones have been emitted yet) is anchored. Captured at flow start
    /// from <see cref="_cursorRow"/> and decremented whenever a tombstone
    /// emission triggers a pre-scroll (so the anchor tracks "where the top
    /// of the flow's content currently lives in the viewport"). Combined
    /// with the per-paragraph widths in <see cref="_emittedTombstones"/>,
    /// this lets the resize handler compute where the active step should
    /// land at any new width without round-tripping a CPR query.
    /// </summary>
    private int _initialRowOrigin;

    /// <summary>
    /// Per-tombstone records of paragraph widths (one inner list per
    /// emitted tombstone, one int per CR+LF-terminated paragraph it
    /// contained). The host terminal guarantees hard-newline-terminated
    /// paragraphs are never reflowed across paragraph boundaries, so the
    /// only thing we need to track to recompute on-screen layout at any
    /// width is the widths themselves. Used by the soft-wrap settle-mode
    /// resize handler.
    /// </summary>
    private readonly List<IReadOnlyList<int>> _emittedTombstones = new();

    // The currently active step, if any. Only one step may run at a time.
    private FlowStep? _activeStep;

    // Serializes every write to the parent terminal. The live app's frame pump,
    // the resize/reposition paths, tombstone emission, and continuous-history
    // commitment all write through it, so a cursor-positioning sequence and the
    // content it belongs to can never be split by another writer.
    private readonly object _terminalWriteLock = new();

    // Serializes step-level operations that move the live region and update its
    // bookkeeping, so the runner's resize handling and an in-flight history
    // commit cannot reposition the same region concurrently.
    private readonly object _stepOpsLock = new();

    // Atomic terminal-update scope. While one is open, the caller's writes are
    // composed into one serialized hand-off and optionally bracketed by DEC
    // synchronized-output mode 2026. The lock provides framework serialization;
    // it does not claim that a host will present the hand-off atomically.
    // The scope holds both step locks for its synchronous duration, so no other
    // writer can interleave inside a unit's update.
    private readonly StringBuilder _atomicUpdate = new(4096);
    private bool _atomicUpdateActive;
    private int _atomicUpdateThreadId;

    // Reused buffer for one forwarded live frame plus the park that leaves the
    // host cursor at the live region's top-left. Guarded by _terminalWriteLock,
    // the same lock as the write it feeds, so a frame is never composed while
    // another writer owns the parent terminal.
    private readonly StringBuilder _softWrapFrameBuffer = new(4096);

    // Optional JSONL file receiving framework-side commit events. The driver
    // writes its own app-side evidence; this is the framework's view of the
    // same commits, and both are observations rather than host history proof.
    // Read per record rather than cached at type load: a process that runs more
    // than one flow — a test host, or a harness that arms the variable around a
    // single commit — must be able to change the path between them.
    private static string? CommitEventLogPath =>
        Environment.GetEnvironmentVariable("HEX1B_FLOW_COMMIT_EVENTS");

    private static readonly object CommitEventLock = new();

    /// <summary>
    /// Writes under the terminal write lock, composing into the calling thread's
    /// atomic update when one is active.
    /// </summary>
    private void WriteTerminal(string text)
    {
        lock (_terminalWriteLock)
        {
            if (_atomicUpdateActive && Environment.CurrentManagedThreadId == _atomicUpdateThreadId)
            {
                _atomicUpdate.Append(text);
                return;
            }

            _parentAdapter.Write(text);
        }
    }

    /// <summary>
    /// Writes live-application output under the terminal write lock, re-checking
    /// the mute gate <em>inside</em> the lock.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The gate must be sampled while holding the lock. A frame that passed an
    /// unlocked check can be held behind the lock while a commit mutes,
    /// repositions the live region and unmutes; it would then be forwarded at a
    /// superseded origin — the stale-origin replay the mute exists to prevent.
    /// </para>
    /// <para>
    /// When <paramref name="parkAtOriginOf"/> is supplied the frame and the park
    /// it is followed by are composed into the <em>same</em> parent write: a
    /// separate positioning write could be split off by another writer, and the
    /// row is read inside the lock so a concurrent re-anchor cannot park the
    /// host cursor at an origin that has already been superseded.
    /// </para>
    /// </remarks>
    private void WriteTerminalUnlessMuted(
        string text,
        Func<bool>? isMuted,
        InlineStepAdapter? parkAtOriginOf = null,
        long? outputEpoch = null,
        InlineStepAdapter? generationSource = null)
    {
        lock (_terminalWriteLock)
        {
            if (isMuted?.Invoke() == true)
            {
                return;
            }
            if (generationSource is not null)
            {
                var (presentationWidth, presentationHeight) = ReadCurrentGeometry();
                if ((outputEpoch is { } frameEpoch
                        && frameEpoch != generationSource.OutputEpoch)
                    || presentationWidth != generationSource.Width
                    || presentationHeight != Math.Max(1, _parentAdapter.Height))
                {
                    // A native resize can be visible through the parent
                    // presentation before its resize event has reached the
                    // inline adapter. Hold the old frame until the resize
                    // handler publishes matching render dimensions.
                    return;
                }

                var expectedStepHeight = _activeStep is { StepHeight: > 0 } activeStep
                    ? Math.Min(activeStep.StepHeight, presentationHeight)
                    : generationSource.Height;
                if (generationSource.Height != expectedStepHeight)
                {
                    // The presentation may have published a new height while
                    // the app's resize event is still queued. Do not forward a
                    // frame from the old live rectangle.
                    return;
                }
            }

            if (parkAtOriginOf is null)
            {
                _parentAdapter.Write(text);
                return;
            }

            // Leave the host cursor at the live region's top-left, so an
            // observation made while the pump is muted measures the live boundary
            // instead of wherever the app's last row ended. One write, one buffer.
            var terminalHeight = ReadCurrentGeometry().Height;
            var row = Math.Clamp(
                parkAtOriginOf.RowOrigin,
                0,
                Math.Max(0, terminalHeight - 1));
            _softWrapFrameBuffer.Clear();
            _softWrapFrameBuffer.Append(text);
            _softWrapFrameBuffer.Append("\x1b[").Append(row + 1).Append(";1H");
            _parentAdapter.Write(_softWrapFrameBuffer.ToString());
        }
    }

    /// <summary>
    /// Positions at an absolute terminal row and writes, atomically under the
    /// terminal write lock, so the cursor can never be moved away from the row
    /// between positioning and content. Inside an atomic update owned by this
    /// thread the pair is appended to that update's single terminal write.
    /// </summary>
    private void WriteTerminalAt(int row, string text) => WriteTerminalUpdate(row, text);

    /// <summary>
    /// Moves the terminal's cursor to the start of an absolute row without
    /// writing content.
    /// </summary>
    private void SetTerminalCursorRow(int row) => WriteTerminalUpdate(row, text: null);

    /// <summary>
    /// The single writer for live-step terminal bytes: positions the cursor at
    /// <paramref name="row"/> and writes <paramref name="text"/>, or only moves
    /// the cursor when <paramref name="text"/> is null.
    /// </summary>
    /// <remarks>
    /// Inside an atomic update owned by the calling thread the update is
    /// appended to the scope's buffer; otherwise it goes to the parent terminal
    /// under the write lock. The buffered form emits the same cursor-position
    /// bytes the adapter would (<c>ESC[row;1H</c>).
    /// </remarks>
    private void WriteTerminalUpdate(int row, string? text)
    {
        var terminalHeight = ReadCurrentGeometry().Height;
        var clamped = Math.Clamp(row, 0, Math.Max(0, terminalHeight - 1));

        if (_atomicUpdateActive && Environment.CurrentManagedThreadId == _atomicUpdateThreadId)
        {
            _atomicUpdate.Append("\x1b[").Append(clamped + 1).Append(";1H");
            if (!string.IsNullOrEmpty(text))
            {
                _atomicUpdate.Append(text);
            }
            return;
        }

        lock (_terminalWriteLock)
        {
            _parentAdapter.SetCursorPosition(0, clamped);
            if (!string.IsNullOrEmpty(text))
            {
                _parentAdapter.Write(text);
            }
        }
    }

    /// <summary>
    /// Reads the presentation's current geometry when this workload is attached
    /// to one, refreshing the adapter's event-delivered dimensions first.
    /// </summary>
    private (int Width, int Height) ReadCurrentGeometry()
    {
        var cachedWidth = _parentAdapter.Width;
        var cachedHeight = _parentAdapter.Height;
        var geometry = _parentAdapter is IFlowCurrentGeometrySource source
            ? source.ReadCurrentGeometry()
            : (Width: cachedWidth, Height: cachedHeight);

        var width = Math.Max(1, geometry.Width);
        var height = Math.Max(1, geometry.Height);
        lock (_geometrySync)
        {
            var previousWidth = _lastGeometryWidth;
            var previousHeight = _lastGeometryHeight;
            if (previousWidth == int.MinValue || previousHeight == int.MinValue)
            {
                _lastGeometryWidth = width;
                _lastGeometryHeight = height;
            }
            else if (width != previousWidth || height != previousHeight)
            {
                // A native presentation may apply its resize before the
                // workload's Hex1bResizeEvent arrives. Treat that observation
                // as a resize boundary exactly once, so pending units are
                // rechecked.
                _lastGeometryWidth = width;
                _lastGeometryHeight = height;
                Interlocked.Increment(ref _resizeVersion);
            }
        }

        return (width, height);
    }
    private void RecordResizeDimensions(int width, int height)
    {
        lock (_geometrySync)
        {
            _lastGeometryWidth = Math.Max(1, width);
            _lastGeometryHeight = Math.Max(1, height);
        }
    }

    /// <summary>
    /// Refreshes geometry after the atomic terminal-update locks are held.
    /// Callers must perform this check before appending any positioning or
    /// content bytes to the scope.
    /// </summary>
    private (int Width, int Height, long ResizeVersion) ReadFreshGeometryForEmission()
    {
        var (width, height) = ReadCurrentGeometry();
        return (width, height, Interlocked.Read(ref _resizeVersion));
    }

    private void EnsureHistoryCommitSupported()
    {
        var provider = _options.HostProfileProvider;
        if (provider is null)
        {
            // Headless and custom adapters without a native profile provider
            // retain the existing deterministic/cursor-observation path.
            return;
        }

        switch (provider())
        {
            case FlowTerminalHostProfile.Ghostty_1_3_1:
            case FlowTerminalHostProfile.WindowsConsole:
                return;
            case FlowTerminalHostProfile.Ghostty_Unqualified:
                throw new NotSupportedException(
                    "Continuous history commitment requires the qualified Ghostty 1.3.1 " +
                    "XTVERSION profile; this Ghostty build is not allowlisted.");
            default:
                throw new NotSupportedException(
                    "Continuous history commitment requires a completed, qualified native " +
                    "terminal profile; the host did not identify as a supported profile.");
        }
    }

    private bool UseOsc133PromptMarks =>
        _options.HostProfileProvider?.Invoke() == FlowTerminalHostProfile.Ghostty_1_3_1;
    private void MarkCommittedRow(int row)
    {
        if (UseOsc133PromptMarks)
        {
            WriteTerminalUpdate(row, GhosttyCommitBoundaryMark);
        }
    }

    /// <summary>
    /// Opens an atomic terminal-update scope. Every write the caller issues
    /// through this runner on the same thread while the scope is open is
    /// composed into ONE terminal write, bracketed by synchronized output
    /// (DEC mode 2026), so a host that honors mode 2026 never paints the
    /// intermediate state — and no other writer can interleave between the rows
    /// that displace the live region and the repaint that restores it.
    /// </summary>
    /// <remarks>
    /// The scope is synchronous by contract: nothing inside it may await, and it
    /// must be disposed on the thread that opened it. Both step-level locks are
    /// held for its duration; the writes it contains are a bounded number of
    /// escape sequences, so the hold is short.
    /// </remarks>
    private IAtomicTerminalUpdate BeginAtomicTerminalUpdate()
    {
        Monitor.Enter(_stepOpsLock);
        Monitor.Enter(_terminalWriteLock);
        _atomicUpdate.Clear();
        _atomicUpdateThreadId = Environment.CurrentManagedThreadId;
        _atomicUpdateActive = true;
        return new AtomicTerminalUpdateScope(this);
    }

    /// <summary>
    /// Closes an atomic update scope, handing its composed bytes to the parent
    /// terminal as one write.
    /// </summary>
    /// <returns>
    /// Whether the hand-off completed. A throw from the write propagates after
    /// both locks are released, leaving the caller's scope reporting
    /// <see cref="IAtomicTerminalUpdate.FlushCompleted"/> as false.
    /// </returns>
    private bool EndAtomicTerminalUpdate()
    {
        try
        {
            if (_atomicUpdate.Length > 0)
            {
                // One write for the whole update. Terminals that ignore mode
                // 2026 ignore both bracket sequences and see the same bytes.
                _atomicUpdate.Insert(0, SyncUpdateBegin);
                _atomicUpdate.Append(SyncUpdateEnd);
                var payload = _atomicUpdate.ToString();
                _atomicUpdate.Clear();

                // The real workload adapter rejects a hand-off to a disposed or
                // closed channel instead of silently dropping it, so a unit that
                // never reached the terminal is reported as a failed flush
                // rather than counted as emitted. Test adapters keep the
                // interface's best-effort write.
                if (_parentAdapter is Hex1bAppWorkloadAdapter app)
                {
                    app.WriteRequired(payload);
                }
                else
                {
                    _parentAdapter.Write(payload);
                }
            }

            return true;
        }
        finally
        {
            _atomicUpdateActive = false;
            Monitor.Exit(_terminalWriteLock);
            Monitor.Exit(_stepOpsLock);
        }
    }

    /// <summary>
    /// Admission state shared by one live step's output pump, resize machinery
    /// and commit-preparation hook.
    /// </summary>
    /// <remarks>
    /// A class rather than a set of captured locals because three independent
    /// closures read it: the output pump's gate, the resize settle path, and the
    /// handle the commit calls into.
    /// </remarks>
    private sealed class LiveAdmissionState
    {
        /// <summary>
        /// True while the live pump may forward frames. Cleared when a commit is
        /// admitted — even before the commit's own mute lands — and set again when
        /// the commit resumes the pump.
        /// </summary>
        public bool ResumeGranted = true;

        /// <summary>
        /// True while the mute in force belongs to commit admission rather than to
        /// the caller, so <c>SetLiveOutputMuted(true)</c> reports the caller's
        /// ownership instead of echoing the commit's own mute back at it.
        /// </summary>
        public bool MuteTaken;

        /// <summary>Mute ownership from immediately before admission took the pump.</summary>
        public bool PriorMute;
    }

    /// <summary>
    /// How many times a cursor observation is re-taken when a resize lands while
    /// it is in flight. A row observed against a geometry that has since changed
    /// is not an anchor for the current geometry.
    /// </summary>
    private const int CursorObservationAttempts = 4;

    /// <summary>
    /// How long a settled soft-wrap resize waits for the app's frame at the new
    /// geometry before repainting from whatever surface it has.
    /// </summary>
    private static readonly TimeSpan ResizeRenderTimeout = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// True when the parent can authoritatively report the host's own cursor row.
    /// </summary>
    private bool SupportsCursorObservation =>
        _parentAdapter is Hex1bAppWorkloadAdapter appAdapter
            ? appAdapter.HasCursorSource
            : _parentAdapter is ICursorPositionSource;

    /// <summary>
    /// True when the resize and admission paths anchor to an observed host row.
    /// Both halves are required: the parent must be able to report its cursor,
    /// and the live output must be soft-wrap emission — every forwarded soft-wrap
    /// frame ends parked at the live region's top-left, which is what makes the
    /// observed row the region's own row. Cell-positioned (legacy) emission does
    /// not leave that park, so it keeps anchoring to the step's tracked origin.
    /// </summary>
    private bool UsesObservedCursorOrigin =>
        SupportsCursorObservation && _options.UseSoftWrapTombstones;

    /// <summary>
    /// The host's cursor row, or null when the parent cannot authoritatively
    /// report it. Never a fallback: null means "unknown", not row zero.
    /// </summary>
    private async Task<int?> ObserveCursorRowAsync(CancellationToken cancellationToken)
    {
        if (_parentAdapter is not ICursorPositionSource source)
        {
            return null;
        }

        var position = await source.ObserveCursorPositionAsync(cancellationToken).ConfigureAwait(false);
        return position?.Row;
    }

    /// <summary>
    /// Observes the host cursor row and retries while a resize lands during the
    /// observation, so the returned row describes the geometry in force when it
    /// was taken.
    /// </summary>
    private async Task<int?> ObserveStableCursorRowAsync(CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < CursorObservationAttempts; attempt++)
        {
            var (width, height) = ReadCurrentGeometry();
            var version = Interlocked.Read(ref _resizeVersion);

            var observed = await ObserveCursorRowAsync(cancellationToken).ConfigureAwait(false);
            if (observed is null)
            {
                return null;
            }

            var after = ReadCurrentGeometry();
            if (Interlocked.Read(ref _resizeVersion) == version
                && after.Width == width
                && after.Height == height)
            {
                return observed;
            }

            Trace($"cursor observation discarded: geometry changed while observing (attempt {attempt + 1})");
        }

        Trace($"cursor observation gave up after {CursorObservationAttempts} attempts: geometry kept changing");
        return null;
    }

    /// <summary>
    /// Reserves room below <paramref name="observedRow"/> for a region
    /// <paramref name="height"/> rows tall, scrolling the viewport only when the
    /// region would not otherwise fit, and rebases the scalar model onto the
    /// resulting anchor.
    /// </summary>
    /// <remarks>
    /// Only rows <em>below</em> the observed anchor are ever scrolled or
    /// cleared: everything above it is the host's reflowed history, and the host
    /// owns it. Callers hold <c>_stepOpsLock</c>.
    /// </remarks>
    private int ReserveBelowObservedAnchor(int observedRow, int height)
    {
        var terminalHeight = ReadCurrentGeometry().Height;
        var overflow = (observedRow + height) - terminalHeight;
        if (overflow > 0)
        {
            _parentAdapter.SetCursorPosition(0, terminalHeight - 1);
            for (var i = 0; i < overflow; i++)
            {
                WriteTerminal("\n");
            }

            observedRow = Math.Max(0, observedRow - overflow);
        }

        RebaseModelToObservedOrigin(observedRow);
        return observedRow;
    }

    /// <summary>
    /// Rebases the scalar reflow model onto an observed row, so later model
    /// computations agree with where the host says the live region is.
    /// </summary>
    /// <remarks>
    /// The observation is authoritative and the model is not: the model cannot see
    /// a reflow that moved a row and moved it back. Rebasing keeps the model's
    /// <em>relative</em> tracking (which committed rows did to the region) while
    /// taking the host's absolute row as the truth.
    /// </remarks>
    private void RebaseModelToObservedOrigin(int observedRow)
    {
        var width = ReadCurrentGeometry().Width;
        var modelRow = FlowResizeMath.ComputeRowOriginAtWidth(
            _initialRowOrigin, _emittedTombstones, width);
        var delta = observedRow - modelRow;
        _initialRowOrigin += delta;
        _cursorRow = observedRow;
        Trace(
            $"observed origin {observedRow} rebases model {modelRow} -> " +
            $"initialRowOrigin={_initialRowOrigin} width={width} delta={delta}");
    }

    /// <summary>
    /// Atomically moves the live region to <paramref name="rowOrigin"/> and
    /// resizes it to <paramref name="liveHeight"/>, then repaints it as one
    /// serialized pass: blank every row, paint <paramref name="liveSurface"/>,
    /// and leave the cursor at the region's top-left. Callers hold
    /// <c>_stepOpsLock</c>.
    /// </summary>
    /// <returns>The clamped origin and height actually painted.</returns>
    private (int RowOrigin, int LiveHeight) ReanchorLiveRegion(
        InlineStepAdapter stepAdapter,
        int rowOrigin,
        int liveHeight,
        Surface liveSurface)
    {
        var terminalHeight = ReadCurrentGeometry().Height;
        liveHeight = Math.Clamp(liveHeight, 1, terminalHeight);
        rowOrigin = Math.Clamp(rowOrigin, 0, Math.Max(0, terminalHeight - liveHeight));
        Interlocked.Increment(ref _anchorGeneration);

        // Bookkeeping first: the app's next frame is laid out for the new
        // origin/height, and a resize computation reads these.
        _cursorRow = rowOrigin;
        stepAdapter.RowOrigin = rowOrigin;

        lock (_terminalWriteLock)
        {
            try
            {
                // This is a replaceable live image, not a history paragraph.
                // Its snapshot may still have the pre-resize width: clipping
                // that frame is safe, wrapping it into scrollback is not.
                WriteTerminal(UseOsc133PromptMarks ? "\x1b[?7h" : "\x1b[?7l");

                // Blank before painting. A hard LF also clears persistent
                // soft-wrap metadata, which EL alone leaves behind. Never send
                // it at the bottom: that row is reset by the next reservation's
                // LF, without introducing an extra scroll during repaint.
                for (var row = 0; row < liveHeight; row++)
                {
                    var absolute = rowOrigin + row;
                    if (absolute < 0 || absolute >= terminalHeight) continue;
                    WriteTerminalUpdate(
                        absolute,
                        absolute < terminalHeight - 1 ? "\x1b[2K\r\n" : "\x1b[2K");
                }

                // Qualified Ghostty uses the prompt mark at the live boundary;
                // the mark must precede the first live row in this repaint.
                if (UseOsc133PromptMarks)
                {
                    WriteTerminalUpdate(rowOrigin, GhosttyLivePromptMark);
                }

                var paintRows = Math.Min(liveHeight, Math.Max(1, liveSurface.Height));
                for (var row = 0; row < paintRows; row++)
                {
                    var absolute = rowOrigin + row;
                    if (absolute < 0 || absolute >= terminalHeight) continue;
                    WriteTerminalUpdate(
                        absolute,
                        SoftWrapEmitter.OrderedRowPrefix + SoftWrapEmitter.RenderRowText(liveSurface, row));
                }

                SetTerminalCursorRow(rowOrigin);
            }
            finally
            {
                // History emission must retain its wrapping semantics, including
                // when composing a repaint fails partway through an update.
                WriteTerminal("\x1b[?7h");
            }
        }

        return (rowOrigin, liveHeight);
    }

    /// <summary>
    /// Waits until <paramref name="app"/> has completed a frame newer than
    /// <paramref name="afterFrame"/>. Subscribes before the first check so a frame
    /// landing in between is not missed; returns false on timeout or cancellation.
    /// </summary>
    private static async Task<bool> WaitForFrameAfterAsync(
        Hex1bApp app,
        long afterFrame,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        void OnFrameRendered()
        {
            if (app.FrameCount > afterFrame)
            {
                completion.TrySetResult(true);
            }
        }

        app.FrameRendered += OnFrameRendered;
        try
        {
            if (app.FrameCount > afterFrame)
            {
                return true;
            }

            using var timeoutCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            var delay = Task.Delay(timeout, timeoutCts.Token);
            var finished = await Task.WhenAny(completion.Task, delay).ConfigureAwait(false);
            if (finished == completion.Task)
            {
                await timeoutCts.CancelAsync().ConfigureAwait(false);
                return true;
            }

            return false;
        }
        finally
        {
            app.FrameRendered -= OnFrameRendered;
        }
    }

    private sealed class AtomicTerminalUpdateScope(Hex1bFlowRunner runner) : IAtomicTerminalUpdate
    {
        private bool _disposed;

        /// <summary>
        /// True once the adapter accepted the scope's composed bytes. Acceptance
        /// is not proof the host consumed them — only that the hand-off was
        /// taken, which is the fact the exception alone cannot carry.
        /// </summary>
        public bool FlushCompleted { get; private set; }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Assigned only when the hand-off returns: if it throws, this stays
            // false and Dispose rethrows, so a caller catching that exception
            // can still read it.
            FlushCompleted = runner.EndAtomicTerminalUpdate();
        }
    }

    /// <summary>
    /// Scrolls the viewport up by <paramref name="rows"/> rows by parking the
    /// cursor on the bottom row and emitting that many linefeeds. Each linefeed
    /// at the bottom row pushes the top row into the host's scrollback. The
    /// scrollback itself is never cleared or replayed.
    /// </summary>
    private void ScrollViewportUp(int rows)
    {
        if (rows <= 0) return;
        var terminalHeight = ReadCurrentGeometry().Height;
        var sb = new StringBuilder(rows);
        for (var i = 0; i < rows; i++)
        {
            sb.Append('\n');
        }
        WriteTerminalAt(terminalHeight - 1, sb.ToString());
    }

    /// <summary>
    /// Appends one framework-side commit event to the JSONL evidence file when
    /// <c>HEX1B_FLOW_COMMIT_EVENTS</c> names a writable path. Best-effort: a
    /// logging failure never disturbs the flow.
    /// </summary>
    private static void RecordCommitEvent(string message)
    {
        var path = CommitEventLogPath;
        if (string.IsNullOrEmpty(path)) return;
        lock (CommitEventLock)
        {
            try
            {
                var line = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{{\"ts\":{Environment.TickCount64},\"kind\":\"commit\",\"source\":\"hex1b\",\"detail\":\"{EscapeJson(message)}\"}}{Environment.NewLine}");
                File.AppendAllText(path, line);
            }
            catch
            {
                // Evidence logging is best-effort only.
            }
        }
    }

    /// <summary>
    /// Minimal JSON string escaping for evidence records. Hand-rolled rather
    /// than using <c>JsonSerializer</c>, which the library cannot use under
    /// trimming/AOT without a source-generated context.
    /// </summary>
    private static string EscapeJson(string value)
    {
        var sb = new StringBuilder(value.Length + 8);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ')
                    {
                        sb.Append("\\u");
                        sb.Append(((int)ch).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        sb.Append(ch);
                    }
                    break;
            }
        }
        return sb.ToString();
    }

    // CancellationToken from RunAsync, surfaced to flow callbacks via Hex1bFlowContext.
    private CancellationToken _cancellationToken;

    // DEC private mode 2026 (Synchronized Update Mode). Wrapping the
    // resize-time clear-and-redraw in BSU/ESU lets supporting terminals
    // present the whole repaint as a single atomic frame, eliminating any
    // brief blank flash between clearing the old step region and the step
    // app re-rendering into the new one. Terminals that don't recognise
    // mode 2026 ignore both sequences.
    private const string SyncUpdateBegin = "\x1b[?2026h";
    private const string SyncUpdateEnd = "\x1b[?2026l";

    // Diagnostic trace gated on the HEX1B_FLOW_TRACE environment variable.
    // When set to a writable file path, every interesting state transition
    // (tombstone emission, pre-scroll, resize handling) is appended to
    // that file. Used to investigate visual artefacts like duplicate
    // tombstones on resize. The path is read once at process start.
    private static readonly string? TraceLogPath = Environment.GetEnvironmentVariable("HEX1B_FLOW_TRACE");
    private static readonly object TraceLock = new();
    private static int _resizeCounter;
    private static int _emitCounter;

    // Always callable; becomes a near-zero-cost no-op when the env var is
    // unset (one nullable field read, one early return). Not gated on
    // [Conditional("DEBUG")] so users can capture traces from a Release-mode
    // build of FlowDemo without a special rebuild.
    private static void Trace(string message)
    {
        var path = TraceLogPath;
        if (string.IsNullOrEmpty(path)) return;
        lock (TraceLock)
        {
            try
            {
                File.AppendAllText(path, $"[{DateTime.UtcNow:HH:mm:ss.fff}] {message}{Environment.NewLine}");
            }
            catch
            {
                // Diagnostic logging is best-effort; never let a trace write
                // disrupt the flow.
            }
        }
    }

    public Hex1bFlowRunner(
        Func<Hex1bFlowContext, Task> flowCallback,
        Hex1bFlowOptions options,
        IHex1bAppTerminalWorkloadAdapter parentAdapter)
    {
        _flowCallback = flowCallback;
        _options = options;
        _parentAdapter = parentAdapter;
    }

    /// <summary>
    /// Gets the cancellation token from the outer flow runner.
    /// </summary>
    internal CancellationToken CancellationToken => _cancellationToken;

    /// <summary>
    /// Gets the terminal width in columns.
    /// </summary>
    internal int TerminalWidth => ReadCurrentGeometry().Width;

    /// <summary>
    /// Gets the terminal height in rows.
    /// </summary>
    internal int TerminalHeight => ReadCurrentGeometry().Height;

    /// <summary>
    /// Gets the number of rows available from the current cursor position
    /// to the bottom of the terminal (before any scrolling would occur).
    /// </summary>
    internal int AvailableHeight => Math.Max(0, ReadCurrentGeometry().Height - _cursorRow);

    /// <summary>
    /// Runs the entire flow from start to finish.
    /// </summary>
    public async Task RunAsync(CancellationToken ct)
    {
        _cancellationToken = ct;

        // Query the current cursor position using the host terminal's
        // synchronous cursor API (when available). Falls back to
        // InitialCursorRow or 0 when no live query is wired.
        _cursorRow = await QueryCursorRowAsync(ct) ?? _options.InitialCursorRow ?? 0;
        _initialRowOrigin = _cursorRow;
        _emittedTombstones.Clear();

        var initialGeometry = ReadCurrentGeometry();
        Trace($"RunAsync start: termSize={initialGeometry.Width}x{initialGeometry.Height} cursorRow={_cursorRow} useSoftWrap={_options.UseSoftWrapTombstones}");

        var context = new Hex1bFlowContext(this);
        await _flowCallback(context);

        Trace($"RunAsync end: cursorRow={_cursorRow}");

        // After flow completes, position cursor below the last yield widget
        _parentAdapter.SetCursorPosition(0, _cursorRow);
        WriteTerminal("\x1b[?25h"); // Ensure cursor is visible
    }

    /// <summary>
    /// Renders a static widget as frozen terminal output and advances the cursor.
    /// No interactive step is created — this is a fire-and-forget render.
    /// </summary>
    internal async Task RenderStaticAsync(Func<RootContext, Task<Hex1bWidget>> builder)
    {
        var terminalWidth = ReadCurrentGeometry().Width;
        var terminalHeight = ReadCurrentGeometry().Height;

        // Measure the content to determine how much space it needs
        var contentHeight = await MeasureYieldHeightAsync(builder, terminalWidth, terminalHeight);
        if (contentHeight < 1) contentHeight = 1;

        // Scroll if needed to make room
        var overflow = (_cursorRow + contentHeight) - terminalHeight;
        if (overflow > 0)
        {
            _parentAdapter.SetCursorPosition(0, terminalHeight - 1);
            for (int i = 0; i < overflow; i++)
            {
                WriteTerminal("\n");
            }
            _cursorRow -= overflow;
        }

        // Clear and render
        ClearRegion(_cursorRow, contentHeight);
        if (_options.UseSoftWrapTombstones)
        {
            // Render the static content into a surface and emit it as
            // soft-wrap-friendly logical lines so the host terminal owns
            // the reflow/scroll behaviour for the lifetime of the flow.
            var surface = await RenderToSurfaceAsync(builder, terminalWidth, contentHeight);
            if (surface is not null)
            {
                EmitSoftWrapTombstone(surface);
                return;
            }
            // Fall through to legacy path if surface rendering failed.
        }

        var renderedHeight = await RenderYieldWidgetAsync(builder, terminalWidth, contentHeight);
        _cursorRow += renderedHeight;
    }

    /// <summary>
    /// Starts an inline step and returns a <see cref="FlowStep"/> handle for
    /// controlling it. The step runs on a background task; use the handle to
    /// invalidate, complete, and await the step.
    /// </summary>
    internal FlowStep StartStep(
        Func<FlowStepContext, Task<Hex1bWidget>> builder,
        Hex1bFlowStepOptions? options)
    {
        if (_activeStep != null)
            throw new InvalidOperationException(
                "A step is already active. Call Complete() and await the current step before starting a new one.");

        var currentGeometry = ReadCurrentGeometry();
        var terminalWidth = currentGeometry.Width;
        var terminalHeight = currentGeometry.Height;

        var maxHeight = Math.Min(options?.MaxHeight ?? terminalHeight, terminalHeight);
        if (maxHeight < 1) maxHeight = 1;

        // Pre-measure the widget to determine actual content height
        var step = new FlowStep(terminalWidth, terminalHeight, maxHeight);
        var contentHeight = MeasureStepContent(builder, step, terminalWidth, maxHeight);

        // MinHeight gives the live region a stable, reliably interactive
        // allocation from the first frame instead of letting it grow with the
        // body, so a short initial body is not mistaken for a smaller region.
        // Clamped to the host terminal height and to MaxHeight so it can never
        // allocate off-screen. (The previously disproved FixedHeight workaround
        // padded the *content*; this sets the region allocation.)
        var minHeight = Math.Clamp(options?.MinHeight ?? 0, 0, maxHeight);
        var desiredHeight = Math.Clamp(Math.Max(contentHeight, minHeight), 1, maxHeight);
        step.StepHeight = desiredHeight;

        _activeStep = step;

        // Start the step lifecycle on a background task
        _ = RunStepLifecycleAsync(step, builder, options, desiredHeight);

        return step;
    }

    /// <summary>
    /// Measures the content height of a step's widget tree by building and measuring
    /// the widget without rendering it.
    /// </summary>
    private int MeasureStepContent(
        Func<FlowStepContext, Task<Hex1bWidget>> builder,
        FlowStep step,
        int width,
        int maxHeight)
    {
        try
        {
            var stepCtx = new FlowStepContext(step);
            var widgetTask = builder(stepCtx);
            // Synchronous fallback for the measurement pass — see the
            // RenderToSurface invariant for the rationale.
            if (!widgetTask.IsCompletedSuccessfully) return maxHeight;
            var widget = widgetTask.Result;
            if (widget == null) return maxHeight;

            // Reconcile the widget into a node tree and measure it
            var reconcileCtx = ReconcileContext.CreateRoot();
            var nodeTask = widget.ReconcileAsync(null, reconcileCtx);
            // ReconcileAsync should complete synchronously for simple widgets
            if (!nodeTask.IsCompleted)
                return maxHeight; // Can't measure async widgets, use max

            var node = nodeTask.Result;
            if (node == null) return maxHeight;

            var constraints = new Layout.Constraints(0, width, 0, maxHeight);
            var measured = node.Measure(constraints);
            return Math.Max(1, measured.Height);
        }
        catch
        {
            // If measurement fails, fall back to maxHeight
            return maxHeight;
        }
    }

    private async Task RunStepLifecycleAsync(
        FlowStep step,
        Func<FlowStepContext, Task<Hex1bWidget>> builder,
        Hex1bFlowStepOptions? options,
        int desiredHeight)
    {
        try
        {
            var (terminalWidth, terminalHeight) = ReadCurrentGeometry();

            // Track the row origin for this step (may be updated on resize)
            var startRowOrigin = _cursorRow;

            // Scroll the terminal if the cursor is too far down to fit the step
            var overflow = (startRowOrigin + desiredHeight) - terminalHeight;
            if (overflow > 0)
            {
                _parentAdapter.SetCursorPosition(0, terminalHeight - 1);
                for (int i = 0; i < overflow; i++)
                {
                    WriteTerminal("\n");
                }
                startRowOrigin -= overflow;
                _cursorRow = startRowOrigin;
            }

            // Clear the step region
            ClearRegion(startRowOrigin, desiredHeight);

            // Create the inline adapter for this step
            var stepEnableMouse = options?.EnableMouse ?? false;
            var stepCapabilities = _parentAdapter.Capabilities;
            if (stepEnableMouse && !stepCapabilities.SupportsMouse)
            {
                stepCapabilities = stepCapabilities with { SupportsMouse = true };
            }

            using var stepAdapter = new InlineStepAdapter(
                terminalWidth, desiredHeight, startRowOrigin,
                stepCapabilities);

            if (UseOsc133PromptMarks)
            {
                // Establish the live prompt boundary before the first frame
                // enters the output pump; later reanchors refresh this mark.
                WriteTerminalUpdate(startRowOrigin, GhosttyLivePromptMark);
            }

            var appOptions = new Hex1bAppOptions
            {
                WorkloadAdapter = stepAdapter,
                EnableMouse = options?.EnableMouse ?? false,
                EnableDefaultCtrlCExit = true,
                // On the soft-wrap path the active step is rendered as
                // logical lines (text + ESC[K + CR+LF) instead of as a
                // CUP-positioned cell diff. This makes the step content
                // reflowable by the host terminal alongside any
                // tombstones above it, so a horizontal resize doesn't
                // leave wrap-spillover ghost cells around the new step
                // region.
                UseSoftWrapEmission = _options.UseSoftWrapTombstones,
            };

            if (_options.Theme != null)
            {
                appOptions.Theme = _options.Theme;
            }

            // Pump output from step adapter to parent adapter. The pump is
            // muted for the duration of a resize burst (see resize handler
            // below) — without it, the inner Hex1bApp's continuous frame
            // emission (glow animations, focus blink, etc.) lands at the
            // stale rowOrigin/oldHeight and scrolls the buffer up via the
            // CR+LF row terminators inside each frame.
            using var outputPumpCts = new CancellationTokenSource();
            var outputMuteGate = new System.Runtime.CompilerServices.StrongBox<bool>(false);

            // Set once the app and its commit coordinator exist. The resize
            // handler reads it so a resize that lands during an in-flight
            // history commit updates geometry without fighting the commit for
            // the live region's origin.
            var commitCoordinatorBox =
                new System.Runtime.CompilerServices.StrongBox<FlowCommitCoordinator?>(null);

            // Set once the live app exists, so the settle path can wait for a
            // frame rendered at the settled geometry before it repaints.
            var appBox = new System.Runtime.CompilerServices.StrongBox<Hex1bApp?>(null);

            // Admission state shared with the commit's preparation hook: the
            // frame gate, the mute ownership admission takes, and the app whose
            // frames a settle waits for.
            var admission = new LiveAdmissionState();
            var outputPumpTask = PumpStepOutputAsync(
                stepAdapter,
                outputPumpCts.Token,
                isMuted: () => System.Threading.Volatile.Read(ref outputMuteGate.Value)
                    || (System.Threading.Volatile.Read(ref commitCoordinatorBox.Value)?.IsCommitInFlight == true
                        && !System.Threading.Volatile.Read(ref admission.ResumeGranted)),
                parkCursorAtLiveOrigin: _options.UseSoftWrapTombstones);

            // Pump input from parent adapter to step adapter, with resize handling
            using var inputPumpCts = new CancellationTokenSource();

            // Settle state for the new debounced resize path. Captured by
            // both the per-event handler and the timer callback. The lock
            // protects every write the resize machinery makes to the parent
            // adapter so a settle timer firing on the threadpool cannot
            // interleave with a track-and-clear pass running on the pump
            // loop.
            var settleSync = new object();
            CancellationTokenSource? settleTimerCts = null;
            (int Width, int Height)? settleOriginalDims = null;
            (int Width, int Height) settleLatestDims = default;
            var resizeTaskSync = new object();
            var resizeRepaintTasks = new List<Task>();
            var resizeObservationGate = new SemaphoreSlim(1, 1);
            long resizeGeneration = 0;
            // Mute-gate value from immediately before the burst muted the pump,
            // so the settle pass (and admission) can restore exactly what the
            // burst transiently overrode instead of clobbering another owner.
            var settlePreBurstMute = false;
            var (lastKnownWidth, lastKnownHeight) = ReadCurrentGeometry();

            // Admission hook for continuous-history commitment. The coordinator
            // calls this through <see cref="ILiveStepHandle"/> after a commit is
            // admitted and before it samples any geometry. On success the live
            // pump is muted and the anchor the commit reads is the host's own
            // observed row for a host that can report one. A failed observation
            // leaves output muted: restoring an unobserved row could erase history.
            Func<CancellationToken, Task<bool>> prepareForCommitAsync = async cancellationToken =>
            {
                // Taking the step-ops lock waits out a settle pass that is
                // already executing: every settle mutation holds it. Holding it
                // is also what makes the per-event handler's in-lock admission
                // re-check effective — either that handler ran first and armed a
                // timer this method then cancels, or it observes the admission
                // and only updates geometry.
                bool priorMute;
                lock (_stepOpsLock)
                {
                    lock (settleSync)
                    {
                        // Drop live frames until the commit resumes the pump:
                        // a frame forwarded before the commit's own handling
                        // would move the cursor the anchor below records.
                        System.Threading.Volatile.Write(ref admission.ResumeGranted, false);

                        // Cancel the armed settle so its clear/scroll/repaint
                        // pass can never run against output the commit appends.
                        settleTimerCts?.Cancel();
                        settleTimerCts = null;

                        var burstWasActive = settleOriginalDims is not null;
                        settleOriginalDims = null;

                        if (burstWasActive)
                        {
                            // The settle pass that would have restored the drag's
                            // transient terminal state was just cancelled, so do
                            // it here: autowrap back on (oversized rows must wrap,
                            // not truncate), cursor visible, the burst's
                            // transient mute returned to whatever it was before
                            // the burst, and the pending geometry pushed to the
                            // app so it is laid out for the size the host already
                            // has rather than the pre-resize one.
                            WriteTerminal("\x1b[?7h");
                            WriteTerminal("\x1b[?25h");
                            _ = stepAdapter.ResizeAsync(
                                Math.Max(1, settleLatestDims.Width),
                                Math.Max(1, FlowResizeMath.ComputeStepHeight(
                                    options?.MaxHeight, settleLatestDims.Height)));
                            System.Threading.Volatile.Write(ref outputMuteGate.Value, settlePreBurstMute);

                            // The burst's mute was transient and has just been
                            // released, so the ownership to report is the pump's
                            // state before the burst — reporting the burst's own
                            // mute would leave the pump muted forever.
                            priorMute = settlePreBurstMute;
                        }
                        else
                        {
                            priorMute = System.Threading.Volatile.Read(ref outputMuteGate.Value);
                        }

                        // The commit holds the pump for its whole duration. The
                        // prior ownership is reported back so the caller can
                        // return the pump to its real owner, and recorded here so
                        // a caller that mutes through the handle anyway still
                        // receives that prior ownership instead of the commit's
                        // own mute.
                        System.Threading.Volatile.Write(ref outputMuteGate.Value, true);
                        System.Threading.Volatile.Write(ref admission.PriorMute, priorMute);
                        System.Threading.Volatile.Write(ref admission.MuteTaken, true);
                    }
                }

                if (!UsesObservedCursorOrigin)
                {
                    // A synthetic adapter cannot report the host's own position,
                    // so the step's tracked origin stays the anchor and the cursor
                    // is parked there.
                    lock (_stepOpsLock)
                    {
                        lock (settleSync)
                        {
                            SetTerminalCursorRow(stepAdapter.RowOrigin);
                        }
                    }

                    return priorMute;
                }

                int? observed;
                try
                {
                    // Nothing may CUP or home the cursor before this: the park
                    // that every forwarded soft-wrap frame ends with is the
                    // anchor, and overwriting it with a tracked row would replace
                    // the host's answer with the model's.
                    observed = await ObserveStableCursorRowAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    commitCoordinatorBox.Value?.Suspend(ex);
                    throw;
                }

                if (observed is null)
                {
                    // No authoritative anchor, and the flow's scalar reflow model
                    // is not a substitute: it cannot see a reflow that moved a row
                    // and moved it back. Keep the pump muted — nothing may repaint
                    // over content whose position cannot be stated — and suspend
                    // the step's commitment instead of committing from a guess.
                    throw SuspendOnMissingObservation("commit admission");
                }

                lock (_stepOpsLock)
                {
                    lock (settleSync)
                    {
                        RebaseModelToObservedOrigin(observed.Value);
                        stepAdapter.RowOrigin = observed.Value;
                        Interlocked.Increment(ref _anchorGeneration);
                    }
                }

                return priorMute;
            };

            // True while a commit is admitted and running. Read without taking a
            // lock: it is a volatile flag on the coordinator the runner publishes.
            bool CommitInFlightNow() =>
                System.Threading.Volatile.Read(ref commitCoordinatorBox.Value)?.IsCommitInFlight == true;

            // A host that cannot say where the live region is leaves nothing to
            // anchor against: the flow's scalar reflow model is not a substitute,
            // because it cannot see a reflow that moved a row and moved it back.
            // Suspends the step's commitment and leaves the pump muted rather than
            // painting over content whose position cannot be stated.
            InvalidOperationException SuspendOnMissingObservation(string what)
            {
                var failure = new InvalidOperationException(
                    $"The host did not report a cursor row for the {what}, and the flow's scalar reflow " +
                    "model is not a substitute for an authoritative observation.");
                Trace($"{what}: no authoritative cursor observation; suspending commitment");
                try
                {
                    commitCoordinatorBox.Value?.Suspend(failure);
                }
                catch (Exception ex)
                {
                    Trace($"{what}: suspend threw {ex.GetType().Name}: {ex.Message}");
                }

                return failure;
            }
            async Task RepaintLiveAfterResizeAsync(long generation, CancellationToken resizeToken)
            {
                await resizeObservationGate.WaitAsync(resizeToken).ConfigureAwait(false);
                try
                {
                    if (generation != Interlocked.Read(ref resizeGeneration))
                        return;

                    var app = appBox.Value;
                    if (app is not null)
                    {
                        // The resize event and an explicit Invalidate may both
                        // be queued behind this callback. Do not snapshot until
                        // the app has completed a frame after this repaint
                        // request; otherwise the immediate hand-off can repaint
                        // the old prompt before the new frame reaches the pump.
                        var frameBefore = app.FrameCount;
                        app.Invalidate();
                        var frameObserved = await WaitForFrameAfterAsync(
                            app, frameBefore, ResizeRenderTimeout, resizeToken)
                            .ConfigureAwait(false);
                        resizeToken.ThrowIfCancellationRequested();
                        if (!frameObserved)
                        {
                            throw new TimeoutException(
                                "The resized live layout did not render before repaint.");
                        }
                    }

                    for (var attempt = 0; attempt < 3; attempt++)
                    {
                        resizeToken.ThrowIfCancellationRequested();
                        // Commit admission owns the live region. A resize
                        // repaint must not write (or move the host cursor)
                        // while the committer is between its admission and
                        // final re-anchor; the commit's own geometry fence
                        // will incorporate the resize instead.
                        if (CommitInFlightNow())
                            return;

                        var observedAnchorGeneration =
                            Interlocked.Read(ref _anchorGeneration);
                        int? observed = null;
                        if (UsesObservedCursorOrigin)
                        {
                            observed = await ObserveStableCursorRowAsync(resizeToken)
                                .ConfigureAwait(false);
                            if (observed is null)
                                throw SuspendOnMissingObservation("resize repaint");
                        }

                        resizeToken.ThrowIfCancellationRequested();
                        var retry = false;
                        lock (_stepOpsLock)
                        {
                            if (generation != Interlocked.Read(ref resizeGeneration))
                                return;
                            // Admission can begin after the observation
                            // above but before this lock. Do not repaint
                            // from a row captured outside the commit.
                            if (CommitInFlightNow())
                                return;
                            if (observedAnchorGeneration !=
                                Interlocked.Read(ref _anchorGeneration))
                            {
                                retry = true;
                            }
                            else
                            {
                                var (width, terminalHeight) = ReadCurrentGeometry();
                                var liveHeight = FlowResizeMath.ComputeStepHeight(
                                    options?.MaxHeight, terminalHeight);
                                var anchor = CommitInFlightNow()
                                    ? Math.Clamp(
                                        stepAdapter.RowOrigin,
                                        0,
                                        Math.Max(0, terminalHeight - liveHeight))
                                    : observed is { } observedRow
                                        ? ReserveBelowObservedAnchor(observedRow, liveHeight)
                                        : FlowResizeMath.ComputeRowOriginAtWidth(
                                            _initialRowOrigin, _emittedTombstones, width);
                                anchor = Math.Clamp(
                                    anchor,
                                    0,
                                    Math.Max(0, terminalHeight - liveHeight));

                                desiredHeight = liveHeight;
                                step.StepHeight = liveHeight;
                                step.TerminalWidth = width;
                                stepAdapter.RowOrigin = anchor;
                                if (stepAdapter.Width != width
                                    || stepAdapter.Height != liveHeight)
                                {
                                    _ = stepAdapter.ResizeAsync(width, liveHeight);
                                }

                                // Keep positioning, clearing, prompt mark and
                                // surface bytes in one serialized hand-off.
                                var scope = BeginAtomicTerminalUpdate();
                                try
                                {
                                    var surface = appBox.Value?.SnapshotCurrentSurface();
                                    if (surface is not null)
                                    {
                                        var painted = ReanchorLiveRegion(
                                            stepAdapter, anchor, liveHeight, surface);
                                        stepAdapter.RowOrigin = painted.RowOrigin;
                                        step.StepHeight = painted.LiveHeight;
                                        desiredHeight = painted.LiveHeight;
                                    }
                                    else
                                    {
                                        SetTerminalCursorRow(anchor);
                                    }
                                }
                                finally
                                {
                                    scope.Dispose();
                                }

                            }
                        }

                        if (!retry)
                            return;
                    }

                    throw new InvalidOperationException(
                        "Resize repaint could not establish a stable live anchor.");
                }
                finally
                {
                    resizeObservationGate.Release();
                }
            }

            async Task CompleteResizeBurstAsync(long generation, CancellationToken settleTokenIn)
            {
                await RepaintLiveAfterResizeAsync(generation, settleTokenIn)
                    .ConfigureAwait(false);

                lock (_stepOpsLock)
                {
                    lock (settleSync)
                    {
                        if (settleTokenIn.IsCancellationRequested
                            || generation != Interlocked.Read(ref resizeGeneration)
                            || CommitInFlightNow())
                            return;

                        System.Threading.Volatile.Write(
                            ref outputMuteGate.Value, settlePreBurstMute);
                        settleOriginalDims = null;
                        settleTimerCts = null;
                        Trace("settle(repaint): resumed live output");
                    }
                }
            }

            void TrackResizeTask(Task task)
            {
                lock (resizeTaskSync)
                {
                    resizeRepaintTasks.Add(task);
                }
            }

            var inputPumpTask = PumpStepInputAsync(stepAdapter, inputPumpCts.Token,
                onResize: (newWidth, newHeight) =>
                {
                    RecordResizeDimensions(newWidth, newHeight);
                    Interlocked.Increment(ref _resizeVersion);
                    var generation = Interlocked.Increment(ref resizeGeneration);
                    var newStepHeight = FlowResizeMath.ComputeStepHeight(options?.MaxHeight, newHeight);
                    var useSettle = _options.UseSoftWrapTombstones
                        && _options.ResizeSettleDelay is not null;

                    // Publish the event-delivered geometry before scheduling
                    // any repaint, and mute old frames while the new surface is
                    // composed. The native source is still re-read by the
                    // repaint and commit emission fences.
                    CancellationToken settleToken = default;
                    lock (_stepOpsLock)
                    {
                        lock (settleSync)
                        {
                            if (settleOriginalDims is null)
                            {
                                settlePreBurstMute =
                                    System.Threading.Volatile.Read(ref outputMuteGate.Value);
                                settleOriginalDims = (lastKnownWidth, lastKnownHeight);
                            }

                            settleLatestDims = (newWidth, newHeight);
                            desiredHeight = newStepHeight;
                            step.StepHeight = Math.Max(1, newStepHeight);
                            step.TerminalWidth = Math.Max(1, newWidth);
                            lastKnownWidth = newWidth;
                            lastKnownHeight = newHeight;
                            System.Threading.Volatile.Write(ref outputMuteGate.Value, true);
                            _ = stepAdapter.ResizeAsync(
                                Math.Max(1, newWidth), Math.Max(1, newStepHeight));

                            // ResizeSettleDelay remains a quiet-period cleanup,
                            // not a visibility debounce. Re-arm only cleanup;
                            // immediate repaints use the independent generation.
                            if (useSettle)
                            {
                                settleTimerCts?.Cancel();
                                settleTimerCts?.Dispose();
                                settleTimerCts = CancellationTokenSource.CreateLinkedTokenSource(
                                    inputPumpCts.Token);
                                settleToken = settleTimerCts.Token;
                            }
                        }
                    }

                    var repaintTask = Task.Run(async () =>
                    {
                        try
                        {
                            await RepaintLiveAfterResizeAsync(
                                generation, inputPumpCts.Token).ConfigureAwait(false);
                            var requestFreshFrame = false;
                            lock (_stepOpsLock)
                            {
                                lock (settleSync)
                                {
                                    if (generation == Interlocked.Read(ref resizeGeneration)
                                        && !CommitInFlightNow())
                                    {
                                        // The immediate repaint is the ownership
                                        // hand-off for this generation. Keep
                                        // current-generation frames queued so
                                        // an edit that lands after the snapshot
                                        // can patch the freshly painted region.
                                        System.Threading.Volatile.Write(
                                            ref outputMuteGate.Value, settlePreBurstMute);
                                        if (!useSettle)
                                        {
                                            settleOriginalDims = null;
                                        }

                                        requestFreshFrame = true;
                                    }
                                }
                            }

                            // Close the snapshot-to-unmute window with a fresh
                            // render request. The guarded hand-off above has
                            // already released the transient mute; this request
                            // is intentionally after it, so it cannot be lost
                            // to a freshness-blind queue drain.
                            if (requestFreshFrame)
                            {
                                appBox.Value?.Invalidate();
                            }
                        }
                        catch (OperationCanceledException) when (inputPumpCts.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            commitCoordinatorBox.Value?.Suspend(ex);
                            Trace($"resize(repaint) failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                    TrackResizeTask(repaintTask);

                    if (!useSettle) return;

                    // Captured while holding settleSync so admission cannot
                    // dispose the source before this task starts.
                    var delay = _options.ResizeSettleDelay!.Value;
                    var cleanupTask = Task.Run(async () =>
                    {
                        try
                        {
                            await Task.Delay(delay, settleToken).ConfigureAwait(false);
                            await CompleteResizeBurstAsync(generation, settleToken)
                                .ConfigureAwait(false);
                        }
                        catch (OperationCanceledException) when (settleToken.IsCancellationRequested) { }
                        catch (Exception ex)
                        {
                            commitCoordinatorBox.Value?.Suspend(ex);
                            Trace($"settle(repaint) failed: {ex.GetType().Name}: {ex.Message}");
                        }
                    });
                    TrackResizeTask(cleanupTask);
                });

            try
            {
                // Wrap the user's builder to inject the FlowStepContext
                var stepCtx = new FlowStepContext(step);
                await using var app = new Hex1bApp(rootCtx =>
                                                        {
                                                            stepCtx.CancellationToken = rootCtx.CancellationToken;
                                                            return builder(stepCtx);
                                                        }, appOptions);
                step.SetApp(app);
                System.Threading.Volatile.Write(ref appBox.Value, app);

                // Continuous-history commitment: this step keeps running while
                // finalized content is appended to native history above it.
                var liveHandle = new LiveStepHandle(
                    this, step, stepAdapter, app, stepCtx, outputMuteGate, admission, prepareForCommitAsync);
                var commitCoordinator = new FlowCommitCoordinator(
                    liveHandle, _parentAdapter, _cancellationToken);
                step.AttachCommitCoordinator(commitCoordinator);
                System.Threading.Volatile.Write(ref commitCoordinatorBox.Value, commitCoordinator);

                // Publish the live app as the diagnostic tree provider on the
                // terminal-facing adapter while the step runs, so the active
                // prompt's real widget tree can be inspected. An inline step's
                // own adapter is not a Hex1bAppWorkloadAdapter, so the app's
                // own registration cannot reach the host; this is the existing
                // seam, not an additional renderer.
                Hex1bAppWorkloadAdapter? diagnosticHost = null;
                Diagnostics.IDiagnosticTreeProvider? previousDiagnosticProvider = null;
                if (_parentAdapter is Hex1bAppWorkloadAdapter parentWorkloadAdapter)
                {
                    diagnosticHost = parentWorkloadAdapter;
                    previousDiagnosticProvider = parentWorkloadAdapter.DiagnosticTreeProvider;
                    parentWorkloadAdapter.DiagnosticTreeProvider = app;
                }

                try
                {
                    await app.RunAsync(default);
                }
                finally
                {
                    // Restore only if the app is still the registered provider,
                    // so a later owner is never clobbered.
                    if (diagnosticHost is not null
                        && ReferenceEquals(diagnosticHost.DiagnosticTreeProvider, app))
                    {
                        diagnosticHost.DiagnosticTreeProvider = previousDiagnosticProvider;
                    }
                }
            }
            finally
            {
                outputPumpCts.Cancel();
                inputPumpCts.Cancel();

                try { await outputPumpTask; } catch (OperationCanceledException) { }
                try { await inputPumpTask; } catch (OperationCanceledException) { }
                Task[] pendingResizeTasks;
                lock (resizeTaskSync)
                {
                    pendingResizeTasks = resizeRepaintTasks.ToArray();
                }

                try
                {
                    await Task.WhenAll(pendingResizeTasks).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (inputPumpCts.IsCancellationRequested) { }

                settleTimerCts?.Cancel();
                settleTimerCts?.Dispose();
                resizeObservationGate.Dispose();
            }

            // Clear the step region so remnants don't show through the yield widget
            ClearRegion(_cursorRow, desiredHeight);

            // Render the completed widget as frozen output
            var completedBuilder = step.CompletedBuilder;
            if (completedBuilder != null)
            {
                if (_options.UseSoftWrapTombstones)
                {
                    var surface = RenderToSurface(completedBuilder, terminalWidth, desiredHeight);
                    if (surface is not null)
                    {
                        EmitSoftWrapTombstone(surface);
                        Trace("Step completed: tombstone emitted (append-only, terminal owns reflow)");
                    }
                    else
                    {
                        // Fall back to the legacy path if surface rendering failed.
                        var completedHeight = await RenderYieldWidgetAsync(completedBuilder, terminalWidth, desiredHeight);
                        _cursorRow += completedHeight;
                    }
                }
                else
                {
                    var completedHeight = await RenderYieldWidgetAsync(completedBuilder, terminalWidth, desiredHeight);
                    _cursorRow += completedHeight;
                }
            }

            _activeStep = null;
            step.SetCompleted();
        }
        catch (Exception ex)
        {
            _activeStep = null;
            step.SetFaulted(ex);
        }
    }

    /// <summary>
    /// Runs a full-screen TUI application in the alternate screen buffer.
    /// </summary>
    internal async Task RunFullScreenStepAsync(
        Func<Hex1bApp, Hex1bAppOptions, Func<RootContext, Task<Hex1bWidget>>> configure)
    {
        // The parent adapter handles alt-buffer transitions naturally
        // We create a standard Hex1bApp with the parent adapter
        var appOptions = new Hex1bAppOptions
        {
            WorkloadAdapter = _parentAdapter,
            EnableMouse = _options.EnableMouse,
        };

        if (_options.Theme != null)
        {
            appOptions.Theme = _options.Theme;
        }

        Hex1bApp? app = null;
        Func<RootContext, Task<Hex1bWidget>>? widgetBuilder = null;
        bool configureInvoked = false;

        Func<RootContext, Task<Hex1bWidget>> wrappedBuilder = ctx =>
        {
            if (!configureInvoked)
            {
                configureInvoked = true;
                widgetBuilder = configure(app!, appOptions);
            }
            return widgetBuilder!(ctx);
        };

        app = new Hex1bApp(wrappedBuilder, appOptions);
        await using (app)
        {
            await app.RunAsync(default);
        }

        // After returning from full-screen, the terminal restores the normal buffer
        // which already contains the frozen yield output. No re-rendering needed.
    }

    /// <summary>
    /// Renders a yield widget and returns its height.
    /// If the content exceeds the available screen space, it is rendered in
    /// pages with the terminal scrolling between each page so no content is lost.
    /// </summary>
    private async Task<int> RenderYieldWidgetAsync(
        Func<RootContext, Task<Hex1bWidget>> yieldBuilder,
        int width,
        int maxHeight)
    {
        // First, measure the yield widget to determine its natural height.
        var measuredHeight = await MeasureYieldHeightAsync(yieldBuilder, width, maxHeight * 10);
        if (measuredHeight < 1) measuredHeight = 1;

        // If it fits in one screen, render in place
        if (measuredHeight <= maxHeight)
        {
            await RenderYieldPageAsync(yieldBuilder, width, measuredHeight);
            return measuredHeight;
        }

        // Content overflows the screen — render in pages.
        // We render the full content into a tall adapter, then write it page by page
        // to the terminal, scrolling between pages.
        var totalRendered = 0;
        var terminalHeight = ReadCurrentGeometry().Height;
        var remainingLines = measuredHeight;

        while (remainingLines > 0)
        {
            var pageHeight = Math.Min(remainingLines, terminalHeight);

            // Scroll to make room for this page
            var overflow = (_cursorRow + pageHeight) - terminalHeight;
            if (overflow > 0)
            {
                _parentAdapter.SetCursorPosition(0, terminalHeight - 1);
                for (int i = 0; i < overflow; i++)
                    WriteTerminal("\n");
                _cursorRow -= overflow;
            }

            // Clear the page region
            ClearRegion(_cursorRow, pageHeight);

            // Render a step of the yield content at the current offset
            int offset = totalRendered;
            await RenderYieldPageAsync(async ctx =>
            {
                // Build a wrapper that skips the first 'offset' rows and takes 'pageHeight'
                var fullWidget = await yieldBuilder(ctx);
                return fullWidget;
            }, width, pageHeight, offset);

            _cursorRow += pageHeight;
            totalRendered += pageHeight;
            remainingLines -= pageHeight;
        }

        return totalRendered;
    }

    /// <summary>
    /// Measures the natural height of a yield widget tree. Async sibling that awaits the builder
    /// properly; falls back to height 1 if the builder or reconciliation isn't synchronous.
    /// </summary>
    private async Task<int> MeasureYieldHeightAsync(Func<RootContext, Task<Hex1bWidget>> yieldBuilder, int width, int maxHeight)
    {
        try
        {
            var rootCtx = new RootContext();
            var widget = await yieldBuilder(rootCtx);
            if (widget == null) return 1;

            var reconcileCtx = ReconcileContext.CreateRoot();
            var node = await widget.ReconcileAsync(null, reconcileCtx);
            if (node == null) return 1;

            var constraints = new Constraints(0, width, 0, maxHeight);
            var measured = node.Measure(constraints);
            return Math.Max(1, measured.Height);
        }
        catch
        {
            return 1;
        }
    }

    /// <summary>
    /// Renders a yield widget page at the current cursor position.
    /// </summary>
    private async Task RenderYieldPageAsync(
        Func<RootContext, Task<Hex1bWidget>> yieldBuilder,
        int width,
        int height,
        int skipRows = 0)
    {
        Func<RootContext, Task<Hex1bWidget>> actualBuilder;
        if (skipRows > 0)
        {
            actualBuilder = async ctx =>
            {
                var widget = await yieldBuilder(ctx);
                if (widget is VStackWidget vstack && skipRows < vstack.Children.Count)
                {
                    var remaining = vstack.Children.Skip(skipRows).Take(height).ToArray();
                    return new VStackWidget(remaining);
                }
                return widget;
            };
        }
        else
        {
            actualBuilder = yieldBuilder;
        }

        using var yieldAdapter = new InlineStepAdapter(
            width, height, _cursorRow,
            _parentAdapter.Capabilities);

        var yieldOptions = new Hex1bAppOptions
        {
            WorkloadAdapter = yieldAdapter,
            EnableMouse = false,
            EnableDefaultCtrlCExit = false,
        };

        if (_options.Theme != null)
            yieldOptions.Theme = _options.Theme;

        var pumpCts = new CancellationTokenSource();
        var pumpTask = PumpStepOutputAsync(yieldAdapter, pumpCts.Token);

        try
        {
            Hex1bApp? yieldApp = null;
            bool rendered = false;

            yieldApp = new Hex1bApp(ctx =>
            {
                var widgetTask = actualBuilder(ctx);
                if (!rendered)
                {
                    rendered = true;
                    _ = Task.Run(async () =>
                    {
                        await Task.Delay(50);
                        yieldApp?.RequestStop();
                    });
                }
                return widgetTask;
            }, yieldOptions);

            await using (yieldApp)
            {
                await yieldApp.RunAsync(default);
            }
        }
        finally
        {
            pumpCts.Cancel();
            try { await pumpTask; } catch (OperationCanceledException) { }
        }
    }

    /// <summary>
    /// Clears a region of the terminal at the given row origin.
    /// </summary>
    private void ClearRegion(int rowOrigin, int height)
    {
        var sb = new StringBuilder();
        for (int row = 0; row < height; row++)
        {
            sb.Append($"\x1b[{rowOrigin + row + 1};1H");
            sb.Append("\x1b[2K");
        }
        WriteTerminal(sb.ToString());
    }

    private Surface? RenderToSurface(
        Func<RootContext, Hex1bWidget> builder,
        int width,
        int maxHeight)
        => RenderToSurface(ctx => Task.FromResult(builder(ctx)), width, maxHeight);

    /// <summary>
    /// Renders an async widget builder into a freshly-allocated <see cref="Surface"/>
    /// synchronously. Returns <c>null</c> if the builder Task hasn't already completed,
    /// if reconciliation needs to go async, or if anything throws — callers should
    /// fall back to the legacy emission path in that case.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Used by call sites that can't await (the per-event resize-burst handler and
    /// the settle-task body, both of which run inside <c>lock(settleSync)</c>).
    /// The "must already be completed" invariant mirrors the existing constraint
    /// on <see cref="Hex1bWidget.ReconcileAsync"/>: flow tombstones build widgets
    /// from pre-resolved state and wrap them with <see cref="Task.FromResult{TResult}(TResult)"/>,
    /// so the Task is completed inline on the caller's thread. Truly-async builders
    /// (network IO etc.) simply skip a resize frame and the next event re-triggers.
    /// </para>
    /// <para>
    /// Used only on the soft-wrap tombstone path
    /// (<see cref="Hex1bFlowOptions.UseSoftWrapTombstones"/>). The surface is sized
    /// to <paramref name="width"/> by the widget's measured height (clamped to
    /// <paramref name="maxHeight"/> × 10 to bound page-by-page content). The surface
    /// is then arranged and rendered using the standard rendering pipeline so any
    /// widget that works on screen will work here.
    /// </para>
    /// </remarks>
    private Surface? RenderToSurface(
        Func<RootContext, Task<Hex1bWidget>> builder,
        int width,
        int maxHeight)
    {
        try
        {
            var rootCtx = new RootContext();
            var widgetTask = builder(rootCtx);
            if (!widgetTask.IsCompletedSuccessfully) return null;
            var widget = widgetTask.Result;
            if (widget == null) return null;

            var reconcileCtx = ReconcileContext.CreateRoot();
            var nodeTask = widget.ReconcileAsync(null, reconcileCtx);
            // Reconciliation should complete synchronously for the widgets
            // used in flow tombstones today; if it doesn't, defer to the
            // legacy renderer which has its own measurement fallback.
            if (!nodeTask.IsCompleted) return null;

            var node = nodeTask.Result;
            if (node == null) return null;

            return MaterializeSurface(node, width, maxHeight);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Async sibling of <see cref="RenderToSurface(Func{RootContext, Task{Hex1bWidget}}, int, int)"/>
    /// for callers that can <c>await</c> the builder (and reconciliation) properly.
    /// Used by the static/yield/completed-tombstone paths that already run in
    /// <c>async Task</c> methods.
    /// </summary>
    private async Task<Surface?> RenderToSurfaceAsync(
        Func<RootContext, Task<Hex1bWidget>> builder,
        int width,
        int maxHeight)
    {
        try
        {
            var rootCtx = new RootContext();
            var widget = await builder(rootCtx);
            if (widget == null) return null;

            var reconcileCtx = ReconcileContext.CreateRoot();
            var node = await widget.ReconcileAsync(null, reconcileCtx);
            if (node == null) return null;

            return MaterializeSurface(node, width, maxHeight);
        }
        catch
        {
            return null;
        }
    }

    private Surface? MaterializeSurface(Hex1bNode node, int width, int maxHeight)
    {
        // Measure with a generous height bound so multi-line tombstones
        // get their full height, then clamp to a safety ceiling so a
        // misbehaving widget can't allocate an unbounded surface.
        var measureMax = Math.Max(maxHeight * 10, maxHeight);
        var constraints = new Constraints(0, width, 0, measureMax);
        node.SetTerminalCapabilities(_parentAdapter.Capabilities);
        var measured = node.Measure(constraints);
        var height = Math.Max(1, Math.Min(measured.Height, measureMax));

        var surface = new Surface(width, height);
        node.Arrange(new Rect(0, 0, width, height));

        var renderCtx = new SurfaceRenderContext(surface, _options.Theme);
        renderCtx.SetCapabilities(_parentAdapter.Capabilities);
        node.Render(renderCtx);

        return surface;
    }

    /// <summary>
    /// Emits the contents of <paramref name="surface"/> as a tombstone via
    /// <see cref="SoftWrapEmitter"/>, pre-scrolling the terminal as needed so
    /// the emission fits in the visible area, and updating <see cref="_cursorRow"/>
    /// to point at the row directly below the tombstone.
    /// </summary>
    private void EmitSoftWrapTombstone(Surface surface)
    {
        var emitId = Interlocked.Increment(ref _emitCounter);
        var height = surface.Height;
        var terminalHeight = ReadCurrentGeometry().Height;

        Trace($"EmitSoftWrapTombstone[#{emitId}] enter: surfaceSize={surface.Width}x{height} cursorRow={_cursorRow} termH={terminalHeight}");

        // Pre-scroll the viewport if there isn't enough room below the cursor
        // for the tombstone. We use the same trick as the legacy paths
        // (writing newlines at the bottom row) which causes the terminal to
        // scroll the existing content up — including any older tombstones,
        // which is the desired behaviour.
        var overflow = (_cursorRow + height) - terminalHeight;
        if (overflow > 0)
        {
            _parentAdapter.SetCursorPosition(0, terminalHeight - 1);
            for (int i = 0; i < overflow; i++)
            {
                WriteTerminal("\n");
            }
            _cursorRow -= overflow;
            // The viewport scrolled up by `overflow` rows, so every
            // previously-emitted tombstone (and the initial anchor) moved up
            // by the same amount on screen. Track that shift so
            // ComputeRowOriginAtWidth keeps returning the correct on-screen
            // row for the active step after a future resize.
            _initialRowOrigin -= overflow;
            Trace($"EmitSoftWrapTombstone[#{emitId}] pre-scroll: overflow={overflow} -> cursorRow={_cursorRow} initialRowOrigin={_initialRowOrigin}");
        }

        // Position the cursor at the row where the tombstone should land.
        _parentAdapter.SetCursorPosition(0, _cursorRow);

        SoftWrapEmitter.Emit(surface, _parentAdapter);

        // Capture this tombstone's per-paragraph widths so the soft-wrap
        // settle-mode resize handler can recompute the active step's
        // row origin at any width. Each surface row corresponds to one
        // CR+LF-terminated paragraph (the SoftWrapEmitter contract); the
        // logical width is the column index of the last non-blank cell
        // plus one. Empty rows count as zero-width paragraphs and still
        // occupy one display row on reflow (the Math.Max(1, ...) in
        // ComputeRowOriginAtWidth handles that).
        var paragraphWidths = new int[height];
        for (var row = 0; row < height; row++)
        {
            paragraphWidths[row] = MeasureSurfaceRowWidth(surface, row);
        }
        _emittedTombstones.Add(paragraphWidths);

        // The emitter terminates rows 0 .. height-2 with CR + LF (each
        // advances the cursor down one row, with no scrolling because we
        // pre-scrolled above to guarantee the last row lands at or above the
        // bottom of the viewport). The final row deliberately has no trailing
        // newline so emitting a tombstone at the very bottom does not scroll
        // the content one row up — visually the tombstone freezes in place
        // exactly where the step was. The terminal cursor therefore ends up
        // at (last-row-content-column, _cursorRow + height - 1); for our
        // bookkeeping we want _cursorRow to point at the row immediately
        // *below* the last visible tombstone row, so the next render lands
        // there cleanly.
        _cursorRow += height;

        Trace($"EmitSoftWrapTombstone[#{emitId}] exit: cursorRow={_cursorRow}");
    }

    /// <summary>
    /// Returns the logical paragraph width of the given surface row — the
    /// column index of the last non-blank cell plus one. Mirrors the
    /// trailing-blank trimming that <see cref="SoftWrapEmitter"/> performs
    /// when it emits the row, so the recorded paragraph width matches the
    /// number of cells the host terminal will actually have to reflow.
    /// </summary>
    private static int MeasureSurfaceRowWidth(Surface surface, int row)
    {
        var width = surface.Width;
        for (var x = width - 1; x >= 0; x--)
        {
            var cell = surface.GetCell(x, row);
            if (cell.IsContinuation)
            {
                // A continuation cell means the wide glyph occupies (x-1, x);
                // treat (x) as content because removing only the continuation
                // would mis-report the wide character's footprint.
                return x + 1;
            }
            if (cell.Character != " "
                && cell.Character != string.Empty
                && cell.Character != SurfaceCells.UnwrittenMarker)
            {
                return x + 1;
            }
        }
        return 0;
    }

    /// <summary>
    /// Pushes the entire current viewport contents up off the top of the
    /// screen and into the host terminal's scrollback buffer, then clears
    /// the viewport. Used by the soft-wrap resize handler so the active
    /// step (which uses absolute cursor positioning) can be cleanly
    /// re-anchored at row 0 without competing with reflowed tombstone
    /// content from the previous viewport.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Tombstones above the active step were emitted as soft-wrap-friendly
    /// logical lines, so once they are scrolled into scrollback the host
    /// terminal renders them with proper word wrap at the new width.
    /// Cell-positioned step content cannot reflow that way, so we just
    /// scroll it away and let the step app repaint at the new size.
    /// </para>
    /// <para>
    /// Mechanism: park cursor at the bottom row, write <paramref name="newHeight"/>
    /// linefeeds. Each LF at the bottom row scrolls the viewport up by one
    /// row (pushing the top row into scrollback), so writing a full
    /// viewport-height worth of LFs guarantees every previously-visible
    /// row ends up in scrollback. Then position cursor at home and clear
    /// from there to end of screen — this leaves a fully blank viewport
    /// with cursor at (0,0), ready for the step to re-render.
    /// </para>
    /// <para>
    /// Bracketed in DEC private mode 2026 (Synchronized Update Mode) so
    /// supporting terminals present the scroll+clear as one atomic frame.
    /// Terminals that ignore mode 2026 see a brief flash but no
    /// functional regression.
    /// </para>
    /// </remarks>
    private void ScrollViewportToScrollback(int newHeight)
    {
        var resizeId = Interlocked.Increment(ref _resizeCounter);
        Trace($"ScrollViewportToScrollback[#{resizeId}] enter: newHeight={newHeight}");

        WriteTerminal(SyncUpdateBegin);
        try
        {
            // Park at bottom-left and emit one LF per viewport row. Each
            // LF at the bottom scrolls the viewport up by one row, moving
            // the top line into scrollback. After newHeight LFs every
            // previously-visible row has been pushed into scrollback.
            var sb = new StringBuilder(newHeight + 16);
            for (int i = 0; i < newHeight; i++)
            {
                sb.Append('\n');
            }
            WriteTerminalAt(newHeight - 1, sb.ToString());

            // Home + clear-to-end-of-screen. After scrolling, the cursor
            // may be anywhere; reset to top-left and wipe so the step can
            // render from a known-blank canvas.
            WriteTerminal("\x1b[1;1H\x1b[J");
        }
        finally
        {
            WriteTerminal(SyncUpdateEnd);
        }

        Trace($"ScrollViewportToScrollback[#{resizeId}] exit");
    }

    /// <summary>
    /// Pumps output from a step adapter to the parent adapter.
    /// </summary>
    private async Task PumpStepOutputAsync(
        InlineStepAdapter stepAdapter,
        CancellationToken ct,
        Func<bool>? isMuted = null,
        bool parkCursorAtLiveOrigin = false)
    {
        // Hex1bApp writes the synchronized-output envelope as separate adapter
        // items: BSU, the frame body, and ESU. Keep one pending frame here so
        // admission can mute or forward the complete envelope, never just its
        // opening or closing item.
        var synchronizedFrame = new StringBuilder(4096);
        var synchronizedFrameOpen = false;
        var discardUntilBoundary = false;
        long synchronizedFrameEpoch = 0;

        void Forward(string text, long epoch)
        {
            if (text.Length == 0)
            {
                return;
            }

            lock (_stepOpsLock)
            {
                WriteTerminalUnlessMuted(
                    text,
                    isMuted,
                    parkCursorAtLiveOrigin ? stepAdapter : null,
                    epoch,
                    stepAdapter);
            }
        }

        void DropPendingFrame()
        {
            synchronizedFrame.Clear();
            synchronizedFrameOpen = false;
            synchronizedFrameEpoch = 0;
        }

        void StartPendingFrame(long epoch)
        {
            synchronizedFrame.Clear();
            synchronizedFrame.Append(SyncUpdateBegin);
            synchronizedFrameEpoch = epoch;
            synchronizedFrameOpen = true;
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var frame = await stepAdapter.ReadOutputFrameAsync(ct);
                if (frame.Kind == InlineOutputFrameKind.DiscardBoundary)
                {
                    // DiscardQueuedOutput may have removed the BSU before the
                    // pump read it. The typed sentinel makes that invalidation
                    // visible even when no frame is currently open.
                    DropPendingFrame();
                    discardUntilBoundary = true;
                    continue;
                }

                var data = frame.Bytes;
                if (data.Length == 0)
                {
                    continue;
                }

                var text = Encoding.UTF8.GetString(data);
                var offset = 0;
                while (offset < text.Length)
                {
                    if (discardUntilBoundary)
                    {
                        // An epoch change invalidates the whole pending frame.
                        // Drop its tail until the old ESU, or a fresh BSU that
                        // can start a new frame, so body bytes never leak through
                        // the unframed forwarding path.
                        var begin = text.IndexOf(
                            SyncUpdateBegin,
                            offset,
                            StringComparison.Ordinal);
                        var discardEnd = text.IndexOf(
                            SyncUpdateEnd,
                            offset,
                            StringComparison.Ordinal);
                        if (begin < 0 && discardEnd < 0)
                        {
                            break;
                        }

                        if (begin >= 0 && (discardEnd < 0 || begin < discardEnd))
                        {
                            discardUntilBoundary = false;
                            StartPendingFrame(frame.Epoch);
                            offset = begin + SyncUpdateBegin.Length;
                            continue;
                        }

                        discardUntilBoundary = false;
                        DropPendingFrame();
                        offset = discardEnd + SyncUpdateEnd.Length;
                        continue;
                    }

                    if (!synchronizedFrameOpen)
                    {
                        var begin = text.IndexOf(
                            SyncUpdateBegin,
                            offset,
                            StringComparison.Ordinal);
                        if (begin < 0)
                        {
                            Forward(
                                offset == 0 ? text : text[offset..],
                                frame.Epoch);
                            break;
                        }

                        if (begin > offset)
                        {
                            Forward(
                                offset == 0
                                    ? text[..begin]
                                    : text.Substring(offset, begin - offset),
                                frame.Epoch);
                        }

                        StartPendingFrame(frame.Epoch);
                        offset = begin + SyncUpdateBegin.Length;
                        continue;
                    }

                    if (frame.Epoch != synchronizedFrameEpoch)
                    {
                        DropPendingFrame();
                        discardUntilBoundary = true;
                        continue;
                    }

                    var nextBegin = text.IndexOf(
                        SyncUpdateBegin,
                        offset,
                        StringComparison.Ordinal);
                    var end = text.IndexOf(
                        SyncUpdateEnd,
                        offset,
                        StringComparison.Ordinal);

                    if (end >= 0 && (nextBegin < 0 || end < nextBegin))
                    {
                        var completeLength = end + SyncUpdateEnd.Length - offset;
                        synchronizedFrame.Append(text.AsSpan(offset, completeLength));
                        var completeEpoch = synchronizedFrameEpoch;
                        var complete = synchronizedFrame.ToString();
                        DropPendingFrame();
                        Forward(complete, completeEpoch);
                        offset = end + SyncUpdateEnd.Length;
                        continue;
                    }

                    if (nextBegin >= 0)
                    {
                        // A new BSU before the pending ESU means the previous
                        // frame was abandoned (for example, DiscardQueuedLiveOutput
                        // removed its ESU). Never concatenate stale bytes with
                        // the fresh frame.
                        DropPendingFrame();
                        StartPendingFrame(frame.Epoch);
                        offset = nextBegin + SyncUpdateBegin.Length;
                        continue;
                    }

                    // No boundary in this item: retain the body without copying
                    // the growing frame. It is materialized only at ESU.
                    synchronizedFrame.Append(text.AsSpan(offset));
                    break;
                }
            }
            // An incomplete synchronized frame is deliberately not flushed on
            // cancellation or channel disposal: without ESU it is not a frame.
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Pumps input events from the parent adapter to the step adapter, with resize handling.
    /// </summary>
    private async Task PumpStepInputAsync(
        InlineStepAdapter stepAdapter,
        CancellationToken ct,
        Action<int, int>? onResize = null)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (await _parentAdapter.InputEvents.WaitToReadAsync(ct))
                {
                    while (_parentAdapter.InputEvents.TryRead(out var evt))
                    {
                        if (evt is Hex1bResizeEvent resize && onResize != null)
                        {
                            // Let the runner handle repositioning before forwarding
                            onResize(resize.Width, resize.Height);
                            continue; // ResizeAsync already called in onResize
                        }
                        await stepAdapter.WriteInputEventAsync(evt, ct);
                    }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    /// <summary>
    /// Queries the host terminal's current cursor row (0-based). Uses the
    /// <see cref="Hex1bFlowOptions.CursorRowProvider"/> delegate when set so
    /// the runner can read the post-reflow position of the parked cursor
    /// after a horizontal resize. Falls back to
    /// <see cref="Hex1bFlowOptions.InitialCursorRow"/> when no provider is
    /// available (headless/test scenarios).
    /// </summary>
    private Task<int?> QueryCursorRowAsync(CancellationToken ct)
    {
        if (_options.CursorRowProvider is { } provider)
        {
            try
            {
                return Task.FromResult(provider());
            }
            catch
            {
                // Provider failures collapse to "unavailable" so callers
                // can fall back to bottom-anchor behaviour.
                return Task.FromResult<int?>(null);
            }
        }

        return Task.FromResult<int?>(_options.InitialCursorRow);
    }

    /// <summary>
    /// Per-step bridge between <see cref="FlowCommitCoordinator"/> and the
    /// runner's live app, its output pump, and the terminal write lock. Keeps
    /// every terminal mutation the commit needs inside the runner's existing
    /// serialization seams rather than opening a second writer.
    /// </summary>
    private sealed class LiveStepHandle : ILiveStepHandle
    {
        private readonly Hex1bFlowRunner _runner;
        private readonly FlowStep _step;
        private readonly InlineStepAdapter _stepAdapter;
        private readonly Hex1bApp _app;
        private readonly FlowStepContext _stepContext;
        private readonly System.Runtime.CompilerServices.StrongBox<bool> _muteGate;
        private readonly LiveAdmissionState _admission;
        private readonly Func<CancellationToken, Task<bool>> _prepareForCommitAsync;

        public LiveStepHandle(
            Hex1bFlowRunner runner,
            FlowStep step,
            InlineStepAdapter stepAdapter,
            Hex1bApp app,
            FlowStepContext stepContext,
            System.Runtime.CompilerServices.StrongBox<bool> muteGate,
            LiveAdmissionState admission,
            Func<CancellationToken, Task<bool>> prepareForCommitAsync)
        {
            _runner = runner;
            _step = step;
            _stepAdapter = stepAdapter;
            _app = app;
            _stepContext = stepContext;
            _muteGate = muteGate;
            _admission = admission;
            _prepareForCommitAsync = prepareForCommitAsync;
        }

        public int TerminalWidth => _runner.ReadCurrentGeometry().Width;

        public int TerminalHeight => _runner.ReadCurrentGeometry().Height;

        public long ResizeVersion => Interlocked.Read(ref _runner._resizeVersion);

        public bool SupportsCursorObservation => _runner.SupportsCursorObservation;

        public Task<int?> ObserveCursorRowAsync(CancellationToken cancellationToken)
            => _runner.ObserveCursorRowAsync(cancellationToken);

        public int RowOrigin => _stepAdapter.RowOrigin;

        public (int Width, int Height) ReadCurrentGeometry()
            => _runner.ReadCurrentGeometry();

        public (int Width, int Height, long ResizeVersion) ReadFreshGeometryForEmission()
            => _runner.ReadFreshGeometryForEmission();
        public void EnsureHistoryCommitSupported() =>
            _runner.EnsureHistoryCommitSupported();

        /// <summary>
        /// Prepares the live step for a commit that has already been admitted:
        /// quiesces the resize machinery, takes the live pump, and — for a host
        /// that can report its own cursor — replaces the step's tracked origin
        /// with the row the host says the region is actually on. After this
        /// returns, <see cref="RowOrigin"/> is the host-current anchor.
        /// </summary>
        /// <remarks>
        /// <para>
        /// Called by the coordinator while <c>IsCommitInFlight</c> is already
        /// true and before it samples any geometry. Nothing in here runs under a
        /// monitor while awaiting: the observation is taken outside the step
        /// locks, in the same shape the resize settle path uses.
        /// </para>
        /// <para>
        /// On success the pump is left muted and the returned value is the mute
        /// ownership from <em>before</em> admission, so the caller can return the
        /// pump to its real owner. A host that cannot report a cursor row — and
        /// cannot be substituted by the flow's scalar reflow model — suspends the
        /// step's commitment instead of anchoring to a guess; the pump stays
        /// muted, because nothing may repaint over content whose position cannot
        /// be stated.
        /// </para>
        /// </remarks>
        /// <returns>The mute ownership from before admission took the pump.</returns>
        public Task<bool> PrepareForCommitAsync(CancellationToken cancellationToken)
            => _prepareForCommitAsync(cancellationToken);

        public int LiveHeight => Math.Max(1, _step.StepHeight);

        public long FrameCount => _app.FrameCount;

        public Surface? SnapshotLiveSurface() => _app.SnapshotCurrentSurface();

        public void WriteTerminalAt(int row, string text) => _runner.WriteTerminalAt(row, text);

        public IAtomicTerminalUpdate BeginAtomicTerminalUpdate() => _runner.BeginAtomicTerminalUpdate();

        public void MarkCommittedRow(int row) => _runner.MarkCommittedRow(row);

        public void ScrollViewportUp(int rows)
        {
            if (rows <= 0)
            {
                return;
            }

            lock (_runner._stepOpsLock)
            {
                _runner.ScrollViewportUp(rows);

                // The viewport scrolled up by `rows`, so every row already on
                // screen moved up by the same amount — including tracked
                // tombstones and previously committed content. Shift the flow's
                // on-screen bookkeeping to match, exactly as the tombstone path
                // does, so the soft-wrap settle pass recomputes the live region's
                // origin from a correct starting row instead of anchoring it
                // above the committed history.
                _runner._cursorRow -= rows;
                _runner._initialRowOrigin -= rows;
            }
        }

        public void ReanchorLive(int rowOrigin, int liveHeight, Surface liveSurface)
        {
            lock (_runner._stepOpsLock)
            {
                var painted = _runner.ReanchorLiveRegion(_stepAdapter, rowOrigin, liveHeight, liveSurface);
                _step.StepHeight = painted.LiveHeight;
                _step.TerminalWidth = _runner.ReadCurrentGeometry().Width;
            }
        }

        public void RecordCommittedRows(Surface surface)
        {
            lock (_runner._stepOpsLock)
            {
                // One entry per committed row, appended in emission order, so
                // FlowResizeMath.ComputeRowOriginAtWidth sees the committed
                // content exactly like a tombstone paragraph when it recomputes
                // the live region's row after a reflow.
                for (var row = 0; row < surface.Height; row++)
                {
                    _runner._emittedTombstones.Add(
                        new[] { Hex1bFlowRunner.MeasureSurfaceRowWidth(surface, row) });
                }
            }
        }

        public int DiscardQueuedLiveOutput()
        {
            lock (_runner._stepOpsLock)
            {
                return _stepAdapter.DiscardQueuedOutput();
            }
        }

        public void ResizeLive(int width, int liveHeight)
        {
            // Always push the resize: the inner app treats it as a re-render
            // trigger, and the region must repaint at the settled geometry.
            lock (_runner._stepOpsLock)
            {
                _ = _stepAdapter.ResizeAsync(
                    Math.Max(1, width), Math.Max(1, liveHeight));
            }
        }

        public void ResizeLiveToTerminalGeometry(int width, int terminalHeight)
        {
            var stepHeight = Math.Clamp(
                _step.StepHeight,
                1,
                Math.Max(1, terminalHeight));
            lock (_runner._stepOpsLock)
            {
                _step.TerminalWidth = Math.Max(1, width);
                _step.StepHeight = stepHeight;
                _ = _stepAdapter.ResizeAsync(
                    Math.Max(1, width),
                    Math.Max(1, stepHeight));
            }
        }

        public void ApplyLiveLayout(Func<FlowStepContext, Task<Hex1bWidget>> builder)
        {
            _app.SwapRootComponent(rootContext =>
            {
                _stepContext.CancellationToken = rootContext.CancellationToken;
                return builder(_stepContext);
            });
        }

        public void RequestLiveFrame() => _app.Invalidate();

        public bool SetLiveOutputMuted(bool muted)
        {
            var previous = System.Threading.Volatile.Read(ref _muteGate.Value);
            if (muted && System.Threading.Volatile.Read(ref _admission.MuteTaken))
            {
                // Admission already took the pump. Its mute is not the caller's
                // ownership, so report the state from before admission: an
                // end-of-commit unmute then returns the pump to its real owner
                // instead of echoing the commit's own mute back at it. The
                // caller's idempotent mute still lands.
                System.Threading.Volatile.Write(ref _admission.MuteTaken, false);
                previous = System.Threading.Volatile.Read(ref _admission.PriorMute);
            }

            System.Threading.Volatile.Write(ref _muteGate.Value, muted);
            if (!muted)
            {
                // Resuming the pump also lifts the admission-time frame gate:
                // from here the commit is finished with the live region and the
                // next frame is laid out for it.
                System.Threading.Volatile.Write(ref _admission.ResumeGranted, true);
            }

            return previous;
        }

        public Task<bool> WaitForLiveFrameAfterAsync(
            long afterFrame,
            TimeSpan timeout,
            CancellationToken cancellationToken)
            => Hex1bFlowRunner.WaitForFrameAfterAsync(_app, afterFrame, timeout, cancellationToken);

        public void RecordCommitEvent(string message) => Hex1bFlowRunner.RecordCommitEvent(message);
    }
}

/// <summary>
/// Options for the Hex1b Flow system.
/// </summary>
public sealed class Hex1bFlowOptions
{
    /// <summary>
    /// Theme for all steps and full-screen apps in the flow.
    /// </summary>
    public Hex1bTheme? Theme { get; set; }

    /// <summary>
    /// Presentation-owned host profile provider. The builder binds this to a
    /// lazy source so a profile is read only after the presentation's startup
    /// capability probe has completed.
    /// </summary>
    internal Func<FlowTerminalHostProfile>? HostProfileProvider { get; set; }

    /// <summary>
    /// Whether to enable mouse input for full-screen steps.
    /// </summary>
    public bool EnableMouse { get; set; }

    /// <summary>
    /// Initial cursor row (0-based) where the flow starts rendering.
    /// Set this to <c>Console.GetCursorPosition().Top</c> before calling RunAsync.
    /// If null, defaults to 0.
    /// </summary>
    public int? InitialCursorRow { get; set; }

    /// <summary>
    /// Optional delegate that returns the host terminal's current cursor row
    /// (0-based). When set, the flow runner uses this to read the initial
    /// cursor position at startup, before the input pump is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented on top of <c>Console.GetCursorPosition()</c> by
    /// <c>Hex1bTerminalBuilder.WithHex1bFlow</c> when a presentation adapter
    /// is available. On Windows this resolves through Win32
    /// <c>GetConsoleScreenBufferInfo</c> (no DSR roundtrip); on Unix the BCL
    /// uses a raw DSR query that reads the response from stdin.
    /// </para>
    /// <para>
    /// <strong>Must not be called from within a resize handler.</strong>
    /// On Unix, calling this while Hex1b's input pump is running causes a
    /// deadlock: the DSR response arrives on the same stdin file descriptor
    /// that the input pump owns, so <c>Console.GetCursorPosition()</c>
    /// blocks forever waiting for bytes it will never receive.
    /// </para>
    /// <para>
    /// Returns <c>null</c> to indicate the cursor row could not be
    /// determined.
    /// </para>
    /// </remarks>
    public Func<int?>? CursorRowProvider { get; set; }

    /// <summary>
    /// When <c>true</c>, tombstoned (yield) widget output is emitted as
    /// soft-wrap-friendly logical lines (text + <c>ESC[K</c> + LF) so the host
    /// terminal can reflow tombstones during a resize and scroll older
    /// tombstones into the scrollback buffer naturally. When <c>false</c>
    /// (the default), tombstones are emitted with absolute cursor positioning
    /// and the entire visible area is cleared on every resize, which causes
    /// tombstones to disappear when the terminal is resized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This option is experimental and gates two related behaviours together:
    /// the tombstone emission path and the resize handler. The new resize
    /// handler only clears the active step region and trusts the host
    /// terminal to have reflowed tombstones above it — that is only a valid
    /// assumption when tombstones were emitted as proper logical lines, so
    /// both behaviours move together under this single flag.
    /// </para>
    /// <para>
    /// Once validated across the supported terminal matrix this will become
    /// the default and the legacy cell-positioning path will be removed.
    /// </para>
    /// </remarks>
    public bool UseSoftWrapTombstones { get; set; }

    /// <summary>
    /// Optional quiet window after the last <c>Hex1bResizeEvent</c> used only
    /// for final resize cleanup. Every resize still publishes the current
    /// geometry and repaints the live prompt immediately; this delay does not
    /// debounce prompt visibility or hold the live region blank.
    /// When <c>null</c> (the default), the resize path restores its transient
    /// ownership as soon as the immediate repaint completes.
    /// </summary>
    /// <remarks>
    /// Only takes effect when <see cref="UseSoftWrapTombstones"/> is also
    /// <c>true</c>: the cleanup path relies on tombstones above the active step
    /// being hard-newline-terminated paragraphs that the host terminal will not
    /// reflow across paragraph boundaries. Recommended value: 50–100 ms.
    /// </remarks>
    public TimeSpan? ResizeSettleDelay { get; set; }

}
