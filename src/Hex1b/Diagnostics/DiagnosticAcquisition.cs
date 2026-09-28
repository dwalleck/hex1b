using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// When an observation was acquired, in an explicitly named clock domain. Intervals from
/// different processes are comparable only through their wall-clock values.
/// </summary>
public sealed class DiagnosticAcquisition
{
    /// <summary>
    /// Clock domain of <see cref="StartTimestamp"/> and <see cref="EndTimestamp"/>:
    /// <c>process-monotonic</c> is <see cref="System.Diagnostics.Stopwatch"/> in the target process.
    /// </summary>
    [JsonPropertyName("clockDomain")]
    public string ClockDomain { get; init; } = "";

    /// <summary>Ticks per second of the monotonic clock.</summary>
    [JsonPropertyName("frequency")]
    public long Frequency { get; init; }

    /// <summary>Monotonic timestamp before acquisition began.</summary>
    [JsonPropertyName("startTimestamp")]
    public long StartTimestamp { get; init; }

    /// <summary>Monotonic timestamp after the observation was complete.</summary>
    [JsonPropertyName("endTimestamp")]
    public long EndTimestamp { get; init; }

    /// <summary>UTC wall-clock time before acquisition began.</summary>
    [JsonPropertyName("wallClockStart")]
    public DateTimeOffset WallClockStart { get; init; }

    /// <summary>UTC wall-clock time after the observation was complete.</summary>
    [JsonPropertyName("wallClockEnd")]
    public DateTimeOffset WallClockEnd { get; init; }
}
