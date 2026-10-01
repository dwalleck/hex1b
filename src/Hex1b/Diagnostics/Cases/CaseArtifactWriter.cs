using System.Buffers;
using System.Text.Json;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Writes a case artifact: <c>manifest.json</c> first, then one line per event in <c>events.jsonl</c>,
/// then <c>completion.json</c> (written to a temporary name and renamed, so it is either whole or absent).
/// Each event line is <c>&lt;crc32 hex&gt;\t&lt;json&gt;\n</c>, the CRC covering the JSON bytes, and the JSON
/// is a <see cref="DiagnosticCaseEvent"/>.
/// </summary>
internal sealed class CaseArtifactWriter : IDisposable
{
    // Format 2 records the model configuration structurally and adds checkpoint records; format 1 cases
    // are still inspected (their configuration strings are dropped), never re-applied.
    internal const int FormatVersion = 2;
    internal const int LegacyFormatVersion = 1;
    internal const string ManifestFile = "manifest.json";
    internal const string EventsFile = "events.jsonl";
    internal const string CompletionFile = "completion.json";

    private readonly string _directory;
    private readonly CaseRoom _room;
    private ArrayBufferWriter<byte> _json = new(512);
    private FileStream? _events;

    internal CaseArtifactWriter(string directory, CaseRoom room)
    {
        _directory = directory;
        _room = room;
    }

    /// <summary>Artifact bytes written so far (counted by the room as each line is claimed).</summary>
    internal long BytesWritten => _room.BytesWritten;

    internal void WriteManifest(DiagnosticCaseManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DiagnosticsJsonContext.Default.DiagnosticCaseManifest);
        using (var stream = CaseStorage.CreateFile(Path.Combine(_directory, ManifestFile)))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        _room.Count(bytes.Length);
        _events = CaseStorage.CreateFile(Path.Combine(_directory, EventsFile));
    }

    /// <summary>
    /// Writes one event line unless the artifact would then exceed the <paramref name="tier"/>'s limit; nothing is
    /// written when it would. The line's bytes are claimed from the room before it is written.
    /// </summary>
    internal bool TryWriteEvent(DiagnosticCaseEvent item, CaseRoom.Tier tier)
    {
        _json.Clear();
        using (var writer = new Utf8JsonWriter(_json))
            JsonSerializer.Serialize(writer, item, DiagnosticsJsonContext.Default.DiagnosticCaseEvent);

        var json = _json.WrittenSpan;
        var total = 9 + json.Length + 1;
        if (!_room.TryClaim(tier, total))
            return false;
        Span<byte> prefix = stackalloc byte[9];
        CaseCrc32.Compute(json).TryFormat(prefix, out _, "x8");
        prefix[8] = (byte)'\t';
        var events = _events ?? throw new InvalidOperationException("The manifest has not been written.");
        events.Write(prefix);
        events.Write(json);
        events.WriteByte((byte)'\n');
        return true;
    }

    internal void Flush() => _events?.Flush();

    /// <summary>Lets go of a serialization buffer a checkpoint grew, so a case does not hold it for its life.</summary>
    internal void TrimBuffer()
    {
        if (_json.Capacity > 1024 * 1024)
            _json = new ArrayBufferWriter<byte>(512);
    }

    internal void WriteCompletion(DiagnosticCaseCompletion completion)
    {
        _events?.Flush(flushToDisk: true);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(completion, DiagnosticsJsonContext.Default.DiagnosticCaseCompletion);
        var temporary = Path.Combine(_directory, CompletionFile + ".tmp");
        using (var stream = CaseStorage.CreateFile(temporary))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, Path.Combine(_directory, CompletionFile));
        _room.Count(bytes.Length);
    }

    internal static string StreamName(CaseStream stream) => stream switch
    {
        CaseStream.Model => "model",
        CaseStream.Input => "input",
        CaseStream.Frames => "frames",
        CaseStream.Delivery => "delivery",
        _ => "case",
    };

    public void Dispose() => _events?.Dispose();
}
