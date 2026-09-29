using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Explicit opt-ins for content beyond the default rendered-screen policy.
/// Each authorization is independent; none implies another.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticAuthorization>))]
public enum DiagnosticAuthorization
{
    /// <summary>Hidden terminal metadata such as hyperlink targets and titles.</summary>
    NonScreenMetadata,

    /// <summary>Full application editor text.</summary>
    EditorText,

    /// <summary>Raw keyboard input.</summary>
    RawInput,

    /// <summary>Bytes a terminal wrote to its native presentation (screen output).</summary>
    NativeOutput,
}
