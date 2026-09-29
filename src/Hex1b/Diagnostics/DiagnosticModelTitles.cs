using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected titles.</summary>
public sealed record DiagnosticModelTitles
{
    /// <summary>Window title.</summary>
    [JsonPropertyName("window")]
    public string Window { get; init; } = "";

    /// <summary>Icon name.</summary>
    [JsonPropertyName("icon")]
    public string Icon { get; init; } = "";

    /// <summary>Pushed titles, most recent first.</summary>
    [JsonPropertyName("stack")]
    public IReadOnlyList<DiagnosticModelTitleEntry> Stack { get; init; } = [];
}
