using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The boundary a re-application reached.</summary>
public sealed record DiagnosticCaseReapplyTarget
{
    /// <summary>The target model sequence.</summary>
    [JsonPropertyName("modelSequence")]
    public long ModelSequence { get; init; }

    /// <summary>The checkpoint's label, when the target has one.</summary>
    [JsonPropertyName("label")]
    public string? Label { get; init; }

    /// <summary>The compared checkpoint's case sequence, when there is one.</summary>
    [JsonPropertyName("caseSequence")]
    public long? CaseSequence { get; init; }

    /// <summary>The compared checkpoint's ordinal, when there is one.</summary>
    [JsonPropertyName("checkpointOrdinal")]
    public long? CheckpointOrdinal { get; init; }
}
