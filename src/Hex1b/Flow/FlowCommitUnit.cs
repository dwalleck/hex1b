using Hex1b.Widgets;

using Hex1b.Surfaces;

namespace Hex1b.Flow;

/// <summary>
/// One immutable logical unit of finalized presentation content submitted to
/// <see cref="FlowStep.CommitAsync(FlowCommitSource, Func{FlowStepContext, Task{Hex1bWidget}}, CancellationToken)"/>.
/// </summary>
/// <remarks>
/// <para>
/// A unit is the smallest thing the history path counts as progress. It is
/// materialized from immutable application state at a specific terminal
/// width; the committed content must not change once submitted. Because
/// progress is counted in units (not terminal rows), a unit whose surface
/// wraps to several physical rows on a narrow terminal is still exactly one
/// unit: width changes re-render only the units that have not been emitted
/// yet, and never re-emit or skip a unit.
/// </para>
/// <para>
/// <see cref="RowKey"/> is an optional application-supplied stable identity
/// (for example an immutable record/row id). Hex1b never rewrites it; it is
/// echoed in <see cref="FlowCommitResult.RowKeys"/> and in the driver's
/// structured events so host-export accounting can match emitted units to
/// application rows.
/// </para>
/// </remarks>
/// <param name="RowKey">
/// Stable application identity for this unit, or <see langword="null"/> when
/// the application does not need per-unit accounting.
/// </param>
/// <param name="Surface">
/// The rendered unit at the width it was built for. All physical rows of this
/// surface belong to this one logical unit and are emitted contiguously.
/// </param>
public readonly record struct FlowCommitUnit(string? RowKey, Surface Surface);
