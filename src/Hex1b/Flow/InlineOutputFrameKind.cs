namespace Hex1b.Flow;

/// <summary>
/// Workload adapter for inline step rendering. Renders in the normal terminal buffer
/// without entering the alternate screen. All cursor positioning is offset by the
/// step's row origin in the terminal.
/// </summary>
internal enum InlineOutputFrameKind
{
    Data,
    DiscardBoundary,
}
