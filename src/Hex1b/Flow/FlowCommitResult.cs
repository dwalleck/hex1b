using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Outcome of a successful <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// This is a framework-emission acknowledgement only. It reports how much
/// finalized content Hex1b handed to the terminal-side write path. It is
/// deliberately <em>not</em> a claim about host-terminal scanout, native
/// scrollback retention, or conversation persistence.
/// </remarks>
public sealed class FlowCommitResult
{
    internal FlowCommitResult(
        int completedUnits,
        int completedRows,
        IReadOnlyList<string?> rowKeys,
        FlowCommitStatus status,
        int eventCount,
        bool cancelled,
        int abortedRows,
        bool drainTimedOut)
    {
        CompletedUnits = completedUnits;
        CompletedRows = completedRows;
        RowKeys = rowKeys;
        Status = status;
        EventCount = eventCount;
        Cancelled = cancelled;
        AbortedRows = abortedRows;
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
    public FlowCommitStatus Status { get; }

    /// <summary>
    /// Number of structured emission events the coordinator recorded for this
    /// commit. The driver persists these to its JSONL evidence file.
    /// </summary>
    public int EventCount { get; }

    /// <summary>
    /// True when the commit resolved because its cancellation token was
    /// cancelled. Completed units stay emitted and are never replayed.
    /// </summary>
    public bool Cancelled { get; }

    /// <summary>
    /// Physical rows of the unit that was being emitted when a cancellation
    /// arrived. Those rows may already carry content and are reserved on screen
    /// (the live region is re-anchored below them), but they are not part of
    /// <see cref="CompletedRows"/> because their unit was never completed.
    /// </summary>
    public int AbortedRows { get; }

    /// <summary>
    /// True when the bounded wait for the terminal side to consume the emitted
    /// bytes expired with the adapter's output queue still non-empty. Emission
    /// is not withdrawn — this only says the framework could not observe the
    /// drain, so the result must not be read as terminal-side confirmation.
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
    /// nothing about what the host terminal has displayed or retained.
    /// </summary>
    Emitted = 0,

    /// <summary>
    /// The commit stopped on cancellation with units still pending. Completed
    /// units stay emitted and are never replayed.
    /// </summary>
    Cancelled = 1,
}
