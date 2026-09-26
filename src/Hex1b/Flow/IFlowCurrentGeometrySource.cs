namespace Hex1b.Flow;

/// <summary>
/// Provides current native terminal dimensions to Flow's serialized write path.
/// </summary>
/// <remarks>
/// A workload adapter normally exposes dimensions delivered by resize events.
/// The native presentation can have applied a resize before that event reaches
/// the adapter, so the final emission check uses this source when available.
/// </remarks>
internal interface IFlowCurrentGeometrySource
{
    (int Width, int Height) ReadCurrentGeometry();
}
