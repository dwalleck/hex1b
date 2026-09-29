using System.Diagnostics;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// One active diagnostic case on a terminal. Producers offer events without waiting; a dedicated writer
/// drains the bounded queue into the artifact. The terminal holds at most one recorder, armed and
/// cleared under its model lock.
/// </summary>
internal sealed class DiagnosticCaseRecorder
{
    internal const long DefaultMaxBytes = 64L * 1024 * 1024;
    internal const int DefaultMaxSeconds = 10 * 60;
    internal const long MinMaxBytes = 1024L * 1024;
    internal const long MaxMaxBytes = 1024L * 1024 * 1024;
    internal const int MinMaxSeconds = 1;
    internal const int MaxMaxSeconds = 24 * 60 * 60;

    private const int Recording = 0;
    private const int Stopping = 1;
    private const int Stopped = 2;
    private const int StreamCount = 4;

    private readonly CaseEventQueue _queue = new();
    private readonly CaseArtifactWriter _writer;
    private readonly SemaphoreSlim _signal = new(0);
    private readonly TimeProvider _timeProvider;
    private readonly Action<DiagnosticCaseRecorder> _finished;
    private readonly long[] _ordinals = new long[StreamCount];
    private readonly long[] _offered = new long[StreamCount];
    private readonly long[] _written = new long[StreamCount];
    private readonly long[] _dropped = new long[StreamCount];
    private int _state;
    private long _caseSequence;
    private long _lastWritten = -1;
    private int _stopReason = -1;
    private DateTimeOffset? _stoppedAt;
    private Task? _writerTask;

    internal DiagnosticCaseRecorder(DiagnosticCaseManifest manifest, string path, TimeProvider timeProvider,
        Action<DiagnosticCaseRecorder> finished)
    {
        Manifest = manifest;
        Path = path;
        _timeProvider = timeProvider;
        _finished = finished;
        _writer = new CaseArtifactWriter(path);
        IncludeModelPayloads = manifest.Authorizations.Contains(DiagnosticAuthorization.ReapplicationData);
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

    /// <summary>Records a model event. The caller holds the terminal's model lock, which orders model events.</summary>
    internal void RecordModelEvent(string kind, long modelSequence, int width, int height)
    {
        if (!IsRecording)
            return;
        var ordinal = Interlocked.Increment(ref _ordinals[(int)CaseStream.Model]);
        Offer(new CaseEvent(CaseStream.Model, ordinal, Interlocked.Increment(ref _caseSequence), Stopwatch.GetTimestamp(),
            kind, modelSequence, width, height, 0, null));
    }

    private void Offer(in CaseEvent item)
    {
        var stream = (int)item.Stream;
        Interlocked.Increment(ref _offered[stream]);
        if (_queue.TryEnqueue(item))
        {
            if (_signal.CurrentCount == 0)
                _signal.Release();
        }
        else
        {
            Interlocked.Increment(ref _dropped[stream]);
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
                var stopping = Volatile.Read(ref _state) != Recording;
                while (_queue.TryDequeue(out var item))
                {
                    _writer.WriteEvent(item);
                    Interlocked.Increment(ref _written[(int)item.Stream]);
                    Volatile.Write(ref _lastWritten, item.CaseSequence);
                }

                _writer.Flush();
                if (stopping && _queue.Count == 0)
                    break;
                if (!stopping)
                    await _signal.WaitAsync(50).ConfigureAwait(false);
            }

            _stoppedAt = _timeProvider.GetUtcNow();
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
            _writer.Dispose();
            _finished(this);
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
        Manifest.Streams.Select(s => (Declaration: s, Index: StreamIndex(s.Stream))).Select(s => new DiagnosticCaseStreamStatus
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
        _ => (int)CaseStream.Delivery,
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
