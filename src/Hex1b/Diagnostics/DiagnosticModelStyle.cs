using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A cell style. Colors are <c>default</c>, <c>rgb:#rrggbb</c>, or <c>standard:n:#rrggbb</c>,
/// <c>bright:n:#rrggbb</c> and <c>indexed:n:#rrggbb</c>; an absent color is unset.
/// </summary>
public sealed record DiagnosticModelStyle
{
    /// <summary>Attribute names (<c>bold</c>, <c>soft-wrap</c>, <c>protected</c>, ...) in bit order.</summary>
    [JsonPropertyName("attributes")]
    public IReadOnlyList<string> Attributes { get; init; } = [];

    /// <summary>Foreground color.</summary>
    [JsonPropertyName("foreground")]
    public string? Foreground { get; init; }

    /// <summary>Background color.</summary>
    [JsonPropertyName("background")]
    public string? Background { get; init; }

    /// <summary>Underline color.</summary>
    [JsonPropertyName("underlineColor")]
    public string? UnderlineColor { get; init; }

    /// <summary>Underline style: <c>none</c>, <c>single</c>, <c>double</c>, <c>curly</c>, <c>dotted</c> or <c>dashed</c>.</summary>
    [JsonPropertyName("underlineStyle")]
    public string UnderlineStyle { get; init; } = "none";

    /// <summary>Hyperlink target; absent when the cell has no hyperlink.</summary>
    [JsonPropertyName("hyperlinkUri")]
    public string? HyperlinkUri { get; init; }

    /// <summary>Hyperlink parameters (such as <c>id=</c>); absent when the cell has no hyperlink.</summary>
    [JsonPropertyName("hyperlinkParameters")]
    public string? HyperlinkParameters { get; init; }
}
