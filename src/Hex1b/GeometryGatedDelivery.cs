namespace Hex1b;

/// <summary>
/// One queued output batch whose delivery is conditioned on the native
/// presentation's geometry at write time.
/// </summary>
/// <remarks>
/// The receipt is the only acknowledgement strong enough to gate a retry: it is
/// completed after the adapter has either written the bytes or refused them, not
/// when the batch was merely queued or dequeued.
/// </remarks>
internal sealed class GeometryGatedDelivery(int expectedWidth, int expectedHeight)
{
    /// <summary>Columns the batch was composed for.</summary>
    public int ExpectedWidth { get; } = expectedWidth;

    /// <summary>Rows the batch was composed for.</summary>
    public int ExpectedHeight { get; } = expectedHeight;

    /// <summary>
    /// Completes with the delivery's outcome, or faults when the batch could not
    /// be offered to the presentation at all.
    /// </summary>
    public TaskCompletionSource<NativeDeliveryOutcome> Completion { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}
