using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>A Hex1b build: its version string and its build id (the assembly's module version id).</summary>
public sealed record DiagnosticBuildIdentity
{
    /// <summary>The informational version (the <c>HEX1B_VERSION</c> build variable, with the commit when built from a checkout).</summary>
    [JsonPropertyName("hex1bVersion")]
    public string Hex1bVersion { get; init; } = "";

    /// <summary>The build id: the same source, path and build inputs give the same id, any change another.</summary>
    [JsonPropertyName("hex1bBuild")]
    public string? Hex1bBuild { get; init; }
}
