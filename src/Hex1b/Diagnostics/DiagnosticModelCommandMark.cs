using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A projected command mark, with the position of its text: the buffer, a row and the column. Main rows are the
/// retained history rows (oldest first) followed by the main screen's rows (the saved main screen while the alternate
/// screen is active); alternate rows are the alternate screen's. The text row identities behind a position are
/// assigned lazily when text is read, so they are not projected.
/// </summary>
public sealed record DiagnosticModelCommandMark
{
    /// <summary>The mark's anchor name.</summary>
    [JsonPropertyName("anchor")]
    public string Anchor { get; init; } = "";

    /// <summary>Shell phase name.</summary>
    [JsonPropertyName("phase")]
    public string Phase { get; init; } = "";

    /// <summary>Exit code.</summary>
    [JsonPropertyName("exitCode")]
    public int? ExitCode { get; init; }

    /// <summary>Raw OSC 133 parameters.</summary>
    [JsonPropertyName("rawParameters")]
    public string? RawParameters { get; init; }

    /// <summary>The buffer the mark's text is in: <c>main</c> or <c>alternate</c>.</summary>
    [JsonPropertyName("buffer")]
    public string Buffer { get; init; } = "main";

    /// <summary>
    /// The row of the mark's text in its buffer, or null when that text is gone and the mark awaits collection.
    /// </summary>
    [JsonPropertyName("row")]
    public int? Row { get; init; }

    /// <summary>
    /// The column the mark was placed at: at most the row's width (a mark placed at a pending wrap is past the last
    /// cell). Null exactly when <see cref="Row"/> is.
    /// </summary>
    [JsonPropertyName("column")]
    public int? Column { get; init; }
}
