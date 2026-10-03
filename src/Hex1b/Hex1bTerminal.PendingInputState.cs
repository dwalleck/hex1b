using System.Buffers;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Tokens;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // The output continuation a checkpoint owns, and its restore into a model that has applied nothing: the unfinished
    // escape sequence (the model's decoded text), the bytes of an unfinished UTF-8 scalar (held by the decoder, mirrored
    // by the model), an ESC held before a possible DCS and the framer's expected UTF-8 continuation count.
    // An open non-Sixel DCS owns its intact retained content and parser substate.

    // Why a projected continuation cannot be restored, or null. Each holder is checked against what the model itself
    // could have held: the prefix is one unterminated introducer and nothing before it; the bytes are a valid,
    // incomplete scalar prefix of at most 3 bytes; the framer's count is 0..3, and 0 when an ESC is held.
    internal static string? PendingInputProblem(DiagnosticModelPendingInput? pending)
    {
        if (pending is null)
            return "pending input: missing";
        if (pending.EscapePrefix is null || pending.Utf8 is null)
            return "pending input with a missing field";
        if (pending.EscapePrefix.Length > 0)
        {
            var (complete, incomplete) = ExtractIncompleteEscapeSequence(pending.EscapePrefix, recognizeC1Dcs: false);
            if (complete.Length > 0 || incomplete != pending.EscapePrefix)
                return "pending input whose escape prefix is not one unfinished sequence";
        }
        if (pending.Utf8.Length > 0)
        {
            if (!TryDecodePendingUtf8(pending.Utf8, out var bytes))
                return "pending input whose utf8 is not base64";
            if (bytes.Length is 0 or > 3 || Rune.DecodeFromUtf8(bytes, out _, out _) != OperationStatus.NeedMoreData)
                return "pending input whose utf8 is not an unfinished scalar";
        }
        if (pending.FramerUtf8Remaining is < 0 or > 3)
            return "pending input whose framer count is outside 0..3";
        if (pending.GroundEscape && pending.FramerUtf8Remaining != 0)
            return "pending input holding an ESC with a framer count";
        if (pending.Dcs is { } dcs)
        {
            if (dcs.State is null || dcs.RetainedBytes is null)
                return "pending DCS with a missing field";
            if (!TryDcsState(dcs.State, out _))
                return "pending DCS with an unknown state";
            if (dcs.State == "escape")
            {
                if (!TryDcsState(dcs.StateBeforeEscape, out var before) || before == DcsParserCheckpoint.StateKind.DcsEscape)
                    return "pending DCS with an invalid state before ESC";
            }
            else if (dcs.StateBeforeEscape is not null)
                return "pending DCS with a state before ESC outside escape state";
            if (dcs.ByteCount is < 0 or > int.MaxValue)
                return "pending DCS with an invalid byte count";
            if (dcs.RetainedBytes.Length != (dcs.ByteCount + 2) / 3 * 4)
                return "pending DCS whose retained bytes do not match its byte count";
            if (dcs.RetentionLimitExceeded)
                return "pending DCS whose retained content exceeded its limit";
            if (pending.GroundEscape || pending.FramerUtf8Remaining != 0)
                return "pending DCS coexisting with ground framer state";
        }
        return null;
    }

    private static bool TryDecodePendingUtf8(string base64, out byte[] bytes)
    {
        var buffer = new byte[4];
        if (Convert.TryFromBase64String(base64, buffer, out var written) && written <= 3)
        {
            bytes = buffer[..written];
            return true;
        }
        bytes = [];
        return false;
    }

    // Must hold _bufferLock, on a model that has applied nothing, before the continuation is committed. The decoder is
    // primed by feeding it the scalar's bytes without a flush: a valid incomplete prefix yields no char and leaves the
    // decoder holding exactly those bytes (evidence P2).
    private void RestorePendingInputUnsafe(DiagnosticModelPendingInput pending, DcsByteStreamParser? restoredDcs)
    {
        _incompleteSequenceBuffer = pending.EscapePrefix;
        if (pending.Utf8.Length > 0)
        {
            TryDecodePendingUtf8(pending.Utf8, out var bytes);
            bytes.CopyTo(_pendingUtf8Output, 0);
            _pendingUtf8OutputLength = bytes.Length;
            Span<char> chars = stackalloc char[4];
            if (_utf8Decoder.GetChars(bytes, chars, flush: false) != 0)
                throw new InvalidOperationException("The state cannot be restored: its pending utf8 decoded to text.");
        }
        if (restoredDcs is not null)
        {
            var replaced = _dcsByteStreamParser;
            _dcsByteStreamParser = restoredDcs;
            replaced.Dispose();
        }
        else
            _dcsByteStreamParser.RestoreGroundState(pending.GroundEscape, pending.FramerUtf8Remaining);
    }

    // Reserve the exact retained base64 length and the required DCS metadata before projection allocates.
    // The scalar count uses its one-digit floor; exact serialized checkpoint/event-room checks remain authoritative.
    private long PendingInputJsonFloorUnsafe()
    {
        var dcsBytes = 4L; // null
        if (IsOpenDcs(_committedDcs.State))
        {
            dcsBytes = "{\"state\":\"\",\"retainedBytes\":\"\",\"byteCount\":0,\"retentionLimitExceeded\":true}".Length
                + DcsStateName(_committedDcs.State).Length
                + ((long)_committedDcs.RetainedContent.Length + 2) / 3 * 4;
            if (_committedDcs.State == DcsParserCheckpoint.StateKind.DcsEscape)
                dcsBytes += ",\"stateBeforeEscape\":\"\"".Length + DcsStateName(_committedDcs.StateBeforeEscape).Length;
        }
        return checked(Encoding.UTF8.GetByteCount(_committedEscapePrefix)
            + (_committedUtf8Length > 0 ? 4 : 0) + "\"dcs\":".Length + dcsBytes);
    }

    private DiagnosticModelDcsContinuation? ProjectDcsContinuationUnsafe() => !IsOpenDcs(_committedDcs.State)
        ? null
        : new DiagnosticModelDcsContinuation
        {
            State = DcsStateName(_committedDcs.State),
            StateBeforeEscape = _committedDcs.State == DcsParserCheckpoint.StateKind.DcsEscape
                ? DcsStateName(_committedDcs.StateBeforeEscape) : null,
            RetainedBytes = Convert.ToBase64String(_committedDcs.RetainedContent.Span),
            ByteCount = _committedDcs.ByteCount,
            RetentionLimitExceeded = _committedDcs.RetentionLimitExceeded,
        };

    private void AddPendingInputUnsupportedUnsafe(List<string> unsupported)
    {
        if (_committedDcs.IsSixel)
            unsupported.Add("sixel-continuation");
        if (_committedDcs.RetentionLimitExceeded)
            unsupported.Add("dcs-retention-limit");
    }

    // All model preflight precedes this parser-only reconstruction, and it precedes any resize or model mutation.
    // The decoded array is private and can be transferred to the candidate; the prefix is replayed exactly once.
    internal DcsByteStreamParser? PrepareDcsRestore(DiagnosticModelPendingInput pending)
    {
        if (pending.Dcs is not { } dcs)
            return null;
        if (dcs.ByteCount > _caseConfiguration.Graphics!.MaximumRetainedInputBytesPerImage)
            throw new InvalidOperationException("The state cannot be restored: its pending DCS exceeds the configured retention limit.");
        var bytes = new byte[(int)dcs.ByteCount];
        if (!Convert.TryFromBase64String(dcs.RetainedBytes, bytes, out var written) || written != bytes.Length)
            throw new InvalidOperationException("The state cannot be restored: its pending DCS retained bytes are not intact base64.");
        TryDcsState(dcs.State, out var state);
        var before = DcsParserCheckpoint.StateKind.Ground;
        if (state == DcsParserCheckpoint.StateKind.DcsEscape)
            TryDcsState(dcs.StateBeforeEscape, out before);
        try
        {
            return _dcsByteStreamParser.CreateRestored(
                new DcsParserCheckpoint(state, before, bytes, dcs.ByteCount, dcs.RetentionLimitExceeded, IsSixel: false), bytes);
        }
        catch (ArgumentException error)
        {
            throw new InvalidOperationException("The state cannot be restored: its pending DCS is not a supported intact continuation.", error);
        }
    }

    private static bool IsOpenDcs(DcsParserCheckpoint.StateKind state) =>
        state is DcsParserCheckpoint.StateKind.Introducer or DcsParserCheckpoint.StateKind.Payload
            or DcsParserCheckpoint.StateKind.MalformedIntroducer or DcsParserCheckpoint.StateKind.DcsEscape;

    private static string DcsStateName(DcsParserCheckpoint.StateKind state) => state switch
    {
        DcsParserCheckpoint.StateKind.Introducer => "introducer",
        DcsParserCheckpoint.StateKind.Payload => "payload",
        DcsParserCheckpoint.StateKind.MalformedIntroducer => "malformed-introducer",
        DcsParserCheckpoint.StateKind.DcsEscape => "escape",
        _ => throw new InvalidOperationException("A ground framer has no DCS continuation state."),
    };

    private static bool TryDcsState(string? name, out DcsParserCheckpoint.StateKind state)
    {
        state = name switch
        {
            "introducer" => DcsParserCheckpoint.StateKind.Introducer,
            "payload" => DcsParserCheckpoint.StateKind.Payload,
            "malformed-introducer" => DcsParserCheckpoint.StateKind.MalformedIntroducer,
            "escape" => DcsParserCheckpoint.StateKind.DcsEscape,
            _ => DcsParserCheckpoint.StateKind.Ground,
        };
        return IsOpenDcs(state);
    }
}
