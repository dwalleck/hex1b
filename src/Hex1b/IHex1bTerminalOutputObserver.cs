namespace Hex1b;

/// <summary>
/// A presentation filter that observes output without changing the bytes the
/// presentation receives.
/// </summary>
/// <remarks>
/// <para>
/// A geometry-gated delivery writes its bytes before any presentation filter
/// runs, so a filter that transformed them would change nothing on screen while
/// the terminal believed it had — the rendered output and the model would
/// silently diverge. Marking a filter as an observer is therefore a claim the
/// terminal enforces rather than trusts: after the observers have run, the
/// tokens they returned are serialized and compared byte-for-byte with what was
/// written, and a difference fails the delivery loudly.
/// </para>
/// <para>
/// Observers still receive the applied tokens, so a filter that records or
/// forwards output keeps working; it simply may not alter it.
/// </para>
/// </remarks>
public interface IHex1bTerminalOutputObserver : IHex1bTerminalPresentationFilter
{
}
