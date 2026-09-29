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

    /// <summary>An exception the writer throws on its next event, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> WriterFaultForTesting = new();

    /// <summary>A stream whose next offered event fails, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<string?> StreamFaultForTesting = new();

    /// <summary>Runs just before a start arms its case, on the starting flow, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action?> BeforeArmForTesting = new();

    /// <summary>A shorter drain bound for tests, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<TimeSpan?> DrainTimeoutForTesting = new();

    /// <summary>
    /// Test-only: milliseconds the writer waits after the manifest before its first pass, so an end-to-end
    /// harness can overload a real target's queue. Read once per case; ignored unless a positive integer
    /// below the drain bound.
    /// </summary>
    internal const string WriterHoldEnvironmentVariable = "HEX1B_DIAGNOSTIC_CASE_TEST_WRITER_HOLD_MS";

    private static TimeSpan ReadWriterHold() =>
        int.TryParse(Environment.GetEnvironmentVariable(WriterHoldEnvironmentVariable), out var ms) && ms is > 0 and < 10_000
            ? TimeSpan.FromMilliseconds(ms)
            : TimeSpan.Zero;

    private readonly TimeSpan _writerHold = ReadWriterHold();

    /// <summary>How long a stop waits for queued events to reach the artifact (spec Q11).</summary>
    internal static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    // Artifact bytes kept free under the size bound for the closing records (loss, interval end, completion).
    private const long ClosingReserve = 16 * 1024;

    private Exception? _writerFault = WriterFaultForTesting.Value;
    private string? _streamFault = StreamFaultForTesting.Value;
    private readonly TimeSpan _drainTimeout = DrainTimeoutForTesting.Value ?? DrainTimeout;

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

    // Set when a stop gave up waiting for the writer: whatever it has not written is declared missing.
    private int _drainAbandoned;
    private ITimer? _timeLimit;

    // Streams that failed stop recording; their failure is written outside the queue.
    private readonly bool[] _failed = new bool[StreamCount];
    private readonly System.Collections.Concurrent.ConcurrentQueue<DiagnosticCaseRecord> _streamFailures = new();

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

    /// <summary>Starts the writer and the time bound; called once, after the terminal armed this recorder.</summary>
    internal void Start()
    {
        _timeLimit = _timeProvider.CreateTimer(_ => RequestStop(DiagnosticCaseStopReason.TimeLimit), null,
            TimeSpan.FromSeconds(Manifest.Bounds.MaxSeconds), Timeout.InfiniteTimeSpan);
        _writerTask = Task.Factory.StartNew(WriteLoopAsync, CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default).Unwrap();
    }

    /// <summary>
    /// Records an output application with its original input bytes, copied only under
    /// <c>reapplication-data</c>. The caller holds the model lock, which orders model events.
    /// </summary>
    internal void RecordApplication(long modelSequence, int width, int height, bool hasIngress, ReadOnlySpan<byte> ingress)
    {
        if (!IsRecording || Volatile.Read(ref _failed[(int)CaseStream.Model]))
            return;
        // A model change the case holds no input for cannot be reproduced: coverage ends here.
        if (!hasIngress)
            EndInterval(modelSequence, "application-without-ingress");
        try
        {
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
        catch (Exception error)
        {
            // The event is lost with the stream: re-applicable coverage ends at it.
            EndInterval(modelSequence, "stream-failed");
            FailStream(CaseStream.Model, error);
        }
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

    // The tracker calls these under its own lock from the application's threads: a failure here must end
    // only this stream, never reach the application.
    void IDiagnosticStreamObserver.OnInputAccepted(long id, string kind, string source, Hex1bEvent? evt) =>
        Observe(CaseStream.Input, () => Offer(CaseStream.Input, "accepted", null, null, null, null, null, new DiagnosticCaseInputEvent
        {
            Id = id,
            InputKind = kind,
            Source = source,
            Payload = _rawInput && evt is not null ? InputMilestoneTracker.PayloadOf(evt) : null,
        }));

    void IDiagnosticStreamObserver.OnInputProcessed(long id, string applicationInstanceId, long watermark) =>
        Observe(CaseStream.Input, () => Offer(CaseStream.Input, "processed", null, null, null, null, null, new DiagnosticCaseInputEvent
        {
            Id = id,
            ProcessedBy = applicationInstanceId,
            Watermark = watermark,
        }));

    void IDiagnosticStreamObserver.OnFramePublished(string applicationInstanceId, long frameId, long processedInput, bool wroteOutput,
        long? outputMark) => Observe(CaseStream.Frames, () =>
    {
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
    });

    private void Observe(CaseStream stream, Action record)
    {
        if (!IsRecording || Volatile.Read(ref _failed[(int)stream]))
            return;
        try
        {
            record();
        }
        catch (Exception error)
        {
            FailStream(stream, error);
        }
    }

    // Ends one stream's coverage; the others keep recording. The failure is written outside the queue.
    private void FailStream(CaseStream stream, Exception error)
    {
        if (Volatile.Read(ref _failed[(int)stream]))
            return;
        Volatile.Write(ref _failed[(int)stream], true);
        _streamFailures.Enqueue(new DiagnosticCaseRecord
        {
            Stream = CaseArtifactWriter.StreamName(stream),
            FromOrdinal = Interlocked.Read(ref _ordinals[(int)stream]) + 1,
            Reason = $"stream-failed: {error.GetType().Name}: {error.Message}",
        });
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private void Offer(CaseStream stream, string kind, long? modelSequence, int? width, int? height, int? length, byte[]? payload,
        object? detail)
    {
        var index = (int)stream;
        if (_streamFault is { } faulted && faulted == CaseArtifactWriter.StreamName(stream) && Interlocked.CompareExchange(ref _streamFault, null, faulted) == faulted)
            throw new InvalidOperationException("Injected stream failure.");
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

    /// <summary>
    /// Stops the case and waits (at most the drain bound) for its writer; returns the reason it stopped
    /// with. A writer that has not finished by then abandons its unwritten events as missing.
    /// </summary>
    internal async Task<DiagnosticCaseStopReason> StopAsync(DiagnosticCaseStopReason reason)
    {
        RequestStop(reason);
        if (!Completion.IsCompleted)
        {
            await Task.WhenAny(Completion, Task.Delay(_drainTimeout)).ConfigureAwait(false);
            if (!Completion.IsCompleted)
                Volatile.Write(ref _drainAbandoned, 1);
        }

        return (DiagnosticCaseStopReason)Volatile.Read(ref _stopReason);
    }

    private async Task WriteLoopAsync()
    {
        try
        {
            _writer.WriteManifest(Manifest);
            if (_writerHold > TimeSpan.Zero)
                await Task.Delay(_writerHold).ConfigureAwait(false);
            while (true)
            {
                _writerGate?.Wait();
                var stopping = Volatile.Read(ref _state) != Recording;
                // Loss and failures first: they do not depend on the queue draining.
                WriteLoss(final: false);
                WriteStreamFailures();
                if (Volatile.Read(ref _drainAbandoned) == 1)
                {
                    DiscardQueued("drain-timeout");
                    break;
                }

                if (!DrainQueue() || !PullDelivery())
                {
                    // The size bound: the case ends, and what it could not write is missing.
                    RequestStop(DiagnosticCaseStopReason.SizeLimit);
                    DiscardQueued("size-limit");
                    break;
                }

                _writer.Flush();
                if (stopping && _queue.Count == 0)
                    break;
                if (!stopping)
                    await _signal.WaitAsync(50).ConfigureAwait(false);
            }

            WriteClosingRecords();
            _stoppedAt = _timeProvider.GetUtcNow();
            _writer.Flush();
            _writer.WriteCompletion(DescribeCompletion());
        }
        catch (Exception)
        {
            // The writer or its storage failed: the case stops. The completion is attempted, but with
            // storage failing it may not exist, and the artifact then reads as interrupted.
            Interlocked.Exchange(ref _stopReason, (int)DiagnosticCaseStopReason.CollectorFailed);
            Volatile.Write(ref _state, Stopping);
            _stoppedAt ??= _timeProvider.GetUtcNow();
            try
            {
                _writer.WriteCompletion(DescribeCompletion());
            }
            catch (Exception)
            {
            }
        }
        finally
        {
            Volatile.Write(ref _state, Stopped);
            _timeLimit?.Dispose();
            _sources.Input?.ClearStreamObserver(this);
            _writer.Dispose();
            _finished(this);
        }
    }

    private void WriteClosingRecords()
    {
        WriteIntervalEndIfPending(long.MaxValue);
        WriteLoss(final: true);
        WriteStreamFailures();
    }

    // Writes queued events; false when the size bound stops the case.
    private bool DrainQueue()
    {
        while (Volatile.Read(ref _drainAbandoned) == 0 && _queue.TryDequeue(out var item))
        {
            if (!Write(item))
            {
                _pendingAfterLimit = item;
                return false;
            }
        }

        return true;
    }

    private CaseEvent? _pendingAfterLimit;

    // Declares every event not written (the one that crossed the size bound and the queue) missing.
    private void DiscardQueued(string reason)
    {
        var ranges = new Dictionary<CaseStream, List<(long From, long To)>>();
        void Add(in CaseEvent item)
        {
            if (!ranges.TryGetValue(item.Stream, out var list))
                ranges[item.Stream] = list = [];
            if (list.Count > 0 && list[^1].To == item.Ordinal - 1)
                list[^1] = (list[^1].From, item.Ordinal);
            else
                list.Add((item.Ordinal, item.Ordinal));
            Interlocked.Increment(ref _dropped[(int)item.Stream]);
        }

        if (_pendingAfterLimit is { } pending)
            Add(pending);
        _pendingAfterLimit = null;
        while (_queue.TryDequeue(out var item))
            Add(item);
        WriteClosingRecords();
        foreach (var (stream, list) in ranges)
            foreach (var (from, to) in list)
                WriteCaseRecord("missing", null, new DiagnosticCaseRecord { Stream = CaseArtifactWriter.StreamName(stream), FromOrdinal = from, ToOrdinal = to, Reason = reason });
    }

    private void WriteStreamFailures()
    {
        while (_streamFailures.TryDequeue(out var failure))
            WriteCaseRecord("stream-failed", null, failure);
    }

    private long EventLimit => Manifest.Bounds.MaxBytes - ClosingReserve;

    // Writes one queued event; false (nothing written) when it would cross the size bound.
    private bool Write(in CaseEvent item)
    {
        if (Interlocked.Exchange(ref _writerFault, null) is { } fault)
            throw fault;
        if (item.Stream == CaseStream.Model)
            WriteIntervalEndIfPending(item.ModelSequence ?? long.MaxValue);
        var sequence = _caseSequence + 1;
        if (!_writer.TryWriteEvent(new DiagnosticCaseEvent
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
        }, EventLimit))
            return false;
        _caseSequence = sequence;
        Interlocked.Increment(ref _written[(int)item.Stream]);
        Volatile.Write(ref _lastWritten, sequence);
        return true;
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
    // Writes delivery records pulled since the last pass; false when the size bound stops the case.
    private bool PullDelivery()
    {
        if (_sources.Delivery is not { } delivery)
            return true;
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

            var sequence = _caseSequence + 1;
            Interlocked.Increment(ref _offered[(int)CaseStream.Delivery]);
            if (!_writer.TryWriteEvent(new DiagnosticCaseEvent
            {
                CaseSequence = sequence,
                Stream = "delivery",
                Ordinal = record.Sequence,
                Timestamp = record.StartTimestamp,
                Kind = "delivery",
                Delivery = record,
            }, EventLimit))
            {
                // This record and the rest of the snapshot were pulled but never written.
                var last = snapshot.Records[^1].Sequence;
                Interlocked.Add(ref _offered[(int)CaseStream.Delivery], last - record.Sequence);
                Interlocked.Add(ref _dropped[(int)CaseStream.Delivery], last - record.Sequence + 1);
                WriteCaseRecord("missing", null, new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = record.Sequence, ToOrdinal = last, Reason = "size-limit" });
                return false;
            }

            _caseSequence = sequence;
            Interlocked.Increment(ref _written[(int)CaseStream.Delivery]);
            Volatile.Write(ref _lastWritten, sequence);
            _deliverySince = record.Sequence;
        }

        return true;
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
