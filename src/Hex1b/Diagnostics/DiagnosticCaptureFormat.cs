using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Rendering format of a terminal-model capture.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaptureFormat>))]
public enum DiagnosticCaptureFormat
{
    /// <summary>Plain rendered text without rendition.</summary>
    Text,

    /// <summary>ANSI escape sequences that preserve covered cell rendition.</summary>
    Ansi,

    /// <summary>An SVG rendering of the captured cells.</summary>
    Svg,

    /// <summary>An interactive HTML rendering of the captured cells.</summary>
    Html,
}
