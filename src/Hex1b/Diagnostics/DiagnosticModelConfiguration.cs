using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Terminal configuration relevant to interpreting a terminal-model observation.
/// </summary>
public sealed class DiagnosticModelConfiguration
{
    /// <summary>Kind of workload driving the model, for example <c>hex1b-application</c> or <c>pty-process</c>.</summary>
    [JsonPropertyName("workload")]
    public string Workload { get; init; } = "";

    /// <summary>Presentation adapter type, or <c>headless</c> when output is not presented.</summary>
    [JsonPropertyName("presentation")]
    public string Presentation { get; init; } = "";

    /// <summary>Whether resize reflows soft-wrapped rows.</summary>
    [JsonPropertyName("reflowEnabled")]
    public bool ReflowEnabled { get; init; }

    /// <summary>Configured model history retention in rows; zero when not configured.</summary>
    [JsonPropertyName("historyRetentionCapacity")]
    public int HistoryRetentionCapacity { get; init; }
}
