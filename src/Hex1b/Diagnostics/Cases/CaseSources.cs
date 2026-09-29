namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The terminal-side sources a case reads besides the model: the input tracker it observes, the
/// application adapter whose published frames it reads, and the native delivery ring it pulls from.
/// Each may be absent.
/// </summary>
internal sealed record CaseSources(InputMilestoneTracker? Input, Hex1bAppWorkloadAdapter? Application, NativeDeliveryRecorder? Delivery)
{
    // Read when a frame is published, not at start: a case started at construction precedes the app's
    // registration, and a flow swaps the registered app per step.
    public IApplicationFrameSource? Frames => Application?.ApplicationFrameSource;

    // Frames are published by an app on a diagnostic-timing adapter with a consuming input tracker.
    public bool FramesAvailable => Application is { DiagnosticTimingEnabled: true } && Input is { AcceptanceOnly: false };
}
