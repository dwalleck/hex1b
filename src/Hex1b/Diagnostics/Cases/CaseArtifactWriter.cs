using System.Buffers;
using System.Text.Json;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Writes a case artifact: <c>manifest.json</c> first, then one line per event in <c>events.jsonl</c>,
/// then <c>completion.json</c> (written to a temporary name and renamed, so it is either whole or absent).
/// Each event line is <c>&lt;crc32 hex&gt;\t&lt;json&gt;\n</c>, the CRC covering the JSON bytes.
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
    internal int WriteEvent(in CaseEvent item)
    {
        _json.Clear();
        using (var writer = new Utf8JsonWriter(_json))
        {
            writer.WriteStartObject();
            writer.WriteNumber("caseSequence", item.CaseSequence);
            writer.WriteString("stream", StreamName(item.Stream));
            writer.WriteNumber("ordinal", item.Ordinal);
            writer.WriteNumber("timestamp", item.Timestamp);
            writer.WriteString("kind", item.Kind);
            if (item.ModelSequence is { } modelSequence)
                writer.WriteNumber("modelSequence", modelSequence);
            writer.WriteNumber("width", item.Width);
            writer.WriteNumber("height", item.Height);
            writer.WriteNumber("length", item.Length);
            if (item.Payload is { } payload)
                writer.WriteBase64String("data", payload);
            writer.WriteEndObject();
        }

        var json = _json.WrittenSpan;
        Span<byte> prefix = stackalloc byte[9];
        CaseCrc32.Compute(json).TryFormat(prefix, out _, "x8");
        prefix[8] = (byte)'\t';
        var events = _events ?? throw new InvalidOperationException("The manifest has not been written.");
        events.Write(prefix);
        events.Write(json);
        events.WriteByte((byte)'\n');
        var written = prefix.Length + json.Length + 1;
        Interlocked.Add(ref _bytesWritten, written);
        return written;
    }

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
        _ => "delivery",
    };

    public void Dispose() => _events?.Dispose();
}
