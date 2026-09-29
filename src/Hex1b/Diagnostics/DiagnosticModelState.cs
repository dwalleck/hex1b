using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// A typed projection of a terminal model's text state (profile <c>text-state/1</c>), read in one
/// hold of the model lock. It covers every model field: each is projected here, or is excluded as a
/// clock, identity, configuration or infrastructure field, or is named in <see cref="Unsupported"/>
/// when it holds state the profile cannot represent.
/// </summary>
public sealed record DiagnosticModelState
{
    /// <summary>The projection profile.</summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = DiagnosticCaseCheckpointProfiles.TextState;

    /// <summary>The model sequence the state was read at.</summary>
    [JsonPropertyName("modelSequence")]
    public long ModelSequence { get; init; }

    /// <summary>Columns.</summary>
    [JsonPropertyName("width")]
    public int Width { get; init; }

    /// <summary>Rows.</summary>
    [JsonPropertyName("height")]
    public int Height { get; init; }

    /// <summary><c>main</c> or <c>alternate</c>.</summary>
    [JsonPropertyName("activeBuffer")]
    public string ActiveBuffer { get; init; } = "main";

    /// <summary>The active screen's rows, top to bottom.</summary>
    [JsonPropertyName("screen")]
    public IReadOnlyList<DiagnosticModelRow> Screen { get; init; } = [];

    /// <summary>The main screen's rows while the alternate screen is active; absent otherwise.</summary>
    [JsonPropertyName("savedMainScreen")]
    public IReadOnlyList<DiagnosticModelRow>? SavedMainScreen { get; init; }

    /// <summary>Retained history; absent when the model keeps none.</summary>
    [JsonPropertyName("history")]
    public DiagnosticModelHistory? History { get; init; }

    /// <summary>The distinct cell styles, referenced by index from cells.</summary>
    [JsonPropertyName("styles")]
    public IReadOnlyList<DiagnosticModelStyle> Styles { get; init; } = [];

    /// <summary>The write sequence the next written cell receives.</summary>
    [JsonPropertyName("nextCellSequence")]
    public long NextCellSequence { get; init; }

    /// <summary>The cursor.</summary>
    [JsonPropertyName("cursor")]
    public DiagnosticModelCursor Cursor { get; init; } = new();

    /// <summary>The cursor saved by DECSC; absent when none is saved.</summary>
    [JsonPropertyName("savedCursor")]
    public DiagnosticModelSavedCursor? SavedCursor { get; init; }

    /// <summary>The main screen's cursor saved on entering the alternate screen.</summary>
    [JsonPropertyName("alternateSavedCursor")]
    public DiagnosticModelSavedCursor AlternateSavedCursor { get; init; } = new();

    /// <summary>Terminal modes by name, in a fixed order.</summary>
    [JsonPropertyName("modes")]
    public IReadOnlyDictionary<string, bool> Modes { get; init; } = new Dictionary<string, bool>();

    /// <summary>Character protection mode: <c>off</c>, <c>iso</c> or <c>dec</c>.</summary>
    [JsonPropertyName("protectedMode")]
    public string ProtectedMode { get; init; } = "off";

    /// <summary>Scrolling and left/right margins.</summary>
    [JsonPropertyName("margins")]
    public DiagnosticModelMargins Margins { get; init; } = new();

    /// <summary>Tab stops.</summary>
    [JsonPropertyName("tabStops")]
    public DiagnosticModelTabStops TabStops { get; init; } = new();

    /// <summary>Designated and invoked character sets.</summary>
    [JsonPropertyName("charsets")]
    public DiagnosticModelCharsets Charsets { get; init; } = new();

    /// <summary>The rendition the next written cell receives.</summary>
    [JsonPropertyName("rendition")]
    public DiagnosticModelStyle Rendition { get; init; } = new();

    /// <summary>Window and icon titles and the title stack.</summary>
    [JsonPropertyName("titles")]
    public DiagnosticModelTitles Titles { get; init; } = new();

    /// <summary>Progress, shell integration and working directory.</summary>
    [JsonPropertyName("activity")]
    public DiagnosticModelActivity Activity { get; init; } = new();

    /// <summary>Retained command marks, oldest first.</summary>
    [JsonPropertyName("commandMarks")]
    public IReadOnlyList<DiagnosticModelCommandMark> CommandMarks { get; init; } = [];

    /// <summary>The number the next command mark's anchor is named with, less one.</summary>
    [JsonPropertyName("lastCommandAnchorId")]
    public long LastCommandAnchorId { get; init; }

    /// <summary>The last printed cell, which REP and grapheme continuation refer to; absent when none.</summary>
    [JsonPropertyName("lastPrinted")]
    public DiagnosticModelLastPrinted? LastPrinted { get; init; }

    /// <summary>Whether the next printed text may join the last printed grapheme.</summary>
    [JsonPropertyName("pendingGraphemeCombine")]
    public bool PendingGraphemeCombine { get; init; }

    /// <summary>Output bytes and text held between chunks.</summary>
    [JsonPropertyName("pendingInput")]
    public DiagnosticModelPendingInput PendingInput { get; init; } = new();

    /// <summary>Whether a synchronized update was in progress.</summary>
    [JsonPropertyName("synchronizedUpdate")]
    public DiagnosticSynchronizedUpdate SynchronizedUpdate { get; init; } = new();

    /// <summary>
    /// State surfaces the profile cannot represent that hold state (<c>graphics</c>,
    /// <c>dcs-continuation</c>), in name order. A projection naming any is not comparable.
    /// </summary>
    [JsonPropertyName("unsupported")]
    public IReadOnlyList<string> Unsupported { get; init; } = [];
}
