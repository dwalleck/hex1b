using System.Diagnostics;
using System.Runtime.CompilerServices;
using Hex1b.Input;

namespace Hex1b.Diagnostics;

/// <summary>
/// Tracks one terminal session's inputs through their processing milestones: it numbers every
/// input as it enters the application's input channel, records the application loop's
/// processed-input watermark, and completes bounded milestone waits. It exists only when the
/// session has diagnostics enabled, so an application without diagnostics does no tracking.
/// </summary>
internal sealed class InputMilestoneTracker
{
    /// <summary>The most milestone waits one session may have pending at once.</summary>
    internal const int MaxPendingWaits = 64;

    /// <summary>How many recent input records are retained for milestone results.</summary>
    internal const int RetainedRecords = 1024;

    /// <summary>
    /// Counts trackers constructed in the current async flow while a test has set a counter, so
    /// a test can prove a session without diagnostics creates none.
    /// </summary>
    internal static readonly AsyncLocal<StrongBox<int>?> ConstructionsForTesting = new();

    // The current diagnostic send, collected across the events it enqueues.
    private static readonly AsyncLocal<SendScope?> CurrentSend = new();

    private readonly object _sync = new();

    // One send at a time, and no other input between a send's events, so a send's ids are
    // consecutive. Held across the send; other writers pass through it for each event.
    private readonly SemaphoreSlim _sendGate = new(1, 1);

    // Occurrences of an event object not yet processed, oldest first (an instance may be sent twice).
    private readonly ConditionalWeakTable<Hex1bEvent, Queue<Tracked>> _pending = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Tracked?[] _recent = new Tracked?[RetainedRecords];
    private long _accepted;
    private long _processed;
    private string? _processedBy;
    private (string Code, string Message)? _terminated;
    private string? _stoppedApplication;
    private bool _hostsFlow;
    private PublishedFrame? _latestFrame;
    private long _appliedOutput;
    private string? _outputFailure;

    internal InputMilestoneTracker(bool acceptanceOnly = false)
    {
        AcceptanceOnly = acceptanceOnly;
        if (ConstructionsForTesting.Value is { } counter)
            Interlocked.Increment(ref counter.Value);
    }

    /// <summary>
    /// True for a workload whose consumption is not observable (a PTY child): sends receive
    /// acceptance ids, and every later stage is unavailable.
    /// </summary>
    internal bool AcceptanceOnly { get; }

    /// <summary>
    /// Set by a flow: its channel is read by one step after another, so an input still queued when
    /// a step stops waits for the next step rather than failing.
    /// </summary>
    internal bool HostsFlow
    {
        get { lock (_sync) return _hostsFlow; }
        set { lock (_sync) _hostsFlow = value; }
    }

    /// <summary>
    /// Waits until no other send is in progress and takes the turn; <see cref="BeginSend"/> must
    /// follow at once, in the caller's own async flow.
    /// </summary>
    internal Task WaitForSendTurnAsync(CancellationToken cancellationToken = default) => _sendGate.WaitAsync(cancellationToken);

    /// <summary>
    /// Starts a diagnostic send on a turn taken with <see cref="WaitForSendTurnAsync"/>: every event
    /// this async flow accepts until the scope ends belongs to the send. Synchronous on purpose: an
    /// async method's <see cref="AsyncLocal{T}"/> assignment would not reach its caller.
    /// </summary>
    internal SendScope BeginSend() => Enter(new SendScope(this, native: false));

    /// <summary>
    /// Holds the send turn for a native write that takes the terminal's input write lock, on a turn
    /// taken with <see cref="WaitForSendTurnAsync"/>. Every writer takes the turn before that lock,
    /// so the two are always acquired in one order; the write's events are numbered as native input.
    /// </summary>
    internal SendScope BeginNativeTurn() => Enter(new SendScope(this, native: true));

    /// <summary>True when the current async flow already holds this session's send turn.</summary>
    internal bool OwnsTurn => OwnSend() is not null;

    private static SendScope Enter(SendScope scope)
    {
        CurrentSend.Value = scope;
        return scope;
    }

    /// <summary>Numbers one completed write of <paramref name="kind"/> to an acceptance-only workload.</summary>
    internal long AcceptWrite(string kind)
    {
        var send = OwnSend();
        lock (_sync)
        {
            var id = ++_accepted;
            _recent[id % RetainedRecords] = new Tracked(id, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), kind, "diagnostic-send", null);
            send?.Include(id);
            WakeUnsafe();
            return id;
        }
    }

    /// <summary>
    /// Numbers <paramref name="evt"/> and writes it with <paramref name="write"/> under one lock,
    /// so ids follow the channel's order exactly. The event is registered before the write, so a
    /// reader cannot process it unregistered. Returns the write's result; a rejected event
    /// consumes no id.
    /// </summary>
    internal bool Accept(Hex1bEvent evt, Func<Hex1bEvent, bool> write)
    {
        var send = OwnSend();
        if (send is null)
            _sendGate.Wait();
        try
        {
            lock (_sync)
            {
                var id = _accepted + 1;
                var tracked = new Tracked(id, DateTimeOffset.UtcNow, Stopwatch.GetTimestamp(), KindOf(evt),
                    send is { Native: false } ? "diagnostic-send" : "native", evt is Hex1bPasteEvent ? null : evt);
                var occurrences = _pending.GetOrCreateValue(evt);
                occurrences.Enqueue(tracked);
                if (!write(evt))
                {
                    RemoveLast(occurrences);
                    return false;
                }

                _accepted = id;
                _recent[id % RetainedRecords] = tracked;
                if (send is { Native: false })
                    send.Include(id);
                WakeUnsafe();
                return true;
            }
        }
        finally
        {
            if (send is null)
                _sendGate.Release();
        }
    }

    /// <summary>
    /// Records that <paramref name="applicationInstanceId"/>'s loop consumed <paramref name="evt"/>.
    /// Only application loops advance the watermark, since they consume in channel order; a flow
    /// runner consuming an event itself (a resize) marks just that input processed, so it cannot
    /// carry the watermark past an input a step has not processed yet.
    /// </summary>
    internal void Processed(Hex1bEvent evt, string applicationInstanceId, bool advancesWatermark = true)
    {
        lock (_sync)
        {
            if (!_pending.TryGetValue(evt, out var occurrences) || !occurrences.TryDequeue(out var tracked))
                return;
            tracked.ProcessedBy = applicationInstanceId;
            tracked.ProcessedAt = DateTimeOffset.UtcNow;
            tracked.ProcessedTimestamp = Stopwatch.GetTimestamp();
            if (advancesWatermark && tracked.Id > _processed)
            {
                _processed = tracked.Id;
                _processedBy = applicationInstanceId;
            }

            WakeUnsafe();
        }
    }

    /// <summary>A flow pump handed <paramref name="evt"/> from the terminal's channel to a step.</summary>
    internal void Forwarded(Hex1bEvent evt)
    {
        lock (_sync)
        {
            if (_pending.TryGetValue(evt, out var occurrences) && occurrences.FirstOrDefault(t => !t.Forwarded) is { } tracked)
                tracked.Forwarded = true;
        }
    }

    /// <summary>
    /// Marks inputs a flow handed to a step that ended without processing them as lost. Called when
    /// the step's application stops and again once its input pump has stopped, so an input the pump
    /// forwarded after the application stopped is lost too.
    /// </summary>
    internal void StepEnded(string stepDescription)
    {
        lock (_sync)
        {
            foreach (var tracked in _recent)
            {
                if (tracked is { Forwarded: true, ProcessedBy: null, Lost: null })
                    tracked.Lost = stepDescription;
            }

            WakeUnsafe();
        }
    }

    /// <summary>Completes every pending wait: the awaited events can no longer arrive.</summary>
    internal void Terminate(string code, string message)
    {
        lock (_sync)
        {
            _terminated ??= (code, message);
            WakeUnsafe();
        }
    }

    /// <summary>
    /// An application that read the channel stopped. In a flow the next step reads on, so only the
    /// inputs handed to this step are lost; otherwise unprocessed inputs can no longer be processed.
    /// </summary>
    internal void ApplicationStopped(string applicationInstanceId)
    {
        if (HostsFlow)
        {
            StepEnded(applicationInstanceId);
            return;
        }

        lock (_sync)
        {
            _stoppedApplication = applicationInstanceId;
            WakeUnsafe();
        }
    }

    /// <summary>An application started reading the channel; inputs can be processed again.</summary>
    internal void ApplicationStarted()
    {
        lock (_sync)
            _stoppedApplication = null;
    }

    /// <summary>The highest input id processed so far (the frame watermark source).</summary>
    internal long ProcessedInput
    {
        get { lock (_sync) return _processed; }
    }

    /// <summary>
    /// Records a completed pass's publication. <paramref name="outputMark"/> is the last output
    /// sequence the application had enqueued, or <see langword="null"/> when its output reaches
    /// the model through a relay the tracker cannot follow (an inline flow step).
    /// </summary>
    internal void FramePublished(string applicationInstanceId, long frameId, long processedInput, bool wroteOutput,
        bool projectionFailed, long? outputMark)
    {
        lock (_sync)
        {
            var frame = new PublishedFrame(applicationInstanceId, frameId, processedInput, wroteOutput, projectionFailed, outputMark);
            foreach (var waiter in _waiters)
            {
                if (waiter.CoveringFrame is null && waiter.InputId <= processedInput)
                    waiter.CoveringFrame = frame;
            }

            _latestFrame = frame;
            WakeUnsafe();
        }
    }

    /// <summary>The terminal's output pump finished every output item up to <paramref name="sequence"/>.</summary>
    internal void OutputApplied(long sequence)
    {
        lock (_sync)
        {
            if (sequence <= _appliedOutput)
                return;
            _appliedOutput = sequence;
            WakeUnsafe();
        }
    }

    /// <summary>The terminal's output pump failed: output can no longer reach the model.</summary>
    internal void OutputPumpFailed(string message)
    {
        lock (_sync)
        {
            _outputFailure ??= message;
            WakeUnsafe();
        }
    }

    /// <summary>The id of the oldest unprocessed occurrence of <paramref name="evt"/>, when tracked.</summary>
    internal long? IdOf(Hex1bEvent evt)
    {
        lock (_sync)
            return _pending.TryGetValue(evt, out var occurrences) && occurrences.TryPeek(out var tracked) ? tracked.Id : null;
    }

    /// <summary>The highest input id issued so far.</summary>
    internal long AcceptedInput
    {
        get { lock (_sync) return _accepted; }
    }

    /// <summary>
    /// Waits until <paramref name="milestone"/> is met for <paramref name="inputId"/>, the
    /// timeout elapses, the awaited event becomes impossible, or the caller cancels (which throws).
    /// </summary>
    internal async Task<WaitStatus> WaitAsync(DiagnosticMilestone milestone, long inputId, TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        Waiter waiter;
        lock (_sync)
        {
            if (inputId < 1 || inputId > _accepted)
                return WaitStatus.Invalid("unknown-input-id", $"Input {inputId} was never issued in this session (latest is {_accepted}).");
            if (Evaluate(milestone, inputId) is { } immediate)
                return immediate;
            if (_waiters.Count >= MaxPendingWaits)
                return WaitStatus.Unavailable("too-many-pending-waits",
                    $"{MaxPendingWaits} milestone waits are already pending in this session.");
            waiter = new Waiter(milestone, inputId);
            _waiters.Add(waiter);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);
        try
        {
            return await waiter.Completion.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            lock (_sync)
            {
                return WaitStatus.TimedOut($"Milestone {DiagnosticContractNames.Of(milestone)} for input {inputId} was not met within {timeout.TotalMilliseconds:0} ms.")
                    with { Frame = waiter.CoveringFrame ?? CoveringLatest(inputId) };
            }
        }
        finally
        {
            lock (_sync)
                _waiters.Remove(waiter);
        }
    }

    /// <summary>The watermarks as observed now.</summary>
    internal Observation Observe()
    {
        lock (_sync)
            return new Observation(_accepted, _processed, _processedBy);
    }

    /// <summary>A copy of the retained record for <paramref name="inputId"/>, or <see langword="null"/> once evicted.</summary>
    internal RecordSnapshot? Record(long inputId)
    {
        lock (_sync)
        {
            return _recent[inputId % RetainedRecords] is { } tracked && tracked.Id == inputId
                ? new RecordSnapshot(tracked.Id, tracked.Kind, tracked.Source, tracked.AcceptedAt, tracked.AcceptedTimestamp,
                    tracked.ProcessedAt, tracked.ProcessedTimestamp, tracked.ProcessedBy, tracked.Event is { } evt ? PayloadOf(evt) : null)
                : null;
        }
    }

    /// <summary>Test view: the number of pending waits.</summary>
    internal int PendingWaits
    {
        get { lock (_sync) return _waiters.Count; }
    }

    // Must hold _sync.
    private PublishedFrame? CoveringLatest(long inputId) =>
        _latestFrame is { } latest && latest.ProcessedInput >= inputId ? latest : null;

    // Must hold _sync. Returns the final status when the milestone is decided, else null.
    private WaitStatus? Evaluate(DiagnosticMilestone milestone, long inputId, PublishedFrame? coveringFrame = null)
    {
        if (AcceptanceOnly && milestone != DiagnosticMilestone.InputAccepted)
            return WaitStatus.Unavailable("input-consumption-unobservable",
                "Input is written to a child process whose consumption is not observable; only acceptance is reported.");
        if (milestone == DiagnosticMilestone.InputAccepted)
            return WaitStatus.Met;

        var record = _recent[inputId % RetainedRecords] is { } retained && retained.Id == inputId ? retained : null;

        // A lost input was never processed, whatever later inputs did to the watermark.
        if (record is { Lost: { } lostBy, ProcessedBy: null })
            return WaitStatus.Failed("application-stopped", $"Input {inputId} was handed to a flow step ({lostBy}) that ended before processing it.");

        var processed = record is { ProcessedBy: not null } || _processed >= inputId;

        // A frame covering a pending input is the one recorded at its publication; for an input
        // already covered when the wait starts, the latest frame (progress beyond the request).
        var frame = coveringFrame ?? CoveringLatest(inputId);
        switch (milestone)
        {
            case DiagnosticMilestone.InputProcessed when processed:
                return WaitStatus.Met;
            case DiagnosticMilestone.FramePublished when frame is not null:
                return frame.ProjectionFailed
                    ? WaitStatus.Failed("application-frame-projection-failed", $"Projecting application frame {frame.FrameId}, the first to cover input {inputId}, failed.")
                    : WaitStatus.MetAt(frame);
            case DiagnosticMilestone.ModelApplied when frame is not null:
                if (frame.ProjectionFailed)
                    return WaitStatus.Failed("application-frame-projection-failed", $"Projecting application frame {frame.FrameId}, the first to cover input {inputId}, failed.");
                if (frame.OutputMark is not { } mark)
                    return WaitStatus.Unavailable("model-application-unobservable",
                        "This application's output reaches the model through a flow relay that re-segments it, so frame output cannot be linked to model application.") with { Frame = frame };
                if (_appliedOutput >= mark)
                    return WaitStatus.MetAt(frame);
                if (_outputFailure is { } failure)
                    return WaitStatus.Failed("output-pump-failed", $"The terminal output pump failed: {failure}") with { Frame = frame };

                // The frame's output is still in the pump: an application stopping cannot stop it.
                return _terminated is { } ended ? WaitStatus.Failed(ended.Code, ended.Message) with { Frame = frame } : null;
        }

        // Undecided when the reader stopped: no further processing or frames will come.
        if (_stoppedApplication is { } stopped)
            return WaitStatus.Failed("application-stopped", processed
                ? $"Application instance {stopped} stopped before publishing a frame covering input {inputId}."
                : $"Application instance {stopped} stopped before processing input {inputId}.");
        return _terminated is { } terminated ? WaitStatus.Failed(terminated.Code, terminated.Message) : null;
    }

    // Must hold _sync.
    private void WakeUnsafe()
    {
        if (_waiters.Count == 0)
            return;
        foreach (var waiter in _waiters.ToArray())
        {
            if (Evaluate(waiter.Milestone, waiter.InputId, waiter.CoveringFrame) is { } status)
                Complete(waiter, status);
        }
    }

    private void Complete(Waiter waiter, WaitStatus status)
    {
        _waiters.Remove(waiter);
        waiter.Completion.TrySetResult(status);
    }

    // A task forked inside a send inherits its scope; once the send ends, the scope no longer applies.
    private SendScope? OwnSend() =>
        CurrentSend.Value is { Ended: false } send && ReferenceEquals(send.Tracker, this) ? send : null;

    private static void RemoveLast(Queue<Tracked> occurrences)
    {
        var kept = occurrences.ToArray()[..^1];
        occurrences.Clear();
        foreach (var tracked in kept)
            occurrences.Enqueue(tracked);
    }

    private static string KindOf(Hex1bEvent evt) => evt switch
    {
        Hex1bKeyEvent key => key.Text is { Length: > 0 } && key.Modifiers == Hex1bModifiers.None ? "text" : "key",
        Hex1bPasteEvent => "paste",
        Hex1bMouseEvent => "mouse",
        Hex1bResizeEvent => "resize",
        _ => "other",
    };

    // Described only when a raw-input capture asks. Paste content streams to the application after
    // acceptance, so paste events are not retained.
    private static string? PayloadOf(Hex1bEvent evt) => evt switch
    {
        Hex1bKeyEvent key => $"{key.Key}{(key.Modifiers == Hex1bModifiers.None ? "" : "+" + key.Modifiers)}{(key.Text is { Length: > 0 } t ? $" \"{t}\"" : "")}",
        Hex1bMouseEvent mouse => $"{mouse.Button} {mouse.Action} at {mouse.X},{mouse.Y}",
        Hex1bResizeEvent resize => $"{resize.Width}x{resize.Height}",
        _ => null,
    };

    private sealed class Tracked(long id, DateTimeOffset acceptedAt, long acceptedTimestamp, string kind, string source, Hex1bEvent? evt)
    {
        public long Id { get; } = id;

        public DateTimeOffset AcceptedAt { get; } = acceptedAt;

        public long AcceptedTimestamp { get; } = acceptedTimestamp;

        public string Kind { get; } = kind;

        public string Source { get; } = source;

        // Retained in memory (bounded ring) so a later raw-input capture can describe it; never
        // serialized without that authorization.
        public Hex1bEvent? Event { get; } = evt;

        public string? ProcessedBy { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }

        public long? ProcessedTimestamp { get; set; }

        public bool Forwarded { get; set; }

        public string? Lost { get; set; }
    }

    /// <summary>An immutable copy of one input's record.</summary>
    internal sealed record RecordSnapshot(long Id, string Kind, string Source, DateTimeOffset AcceptedAt, long AcceptedTimestamp,
        DateTimeOffset? ProcessedAt, long? ProcessedTimestamp, string? ProcessedBy, string? Payload);

    internal sealed record PublishedFrame(string ApplicationInstanceId, long FrameId, long ProcessedInput, bool WroteOutput,
        bool ProjectionFailed, long? OutputMark);

    private sealed class Waiter(DiagnosticMilestone milestone, long inputId)
    {
        public DiagnosticMilestone Milestone { get; } = milestone;

        public long InputId { get; } = inputId;

        public PublishedFrame? CoveringFrame { get; set; }

        public TaskCompletionSource<WaitStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// A held send turn: a diagnostic send (collecting the ids it was assigned) or a native write.
    /// Disposing it ends the turn.
    /// </summary>
    internal sealed class SendScope(InputMilestoneTracker tracker, bool native) : IDisposable
    {
        private int _disposed;

        internal InputMilestoneTracker Tracker { get; } = tracker;

        internal bool Native { get; } = native;

        internal bool Ended => Volatile.Read(ref _disposed) != 0;

        public long? FirstId { get; private set; }

        public long? LastId { get; private set; }

        internal void Include(long id)
        {
            FirstId ??= id;
            LastId = id;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;
            if (ReferenceEquals(CurrentSend.Value, this))
                CurrentSend.Value = null;
            Tracker._sendGate.Release();
        }
    }

    internal readonly record struct Observation(long AcceptedInput, long ProcessedInput, string? ProcessedBy);

    /// <summary>How a wait ended.</summary>
    internal sealed record WaitStatus(DiagnosticOutcome? Outcome, string? Code, string? Message)
    {
        public static readonly WaitStatus Met = new(null, null, null);

        /// <summary>The first frame covering the input, for frame and model milestones.</summary>
        public PublishedFrame? Frame { get; init; }

        public static WaitStatus MetAt(PublishedFrame frame) => Met with { Frame = frame };

        public bool IsMet => Outcome is null;

        public static WaitStatus TimedOut(string message) => new(DiagnosticOutcome.TimedOut, "milestone-timed-out", message);

        public static WaitStatus Failed(string code, string message) => new(DiagnosticOutcome.Failed, code, message);

        public static WaitStatus Unavailable(string code, string message) => new(DiagnosticOutcome.Unavailable, code, message);

        public static WaitStatus Invalid(string code, string message) => new(DiagnosticOutcome.InvalidRequest, code, message);
    }
}
