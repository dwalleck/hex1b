using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Classes of content a diagnostic observation can include, exclude, or be unable to provide.
/// </summary>
[JsonConverter(typeof(DiagnosticEnumConverter<DiagnosticContentClass>))]
public enum DiagnosticContentClass
{
    /// <summary>Rendered cells of the terminal model's active screen.</summary>
    RenderedScreen,

    /// <summary>Rendered rows retained in the terminal model's history (not native scrollback).</summary>
    RenderedHistory,

    /// <summary>Text that application nodes render (labels, text blocks, selected items).</summary>
    ApplicationText,

    /// <summary>Text written with the concealed attribute (SGR 8); stored in cells but not rendered.</summary>
    ConcealedText,

    /// <summary>Hyperlink targets and parameters attached to cells; hidden, non-screen metadata.</summary>
    HyperlinkTargets,

    /// <summary>Window title and icon name; hidden, non-screen metadata.</summary>
    WindowTitle,

    /// <summary>Full text of an application editor, beyond what is rendered.</summary>
    EditorText,

    /// <summary>Raw keyboard input bytes sent to the target.</summary>
    RawInput,
}
