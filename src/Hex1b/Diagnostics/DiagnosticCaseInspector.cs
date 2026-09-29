namespace Hex1b.Diagnostics;

/// <summary>
/// Reads a diagnostic case artifact from disk, without the process that recorded it. The CLI and MCP
/// inspect cases through this one reader.
/// </summary>
public static class DiagnosticCaseInspector
{
    /// <summary>Most events one inspection returns.</summary>
    public const int MaxEvents = Cases.CaseArtifactReader.MaxEvents;

    /// <summary>Inspects the case artifact at the request's path.</summary>
    public static DiagnosticCaseInspection Inspect(DiagnosticCaseInspectRequest request) => Cases.CaseArtifactReader.Inspect(request);
}
