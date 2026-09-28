using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Explains why a result field is absent. An absent field is never reported as zero or empty.
/// </summary>
public sealed class DiagnosticUnavailableField
{
    /// <summary>JSON path of the absent field, for example <c>identity.modelSequence</c>.</summary>
    [JsonPropertyName("field")]
    public string Field { get; init; } = "";

    /// <summary>Why the field is not provided.</summary>
    [JsonPropertyName("reason")]
    public string Reason { get; init; } = "";
}
