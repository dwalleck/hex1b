namespace Hex1b;

/// <summary>
/// One required output batch with a processing receipt and optional geometry gate.
/// </summary>
/// <remarks>
/// Queue admission is not acknowledgement. The terminal completes the receipt
/// after applying/forwarding the batch or refusing it before application.
/// </remarks>
internal sealed class WorkloadDelivery
{
    public WorkloadDelivery() { }

    public WorkloadDelivery(int expectedWidth, int expectedHeight)
    {
        IsGeometryGated = true;
        ExpectedWidth = expectedWidth;
        ExpectedHeight = expectedHeight;
    }

    public bool IsGeometryGated { get; }
    public int ExpectedWidth { get; }
    public int ExpectedHeight { get; }

    public TaskCompletionSource<NativeDeliveryOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
