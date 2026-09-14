using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Raised by <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>
/// when a history emission fails after content may already have reached the
/// host terminal.
/// </summary>
/// <remarks>
/// <para>
/// The native scrollback buffer is host-owned: Hex1b cannot roll it back.
/// When this exception is raised, <see cref="CompletedUnits"/> units
/// (and <see cref="CompletedRows"/> physical rows) were fully emitted and are
/// <em>not</em> replayed. <see cref="AbortedRows"/> reports how much of the
/// failing unit had already been composed before the failure; when it is
/// non-zero the host stream may contain a visibly truncated line
/// (<see cref="MayHavePartialRow"/>).
/// </para>
/// <para>
/// A failure that emitted a prefix or composed part of a unit suspends history
/// commitment for the step (see <see cref="FlowStep.CanCommit"/> /
/// <see cref="FlowStep.IsCommitUncertain"/>): replaying the batch would
/// duplicate the prefix, so another commit is refused rather than silently
/// retried. The live region is re-anchored below everything the failed commit
/// may have written, and the step's current layout is retained — the failed
/// commit's next-live layout is applied only when every submitted unit was
/// handed off, which a failure cannot be. A failure that emitted nothing and
/// composed nothing leaves the step committable: no content exists that a retry
/// could duplicate. Explicit recovery is the caller's decision.
/// </para>
/// </remarks>
public sealed class FlowCommitException : Exception
{
    internal FlowCommitException(
        string message,
        int completedUnits,
        int completedRows,
        int abortedRows,
        Exception? innerException,
        Exception? recoveryFailure = null)
        : base(message, innerException)
    {
        CompletedUnits = completedUnits;
        CompletedRows = completedRows;
        AbortedRows = abortedRows;
        MayHavePartialRow = abortedRows > 0;
        RecoveryFailure = recoveryFailure;
    }

    /// <summary>
    /// Number of logical units fully emitted before the failure. Their rows are
    /// in the host's stream and are never replayed.
    /// </summary>
    public int CompletedUnits { get; }

    /// <summary>
    /// Number of physical terminal rows fully emitted before the failure. A
    /// partially written row is not counted; see <see cref="AbortedRows"/> and
    /// <see cref="MayHavePartialRow"/>.
    /// </summary>
    public int CompletedRows { get; }

    /// <summary>
    /// Physical rows of the unit that was being emitted when the failure
    /// occurred, composed into that unit's terminal update before it failed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These rows are reserved on screen — the live region is re-anchored below
    /// them, and they are recorded in the flow's own reflow model, so neither a
    /// later live frame nor a later resize settle paints over them — but they are
    /// not part of <see cref="CompletedRows"/>, because their unit was never
    /// completed and must never be counted as emitted.
    /// </para>
    /// <para>
    /// The count is how many rows the failing unit composed into its single
    /// terminal update. Whether that update was handed off is a separate fact
    /// recorded in the commit trace, not restated here: a rejected hand-off means
    /// the bytes may not have reached the adapter at all, while a completed one
    /// means they were taken — and neither is proof of host presentation. Either
    /// way this count is the conservative upper bound of the uncertain content,
    /// which is what the reservation and a caller's "never re-emit this unit"
    /// rule need.
    /// </para>
    /// </remarks>
    public int AbortedRows { get; }

    /// <summary>
    /// True when the failing unit had already composed part of its content, so
    /// the host stream may contain a truncated row.
    /// </summary>
    /// <remarks>
    /// This is a composed-row fact (<see cref="AbortedRows"/> &gt; 0), not a
    /// statement about the exception type: a write-path failure after rows were
    /// composed reports it, and a failure raised before any row of the unit was
    /// composed does not.
    /// </remarks>
    public bool MayHavePartialRow { get; }

    /// <summary>
    /// The failure raised while re-anchoring and resuming the live region after
    /// this one, or <see langword="null"/> when recovery succeeded.
    /// </summary>
    /// <remarks>
    /// Recovery failure never replaces or hides the original failure: it is
    /// reported here, and in the message, so a caller can tell that the live
    /// region may not have been restored below the failed commit's bytes.
    /// </remarks>
    public Exception? RecoveryFailure { get; }
}
