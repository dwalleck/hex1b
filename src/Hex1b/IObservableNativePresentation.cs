namespace Hex1b;

/// <summary>
/// A presentation that can report what it did with one write: whether the host operating system or
/// transport took the bytes, declined them, or failed. Diagnostics record these outcomes; the write
/// itself behaves exactly as the presentation's ordinary write does.
/// </summary>
internal interface IObservableNativePresentation
{
    /// <summary>The delivery layer written to, for example <c>console</c> or <c>websocket</c>.</summary>
    string DeliveryLayer { get; }

    /// <summary>
    /// Writes <paramref name="data"/> as <see cref="IHex1bTerminalPresentationAdapter.WriteOutputAsync"/>
    /// does, and reports whether it was accepted or refused. A failure throws the same exception the
    /// ordinary write throws, after <paramref name="progress"/> records the bytes taken so far.
    /// </summary>
    ValueTask<NativeWriteResult> WriteObservedAsync(ReadOnlyMemory<byte> data, NativeWriteProgress progress, CancellationToken ct);
}
