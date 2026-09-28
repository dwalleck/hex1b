using System.Diagnostics;

namespace Hex1b.Diagnostics;

/// <summary>
/// Phase costs of one application pass, in <see cref="Stopwatch"/> ticks.
/// </summary>
internal readonly record struct ApplicationPassTimings(long BuildTicks, long ReconcileTicks, long RenderTicks);
