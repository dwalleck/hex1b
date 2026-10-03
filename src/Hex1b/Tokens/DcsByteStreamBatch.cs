namespace Hex1b.Tokens;

internal readonly record struct DcsByteStreamBatch(
    ReadOnlyMemory<byte> TextBytes,
    IReadOnlyList<DcsFrameBoundary> Frames,
    bool SixelIdentified = false)
{
    public static DcsByteStreamBatch Empty { get; } = new(
        ReadOnlyMemory<byte>.Empty,
        Array.Empty<DcsFrameBoundary>());
}
