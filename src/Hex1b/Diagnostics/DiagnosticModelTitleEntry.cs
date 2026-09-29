using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>One pushed title.</summary>
public sealed record DiagnosticModelTitleEntry
{
    /// <summary>Window title.</summary>
    [JsonPropertyName("window")]
    public string Window { get; init; } = "";

    /// <summary>Icon name.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";
}
