using Hex1b.Layout;

namespace Hex1b.Flow;

/// <summary>Reports valid measured content that exceeds requested Flow materialization bounds.</summary>
/// <remarks>
/// Measured dimensions come from layout constrained to the requested width and
/// maximum height plus one row. They are not necessarily unconstrained natural
/// dimensions. This refusal occurs before rendering; cleanup failures may wrap it.
/// Invalid measurements and the renderer's hard dimension limit use other errors.
/// </remarks>
/// <seealso cref="FlowStep.MeasureWidgetAsync"/>
public sealed class FlowWidgetBoundsException : InvalidOperationException
{
    internal FlowWidgetBoundsException(Size measured, int width, int maxHeight)
        : base($"Widget content exceeds width {width} or maximum height {maxHeight}.")
    {
        MeasuredWidth = measured.Width;
        MeasuredHeight = measured.Height;
        RequestedWidth = width;
        RequestedMaxHeight = maxHeight;
    }

    /// <summary>Gets the width returned by constrained measurement.</summary>
    public int MeasuredWidth { get; }
    /// <summary>Gets the height returned by constrained measurement.</summary>
    public int MeasuredHeight { get; }
    /// <summary>Gets the requested materialization width.</summary>
    public int RequestedWidth { get; }
    /// <summary>Gets the requested maximum materialization height.</summary>
    public int RequestedMaxHeight { get; }
}
