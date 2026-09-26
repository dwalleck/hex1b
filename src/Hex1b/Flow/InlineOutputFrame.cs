namespace Hex1b.Flow;

internal readonly record struct InlineOutputFrame(
    byte[] Bytes,
    long Epoch,
    InlineOutputFrameKind Kind);
