using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>What an artifact actually holds for one stream.</summary>
public sealed record DiagnosticCaseStreamCoverage
{
    /// <summary>The stream.</summary>
    [JsonPropertyName("stream")]
    public string Stream { get; init; } = "";

    /// <summary>Verified events of the stream.</summary>
    [JsonPropertyName("events")]
    public long Events { get; init; }

    /// <summary>First and last ordinal present.</summary>
    [JsonPropertyName("firstOrdinal")]
    public long? FirstOrdinal { get; init; }

    /// <summary>Last ordinal present.</summary>
    [JsonPropertyName("lastOrdinal")]
    public long? LastOrdinal { get; init; }

    /// <summary>
    /// <c>complete</c>, <c>incomplete</c> (missing ranges, or ordinals missing with no record of why:
    /// unknown loss) or <c>failed</c>.
    /// </summary>
    [JsonPropertyName("state")]
    public string State { get; init; } = "";

    /// <summary>Recorded and detected missing ranges.</summary>
    [JsonPropertyName("missing")]
    public IReadOnlyList<DiagnosticCaseRecord> Missing { get; init; } = [];
}
