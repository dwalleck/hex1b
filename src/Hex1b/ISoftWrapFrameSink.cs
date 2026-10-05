namespace Hex1b;

/// <summary>
/// Internal handoff from app-owned surface construction to an output owner that
/// can discard complete frames. Queue admission does not accept graphics state.
/// </summary>
internal interface ISoftWrapFrameSink
{
    long CaptureOutputEpoch();
    void SubmitRenderFrame(SoftWrapRenderFrame frame, long epoch);
    event Action? FrameRejected;
}
