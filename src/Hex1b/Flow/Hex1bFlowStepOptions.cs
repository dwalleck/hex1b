using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Options for configuring an inline flow step.
/// </summary>
public sealed class Hex1bFlowStepOptions
{
    /// <summary>
    /// Maximum height in rows for the step. If null, defaults to terminal height.
    /// </summary>
    public int? MaxHeight { get; set; }

    /// <summary>
    /// Minimum height in rows for the step's live region.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The live region is allocated at least this tall even when the initial
    /// body is only one or two rows, so the region is stable and reliably
    /// interactive from the first frame instead of growing with its content.
    /// Use this rather than padding the body with filler rows.
    /// </para>
    /// <para>
    /// Always clamped to the host terminal height and to
    /// <see cref="MaxHeight"/>, so it can never allocate off-screen.
    /// </para>
    /// </remarks>
    public int? MinHeight { get; set; }

    /// <summary>
    /// Whether to enable mouse input for this step. Defaults to false.
    /// </summary>
    public bool EnableMouse { get; set; }
}
