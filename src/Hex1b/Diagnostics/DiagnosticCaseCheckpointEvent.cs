using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A checkpoint recorded during a case: the model's state at a model sequence, taken in one hold of the
/// model lock, at a mark or when the case stopped.
/// </summary>
public sealed record DiagnosticCaseCheckpointEvent
{
    /// <summary>Orders the case's checkpoints, from 1, in the order they were taken.</summary>
    [JsonPropertyName("ordinal")]
    public long Ordinal { get; init; }

    /// <summary>The checkpoint's label: the mark's label, or <c>stop</c>.</summary>
    [JsonPropertyName("label")]
    public string Label { get; init; } = "";

    /// <summary><c>mark</c> or <c>stop</c>.</summary>
    [JsonPropertyName("trigger")]
    public string Trigger { get; init; } = "";

    /// <summary>
    /// <c>recorded</c> (state present), <c>unavailable</c> (the boundary only, see <see cref="Reason"/>), or
    /// <c>missing</c> (state taken but not written, see <see cref="Reason"/>).
    /// </summary>
    [JsonPropertyName("status")]
    public string Status { get; init; } = "";

    /// <summary>Why the state is absent.</summary>
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }

    /// <summary>The state's projection profile.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = DiagnosticCaseCheckpointProfiles.TextState;

    /// <summary>How long the model lock was held to read the state, in milliseconds; absent without state.</summary>
    [JsonPropertyName("captureMilliseconds")]
    public double? CaptureMilliseconds { get; init; }

    /// <summary>The state; absent unless <see cref="Status"/> is <c>recorded</c>.</summary>
    [JsonPropertyName("state")]
    public DiagnosticModelState? State { get; init; }

    /// <summary>True when an inspection page left out the recorded state; re-application reads it.</summary>
    [JsonPropertyName("stateOmitted")]
    public bool? StateOmitted { get; init; }
}
