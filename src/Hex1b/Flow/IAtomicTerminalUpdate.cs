namespace Hex1b.Flow;

/// <summary>
/// One serialized terminal update: bytes are composed into a buffer and handed
/// to the terminal when the scope is disposed.
/// </summary>
/// <remarks>
/// <para>
/// Synchronous by contract: nothing inside the scope may await, it must be
/// disposed on the thread that opened it, and an empty scope writes nothing.
/// </para>
/// <para>
/// The caller must keep the scope object rather than discarding it in a
/// <c>using</c> block, because <see cref="FlushCompleted"/> is the only place
/// that says whether the update was actually handed off — the update's own
/// failure cannot carry that fact.
/// </para>
/// <para>
/// Acceptance is not consumption: a completed hand-off means the adapter took
/// the bytes, never that the host terminal displayed or retained them.
/// </para>
/// </remarks>
internal interface IAtomicTerminalUpdate : IDisposable
{
    /// <summary>
    /// True once the adapter accepted the composed bytes.
    /// </summary>
    /// <remarks>
    /// False when the buffer was never handed off because the write failed. True
    /// for a scope that composed nothing: an empty scope writes nothing, so it
    /// has no hand-off to fail.
    /// </remarks>
    bool FlushCompleted { get; }

    /// <summary>
    /// True when this scope can hand its bytes off conditionally, so a batch
    /// composed for a superseded native geometry is refused instead of written.
    /// </summary>
    bool SupportsGeometryGatedDelivery { get; }

    /// <summary>
    /// Releases the scope and offers its composed bytes for delivery only if the
    /// native presentation still reports the geometry they were composed for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Returns once the bytes have been <em>offered</em>, not written, and must be
    /// called from the thread that opened the scope. The returned task is the
    /// delivery's outcome and completes later, outside the scope's locks, so a
    /// caller never waits on the output path while holding the step and terminal
    /// write locks.
    /// </para>
    /// <para>
    /// A scope that composed nothing has nothing to offer and returns
    /// <see langword="null"/>.
    /// </para>
    /// </remarks>
    /// <param name="expectedWidth">Columns the composed bytes assume.</param>
    /// <param name="expectedHeight">Rows the composed bytes assume.</param>
    /// <returns>The delivery's outcome, or null when the scope was empty.</returns>
    Task<NativeDeliveryOutcome>? SubmitIfGeometry(int expectedWidth, int expectedHeight);
}
