using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Request for the native delivery record of a terminal session.
/// </summary>
public sealed record DiagnosticDeliveryRequest
{
    /// <summary>Return only records with a sequence greater than this; absent returns from the oldest retained record.</summary>
    [JsonPropertyName("since")]
    public long? Since { get; init; }

    /// <summary>Most records to return, 1 to 4,096; absent means 4,096.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary>
    /// Opt-ins. <see cref="DiagnosticAuthorization.NativeOutput"/> adds each record's retained written bytes.
    /// </summary>
    [JsonPropertyName("authorizations")]
    public IReadOnlyList<DiagnosticAuthorization>? Authorizations { get; init; }
}
