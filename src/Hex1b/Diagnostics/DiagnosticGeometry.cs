using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Terminal-model dimensions at the observation.
/// </summary>
public sealed class DiagnosticGeometry
{
    /// <summary>Active screen width in columns.</summary>
    [JsonPropertyName("columns")]
    public int Columns { get; init; }

    /// <summary>Active screen height in rows.</summary>
    [JsonPropertyName("rows")]
    public int Rows { get; init; }

    /// <summary>Whether the model's alternate screen buffer was active.</summary>
    [JsonPropertyName("alternateScreen")]
    public bool AlternateScreen { get; init; }

    /// <summary>Cursor column in the active screen (zero-based).</summary>
    [JsonPropertyName("cursorColumn")]
    public int CursorColumn { get; init; }

    /// <summary>Cursor row in the active screen (zero-based), excluding returned history rows.</summary>
    [JsonPropertyName("cursorRow")]
    public int CursorRow { get; init; }
}
