using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// What a milestone capture waited for and what it actually observed. Observed values can
/// exceed the requested input: they describe the target when the wait ended.
/// </summary>
public sealed class DiagnosticMilestoneResult
{
    /// <summary>The requested stage.</summary>
    [JsonPropertyName("milestone")]
    public DiagnosticMilestone Milestone { get; init; }

    /// <summary>The requested input id.</summary>
    [JsonPropertyName("inputId")]
    public long InputId { get; init; }

    /// <summary>Whether the milestone was met.</summary>
    [JsonPropertyName("met")]
    public bool Met { get; init; }

    /// <summary>The highest input id accepted in this session when the wait ended.</summary>
    [JsonPropertyName("acceptedInput")]
    public long AcceptedInput { get; init; }

    /// <summary>The highest input id the application loop had processed when the wait ended.</summary>
    [JsonPropertyName("processedInput")]
    public long ProcessedInput { get; init; }

    /// <summary>The application instance that processed the input, when it has been processed.</summary>
    [JsonPropertyName("processedBy")]
    public string? ProcessedBy { get; init; }

    /// <summary>The first published frame covering the input, for frame and model milestones.</summary>
    [JsonPropertyName("frame")]
    public DiagnosticMilestoneFrame? Frame { get; init; }

    /// <summary>The terminal-model sequence observed when a model milestone was met.</summary>
    [JsonPropertyName("modelSequence")]
    public long? ModelSequence { get; init; }

    /// <summary>The awaited input's record, when retained; its payload only with raw-input authorization.</summary>
    [JsonPropertyName("input")]
    public DiagnosticInputRecord? Input { get; init; }
}
