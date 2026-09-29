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

    /// <summary>
    /// Original terminal output and control data as it entered the model, its geometry ordering, and
    /// checkpoint continuation, including non-screen metadata. It grants neither raw keyboard input nor
    /// editor text.
    /// </summary>
    ReapplicationData,
}
