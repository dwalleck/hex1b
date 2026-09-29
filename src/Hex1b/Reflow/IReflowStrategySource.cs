namespace Hex1b.Reflow;

/// <summary>
/// A presentation that delegates reflow to a strategy, whether or not reflow is enabled. A diagnostic case
/// records the strategy's identity so a model can be rebuilt with the same reflow and soft-wrap behavior.
/// </summary>
internal interface IReflowStrategySource
{
    /// <summary>The strategy the presentation delegates reflow and soft-wrap clearing to.</summary>
    ITerminalReflowProvider ReflowStrategy { get; }
}
