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
    internal const int FormatVersion = 1;
    internal const string ManifestFile = "manifest.json";
    internal const string EventsFile = "events.jsonl";
    internal const string CompletionFile = "completion.json";

    private readonly string _directory;
    private readonly ArrayBufferWriter<byte> _json = new(512);
    private FileStream? _events;
    private long _bytesWritten;

    internal CaseArtifactWriter(string directory) => _directory = directory;

    /// <summary>Artifact bytes written so far.</summary>
    internal long BytesWritten => Interlocked.Read(ref _bytesWritten);

    internal void WriteManifest(DiagnosticCaseManifest manifest)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest, DiagnosticsJsonContext.Default.DiagnosticCaseManifest);
        using (var stream = CaseStorage.CreateFile(Path.Combine(_directory, ManifestFile)))
        {
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
        }
        Interlocked.Add(ref _bytesWritten, bytes.Length);
        _events = CaseStorage.CreateFile(Path.Combine(_directory, EventsFile));
    }

    /// <summary>Writes one event line; returns the bytes written.</summary>
    internal int WriteEvent(DiagnosticCaseEvent item) =>
        TryWriteEvent(item, long.MaxValue) ? _lastLineBytes : throw new InvalidOperationException("unreachable");

    /// <summary>
    /// Writes one event line unless the artifact would then exceed <paramref name="limit"/> bytes; nothing
    /// is written when it would.
    /// </summary>
    internal bool TryWriteEvent(DiagnosticCaseEvent item, long limit)
    {
        _json.Clear();
        using (var writer = new Utf8JsonWriter(_json))
            JsonSerializer.Serialize(writer, item, DiagnosticsJsonContext.Default.DiagnosticCaseEvent);

        var json = _json.WrittenSpan;
        var total = 9 + json.Length + 1;
        if (BytesWritten + total > limit)
            return false;
        Span<byte> prefix = stackalloc byte[9];
        CaseCrc32.Compute(json).TryFormat(prefix, out _, "x8");
        prefix[8] = (byte)'\t';
        var events = _events ?? throw new InvalidOperationException("The manifest has not been written.");
        events.Write(prefix);
        events.Write(json);
        events.WriteByte((byte)'\n');
        Interlocked.Add(ref _bytesWritten, total);
        _lastLineBytes = total;
        return true;
    }

    private int _lastLineBytes;

    internal void Flush() => _events?.Flush();

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
        Interlocked.Add(ref _bytesWritten, bytes.Length);
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
