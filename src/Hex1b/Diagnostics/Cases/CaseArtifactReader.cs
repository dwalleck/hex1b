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
        ScanResult scan;
        DiagnosticCaseCompletion? completion = null;
        try
        {
            try
            {
                manifest = ReadManifest(File.ReadAllBytes(manifestPath));
            }
            catch (JsonException error)
            {
                return Problem(DiagnosticOutcome.Failed, "invalid-artifact", $"The manifest is not valid: {error.Message}") with { Path = path };
            }

            // Fields the reader relies on; a hand-edited or damaged manifest may lack them.
            if (manifest is null || manifest.Checkpoint is null || manifest.Streams is null || manifest.Bounds is null
                || manifest.Streams.Any(s => s?.Stream is null))
                return Problem(DiagnosticOutcome.Failed, "invalid-artifact", "The manifest is empty or incomplete.") with { Path = path };
            if (manifest.FormatVersion is not (CaseArtifactWriter.FormatVersion or CaseArtifactWriter.LegacyFormatVersion))
                return Problem(DiagnosticOutcome.Failed, "unsupported-format", $"Artifact format {manifest.FormatVersion} is not supported (expected {CaseArtifactWriter.LegacyFormatVersion} or {CaseArtifactWriter.FormatVersion}).") with { Path = path };

            scan = Scan(System.IO.Path.Combine(path, CaseArtifactWriter.EventsFile), request.Since ?? 0, request.Limit ?? 0,
                manifest.Checkpoint.ModelSequence ?? 0);

            var completionPath = System.IO.Path.Combine(path, CaseArtifactWriter.CompletionFile);
            if (File.Exists(completionPath))
            {
                try
                {
                    completion = JsonSerializer.Deserialize(File.ReadAllBytes(completionPath), DiagnosticsJsonContext.Default.DiagnosticCaseCompletion);
                    // A completion without its stream counts was not written whole either.
                    if (completion?.Streams is null || completion.Streams.Any(s => s?.Stream is null))
                        completion = null;
                }
                catch (JsonException)
                {
                    // A completion record that does not parse was never written whole: the case is interrupted.
                }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Problem(DiagnosticOutcome.Failed, "unreadable-artifact", $"The case files cannot be read: {error.Message}") with { Path = path };
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
            Streams = DescribeStreams(manifest, scan, completion),
            Intervals = DescribeIntervals(manifest, completion, state, scan),
            Checkpoints = DescribeCheckpoints(scan, completion),
            Events = scan.Page,
        };
    }

    /// <summary>
    /// Reads a manifest. Format 1 recorded the capabilities, graphics limits and reflow provider as
    /// descriptive strings; they are dropped (the reflow strategy reads <c>unrecorded</c>), so the case stays
    /// inspectable, while re-application refuses it by its format.
    /// </summary>
    internal static DiagnosticCaseManifest? ReadManifest(byte[] bytes)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(bytes);
        if (node is System.Text.Json.Nodes.JsonObject root
            && root["formatVersion"] is System.Text.Json.Nodes.JsonValue version
            && version.TryGetValue<int>(out var number) && number == CaseArtifactWriter.LegacyFormatVersion
            && root["checkpoint"]?["configuration"] is System.Text.Json.Nodes.JsonObject configuration)
        {
            configuration.Remove("capabilities");
            configuration.Remove("graphics");
            configuration.Remove("reflowProvider");
            configuration["reflowStrategy"] = "unrecorded";
        }
        return node?.Deserialize(DiagnosticsJsonContext.Default.DiagnosticCaseManifest);
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
        public DiagnosticCaseRecord? Failure;
        private IReadOnlyList<DiagnosticCaseRecord>? _missing;

        // Computed once, after the scan.
        public IReadOnlyList<DiagnosticCaseRecord> Missing => _missing ??=
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
        public readonly StreamScan Checkpoints = new();
        public long? ModelGapAfter;
        public (long Sequence, string Reason)? IntervalEnd;

        private static Dictionary<string, StreamScan> Streams_() => StreamNames.ToDictionary(s => s, _ => new StreamScan());
    }

    // A case started on a model that had applied output records model events from after its start's sequence
    // (modelStart); a fresh case from 1.
    private static ScanResult Scan(string eventsPath, long since, int limit, long modelStart)
    {
        var result = new ScanResult();
        if (!File.Exists(eventsPath))
            return result;

        long line = 0;
        foreach (var (bytes, terminated) in Lines(eventsPath))
        {
            line++;
            // Checkpoint state is skipped, not deserialized: it can be large, and re-application reads it itself.
            if (!terminated || !TryParse(bytes, out var item, includeState: false, out var stateOmitted))
            {
                result.TruncatedAtLine = line;
                break;
            }

            result.LastCaseSequence = item.CaseSequence;
            if (limit > 0 && item.CaseSequence > since && result.Page.Count < limit)
                result.Page.Add(stateOmitted ? item with { Checkpoint = item.Checkpoint! with { StateOmitted = true } } : item);

            if (item.Checkpoint is { } written)
            {
                result.Checkpoints.Events++;
                result.Checkpoints.First ??= written.Ordinal;
                result.Checkpoints.Last = Math.Max(result.Checkpoints.Last ?? 0, written.Ordinal);
            }
            else if (item.Kind == "missing" && item.Record is { Stream: "checkpoint" } lostCheckpoints)
            {
                result.Checkpoints.Recorded.Add(lostCheckpoints);
            }

            if (item.Stream == "case")
            {
                if (item.Kind == "interval-end" && item.ModelSequence is { } end && result.IntervalEnd is null)
                    result.IntervalEnd = (end, item.Record?.Reason ?? "unsupported");
                else if (item.Kind == "missing" && item.Record is { } missing && result.Streams.TryGetValue(missing.Stream, out var target))
                    target.Recorded.Add(missing);
                else if (item.Kind == "stream-failed" && item.Record is { } failure && result.Streams.TryGetValue(failure.Stream, out var failed))
                    failed.Failure ??= failure;
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
                var next = (result.LastModelSequence ?? modelStart) + 1;
                if (modelSequence != next && result.ModelGapAfter is null)
                    result.ModelGapAfter = next - 1;
                result.LastModelSequence = modelSequence;
            }
        }

        return result;
    }

    /// <summary>
    /// Streams the verified events of an events file, one line at a time, ending at the first line that fails
    /// its checksum or JSON (the verified prefix). Memory is bounded by one line, not by the file.
    /// </summary>
    internal static IEnumerable<DiagnosticCaseEvent> ReadEvents(string eventsPath, long? includeStateAt = null)
    {
        if (!File.Exists(eventsPath))
            yield break;
        foreach (var (bytes, terminated) in Lines(eventsPath))
        {
            if (!terminated || !TryParse(bytes, out var item, includeState: false, out var stateOmitted))
                yield break;
            // Only the one checkpoint whose state is wanted is deserialized whole.
            if (stateOmitted && item.CaseSequence == includeStateAt && TryParse(bytes, out var whole, includeState: true, out _))
                item = whole;
            yield return item;
        }
    }

    // Delivery ordinals are the terminal's delivery sequences, which do not start at 1 for a case.
    private static long FirstDeliveryOrdinal(StreamScan stream, long ordinal) =>
        stream.Recorded.Count > 0 && stream.Recorded[0].FromOrdinal is { } from ? from : ordinal;

    private static bool TryParse(byte[] bytes, out DiagnosticCaseEvent item, bool includeState, out bool stateOmitted)
    {
        item = null!;
        stateOmitted = false;
        if (bytes.Length < 10 || bytes[8] != (byte)'\t')
            return false;
        if (!uint.TryParse(System.Text.Encoding.ASCII.GetString(bytes, 0, 8), System.Globalization.NumberStyles.HexNumber, null, out var crc))
            return false;
        ReadOnlySpan<byte> json = bytes.AsSpan(9);
        if (CaseCrc32.Compute(json) != crc)
            return false;
        try
        {
            if (!includeState && WithoutCheckpointState(json) is { } lighter)
            {
                json = lighter;
                stateOmitted = true;
            }
            item = JsonSerializer.Deserialize(json, DiagnosticsJsonContext.Default.DiagnosticCaseEvent)!;
            return item is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // The line with its checkpoint's state replaced by null, found with a forward-only reader; null when the
    // line holds no checkpoint state. The state's bytes are skipped, never materialized as objects.
    private static byte[]? WithoutCheckpointState(ReadOnlySpan<byte> json)
    {
        var reader = new Utf8JsonReader(json);
        if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            return null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var isCheckpoint = reader.ValueTextEquals("checkpoint"u8);
            reader.Read();
            if (!isCheckpoint || reader.TokenType != JsonTokenType.StartObject)
            {
                reader.Skip();
                continue;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isState = reader.ValueTextEquals("state"u8);
                reader.Read();
                if (!isState || reader.TokenType == JsonTokenType.Null)
                {
                    reader.Skip();
                    continue;
                }

                var start = (int)reader.TokenStartIndex;
                reader.Skip();
                var end = (int)reader.BytesConsumed;
                var lighter = new byte[start + 4 + json.Length - end];
                json[..start].CopyTo(lighter);
                "null"u8.CopyTo(lighter.AsSpan(start));
                json[end..].CopyTo(lighter.AsSpan(start + 4));
                return lighter;
            }
            return null;
        }
        return null;
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

    private static IReadOnlyList<DiagnosticCaseStreamCoverage> DescribeStreams(DiagnosticCaseManifest manifest, ScanResult scan,
        DiagnosticCaseCompletion? completion) =>
        manifest.Streams.Where(s => s.Events == DiagnosticCoverageState.Included).Select(declaration =>
        {
            var stream = scan.Streams.TryGetValue(declaration.Stream, out var found) ? found : new StreamScan();
            var missing = stream.Missing.Select(m => m with { Stream = declaration.Stream }).ToList();
            // Events the completion counts as offered that were neither written nor declared lost (a collector
            // that failed before it could say so): an unknown tail, never a complete stream.
            if (completion?.Streams.FirstOrDefault(s => s?.Stream == declaration.Stream) is { } counts
                && counts.Offered > counts.Written + counts.Dropped)
            {
                var after = Math.Max(stream.Last ?? 0, missing.Select(m => m.ToOrdinal ?? m.FromOrdinal ?? 0).DefaultIfEmpty(0).Max());
                missing.Add(new DiagnosticCaseRecord { Stream = declaration.Stream, FromOrdinal = after + 1, ToOrdinal = null, Reason = "unaccounted" });
            }

            return new DiagnosticCaseStreamCoverage
            {
                Stream = declaration.Stream,
                Events = stream.Events,
                FirstOrdinal = stream.First,
                LastOrdinal = stream.Last,
                State = stream.Failure is not null ? "failed" : missing.Count == 0 ? "complete" : "incomplete",
                Missing = stream.Failure is { } failure ? [.. missing, failure] : missing,
            };
        }).ToList();

    // Checkpoint coverage: written lines, declared missing ranges, and an unaccounted tail from the completion counts.
    private static DiagnosticCaseStreamCoverage DescribeCheckpoints(ScanResult scan, DiagnosticCaseCompletion? completion)
    {
        var stream = scan.Checkpoints;
        var missing = stream.Recorded.Select(m => m with { Stream = "checkpoint" }).ToList();
        if (completion?.Checkpoints is { } counts && counts.Offered > counts.Written + counts.Dropped)
        {
            var after = Math.Max(stream.Last ?? 0, missing.Select(m => m.ToOrdinal ?? m.FromOrdinal ?? 0).DefaultIfEmpty(0).Max());
            missing.Add(new DiagnosticCaseRecord { Stream = "checkpoint", FromOrdinal = after + 1, ToOrdinal = null, Reason = "unaccounted" });
        }

        return new DiagnosticCaseStreamCoverage
        {
            Stream = "checkpoint",
            Events = stream.Events,
            FirstOrdinal = stream.First,
            LastOrdinal = stream.Last,
            State = missing.Count == 0 ? "complete" : "incomplete",
            Missing = missing,
        };
    }

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

        // The interval runs from the checkpoint's model sequence (0 for a fresh model; a start's for a case started
        // on a model that had applied output) to the first event it cannot cover. Model events are recorded from the
        // next sequence, so a model-stream ordinal n is model sequence from + n.
        var from = manifest.Checkpoint.ModelSequence ?? 0;
        var last = scan.LastModelSequence ?? from;
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
            Consider(from + Math.Max(0, (missing.FromOrdinal ?? 1) - 1), missing.Reason == "unknown" ? "model-events-missing" : missing.Reason);
        if (scan.ModelGapAfter is { } gap)
            Consider(gap, "model-events-missing");
        if (state == DiagnosticCaseCompletionState.Truncated)
            Consider(last, "truncated");
        else if (state == DiagnosticCaseCompletionState.Interrupted)
            Consider(last, "interrupted");

        var (to, reason) = end ?? (last, $"case-stopped: {(completion is null ? "unknown" : DiagnosticContractNames.Of(completion.StopReason))}");
        return [new DiagnosticCaseInterval { Valid = true, FromModelSequence = from, ToModelSequence = to, EndReason = reason }];
    }
}
