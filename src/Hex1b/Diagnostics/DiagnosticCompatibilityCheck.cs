using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// One compatibility check between a recorded case and the build re-applying it: what the artifact declares,
/// what this build supports, and the verdict.
/// </summary>
public sealed record DiagnosticCompatibilityCheck
{
    /// <summary>The check's name (<see cref="DiagnosticCaseCompatibility.CheckNames"/>).</summary>
    [JsonPropertyName("check")]
    public string Check { get; init; } = "";

    /// <summary>What the artifact declares; absent until the check ran (the origin's is recorded as the origin is selected, before its verdict).</summary>
    [JsonPropertyName("producer")]
    public string? Producer { get; init; }

    /// <summary>What the consumer build supports.</summary>
    [JsonPropertyName("consumer")]
    public string? Consumer { get; init; }

    /// <summary><c>compatible</c>, <c>incompatible</c>, or <c>not-checked</c> (an earlier check failed first, or the request was refused before it ran).</summary>
    [JsonPropertyName("verdict")]
    public string Verdict { get; init; } = DiagnosticCaseCompatibility.NotChecked;
}
