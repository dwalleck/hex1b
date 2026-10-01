using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The checkpoint a re-applicable interval starts from, and a re-application restores: the case's initial checkpoint
/// (a fresh model, or a <c>text-state/1</c> start), or a complete recovery checkpoint taken during the case.
/// </summary>
public sealed record DiagnosticCaseOrigin
{
    /// <summary>The checkpoint's profile: <c>fresh-model/1</c> or <c>text-state/1</c>.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "";

    /// <summary>The checkpoint's trigger: <c>start</c>, or <c>recovery</c>.</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; init; } = "";

    /// <summary>The checkpoint's label; absent for a fresh model, which has no checkpoint line.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The checkpoint's ordinal among the case's checkpoints; absent for a fresh model.</summary>
    [JsonPropertyName("checkpointOrdinal")]
    public long? CheckpointOrdinal { get; init; }

    /// <summary>The checkpoint line's case sequence; absent for a fresh model.</summary>
    [JsonPropertyName("caseSequence")]
    public long? CaseSequence { get; init; }

    /// <summary>The model sequence the interval starts from (0 for a fresh model).</summary>
    [JsonPropertyName("modelSequence")]
    public long ModelSequence { get; init; }
}
