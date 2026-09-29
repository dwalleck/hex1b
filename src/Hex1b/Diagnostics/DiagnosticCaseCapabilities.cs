using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// The presentation's terminal capabilities, recorded field by field (manifest format 2). A field
/// this build does not know is kept in <see cref="Unknown"/>, so re-application can refuse it.
/// </summary>
public sealed record DiagnosticCaseCapabilities
{
    /// <summary>Presentation supports the delta protocol.</summary>
    [JsonPropertyName("supportsDeltaProtocol")]
    public bool SupportsDeltaProtocol { get; init; }

    /// <summary>Presentation supports Sixel graphics.</summary>
    [JsonPropertyName("supportsSixel")]
    public bool SupportsSixel { get; init; }

    /// <summary>How the presentation renders Sixel graphics (the <c>SixelPresentationSupport</c> name).</summary>
    [JsonPropertyName("sixelSupport")]
    public string SixelSupport { get; init; } = "";

    /// <summary>Reported cell metrics for Sixel placement; absent when none.</summary>
    [JsonPropertyName("sixelCellMetrics")]
    public DiagnosticCaseSixelCellMetrics? SixelCellMetrics { get; init; }

    /// <summary>Presentation supports mouse input.</summary>
    [JsonPropertyName("supportsMouse")]
    public bool SupportsMouse { get; init; }

    /// <summary>Presentation supports 24-bit color.</summary>
    [JsonPropertyName("supportsTrueColor")]
    public bool SupportsTrueColor { get; init; }

    /// <summary>Presentation supports 256 colors.</summary>
    [JsonPropertyName("supports256Colors")]
    public bool Supports256Colors { get; init; }

    /// <summary>Presentation supports the alternate screen.</summary>
    [JsonPropertyName("supportsAlternateScreen")]
    public bool SupportsAlternateScreen { get; init; }

    /// <summary>Presentation handles the alternate screen natively.</summary>
    [JsonPropertyName("handlesAlternateScreenNatively")]
    public bool HandlesAlternateScreenNatively { get; init; }

    /// <summary>Presentation supports bracketed paste.</summary>
    [JsonPropertyName("supportsBracketedPaste")]
    public bool SupportsBracketedPaste { get; init; }

    /// <summary>Presentation supports the Kitty graphics protocol.</summary>
    [JsonPropertyName("supportsKgp")]
    public bool SupportsKgp { get; init; }

    /// <summary>Presentation widens a glyph when a variation selector follows it.</summary>
    [JsonPropertyName("supportsRetroactiveVariationSelectors")]
    public bool SupportsRetroactiveVariationSelectors { get; init; }

    /// <summary>Cell width in pixels.</summary>
    [JsonPropertyName("cellPixelWidth")]
    public int CellPixelWidth { get; init; }

    /// <summary>Measured cell width in pixels; 0 when not measured.</summary>
    [JsonPropertyName("actualCellPixelWidth")]
    public double ActualCellPixelWidth { get; init; }

    /// <summary>Cell height in pixels.</summary>
    [JsonPropertyName("cellPixelHeight")]
    public int CellPixelHeight { get; init; }

    /// <summary>Default foreground as 0xRRGGBB.</summary>
    [JsonPropertyName("defaultForeground")]
    public int DefaultForeground { get; init; }

    /// <summary>Default background as 0xRRGGBB.</summary>
    [JsonPropertyName("defaultBackground")]
    public int DefaultBackground { get; init; }

    /// <summary>Presentation supports styled underlines.</summary>
    [JsonPropertyName("supportsStyledUnderlines")]
    public bool SupportsStyledUnderlines { get; init; }

    /// <summary>Presentation supports underline colors.</summary>
    [JsonPropertyName("supportsUnderlineColor")]
    public bool SupportsUnderlineColor { get; init; }

    /// <summary>Recorded fields this build does not know.</summary>
    [JsonExtensionData]
    public Dictionary<string, JsonElement>? Unknown { get; set; }
}
