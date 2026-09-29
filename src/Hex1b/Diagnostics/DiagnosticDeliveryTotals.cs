using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Counts over every completed write since coverage started; never evicted.
/// </summary>
public sealed record DiagnosticDeliveryTotals
{
    /// <summary>Accepted writes.</summary>
    [JsonPropertyName("accepted")]
    public long Accepted { get; init; }

    /// <summary>Refused writes.</summary>
    [JsonPropertyName("refused")]
    public long Refused { get; init; }

    /// <summary>Failed writes.</summary>
    [JsonPropertyName("failed")]
    public long Failed { get; init; }

    /// <summary>Bytes the host took, including bytes taken before failures when observable.</summary>
    [JsonPropertyName("bytesAccepted")]
    public long BytesAccepted { get; init; }

    /// <summary>The first completed sequence, absent before the first write.</summary>
    [JsonPropertyName("firstSequence")]
    public long? FirstSequence { get; init; }

    /// <summary>The last completed sequence, absent before the first write.</summary>
    [JsonPropertyName("lastSequence")]
    public long? LastSequence { get; init; }
}
