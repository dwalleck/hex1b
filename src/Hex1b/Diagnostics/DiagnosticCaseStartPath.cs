using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>How a case was started.</summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticCaseStartPath>))]
public enum DiagnosticCaseStartPath
{
    /// <summary>When the terminal was built, before any output reached its model.</summary>
    Construction,

    /// <summary>On a running terminal.</summary>
    Live,
}
