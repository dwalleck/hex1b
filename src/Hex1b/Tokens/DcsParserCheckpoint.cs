namespace Hex1b.Tokens;

/// <summary>
/// A no-copy view of a DCS prefix. The parser never changes an owned retained prefix: append writes beyond it,
/// growth replaces the array, and completion or a new DCS replaces rather than clears the array.
/// </summary>
internal readonly record struct DcsParserCheckpoint(
    DcsParserCheckpoint.StateKind State,
    DcsParserCheckpoint.StateKind StateBeforeEscape,
    ReadOnlyMemory<byte> RetainedContent,
    long ByteCount,
    bool RetentionLimitExceeded,
    bool IsSixel)
{
    internal enum StateKind
    {
        Ground,
        GroundEscape,
        Introducer,
        Payload,
        MalformedIntroducer,
        DcsEscape,
    }
}
