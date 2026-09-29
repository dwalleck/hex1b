using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A fault injected into a reconstructed state, and the path it changed.</summary>
public sealed record DiagnosticCaseInjectedFault
{
    /// <summary>The declared fault kind.</summary>
    [JsonPropertyName("kind")]
    public string Kind { get; init; } = "";

    /// <summary>The first path the fault changed.</summary>
    [JsonPropertyName("path")]
    public string Path { get; init; } = "";
}
