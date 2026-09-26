namespace Hex1b;

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
