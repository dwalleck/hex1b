using Hex1b.Widgets;

namespace Hex1b.Flow;

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
