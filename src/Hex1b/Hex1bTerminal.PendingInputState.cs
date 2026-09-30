using System.Buffers;
using System.Text;
using Hex1b.Diagnostics;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // The output continuation a checkpoint owns, and its restore into a model that has applied nothing: the unfinished
    // escape sequence (the model's decoded text), the bytes of an unfinished UTF-8 scalar (held by the decoder, mirrored
    // by the model), an ESC the DCS framer holds because the next byte may begin a DCS, and the continuation bytes the
    // framer still expects. A DCS in progress is a separate surface and stays unsupported.

    // Why a projected continuation cannot be restored, or null. Each holder is checked against what the model itself
    // could have held: the prefix is one unterminated introducer and nothing before it; the bytes are a valid,
    // incomplete scalar prefix of at most 3 bytes; the framer's count is 0..3, and 0 when an ESC is held.
    private static string? PendingInputProblem(DiagnosticModelPendingInput? pending)
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
    private void RestorePendingInputUnsafe(DiagnosticModelPendingInput pending)
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
        _dcsByteStreamParser.RestoreGroundState(pending.GroundEscape, pending.FramerUtf8Remaining);
    }

    // The fewest JSON bytes the pending input takes: the escape prefix's UTF-8 length (it has no cap in the model, so
    // a start counts it as it counts cells) and the scalar's base64. O(prefix length). Must hold _bufferLock.
    private long PendingInputJsonFloorUnsafe() =>
        Encoding.UTF8.GetByteCount(_committedEscapePrefix) + (_committedUtf8Length > 0 ? 4 : 0);
}
