namespace Hex1b.Flow;

/// <summary>
/// The flow bookkeeping an atomic terminal update can move while it composes.
/// </summary>
/// <remarks>
/// <para>
/// Captured before composing and restored when the native presentation refuses the
/// batch: the bytes never reached the device, so any position, scroll offset or
/// live-region geometry the composition advanced describes a screen that was never
/// painted. Retrying from the pre-composition state is what keeps a refused batch
/// from leaving the flow's model one step ahead of the host.
/// </para>
/// <para>
/// The output epoch is deliberately absent. It only ever increases, and restoring
/// it would let the live adapter replay a frame the new origin already invalidated.
/// </para>
/// </remarks>
internal readonly record struct FlowAtomicCheckpoint(
    int CursorRow,
    int InitialRowOrigin,
    int StepRowOrigin,
    int StepHeight,
    int StepTerminalWidth);
