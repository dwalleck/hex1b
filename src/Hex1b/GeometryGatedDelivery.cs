namespace Hex1b;

/// <summary>
/// How a geometry-gated native delivery ended.
/// </summary>
public enum NativeDeliveryOutcome
{
    /// <summary>The bytes reached the native presentation's write path.</summary>
    Applied,

    /// <summary>
    /// The native presentation no longer reported the geometry the bytes were
    /// composed for, so nothing was written.
    /// </summary>
    GeometryChanged,
}

/// <summary>
/// A presentation adapter that can refuse bytes composed for a superseded
/// native geometry instead of writing them.
/// </summary>
/// <remarks>
/// <para>
/// A native host can resize between the moment a producer composes a batch and
/// the moment that batch reaches the device. Positioned and scrolling bytes are
/// only meaningful for the geometry they were composed against: a clear loop
/// that targets rows below a shrunken viewport clamps onto the new bottom row,
/// where each linefeed scrolls live content into the host's scrollback, and a
/// bottom-row linefeed written into a grown viewport no longer scrolls at all,
/// so the erase that follows it destroys the row it was meant to push out.
/// </para>
/// <para>
/// The geometry read and the write are two separate operating-system calls, so
/// an implementation narrows — it does not close — that window: a resize applied
/// between the read and the write is still written. What it guarantees is the
/// direction that corrupts retained history: bytes composed for geometry G are
/// never written once the adapter has observed a geometry other than G.
/// </para>
/// <para>
/// <see cref="NativeDeliveryOutcome.GeometryChanged"/> means the bytes were not
/// written, so the producer may recompose and retry. Every other failure must be
/// reported as an exception, because a write that may have started can never be
/// replayed.
/// </para>
/// </remarks>
public interface IGeometryGatedPresentationAdapter : IHex1bTerminalPresentationAdapter
{
    /// <summary>
    /// Writes <paramref name="data"/> only when the native presentation still
    /// reports exactly <paramref name="expectedWidth"/> columns by
    /// <paramref name="expectedHeight"/> rows.
    /// </summary>
    /// <param name="data">The composed bytes to deliver.</param>
    /// <param name="expectedWidth">Columns the bytes were composed for.</param>
    /// <param name="expectedHeight">Rows the bytes were composed for.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>
    /// <see cref="NativeDeliveryOutcome.Applied"/> when the bytes were written;
    /// <see cref="NativeDeliveryOutcome.GeometryChanged"/> when they were not.
    /// </returns>
    ValueTask<NativeDeliveryOutcome> WriteOutputIfGeometryAsync(
        ReadOnlyMemory<byte> data,
        int expectedWidth,
        int expectedHeight,
        CancellationToken ct = default);
}

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
