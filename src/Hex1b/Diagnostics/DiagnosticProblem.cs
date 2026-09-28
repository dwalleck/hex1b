using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Machine-readable reason an operation did not produce an observation.
/// </summary>
public sealed class DiagnosticProblem
{
    /// <summary>Stable kebab-case code, for example <c>target-unreachable</c>.</summary>
    [JsonPropertyName("code")]
    public string Code { get; init; } = "";

    /// <summary>Human-readable explanation.</summary>
    [JsonPropertyName("message")]
    public string Message { get; init; } = "";
}
