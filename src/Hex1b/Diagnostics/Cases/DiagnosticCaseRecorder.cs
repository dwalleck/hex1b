using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hex1b.Input;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// One active diagnostic case on a terminal. Producers offer events without waiting; a dedicated writer
/// drains the bounded queue into the artifact and pulls native delivery records itself. The terminal
/// holds at most one recorder, armed and cleared under its model lock.
/// </summary>
internal sealed class DiagnosticCaseRecorder : IDiagnosticStreamObserver
{
    internal const long DefaultMaxBytes = 64L * 1024 * 1024;
    internal const int DefaultMaxSeconds = 10 * 60;
    internal const long MinMaxBytes = 1024L * 1024;
    internal const long MaxMaxBytes = 1024L * 1024 * 1024;
    internal const int MinMaxSeconds = 1;
    internal const int MaxMaxSeconds = 24 * 60 * 60;

    /// <summary>Counts ingress copies made in the current async flow while a test has set a counter.</summary>
    internal static readonly AsyncLocal<StrongBox<int>?> IngressCopiesForTesting = new();

    /// <summary>
    /// A gate the writer waits on before each pass, taken from the arming flow while a test has set it,
    /// so a test can hold the writer (to force overload or eviction) and release it.
    /// </summary>
    internal static readonly AsyncLocal<ManualResetEventSlim?> WriterGateForTesting = new();

    private readonly ManualResetEventSlim? _writerGate = WriterGateForTesting.Value;

    private const int Recording = 0;
    private const int Stopping = 1;
    private const int Stopped = 2;
    private const int StreamCount = 5;

    private readonly CaseEventQueue _queue = new();
    private readonly CaseLossLedger _loss = new();
    private readonly CaseArtifactWriter _writer;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeProvider _timeProvider;
    private readonly Action<DiagnosticCaseRecorder> _finished;
    private readonly CaseSources _sources;
    private readonly bool _rawInput;
    private readonly bool _editorText;
    private readonly bool _nativeOutput;
    private readonly long[] _ordinals = new long[StreamCount];
    private readonly long[] _offered = new long[StreamCount];
    private readonly long[] _written = new long[StreamCount];
    private readonly long[] _dropped = new long[StreamCount];
    private int _state;
    // Assigned by the writer as it writes, so case sequences are dense and ordered in the file.
    private long _caseSequence;
    private long _lastWritten = -1;
    private int _stopReason = -1;
    private DateTimeOffset? _stoppedAt;
    private Task? _writerTask;

    // Where re-applicable model coverage ends. Held outside the queue so overload cannot drop it, and
    // written before any model event at or after it.
    private long _intervalEnd = long.MaxValue;
    private string? _intervalEndReason;
    private bool _intervalEndWritten;

    // The last native delivery record already pulled (records up to it predate the case or are written).
    private long _deliverySince;

    internal DiagnosticCaseRecorder(DiagnosticCaseManifest manifest, string path, TimeProvider timeProvider,
        Action<DiagnosticCaseRecorder> finished, CaseSources sources)
    {
        Manifest = manifest;
        Path = path;
        _timeProvider = timeProvider;
        _finished = finished;
        _sources = sources;
        _writer = new CaseArtifactWriter(path);
        IncludeModelPayloads = manifest.Authorizations.Contains(DiagnosticAuthorization.ReapplicationData);
        _rawInput = manifest.Authorizations.Contains(DiagnosticAuthorization.RawInput);
        _editorText = manifest.Authorizations.Contains(DiagnosticAuthorization.EditorText);
        _nativeOutput = manifest.Authorizations.Contains(DiagnosticAuthorization.NativeOutput);
        _deliverySince = sources.Delivery?.Read(long.MaxValue - 1, 1, includeBytes: false).Totals.LastSequence ?? 0;
    }

    internal DiagnosticCaseManifest Manifest { get; }

    internal string Path { get; }

    internal string CaseId => Manifest.CaseId;

    /// <summary>Whether model events carry their original input bytes (<c>reapplication-data</c>).</summary>
    internal bool IncludeModelPayloads { get; }

    internal bool IsRecording => Volatile.Read(ref _state) == Recording;

    /// <summary>Completes when the writer has finished and the completion record is written (or failed).</summary>
    internal Task Completion => _writerTask ?? Task.CompletedTask;

    /// <summary>Starts the writer; called once, after the terminal armed this recorder.</summary>
    internal void Start() => _writerTask = Task.Factory.StartNew(WriteLoopAsync, CancellationToken.None,
        TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();

    /// <summary>
    /// Records an output application with its original input bytes, copied only under
    /// <c>reapplication-data</c>. The caller holds the model lock, which orders model events.
    /// </summary>
    internal void RecordApplication(long modelSequence, int width, int height, bool hasIngress, ReadOnlySpan<byte> ingress)
    {
        if (!IsRecording)
            return;
        // A model change the case holds no input for cannot be reproduced: coverage ends here.
        if (!hasIngress)
            EndInterval(modelSequence, "application-without-ingress");
        byte[]? payload = null;
        if (IncludeModelPayloads && hasIngress)
        {
            payload = ingress.ToArray();
            if (IngressCopiesForTesting.Value is { } copies)
                Interlocked.Increment(ref copies.Value);
        }

        Offer(CaseStream.Model, hasIngress ? "application" : "application-without-ingress", modelSequence, width, height,
            ingress.Length, payload, null);
    }

    /// <summary>Records a model event. The caller holds the terminal's model lock, which orders model events.</summary>
    internal void RecordModelEvent(string kind, long modelSequence, int width, int height)
    {
        if (IsRecording)
            Offer(CaseStream.Model, kind, modelSequence, width, height, null, null, null);
    }

    /// <summary>
    /// Ends re-applicable coverage at <paramref name="modelSequence"/> (the first event that cannot be
    /// reproduced). The first end wins. The caller holds the model lock.
    /// </summary>
    internal void EndInterval(long modelSequence, string reason)
    {
        if (modelSequence >= Volatile.Read(ref _intervalEnd))
            return;
        _intervalEndReason = reason;
        Volatile.Write(ref _intervalEnd, modelSequence);
    }

    void IDiagnosticStreamObserver.OnInputAccepted(long id, string kind, string source, Hex1bEvent? evt)
    {
        if (IsRecording)
            Offer(CaseStream.Input, "accepted", null, null, null, null, null, new DiagnosticCaseInputEvent
            {
                Id = id,
                InputKind = kind,
                Source = source,
                Payload = _rawInput && evt is not null ? InputMilestoneTracker.PayloadOf(evt) : null,
            });
    }

    void IDiagnosticStreamObserver.OnInputProcessed(long id, string applicationInstanceId, long watermark)
    {
        if (IsRecording)
            Offer(CaseStream.Input, "processed", null, null, null, null, null, new DiagnosticCaseInputEvent
            {
                Id = id,
                ProcessedBy = applicationInstanceId,
                Watermark = watermark,
            });
    }

    void IDiagnosticStreamObserver.OnFramePublished(string applicationInstanceId, long frameId, long processedInput, bool wroteOutput,
        long? outputMark)
    {
        if (!IsRecording)
            return;
        // The application writes the frame before it reports the publication, on the same thread.
        var published = _sources.Frames?.LatestFrame is { } latest && latest.FrameId == frameId ? latest : null;
        var projection = published?.Frame is { } frame ? (_editorText ? frame : TerminalDiagnostics.WithoutEditorText(frame)) : null;
        Offer(CaseStream.Frames, "published", null, null, null, null, null, new DiagnosticCaseFrameEvent
        {
            FrameId = frameId,
            ApplicationInstanceId = applicationInstanceId,
            ProcessedInput = processedInput,
            WroteOutput = wroteOutput,
            OutputMark = outputMark,
            Projection = projection,
            Failure = published is null ? "The published projection was already replaced." : published.Failure,
        });
    }

    private void Offer(CaseStream stream, string kind, long? modelSequence, int? width, int? height, int? length, byte[]? payload,
        object? detail)
    {
        var index = (int)stream;
        Interlocked.Increment(ref _offered[index]);
        var ordinal = Interlocked.Increment(ref _ordinals[index]);
        var item = new CaseEvent(stream, ordinal, Stopwatch.GetTimestamp(), kind, modelSequence, width, height, length, payload, detail);
        if (_queue.TryEnqueue(item))
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        else
        {
            // Drop-newest; the loss is recorded outside the queue that could not take the event.
            Interlocked.Increment(ref _dropped[index]);
            _loss.Record(stream, ordinal);
        }
    }

    /// <summary>Asks the case to stop without waiting; the first reason wins.</summary>
    internal void RequestStop(DiagnosticCaseStopReason reason)
    {
        Interlocked.CompareExchange(ref _stopReason, (int)reason, -1);
        if (Interlocked.CompareExchange(ref _state, Stopping, Recording) == Recording)
            _signal.Release();
    }

    /// <summary>Stops the case and waits for its writer; returns the reason it stopped with.</summary>
    internal async Task<DiagnosticCaseStopReason> StopAsync(DiagnosticCaseStopReason reason)
    {
        RequestStop(reason);
        await Completion.ConfigureAwait(false);
        return (DiagnosticCaseStopReason)Volatile.Read(ref _stopReason);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            _writer.WriteManifest(Manifest);
            while (true)
            {
                _writerGate?.Wait();
                var stopping = Volatile.Read(ref _state) != Recording;
                // Loss first: it does not depend on the queue draining.
                WriteLoss(final: false);
                while (_queue.TryDequeue(out var item))
                    Write(item);
                PullDelivery();
                _writer.Flush();
                if (stopping && _queue.Count == 0)
                    break;
                if (!stopping)
                    await _signal.WaitAsync(50).ConfigureAwait(false);
            }

            WriteIntervalEndIfPending(long.MaxValue);
            WriteLoss(final: true);
            _stoppedAt = _timeProvider.GetUtcNow();
            _writer.Flush();
            _writer.WriteCompletion(DescribeCompletion());
        }
        catch (Exception)
        {
            Interlocked.Exchange(ref _stopReason, (int)DiagnosticCaseStopReason.CollectorFailed);
            _stoppedAt ??= _timeProvider.GetUtcNow();
        }
        finally
        {
            Volatile.Write(ref _state, Stopped);
            _sources.Input?.ClearStreamObserver(this);
            _writer.Dispose();
            _finished(this);
        }
    }

    private void Write(in CaseEvent item)
    {
        if (item.Stream == CaseStream.Model)
            WriteIntervalEndIfPending(item.ModelSequence ?? long.MaxValue);
        var sequence = ++_caseSequence;
        _writer.WriteEvent(new DiagnosticCaseEvent
        {
            CaseSequence = sequence,
            Stream = CaseArtifactWriter.StreamName(item.Stream),
            Ordinal = item.Ordinal,
            Timestamp = item.Timestamp,
            Kind = item.Kind,
            ModelSequence = item.ModelSequence,
            Width = item.Width,
            Height = item.Height,
            Length = item.Length,
            Data = item.Payload is { } payload ? Convert.ToBase64String(payload) : null,
            Input = item.Detail as DiagnosticCaseInputEvent,
            Frame = item.Detail as DiagnosticCaseFrameEvent,
        });
        Interlocked.Increment(ref _written[(int)item.Stream]);
        Volatile.Write(ref _lastWritten, sequence);
    }

    // Writes the interval end before the first model event at or after it (or at the end of the case).
    private void WriteIntervalEndIfPending(long nextModelSequence)
    {
        var end = Volatile.Read(ref _intervalEnd);
        if (_intervalEndWritten || end == long.MaxValue || nextModelSequence < end)
            return;
        _intervalEndWritten = true;
        WriteCaseRecord("interval-end", end, new DiagnosticCaseRecord { Stream = "model", Reason = _intervalEndReason ?? "unsupported" });
    }

    private void WriteLoss(bool final)
    {
        foreach (var record in _loss.Take(final))
            WriteCaseRecord("missing", null, record);
    }

    private void WriteCaseRecord(string kind, long? modelSequence, DiagnosticCaseRecord record)
    {
        var sequence = ++_caseSequence;
        _writer.WriteEvent(new DiagnosticCaseEvent
        {
            CaseSequence = sequence,
            Stream = "case",
            Ordinal = Interlocked.Increment(ref _ordinals[(int)CaseStream.Case]),
            Timestamp = Stopwatch.GetTimestamp(),
            Kind = kind,
            ModelSequence = modelSequence,
            Record = record,
        });
        Interlocked.Increment(ref _written[(int)CaseStream.Case]);
        Volatile.Write(ref _lastWritten, sequence);
    }

    // Native delivery records live in their own bounded ring; the writer pulls them instead of adding a
    // hook to the presentation write path. Records evicted between pulls become a missing range.
    private void PullDelivery()
    {
        if (_sources.Delivery is not { } delivery)
            return;
        var snapshot = delivery.Read(_deliverySince, NativeDeliveryRecorder.MaxRecords, _nativeOutput);
        foreach (var record in snapshot.Records)
        {
            if (record.Sequence > _deliverySince + 1)
            {
                var missing = record.Sequence - _deliverySince - 1;
                Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], missing);
                Interlocked.Add(ref _offered[(int)CaseStream.Delivery], missing);
                WriteCaseRecord("missing", null, new DiagnosticCaseRecord
                {
                    Stream = "delivery",
                    FromOrdinal = _deliverySince + 1,
                    ToOrdinal = record.Sequence - 1,
                    Reason = "evicted",
                });
            }

            var sequence = ++_caseSequence;
            _writer.WriteEvent(new DiagnosticCaseEvent
            {
                CaseSequence = sequence,
                Stream = "delivery",
                Ordinal = record.Sequence,
                Timestamp = record.StartTimestamp,
                Kind = "delivery",
                Delivery = record,
            });
            Interlocked.Increment(ref _offered[(int)CaseStream.Delivery]);
            Interlocked.Increment(ref _written[(int)CaseStream.Delivery]);
            Volatile.Write(ref _lastWritten, sequence);
            _deliverySince = record.Sequence;
        }
    }

    private DiagnosticCaseCompletion DescribeCompletion() => new()
    {
        CaseId = CaseId,
        StopReason = (DiagnosticCaseStopReason)Volatile.Read(ref _stopReason),
        StoppedAt = _stoppedAt ?? _timeProvider.GetUtcNow(),
        LastCaseSequence = Volatile.Read(ref _lastWritten) is var last and >= 0 ? last : null,
        BytesWritten = _writer.BytesWritten,
        Streams = DescribeStreams(),
    };

    private IReadOnlyList<DiagnosticCaseStreamStatus> DescribeStreams() =>
        Manifest.Streams.Where(s => s.Events == DiagnosticCoverageState.Included).Select(s => (Declaration: s, Index: StreamIndex(s.Stream)))
            .Select(s => new DiagnosticCaseStreamStatus
            {
                Stream = s.Declaration.Stream,
                Offered = Interlocked.Read(ref _offered[s.Index]),
                Written = Interlocked.Read(ref _written[s.Index]),
                Dropped = Interlocked.Read(ref _dropped[s.Index]),
            }).ToList();

    private static int StreamIndex(string name) => name switch
    {
        "model" => (int)CaseStream.Model,
        "input" => (int)CaseStream.Input,
        "frames" => (int)CaseStream.Frames,
        "delivery" => (int)CaseStream.Delivery,
        _ => (int)CaseStream.Case,
    };

    /// <summary>The case as a start, stop or status result.</summary>
    internal DiagnosticCaseResult Describe()
    {
        var state = Volatile.Read(ref _state);
        var reason = Volatile.Read(ref _stopReason);
        var end = state == Stopped ? _stoppedAt : null;
        return new DiagnosticCaseResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = CaseId,
            Path = Path,
            State = state switch { Recording => DiagnosticCaseState.Recording, Stopping => DiagnosticCaseState.Stopping, _ => DiagnosticCaseState.Stopped },
            StartPath = Manifest.StartPath,
            Bounds = Manifest.Bounds,
            Authorizations = Manifest.Authorizations,
            Checkpoint = Manifest.Checkpoint,
            StartedAt = Manifest.StartedAt,
            ElapsedSeconds = ((end ?? _timeProvider.GetUtcNow()) - Manifest.StartedAt).TotalSeconds,
            BytesWritten = _writer.BytesWritten,
            Streams = DescribeStreams(),
            StopReason = state == Recording || reason < 0 ? null : (DiagnosticCaseStopReason)reason,
        };
    }
}
