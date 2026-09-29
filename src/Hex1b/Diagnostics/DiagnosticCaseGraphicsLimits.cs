using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>The terminal's graphics resource limits, recorded field by field (manifest format 2).</summary>
public sealed record DiagnosticCaseGraphicsLimits
{
    /// <summary>Retained input bytes per image.</summary>
    [JsonPropertyName("maximumRetainedInputBytesPerImage")]
    public int MaximumRetainedInputBytesPerImage { get; init; }

    /// <summary>Raster pixels per image.</summary>
    [JsonPropertyName("maximumRasterPixelsPerImage")]
    public long MaximumRasterPixelsPerImage { get; init; }

    /// <summary>Raster operations per image.</summary>
    [JsonPropertyName("maximumRasterOperationsPerImage")]
    public long MaximumRasterOperationsPerImage { get; init; }

    /// <summary>Images per screen.</summary>
    [JsonPropertyName("maximumImagesPerScreen")]
    public int MaximumImagesPerScreen { get; init; }

    /// <summary>Placements per screen.</summary>
    [JsonPropertyName("maximumPlacementsPerScreen")]
    public int MaximumPlacementsPerScreen { get; init; }

    /// <summary>Placements retained in history.</summary>
    [JsonPropertyName("maximumHistoryPlacements")]
    public int MaximumHistoryPlacements { get; init; }

    /// <summary>Retained logical pixels per screen.</summary>
    [JsonPropertyName("maximumRetainedLogicalPixelsPerScreen")]
    public long MaximumRetainedLogicalPixelsPerScreen { get; init; }

    /// <summary>Retained bytes per screen.</summary>
    [JsonPropertyName("maximumRetainedBytesPerScreen")]
    public long MaximumRetainedBytesPerScreen { get; init; }

    /// <summary>Recorded fields this build does not know.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
