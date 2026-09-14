using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Outcome of a successful <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// This is a framework-emission acknowledgement only. It reports how much
/// finalized content Hex1b handed to the terminal-side write path. It is
/// deliberately <em>not</em> a claim about host-terminal scanout, native
/// scrollback retention, or conversation persistence.
/// </para>
/// <para>
/// The two facts a caller must keep apart are the hand-off split
/// (<see cref="Status"/>, <see cref="CompletedUnits"/>) and whether the commit
/// observed cancellation (<see cref="CancellationRequested"/>). Cancellation
/// that arrives after the last unit has been handed off is an emission success
/// with an unobserved drain, not a partial commit.
/// </para>
/// </remarks>
public sealed class FlowCommitResult
{
    internal FlowCommitResult(
        int completedUnits,
        int completedRows,
        IReadOnlyList<string?> rowKeys,
        int pendingUnits,
        int eventCount,
        bool cancellationRequested,
        int abortedRows,
        bool drainObserved,
        bool drainTimedOut)
    {
        CompletedUnits = completedUnits;
        CompletedRows = completedRows;
        RowKeys = rowKeys;
        // The status is the hand-off split, derived rather than restated, so it
        // cannot disagree with the unit counts: a commit that stopped while
        // units were still pending is Cancelled, and one that handed every unit
        // off is Emitted even if cancellation was observed afterwards.
        Status = pendingUnits > 0 ? FlowCommitStatus.Cancelled : FlowCommitStatus.Emitted;
        EventCount = eventCount;
        CancellationRequested = cancellationRequested;
        AbortedRows = abortedRows;
        EmissionDrainObserved = drainObserved;
        EmissionDrainTimedOut = drainTimedOut;
    }

    /// <summary>
    /// Number of logical units fully emitted. Equal to
    /// <see cref="RowKeys"/>.Count.
    /// </summary>
    public int CompletedUnits { get; }

    /// <summary>
    /// Number of physical terminal rows successfully emitted for those units.
    /// This is the count to use for host-export accounting: a unit submitted on
    /// a narrow terminal may occupy more than one physical row, and only
    /// fully-emitted rows are counted. A partially written row is never
    /// counted.
    /// </summary>
    public int CompletedRows { get; }

    /// <summary>
    /// Application-supplied row keys (<see cref="FlowCommitUnit.RowKey"/>) of
    /// the units that were fully emitted, in emission order. Contains
    /// <see langword="null"/> entries for units submitted without a key.
    /// </summary>
    public IReadOnlyList<string?> RowKeys { get; }

    /// <summary>
    /// How the commit operation resolved. See <see cref="FlowCommitStatus"/>.
    /// </summary>
    /// <remarks>
    /// This is the hand-off split, not a cancellation flag:
    /// <see cref="FlowCommitStatus.Cancelled"/> exactly when units were still
    /// pending when the commit stopped, <see cref="FlowCommitStatus.Emitted"/>
    /// when every submitted unit was handed off.
    /// </remarks>
    public FlowCommitStatus Status { get; }

    /// <summary>
    /// Number of structured emission events the coordinator recorded for this
    /// commit. The driver persists these to its JSONL evidence file.
    /// </summary>
    public int EventCount { get; }

    /// <summary>
    /// True exactly when <see cref="Status"/> is
    /// <see cref="FlowCommitStatus.Cancelled"/>: the commit stopped while units
    /// were still pending, and the units that were handed off stay emitted and
    /// are never replayed.
    /// </summary>
    /// <remarks>
    /// This says nothing about cancellation itself — a cancellation that landed
    /// after the last unit had been handed off resolves as
    /// <see cref="FlowCommitStatus.Emitted"/> and leaves this false. Use
    /// <see cref="CancellationRequested"/> to tell whether cancellation was
    /// observed at all.
    /// </remarks>
    public bool Cancelled => Status == FlowCommitStatus.Cancelled;

    /// <summary>
    /// True when the commit observed its cancellation token cancelled before it
    /// resolved.
    /// </summary>
    /// <remarks>
    /// Independent of <see cref="Status"/>: cancellation that lands only after
    /// the last unit has been handed off — during the live-frame wait or the
    /// final drain — still reports a complete emission
    /// (<see cref="FlowCommitStatus.Emitted"/>) with this set, because emission
    /// is the commit outcome and cancellation observation is not. A commit that
    /// ran to completion or failed reports false.
    /// </remarks>
    public bool CancellationRequested { get; }

    /// <summary>
    /// Physical rows of the unit that was being emitted when a cancellation
    /// arrived. Those rows may already carry content and are reserved on screen
    /// (the live region is re-anchored below them), but they are not part of
    /// <see cref="CompletedRows"/> because their unit was never completed.
    /// </summary>
    public int AbortedRows { get; }

    /// <summary>
    /// True when the commit's final bounded drain observed the adapter's shared
    /// output queue empty.
    /// </summary>
    /// <remarks>
    /// False when the queue was still non-empty at the deadline, and false when
    /// the drain never ran because the commit stopped earlier (cancellation, or
    /// a failure). The adapter's queue is shared with the live application's own
    /// output, so this is a shared-queue observation, not host presentation: it
    /// does not prove the host terminal displayed or retained anything.
    /// </remarks>
    public bool EmissionDrainObserved { get; }

    /// <summary>
    /// True only when the commit's final bounded drain reached its deadline with
    /// the adapter's output queue still non-empty. Emission is not withdrawn —
    /// this only says the framework could not observe the drain, so the result
    /// must not be read as terminal-side confirmation. Never true on a path that
    /// did not run the drain.
    /// </summary>
    public bool EmissionDrainTimedOut { get; }
}

/// <summary>
/// How a <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>
/// operation resolved.
/// </summary>
public enum FlowCommitStatus
{
    /// <summary>
    /// Every submitted unit was handed to the terminal-side write path. This is
    /// the normal outcome, and it is a framework-emission fact only — it says
    /// nothing about what the host terminal has displayed or retained. A commit
    /// whose cancellation arrived only after the last unit was handed off also
    /// resolves this way; see <see cref="FlowCommitResult.CancellationRequested"/>.
    /// </summary>
    Emitted = 0,

    /// <summary>
    /// The commit stopped on cancellation with units still pending. Completed
    /// units stay emitted and are never replayed. Cancellation never resolves
    /// this way once every submitted unit has been handed off.
    /// </summary>
    Cancelled = 1,
}
