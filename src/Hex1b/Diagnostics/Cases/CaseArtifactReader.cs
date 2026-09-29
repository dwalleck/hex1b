using System.Text.Json;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Reads a case artifact from disk without the process that wrote it. Every event line is verified
/// (checksum, then JSON) before it is used; the first line that fails ends the verified prefix. Coverage
/// and re-applicable intervals are derived only from what was verified and recorded.
/// </summary>
internal static class CaseArtifactReader
{
    internal const int MaxEvents = 4096;

    private static readonly string[] StreamNames = ["model", "input", "frames", "delivery"];

    internal static DiagnosticCaseInspection Inspect(DiagnosticCaseInspectRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Path))
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-path", "path must name a case directory.");
        if (request.Limit is < 1 or > MaxEvents)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-limit", $"limit must be 1 to {MaxEvents}.");
        if (request.Since is < 0)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-since", "since must be zero or greater.");

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(request.Path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-path", "path must name a case directory.");
        }

        if (!Directory.Exists(path))
            return Problem(DiagnosticOutcome.Unavailable, "case-not-found", $"No case directory at '{path}'.");
        var manifestPath = System.IO.Path.Combine(path, CaseArtifactWriter.ManifestFile);
        if (!File.Exists(manifestPath))
            return Problem(DiagnosticOutcome.Failed, "invalid-artifact", "The case has no manifest.") with { Path = path };

        DiagnosticCaseManifest? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize(File.ReadAllBytes(manifestPath), DiagnosticsJsonContext.Default.DiagnosticCaseManifest);
        }
        catch (JsonException error)
        {
            return Problem(DiagnosticOutcome.Failed, "invalid-artifact", $"The manifest is not valid: {error.Message}") with { Path = path };
        }

        if (manifest is null)
            return Problem(DiagnosticOutcome.Failed, "invalid-artifact", "The manifest is empty.") with { Path = path };
        if (manifest.FormatVersion != CaseArtifactWriter.FormatVersion)
            return Problem(DiagnosticOutcome.Failed, "unsupported-format", $"Artifact format {manifest.FormatVersion} is not supported (expected {CaseArtifactWriter.FormatVersion}).") with { Path = path };

        var scan = Scan(System.IO.Path.Combine(path, CaseArtifactWriter.EventsFile), request.Since ?? 0, request.Limit ?? 0);

        DiagnosticCaseCompletion? completion = null;
        var completionPath = System.IO.Path.Combine(path, CaseArtifactWriter.CompletionFile);
        if (File.Exists(completionPath))
        {
            try
            {
                completion = JsonSerializer.Deserialize(File.ReadAllBytes(completionPath), DiagnosticsJsonContext.Default.DiagnosticCaseCompletion);
            }
            catch (JsonException)
            {
                // A completion record that does not parse was never written whole: the case is interrupted.
            }
        }

        var state = scan.TruncatedAtLine is not null ? DiagnosticCaseCompletionState.Truncated
            : completion is null ? DiagnosticCaseCompletionState.Interrupted
            : DiagnosticCaseCompletionState.Complete;

        return new DiagnosticCaseInspection
        {
            Outcome = DiagnosticOutcome.Captured,
            Path = path,
            Manifest = manifest,
            Completion = completion,
            CompletionState = state,
            TruncatedAtLine = scan.TruncatedAtLine,
            LastCaseSequence = scan.LastCaseSequence,
            Streams = DescribeStreams(manifest, scan),
            Intervals = DescribeIntervals(manifest, completion, state, scan),
            Events = scan.Page,
        };
    }

    private static DiagnosticCaseInspection Problem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    private sealed class StreamScan
    {
        public long Events;
        public long? First;
        public long? Last;
        // Recorded missing ranges (anywhere in the file) and ordinal gaps seen while scanning; a gap no
        // recorded range explains is loss of unknown cause.
        public readonly List<DiagnosticCaseRecord> Recorded = [];
        public readonly List<(long From, long To)> Gaps = [];

        public IReadOnlyList<DiagnosticCaseRecord> Missing =>
            Recorded.Concat(Gaps.Where(g => !Recorded.Any(m => m.FromOrdinal <= g.From && (m.ToOrdinal is null || m.ToOrdinal >= g.To)))
                .Select(g => new DiagnosticCaseRecord { FromOrdinal = g.From, ToOrdinal = g.To, Reason = "unknown" }))
                .OrderBy(m => m.FromOrdinal).ToList();
    }

    private sealed class ScanResult
    {
        public readonly Dictionary<string, StreamScan> Streams = Streams_();
        public readonly List<DiagnosticCaseEvent> Page = [];
        public long? TruncatedAtLine;
        public long? LastCaseSequence;
        public long? LastModelSequence;
        public long? ModelGapAfter;
        public (long Sequence, string Reason)? IntervalEnd;

        private static Dictionary<string, StreamScan> Streams_() => StreamNames.ToDictionary(s => s, _ => new StreamScan());
    }

    private static ScanResult Scan(string eventsPath, long since, int limit)
    {
        var result = new ScanResult();
        if (!File.Exists(eventsPath))
            return result;

        long line = 0;
        foreach (var (bytes, terminated) in Lines(eventsPath))
        {
            line++;
            if (!terminated || !TryParse(bytes, out var item))
            {
                result.TruncatedAtLine = line;
                break;
            }

            result.LastCaseSequence = item.CaseSequence;
            if (limit > 0 && item.CaseSequence > since && result.Page.Count < limit)
                result.Page.Add(item);

            if (item.Stream == "case")
            {
                if (item.Kind == "interval-end" && item.ModelSequence is { } end && result.IntervalEnd is null)
                    result.IntervalEnd = (end, item.Record?.Reason ?? "unsupported");
                else if (item.Kind == "missing" && item.Record is { } missing && result.Streams.TryGetValue(missing.Stream, out var target))
                    target.Recorded.Add(missing);
                continue;
            }

            if (!result.Streams.TryGetValue(item.Stream, out var stream))
                continue;
            var expected = stream.Last is { } last ? last + 1 : (item.Stream == "delivery" ? FirstDeliveryOrdinal(stream, item.Ordinal) : 1);
            if (item.Ordinal > expected)
                stream.Gaps.Add((expected, item.Ordinal - 1));
            stream.Events++;
            stream.First ??= item.Ordinal;
            stream.Last = item.Ordinal;

            if (item.Stream == "model" && item.ModelSequence is { } modelSequence)
            {
                var next = (result.LastModelSequence ?? 0) + 1;
                if (modelSequence != next && result.ModelGapAfter is null)
                    result.ModelGapAfter = next - 1;
                result.LastModelSequence = modelSequence;
            }
        }

        return result;
    }

    // Delivery ordinals are the terminal's delivery sequences, which do not start at 1 for a case.
    private static long FirstDeliveryOrdinal(StreamScan stream, long ordinal) =>
        stream.Recorded.Count > 0 && stream.Recorded[0].FromOrdinal is { } from ? from : ordinal;

    private static bool TryParse(byte[] bytes, out DiagnosticCaseEvent item)
    {
        item = null!;
        if (bytes.Length < 10 || bytes[8] != (byte)'\t')
            return false;
        if (!uint.TryParse(System.Text.Encoding.ASCII.GetString(bytes, 0, 8), System.Globalization.NumberStyles.HexNumber, null, out var crc))
            return false;
        var json = bytes.AsSpan(9);
        if (CaseCrc32.Compute(json) != crc)
            return false;
        try
        {
            item = JsonSerializer.Deserialize(json, DiagnosticsJsonContext.Default.DiagnosticCaseEvent)!;
            return item is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // Yields each line's bytes (without the newline) and whether it was newline-terminated.
    private static IEnumerable<(byte[] Bytes, bool Terminated)> Lines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16);
        var buffer = new byte[1 << 16];
        var line = new List<byte>(1024);
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            var start = 0;
            for (var i = 0; i < read; i++)
            {
                if (buffer[i] != (byte)'\n')
                    continue;
                line.AddRange(buffer.AsSpan(start, i - start));
                yield return (line.ToArray(), true);
                line.Clear();
                start = i + 1;
            }

            line.AddRange(buffer.AsSpan(start, read - start));
        }

        if (line.Count > 0)
            yield return (line.ToArray(), false);
    }

    private static IReadOnlyList<DiagnosticCaseStreamCoverage> DescribeStreams(DiagnosticCaseManifest manifest, ScanResult scan) =>
        manifest.Streams.Where(s => s.Events == DiagnosticCoverageState.Included).Select(declaration =>
        {
            var stream = scan.Streams.TryGetValue(declaration.Stream, out var found) ? found : new StreamScan();
            var missing = stream.Missing.Select(m => m with { Stream = declaration.Stream }).ToList();
            return new DiagnosticCaseStreamCoverage
            {
                Stream = declaration.Stream,
                Events = stream.Events,
                FirstOrdinal = stream.First,
                LastOrdinal = stream.Last,
                State = missing.Count == 0 ? "complete" : "incomplete",
                Missing = missing,
            };
        }).ToList();

    private static IReadOnlyList<DiagnosticCaseInterval> DescribeIntervals(DiagnosticCaseManifest manifest, DiagnosticCaseCompletion? completion,
        DiagnosticCaseCompletionState state, ScanResult scan)
    {
        if (manifest.Checkpoint.Status != DiagnosticCaseCheckpointStatus.Complete)
        {
            return
            [
                new DiagnosticCaseInterval
                {
                    Valid = false,
                    EndReason = $"checkpoint {DiagnosticContractNames.Of(manifest.Checkpoint.Status)}: {manifest.Checkpoint.Reason}",
                },
            ];
        }

        // A fresh checkpoint is model sequence 0; the interval runs to the first event it cannot cover.
        var last = scan.LastModelSequence ?? 0;
        var model = scan.Streams["model"];
        (long To, string Reason)? end = null;
        void Consider(long to, string reason)
        {
            if (end is null || to < end.Value.To)
                end = (to, reason);
        }

        // On a tie the first reason considered is reported: a recorded cause before an inferred gap.
        if (scan.IntervalEnd is { } intervalEnd)
            Consider(intervalEnd.Sequence - 1, intervalEnd.Reason);
        foreach (var missing in model.Missing)
            Consider(Math.Max(0, (missing.FromOrdinal ?? 1) - 1), missing.Reason == "unknown" ? "model-events-missing" : missing.Reason);
        if (scan.ModelGapAfter is { } gap)
            Consider(gap, "model-events-missing");
        if (state == DiagnosticCaseCompletionState.Truncated)
            Consider(last, "truncated");
        else if (state == DiagnosticCaseCompletionState.Interrupted)
            Consider(last, "interrupted");

        var (to, reason) = end ?? (last, $"case-stopped: {(completion is null ? "unknown" : DiagnosticContractNames.Of(completion.StopReason))}");
        return [new DiagnosticCaseInterval { Valid = true, FromModelSequence = 0, ToModelSequence = to, EndReason = reason }];
    }
}
