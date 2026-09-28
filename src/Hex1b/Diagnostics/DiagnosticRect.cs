using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hex1b.Layout;
using Hex1b.Nodes;

namespace Hex1b.Diagnostics;

/// <summary>
/// A rectangle in terminal cells: columns from the left, rows from the top.
/// </summary>
public sealed class DiagnosticRect
{
    /// <summary>Left column.</summary>
    [JsonPropertyName("x")]
    public int X { get; init; }
    
    /// <summary>Top row.</summary>
    [JsonPropertyName("y")]
    public int Y { get; init; }
    
    /// <summary>Width in columns.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }
    
    /// <summary>Height in rows.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }
    
    internal static DiagnosticRect FromRect(Rect rect) => new()
    {
        X = rect.X,
        Y = rect.Y,
        Width = rect.Width,
        Height = rect.Height
    };
    
    /// <inheritdoc />
    public override string ToString() => $"x={X} y={Y} w={Width} h={Height} ({X},{Y} → {X + Width},{Y + Height})";
}
