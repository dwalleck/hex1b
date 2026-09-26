namespace Hex1b.Flow;

/// <summary>
/// Lazy, presentation-owned Flow host capability source.
/// </summary>
/// <remarks>
/// The source is read by the Flow runner at emission time rather than copied
/// while the builder creates its workload factory. This keeps the profile tied
/// to the result of the presentation adapter's existing startup probe.
/// </remarks>
internal interface IFlowTerminalHostProfileSource
{
    FlowTerminalHostProfile FlowHostProfile { get; }
}
