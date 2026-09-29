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

    /// <summary>Runs after a start armed its case and before its writer starts, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action?> AfterArmForTesting = new();

    /// <summary>
    /// Runs on the producer's thread after an offer passed its recording check and before it enqueues,
    /// taken from the arming flow while a test has set it (to hold a producer across a stop).
    /// </summary>
    internal static readonly AsyncLocal<Action?> BeforeEnqueueForTesting = new();

    /// <summary>An exception the writer's disposal throws, taken from the arming flow while a test has set it.</summary>
    internal static readonly AsyncLocal<Exception?> WriterDisposeFaultForTesting = new();

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

    // The size bound is kept in tiers, so what must be written always has room:
    //   events and running loss ranges                 below MaxBytes - 16 KiB (EventLimit);
    //   ranges written as the case closes (discards)   below MaxBytes - 8 KiB  (RangeLimit);
    //   closing records: the interval end, at most one failure and one overflow summary per stream,
    //   each a bounded line                             below MaxBytes - 2 KiB  (ClosingLimit);
    //   the completion record                          in the rest.
    private const long EventReserve = 16 * 1024;
    private const long RangeReserve = 8 * 1024;
    private const long ClosingReserve = 2 * 1024;
    private const int MaxFailureMessage = 256;

    private Exception? _writerFault = WriterFaultForTesting.Value;
    private Exception? _writerDisposeFault = WriterDisposeFaultForTesting.Value;
    private readonly Action? _beforeEnqueue = BeforeEnqueueForTesting.Value;
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
    // Completed when the writer has finished. It exists from construction, so a stop that finds the recorder
    // armed but not yet started still waits for the artifact.
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    // Producers between their recording check and their enqueue; the writer's last pass waits for them.
    private int _offersInFlight;
    // The application begun under the model lock and offered when it ends, after its graphics check.
    private (long ModelSequence, int Width, int Height, int Length, bool HasIngress, byte[]? Payload)? _application;
    // Per stream, the first ordinal of a missing range that did not fit under the size bound. From there the
    // stream's coverage is summarized as one range of unknown extent when the case closes.
    private readonly long?[] _undescribedFrom = new long?[StreamCount];

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

    /// <summary>The model sequence where re-applicable coverage ends, or <see cref="long.MaxValue"/>.</summary>
    internal long IntervalEnd => Volatile.Read(ref _intervalEnd);

    /// <summary>The loss ledger, for tests that need more loss ranges than a real overload produces.</summary>
    internal CaseLossLedger LossForTesting => _loss;

    /// <summary>Completes when the writer has finished and the completion record is written (or failed).</summary>
    internal Task Completion => _completion.Task;

    /// <summary>Starts the writer and the time bound; called once, after the terminal armed this recorder.</summary>
    internal void Start()
    {
        _timeLimit = _timeProvider.CreateTimer(_ => RequestStop(DiagnosticCaseStopReason.TimeLimit), null,
            TimeSpan.FromSeconds(Manifest.Bounds.MaxSeconds), Timeout.InfiniteTimeSpan);
        _ = Task.Run(WriteLoopAsync);
    }

    /// <summary>
    /// Begins an output application: copies its original input bytes (only under <c>reapplication-data</c>)
    /// while they are valid. The event is offered by <see cref="EndApplication"/>, once the application's
    /// effect on graphics state is known, so an interval end it causes precedes it in the artifact. The caller
    /// holds the model lock for the whole application.
    /// </summary>
    internal void BeginApplication(long modelSequence, int width, int height, bool hasIngress, ReadOnlySpan<byte> ingress)
    {
        _application = null;
        if (!IsRecording || Volatile.Read(ref _failed[(int)CaseStream.Model]))
            return;
        try
        {
            byte[]? payload = null;
            if (IncludeModelPayloads && hasIngress)
            {
                payload = ingress.ToArray();
                if (IngressCopiesForTesting.Value is { } copies)
                    Interlocked.Increment(ref copies.Value);
            }

            _application = (modelSequence, width, height, ingress.Length, hasIngress, payload);
        }
        catch (Exception error)
        {
            FailModel(modelSequence, error);
        }
    }

    /// <summary>
    /// Offers the application begun by <see cref="BeginApplication"/>. Graphics state the checkpoint cannot
    /// hold, or an application without input bytes, ends re-applicable coverage at it.
    /// </summary>
    internal void EndApplication(bool graphics)
    {
        if (_application is not { } application)
            return;
        _application = null;
        // A model change the case holds no input for cannot be reproduced: coverage ends here.
        if (!application.HasIngress)
            EndInterval(application.ModelSequence, "application-without-ingress");
        if (graphics)
            EndInterval(application.ModelSequence, "graphics");
        OfferModel(application.HasIngress ? "application" : "application-without-ingress", application.ModelSequence,
            application.Width, application.Height, application.Length, application.Payload);
    }

    /// <summary>Records a model event. The caller holds the terminal's model lock, which orders model events.</summary>
    internal void RecordModelEvent(string kind, long modelSequence, int width, int height) =>
        OfferModel(kind, modelSequence, width, height, null, null);

    // A model-stream failure must never reach the terminal, whose model sequence has already advanced: the
    // stream ends, and re-applicable coverage with it.
    private void OfferModel(string kind, long modelSequence, int width, int height, int? length, byte[]? payload)
    {
        if (!IsRecording || Volatile.Read(ref _failed[(int)CaseStream.Model]))
            return;
        try
        {
            Offer(CaseStream.Model, kind, modelSequence, width, height, length, payload, null);
        }
        catch (Exception error)
        {
            FailModel(modelSequence, error);
        }
    }

    private void FailModel(long modelSequence, Exception error)
    {
        EndInterval(modelSequence, "stream-failed");
        FailStream(CaseStream.Model, error);
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
        Offer(CaseStream.Frames, "published", null, null, null, null, null, detailBytes: CaseFrameSize.Estimate(projection), detail: new DiagnosticCaseFrameEvent
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
            Reason = Bounded($"stream-failed: {error.GetType().Name}: {error.Message}"),
        });
        if (_signal.CurrentCount == 0)
            _signal.Release();
    }

    private static string Bounded(string text) => text.Length <= MaxFailureMessage ? text : text[..MaxFailureMessage] + "…";

    private void Offer(CaseStream stream, string kind, long? modelSequence, int? width, int? height, int? length, byte[]? payload,
        object? detail = null, int detailBytes = 0)
    {
        // Counted in flight before the recording check (both full fences), so the writer's last pass, which
        // runs after the stop and waits for no offer in flight, sees every event offered while recording.
        Interlocked.Increment(ref _offersInFlight);
        try
        {
            if (Volatile.Read(ref _state) != Recording)
                return;
            var index = (int)stream;
            if (_streamFault is { } faulted && faulted == CaseArtifactWriter.StreamName(stream) && Interlocked.CompareExchange(ref _streamFault, null, faulted) == faulted)
                throw new InvalidOperationException("Injected stream failure.");
            _beforeEnqueue?.Invoke();
            Interlocked.Increment(ref _offered[index]);
            var ordinal = Interlocked.Increment(ref _ordinals[index]);
            var item = new CaseEvent(stream, ordinal, Stopwatch.GetTimestamp(), kind, modelSequence, width, height, length, payload, detail, detailBytes);
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
        finally
        {
            Interlocked.Decrement(ref _offersInFlight);
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
                if (stopping && _queue.Count == 0 && Volatile.Read(ref _offersInFlight) == 0)
                    break;
                if (stopping)
                    await Task.Delay(1).ConfigureAwait(false);
                else
                    await _signal.WaitAsync(50).ConfigureAwait(false);
            }

            WriteClosingRecords();
            _stoppedAt = _timeProvider.GetUtcNow();
            _writer.Flush();
            _writer.WriteCompletion(DescribeCompletion());
        }
        catch (Exception)
        {
            // The writer or its storage failed: the case stops. What was never written is declared missing
            // and the completion is attempted; with storage failing either may not exist, and the artifact
            // then reads as interrupted. The reader also marks any stream whose completion counts show
            // unaccounted events.
            Interlocked.Exchange(ref _stopReason, (int)DiagnosticCaseStopReason.CollectorFailed);
            Volatile.Write(ref _state, Stopping);
            _stoppedAt ??= _timeProvider.GetUtcNow();
            try
            {
                DiscardQueued("collector-failed");
            }
            catch (Exception)
            {
            }

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
            try
            {
                _timeLimit?.Dispose();
                _sources.Input?.ClearStreamObserver(this);
                if (Interlocked.Exchange(ref _writerDisposeFault, null) is { } fault)
                    throw fault;
                _writer.Dispose();
            }
            catch (Exception)
            {
                // Disposal re-flushes a buffer a failed write left behind; the case has ended either way.
            }
            finally
            {
                // Always forget the case, so the terminal can start another.
                _finished(this);
                _completion.TrySetResult();
            }
        }
    }

    private void WriteClosingRecords()
    {
        WriteIntervalEndIfPending(long.MaxValue);
        WriteLoss(final: true);
        WriteStreamFailures();
        // Missing ranges that did not fit under the size bound: one range of unknown extent per stream.
        for (var index = 0; index < StreamCount; index++)
        {
            if (_undescribedFrom[index] is not { } from)
                continue;
            _undescribedFrom[index] = null;
            AppendCase("missing", null, new DiagnosticCaseRecord
            {
                Stream = CaseArtifactWriter.StreamName((CaseStream)index),
                FromOrdinal = from,
                ToOrdinal = null,
                Reason = "size-limit-unknown-extent",
            }, ClosingLimit);
        }
    }

    // Writes queued events; false when the size bound stops the case.
    private bool DrainQueue()
    {
        while (Volatile.Read(ref _drainAbandoned) == 0 && _queue.TryDequeue(out var item))
        {
            // Pending until written, so a size bound or a writer failure declares it missing with the queue.
            _pendingAfterLimit = item;
            if (!Write(item))
                return false;
            _pendingAfterLimit = null;
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
        foreach (var (stream, list) in ranges)
            foreach (var (from, to) in list)
                WriteMissing(new DiagnosticCaseRecord { Stream = CaseArtifactWriter.StreamName(stream), FromOrdinal = from, ToOrdinal = to, Reason = reason }, RangeLimit);
        WriteClosingRecords();
    }

    private void WriteStreamFailures()
    {
        while (_streamFailures.TryDequeue(out var failure))
            AppendCase("stream-failed", null, failure, ClosingLimit);
    }

    private long EventLimit => Manifest.Bounds.MaxBytes - EventReserve;

    private long RangeLimit => Manifest.Bounds.MaxBytes - RangeReserve;

    private long ClosingLimit => Manifest.Bounds.MaxBytes - ClosingReserve;

    // A missing range must fit below its tier. One that does not stops the case at its size bound, and its
    // stream's loss from there is summarized as one range of unknown extent when the case closes.
    private void WriteMissing(DiagnosticCaseRecord record, long limit)
    {
        var index = StreamIndex(record.Stream);
        if (_undescribedFrom[index] is null && AppendCase("missing", null, record, limit))
            return;
        if (_undescribedFrom[index] is not { } from || record.FromOrdinal < from)
            _undescribedFrom[index] = record.FromOrdinal;
        RequestStop(DiagnosticCaseStopReason.SizeLimit);
    }

    // Writes one queued event; false (nothing written) when it would cross the size bound.
    private bool Write(in CaseEvent item)
    {
        if (Interlocked.Exchange(ref _writerFault, null) is { } fault)
            throw fault;
        if (item.Stream == CaseStream.Model)
            WriteIntervalEndIfPending(item.ModelSequence ?? long.MaxValue);
        return Append(item.Stream, new DiagnosticCaseEvent
        {
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
        }, EventLimit);
    }

    // The one append path: assigns the next case sequence and counts the line only when it was written.
    private bool Append(CaseStream stream, DiagnosticCaseEvent item, long limit)
    {
        var sequence = _caseSequence + 1;
        if (!_writer.TryWriteEvent(item with { CaseSequence = sequence }, limit))
            return false;
        _caseSequence = sequence;
        Interlocked.Increment(ref _written[(int)stream]);
        Volatile.Write(ref _lastWritten, sequence);
        return true;
    }

    // Case records are numbered by the writer alone, and only when written.
    private bool AppendCase(string kind, long? modelSequence, DiagnosticCaseRecord record, long limit)
    {
        var ordinal = _ordinals[(int)CaseStream.Case] + 1;
        if (!Append(CaseStream.Case, new DiagnosticCaseEvent
        {
            Stream = "case",
            Ordinal = ordinal,
            Timestamp = Stopwatch.GetTimestamp(),
            Kind = kind,
            ModelSequence = modelSequence,
            Record = record,
        }, limit))
            return false;
        _ordinals[(int)CaseStream.Case] = ordinal;
        return true;
    }

    // Writes the interval end before the first model event at or after it (or at the end of the case).
    private void WriteIntervalEndIfPending(long nextModelSequence)
    {
        var end = Volatile.Read(ref _intervalEnd);
        if (_intervalEndWritten || end == long.MaxValue || nextModelSequence < end)
            return;
        _intervalEndWritten = true;
        AppendCase("interval-end", end, new DiagnosticCaseRecord { Stream = "model", Reason = _intervalEndReason ?? "unsupported" }, ClosingLimit);
    }

    private void WriteLoss(bool final)
    {
        foreach (var record in _loss.Take(final))
            WriteMissing(record, final ? RangeLimit : EventLimit);
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
                WriteMissing(new DiagnosticCaseRecord
                {
                    Stream = "delivery",
                    FromOrdinal = _deliverySince + 1,
                    ToOrdinal = record.Sequence - 1,
                    Reason = "evicted",
                }, EventLimit);
            }

            Interlocked.Increment(ref _offered[(int)CaseStream.Delivery]);
            if (!Append(CaseStream.Delivery, new DiagnosticCaseEvent
            {
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
                WriteMissing(new DiagnosticCaseRecord { Stream = "delivery", FromOrdinal = record.Sequence, ToOrdinal = last, Reason = "size-limit" }, RangeLimit);
                _deliverySince = last;
                return false;
            }

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
