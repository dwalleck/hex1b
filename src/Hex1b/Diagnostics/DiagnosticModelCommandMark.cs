using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A projected command mark. Its text position is an identity the model assigns lazily when text is
/// read, so it depends on observation and is not projected.
/// </summary>
public sealed record DiagnosticModelCommandMark
{
    /// <summary>The mark's anchor name.</summary>
    [JsonPropertyName("anchor")]
    public string Anchor { get; init; } = "";

    /// <summary>Shell phase name.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    /// <summary>Exit code.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Raw OSC 133 parameters.</summary>
    [JsonPropertyName("rawParameters")]
    public string? RawParameters { get; init; }
}
