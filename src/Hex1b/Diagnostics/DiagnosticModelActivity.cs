using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>Projected activity state.</summary>
public sealed record DiagnosticModelActivity
{
    /// <summary>OSC 9;4 progress state name.</summary>
    [JsonPropertyName("progressState")]
    public string ProgressState { get; init; } = "";

    /// <summary>OSC 9;4 percentage.</summary>
    [JsonPropertyName("progressPercentage")]
    public int? ProgressPercentage { get; init; }

    /// <summary>OSC 133 shell phase name.</summary>
    [JsonPropertyName("shellPhase")]
    public string ShellPhase { get; init; } = "";

    /// <summary>OSC 133 last exit code.</summary>
    [JsonPropertyName("lastExitCode")]
    public int? LastExitCode { get; init; }

    /// <summary>OSC 7 working directory URI.</summary>
    [JsonPropertyName("workingDirectoryUri")]
    public string? WorkingDirectoryUri { get; init; }

    /// <summary>OSC 7 host.</summary>
    [JsonPropertyName("workingDirectoryHost")]
    public string? WorkingDirectoryHost { get; init; }

    /// <summary>OSC 7 path.</summary>
    [JsonPropertyName("workingDirectoryPath")]
    public string? WorkingDirectoryPath { get; init; }
}
