namespace Hex1b.Diagnostics;

/// <summary>
/// One publication by the application loop: the projected frame, or the failure that prevented
/// projecting that frame. Immutable once published.
/// </summary>
internal sealed record PublishedApplicationFrame(
    long FrameId,
    DiagnosticApplicationFrame? Frame,
    string? Failure,
    long ProjectionStartTimestamp,
    long ProjectionEndTimestamp,
    DateTimeOffset ProjectionStart,
    DateTimeOffset ProjectionEnd);
