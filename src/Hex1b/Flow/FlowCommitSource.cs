using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Supplies the immutable finalized presentation units a
/// <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>
/// operation commits to native history.
/// </summary>
/// <remarks>
/// <para>
/// The application owns unit identity: it decides what is eligible for history
/// commitment, in what logical order, and how a unit is materialized at a given
/// width. Hex1b only transports it. That is why a commitment is counted in
/// units rather than physical terminal rows — a unit that soft-wraps into
/// several rows on a narrow terminal is still exactly one unit, so progress
/// accounting and post-resize re-materialization can never re-identify content.
/// </para>
/// <para>
/// <see cref="UnitCount"/> must be stable across widths. A source that can only
/// learn its count by rendering reports it from <see cref="PrepareAsync"/>, which
/// the committer always awaits before requesting the first unit and again after
/// any width change. A source whose count changes across widths fails the
/// commit loudly rather than being re-indexed.
/// </para>
/// </remarks>
public abstract class FlowCommitSource
{
    /// <summary>
    /// Number of logical units in this source. Must be stable regardless of the
    /// width the units are materialized at.
    /// </summary>
    public abstract int UnitCount { get; }

    /// <summary>
    /// Prepares the source for a commit at <paramref name="width"/> and returns
    /// the authoritative unit count.
    /// </summary>
    /// <remarks>
    /// Called by the committer before the first unit, and again whenever the
    /// terminal width changes, so a render-backed source can materialize the
    /// units it has not emitted yet at the new width. The default implementation
    /// returns <see cref="UnitCount"/>, which is correct for sources whose unit
    /// count is known without rendering.
    /// </remarks>
    public virtual Task<int> PrepareAsync(int width, CancellationToken cancellationToken)
        => Task.FromResult(UnitCount);

    /// <summary>
    /// Materializes unit <paramref name="index"/> at <paramref name="width"/>
    /// columns.
    /// </summary>
    /// <remarks>
    /// Called at most once per (index, committed-width) pair, and never for a
    /// unit that has already been emitted. All physical rows of the returned
    /// unit's surface belong to that one logical unit and are emitted
    /// contiguously.
    /// </remarks>
    /// <param name="index">Zero-based unit index, less than <see cref="UnitCount"/>.</param>
    /// <param name="width">Terminal width in columns in force for this materialization.</param>
    /// <param name="cancellationToken">Commit cancellation token.</param>
    public abstract Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken);
}
