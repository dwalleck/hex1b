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
    LiveRenderSnapshot? SnapshotLiveFrame();

    /// <summary>Serializes a graphics transaction before the synchronous write locks.</summary>
    ValueTask<IDisposable> AcquireGraphicsAsync(CancellationToken cancellationToken);

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
    /// True when this step's presentation can refuse bytes composed for a
    /// superseded native geometry, so a commitment can retry a refused batch
    /// instead of writing it against the wrong viewport.
    /// </summary>
    bool SupportsGeometryGatedDelivery { get; }

    /// <summary>
    /// Captures the bookkeeping an atomic terminal update can move while composing.
    /// </summary>
    FlowAtomicCheckpoint CaptureAtomicCheckpoint();

    /// <summary>
    /// Restores a checkpoint taken before an atomic update whose delivery the
    /// presentation refused.
    /// </summary>
    /// <remarks>
    /// Must be called after the refused scope has released its locks, because it
    /// re-enters the step-lock it protects.
    /// </remarks>
    /// <param name="checkpoint">The checkpoint to restore.</param>
    void RestoreAtomicCheckpoint(FlowAtomicCheckpoint checkpoint);

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
    void ReanchorLive(int rowOrigin, int liveHeight, LiveRenderSnapshot liveSurface);

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
