using ParserState = Hex1b.Tokens.DcsParserCheckpoint.StateKind;

namespace Hex1b.Tokens;

internal sealed partial class DcsByteStreamParser
{
    internal DcsParserCheckpoint CaptureCheckpoint() => new(
        _state,
        _state == ParserState.DcsEscape ? _stateBeforeDcsEscape : ParserState.Ground,
        IsInDcs ? _retained.AsMemory(0, _retainedCount) : ReadOnlyMemory<byte>.Empty,
        IsInDcs ? _byteCount : 0,
        IsInDcs && _retentionLimitExceeded,
        IsSixel);

    /// <summary>
    /// Reconstructs an open non-Sixel DCS with this parser's effective policy without changing this parser.
    /// The optional retained array transfers private ownership; it must be the entire checkpoint content,
    /// never an array belonging to a caller's checkpoint view. Replay rebuilds the existing grammar and hash once.
    /// </summary>
    /// <exception cref="ArgumentException">The continuation is not intact, supported, or consistent with its prefix.</exception>
    internal DcsByteStreamParser CreateRestored(
        DcsParserCheckpoint checkpoint,
        byte[]? ownedRetainedContent = null)
    {
        if (!IsOpenDcsState(checkpoint.State) ||
            (checkpoint.State == ParserState.DcsEscape
                ? !IsContentDcsState(checkpoint.StateBeforeEscape)
                : checkpoint.StateBeforeEscape != ParserState.Ground) ||
            checkpoint.RetentionLimitExceeded || checkpoint.IsSixel ||
            checkpoint.ByteCount != checkpoint.RetainedContent.Length ||
            checkpoint.RetainedContent.Length > _retentionLimit)
        {
            throw new ArgumentException("The DCS continuation is not intact or supported.", nameof(checkpoint));
        }

        if (ownedRetainedContent is not null &&
            !checkpoint.RetainedContent.Equals((ReadOnlyMemory<byte>)ownedRetainedContent))
        {
            throw new ArgumentException("The owned array must contain the entire retained prefix.", nameof(ownedRetainedContent));
        }

        var candidate = new DcsByteStreamParser(_sixelPolicy);
        try
        {
            candidate.StartDcs();
            if (ownedRetainedContent is not null)
            {
                // The canonical introducer has initialized the DCS. Replay writes the same bytes into the
                // transferred private array, avoiding a second full-prefix allocation and copy.
                candidate._retained = ownedRetainedContent;
            }

            var replay = candidate.Process(checkpoint.RetainedContent.Span);
            if (!replay.TextBytes.IsEmpty || replay.Frames.Count != 0 || replay.SixelIdentified)
            {
                throw new ArgumentException("The retained prefix emits output or identifies Sixel.", nameof(checkpoint));
            }

            if (checkpoint.State == ParserState.DcsEscape)
                _ = candidate.Process("\x1b"u8);

            var restored = candidate.CaptureCheckpoint();
            if (restored.State != checkpoint.State ||
                restored.StateBeforeEscape != checkpoint.StateBeforeEscape ||
                restored.ByteCount != checkpoint.ByteCount ||
                restored.RetainedContent.Length != checkpoint.RetainedContent.Length ||
                restored.RetentionLimitExceeded || restored.IsSixel)
            {
                throw new ArgumentException("The DCS continuation state does not match its retained prefix.", nameof(checkpoint));
            }

            return candidate;
        }
        catch
        {
            candidate.Dispose();
            throw;
        }
    }

    private static bool IsOpenDcsState(ParserState state) =>
        IsContentDcsState(state) || state == ParserState.DcsEscape;

    private static bool IsContentDcsState(ParserState state) => state is
        ParserState.Introducer or ParserState.Payload or ParserState.MalformedIntroducer;
}
