using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Coverage of retained terminal-model history in a capture. Model history is distinct from
/// the native host's scrollback, and a requested export is not a complete continuation checkpoint.
/// </summary>
public sealed class DiagnosticHistoryCoverage
{
    /// <summary>Rows the caller requested.</summary>
    [JsonPropertyName("requestedRows")]
    public int RequestedRows { get; init; }

    /// <summary>
    /// Rows the model retained and could return at the observation, or <c>null</c> when
    /// history is not accessible (see <see cref="Reason"/>).
    /// </summary>
    [JsonPropertyName("availableRows")]
    public int? AvailableRows { get; init; }

    /// <summary>Rows actually returned above the active screen.</summary>
    [JsonPropertyName("returnedRows")]
    public int ReturnedRows { get; init; }

    /// <summary>
    /// Returned rows whose retained content was wider than the current screen and was cropped
    /// to the current width. Cropped blank columns are not counted.
    /// </summary>
    [JsonPropertyName("croppedRows")]
    public int CroppedRows { get; init; }

    /// <summary>
    /// Whether returned history omits retained content: fewer rows than were both requested and
    /// available, or rows cropped to the current width.
    /// </summary>
    [JsonPropertyName("truncated")]
    public bool Truncated { get; init; }

    /// <summary>
    /// Configured maximum number of rows the model retains; zero when retention is not configured.
    /// </summary>
    [JsonPropertyName("retentionCapacity")]
    public int RetentionCapacity { get; init; }

    /// <summary>Why history is limited or inaccessible, when it is.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}
