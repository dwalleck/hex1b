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
/// <em>not</em> replayed. <see cref="MayHavePartialRow"/> reports whether the
/// failing unit had already written part of a row before the failure, in which
/// case the host stream may contain a visibly truncated line.
/// </para>
/// <para>
/// After this exception the step refuses further history commits
/// (see <see cref="FlowStep.CanCommit"/> / <see cref="FlowStep.IsCommitUncertain"/>)
/// so an uncertain batch can never be silently retried. Explicit recovery is
/// the caller's decision.
/// </para>
/// </remarks>
public sealed class FlowCommitException : Exception
{
    internal FlowCommitException(
        string message,
        int completedUnits,
        int completedRows,
        bool mayHavePartialRow,
        Exception? innerException)
        : base(message, innerException)
    {
        CompletedUnits = completedUnits;
        CompletedRows = completedRows;
        MayHavePartialRow = mayHavePartialRow;
    }

    /// <summary>
    /// Number of logical units fully emitted before the failure.
    /// </summary>
    public int CompletedUnits { get; }

    /// <summary>
    /// Number of physical terminal rows fully emitted before the failure. A
    /// partially written row is not counted; see <see cref="MayHavePartialRow"/>.
    /// </summary>
    public int CompletedRows { get; }

    /// <summary>
    /// True when the failing unit had already written part of its content to
    /// the terminal when the failure occurred, so the host stream may contain
    /// a truncated row.
    /// </summary>
    public bool MayHavePartialRow { get; }
}
