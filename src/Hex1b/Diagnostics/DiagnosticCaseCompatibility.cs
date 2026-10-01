using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// What a re-application compared between the recorded case's declarations and the build re-applying it
/// (ticket 14): seven checks in the order the consumer runs them, each with both sides' values and a verdict,
/// and whether the two builds are the same build.
/// </summary>
public sealed record DiagnosticCaseCompatibility
{
    /// <summary>The verdict of a check that passed.</summary>
    public const string Compatible = "compatible";

    /// <summary>The verdict of the check that refused the re-application (problem code <c>incompatible</c>).</summary>
    public const string Incompatible = "incompatible";

    /// <summary>The verdict of a check that did not run: an earlier check failed first, or the request was refused before it.</summary>
    public const string NotChecked = "not-checked";

    /// <summary>The checks, in the order the consumer runs them.</summary>
    public static readonly IReadOnlyList<string> CheckNames =
    [
        "formatVersion",
        "contractVersion",
        "checkpoint.profile",
        "checkpoint.coveredSurfaces",
        "configuration",
        "configuration.capabilities",
        "origin",
    ];

    /// <summary>The record of a result refused before any check ran: every check <c>not-checked</c>, the builds unknown.</summary>
    public static readonly DiagnosticCaseCompatibility Unchecked = new()
    {
        Checks = [.. CheckNames.Select(name => new DiagnosticCompatibilityCheck { Check = name })],
    };

    /// <summary>
    /// True when the producer's and consumer's build ids are both recorded and equal, false when both are recorded
    /// and differ, absent when the artifact predates the build id. Reported, never required.
    /// </summary>
    [JsonPropertyName("sameBuild")]
    public bool? SameBuild { get; init; }

    /// <summary>The checks in order (<see cref="CheckNames"/>).</summary>
    [JsonPropertyName("checks")]
    public IReadOnlyList<DiagnosticCompatibilityCheck> Checks { get; init; } = [];
}
