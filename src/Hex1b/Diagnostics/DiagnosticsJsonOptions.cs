using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// JSON serialization options for diagnostics protocol.
/// </summary>
internal static class DiagnosticsJsonOptions
{
    public static readonly JsonSerializerOptions Default = DiagnosticsJsonContext.Default.Options;

    /// <summary>Indented output for human-facing clients, using the same source-generated contract.</summary>
    public static readonly JsonSerializerOptions Indented = new(DiagnosticsJsonContext.Default.Options) { WriteIndented = true };
}
