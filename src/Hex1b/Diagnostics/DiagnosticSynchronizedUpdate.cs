using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Whether a synchronized update (DEC private mode 2026) was in progress at the model read.
/// While one is pending, the model has applied part of an update the application has not
/// finished, so the returned content is partial and not a completed frame.
/// </summary>
public sealed class DiagnosticSynchronizedUpdate
{
    /// <summary>Whether a synchronized update had begun and not yet ended or timed out.</summary>
    [JsonPropertyName("active")]
    public bool Active { get; init; }

    /// <summary>
    /// <see cref="DiagnosticObservationIdentity.ModelSequence"/> of the output batch that began the
    /// pending update; absent when no update is pending.
    /// </summary>
    [JsonPropertyName("startedAtSequence")]
    public long? StartedAtSequence { get; init; }
}
