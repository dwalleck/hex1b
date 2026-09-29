using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Output held between chunks.</summary>
public sealed record DiagnosticModelPendingInput
{
    /// <summary>An unfinished escape sequence's decoded text.</summary>
    [JsonPropertyName("escapePrefix")]
    public string EscapePrefix { get; init; } = "";

    /// <summary>Undecoded bytes of an unfinished UTF-8 sequence, base64.</summary>
    [JsonPropertyName("utf8")]
    public string Utf8 { get; init; } = "";

    /// <summary>Whether an ESC is held because the next byte may begin a DCS.</summary>
    [JsonPropertyName("groundEscape")]
    public bool GroundEscape { get; init; }

    /// <summary>UTF-8 continuation bytes the byte framer still expects.</summary>
    [JsonPropertyName("framerUtf8Remaining")]
    public int FramerUtf8Remaining { get; init; }
}
