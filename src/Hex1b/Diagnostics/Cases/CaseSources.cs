namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The terminal-side sources a case reads besides the model: the input tracker it observes, the
/// application's published frames, and the native delivery ring it pulls from. Each may be absent.
/// </summary>
internal sealed record CaseSources(InputMilestoneTracker? Input, IApplicationFrameSource? Frames, NativeDeliveryRecorder? Delivery);
