using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Hidden terminal metadata returned only with <see cref="DiagnosticAuthorization.NonScreenMetadata"/>.
/// </summary>
public sealed class DiagnosticNonScreenMetadata
{
    /// <summary>Current window title (OSC 0/2).</summary>
    [JsonPropertyName("windowTitle")]
    public string WindowTitle { get; init; } = "";

    /// <summary>Current icon name (OSC 0/1).</summary>
    [JsonPropertyName("iconName")]
    public string IconName { get; init; } = "";
}
