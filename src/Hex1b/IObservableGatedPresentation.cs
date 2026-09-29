namespace Hex1b;

/// <summary>
/// An observable presentation that gates writes on geometry: the observed form of
/// <see cref="IGeometryGatedPresentationAdapter.WriteOutputIfGeometryAsync"/>.
/// </summary>
internal interface IObservableGatedPresentation : IObservableNativePresentation, IGeometryGatedPresentationAdapter
{
    /// <summary>
    /// Writes like <see cref="IGeometryGatedPresentationAdapter.WriteOutputIfGeometryAsync"/>, advancing
    /// <paramref name="progress"/> by the bytes the host takes.
    /// </summary>
    ValueTask<NativeDeliveryOutcome> WriteObservedIfGeometryAsync(ReadOnlyMemory<byte> data, int expectedWidth, int expectedHeight,
        NativeWriteProgress progress, CancellationToken ct);
}
