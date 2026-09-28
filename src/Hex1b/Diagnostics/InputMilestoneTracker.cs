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

    // The id of the current diagnostic send, collected across the events it enqueues.
    private static readonly AsyncLocal<SendScope?> CurrentSend = new();

    private readonly object _sync = new();
    private readonly ConditionalWeakTable<Hex1bEvent, Tracked> _tracked = new();
    private readonly List<Waiter> _waiters = [];
    private readonly Tracked?[] _recent = new Tracked?[RetainedRecords];
    private long _accepted;
    private long _processed;
    private string? _processedBy;
    private (string Code, string Message)? _terminated;
    private string? _stoppedApplication;
    private PublishedFrame? _latestFrame;
    private long _appliedOutput;
    private string? _outputFailure;

    internal InputMilestoneTracker()
    {
        if (ConstructionsForTesting.Value is { } counter)
            Interlocked.Increment(ref counter.Value);
    }

    /// <summary>
    /// Numbers <paramref name="evt"/> and writes it with <paramref name="write"/> under one lock,
    /// so ids follow the channel's order exactly. Returns the write's result; an event the write
    /// rejects consumes no id.
    /// </summary>
    internal bool Accept(Hex1bEvent evt, Func<Hex1bEvent, bool> write)
    {
        var send = CurrentSend.Value;
        var (kind, payload) = Describe(evt);
        lock (_sync)
        {
            if (!write(evt))
                return false;
            var id = ++_accepted;
            var tracked = new Tracked(id, DateTimeOffset.UtcNow)
            {
                Kind = kind,
                Source = send is null ? "native" : "diagnostic-send",
                Payload = payload,
            };
            _tracked.AddOrUpdate(evt, tracked);
            _recent[id % RetainedRecords] = tracked;
            send?.Include(id);
            WakeUnsafe();
            return true;
        }
    }

    /// <summary>
    /// Starts a diagnostic send: every event accepted in this async flow until the scope ends is
    /// attributed to it.
    /// </summary>
    internal SendScope BeginSend()
    {
        var scope = new SendScope(this);
        CurrentSend.Value = scope;
        return scope;
    }

    /// <summary>
    /// Records that <paramref name="applicationInstanceId"/>'s loop consumed <paramref name="evt"/>.
    /// Only application loops advance the watermark, since they consume in channel order; a flow
    /// runner consuming an event itself (a resize) marks just that input processed, so it cannot
    /// carry the watermark past an input a step has not processed yet.
    /// </summary>
    internal void Processed(Hex1bEvent evt, string applicationInstanceId, bool advancesWatermark = true)
    {
        if (!_tracked.TryGetValue(evt, out var tracked))
            return;
        lock (_sync)
        {
            tracked.ProcessedBy = applicationInstanceId;
            tracked.ProcessedAt = DateTimeOffset.UtcNow;
            if (!advancesWatermark)
            {
                WakeUnsafe();
                return;
            }

            if (tracked.Id <= _processed)
                return;
            _processed = tracked.Id;
            _processedBy = applicationInstanceId;
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
    /// Set by a flow: its channel is read by one step after another, so an input still queued when
    /// a step stops waits for the next step rather than failing.
    /// </summary>
    internal bool HostsFlow { get; set; }

    /// <summary>
    /// Fails the waits for unprocessed inputs when the application reading the channel stops and
    /// no later application will read it.
    /// </summary>
    internal void ApplicationStopped(string applicationInstanceId)
    {
        lock (_sync)
        {
            if (HostsFlow)
            {
                // Inputs a flow pump handed to this step and it never processed are lost; inputs
                // still queued at the terminal wait for the next step.
                foreach (var tracked in _recent)
                {
                    if (tracked is { Forwarded: true, ProcessedBy: null, Lost: null })
                        tracked.Lost = applicationInstanceId;
                }
            }
            else
            {
                _stoppedApplication = applicationInstanceId;
            }

            WakeUnsafe();
        }
    }

    /// <summary>A flow pump handed <paramref name="evt"/> from the terminal's channel to a step.</summary>
    internal void Forwarded(Hex1bEvent evt)
    {
        if (_tracked.TryGetValue(evt, out var tracked))
            tracked.Forwarded = true;
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

    /// <summary>The terminal applied every output item up to <paramref name="sequence"/>.</summary>
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

    /// <summary>An application started reading the channel; inputs can be processed again.</summary>
    internal void ApplicationStarted()
    {
        lock (_sync)
            _stoppedApplication = null;
    }

    /// <summary>The id <paramref name="evt"/> was assigned, when it was tracked.</summary>
    internal long? IdOf(Hex1bEvent evt) => _tracked.TryGetValue(evt, out var tracked) ? tracked.Id : null;

    /// <summary>The highest input id issued so far.</summary>
    internal long AcceptedInput
    {
        get { lock (_sync) return _accepted; }
    }

    /// <summary>
    /// Waits until <paramref name="milestone"/> is met for <paramref name="inputId"/>, the
    /// timeout elapses, or the awaited event becomes impossible.
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
            return WaitStatus.TimedOut($"Milestone {DiagnosticContractNames.Of(milestone)} for input {inputId} was not met within {timeout.TotalMilliseconds:0} ms.");
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

    /// <summary>The retained record for <paramref name="inputId"/>, or <see langword="null"/> once evicted.</summary>
    internal Tracked? Record(long inputId)
    {
        lock (_sync)
            return _recent[inputId % RetainedRecords] is { } tracked && tracked.Id == inputId ? tracked : null;
    }

    /// <summary>Test view: the number of pending waits.</summary>
    internal int PendingWaits
    {
        get { lock (_sync) return _waiters.Count; }
    }

    // Must hold _sync. Returns the final status when the milestone is decided, else null.
    private WaitStatus? Evaluate(DiagnosticMilestone milestone, long inputId, PublishedFrame? coveringFrame = null)
    {
        // A frame covering a pending input is the one recorded at its publication; for an input
        // already covered when the wait starts, the latest frame (progress beyond the request).
        var frame = coveringFrame ?? (_latestFrame is { } latest && latest.ProcessedInput >= inputId ? latest : null);
        switch (milestone)
        {
            case DiagnosticMilestone.InputAccepted:
                return WaitStatus.Met;
            case DiagnosticMilestone.InputProcessed:
                if (_processed >= inputId || _recent[inputId % RetainedRecords] is { ProcessedBy: not null } record && record.Id == inputId)
                    return WaitStatus.Met;
                break;
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
                break;
        }

        if (_recent[inputId % RetainedRecords] is { Lost: { } lostBy } lost && lost.Id == inputId)
            return WaitStatus.Failed("application-stopped", $"Application instance {lostBy} stopped before processing input {inputId}.");

        if (_stoppedApplication is { } stopped)
            return WaitStatus.Failed("application-stopped",
                $"Application instance {stopped} stopped before processing input {inputId}.");
        return _terminated is { } terminated ? WaitStatus.Failed(terminated.Code, terminated.Message) : null;
    }

    // Must hold _sync.
    private void WakeUnsafe()
    {
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

    internal sealed class Tracked(long id, DateTimeOffset acceptedAt)
    {
        public long Id { get; } = id;

        public DateTimeOffset AcceptedAt { get; } = acceptedAt;

        public string Kind { get; init; } = "other";

        public string Source { get; init; } = "native";

        // Retained in memory (bounded ring) so a later raw-input capture can report it; never
        // serialized without that authorization.
        public string? Payload { get; init; }

        public string? ProcessedBy { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }

        public bool Forwarded { get; set; }

        public string? Lost { get; set; }
    }

    internal sealed record PublishedFrame(string ApplicationInstanceId, long FrameId, long ProcessedInput, bool WroteOutput,
        bool ProjectionFailed, long? OutputMark);

    private static (string Kind, string? Payload) Describe(Hex1bEvent evt) => evt switch
    {
        Hex1bKeyEvent key => (key.Text is { Length: > 0 } && key.Modifiers == Hex1bModifiers.None ? "text" : "key",
            $"{key.Key}{(key.Modifiers == Hex1bModifiers.None ? "" : "+" + key.Modifiers)}{(key.Text is { Length: > 0 } t ? $" \"{t}\"" : "")}"),
        // Paste content streams to the application after acceptance; only the kind is known here.
        Hex1bPasteEvent => ("paste", null),
        Hex1bMouseEvent mouse => ("mouse", $"{mouse.Button} {mouse.Action} at {mouse.X},{mouse.Y}"),
        Hex1bResizeEvent resize => ("resize", $"{resize.Width}x{resize.Height}"),
        _ => ("other", null),
    };

    private sealed class Waiter(DiagnosticMilestone milestone, long inputId)
    {
        public DiagnosticMilestone Milestone { get; } = milestone;

        public long InputId { get; } = inputId;

        public PublishedFrame? CoveringFrame { get; set; }

        public TaskCompletionSource<WaitStatus> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>The ids one diagnostic send was assigned.</summary>
    internal sealed class SendScope(InputMilestoneTracker tracker) : IDisposable
    {
        private readonly InputMilestoneTracker _tracker = tracker;

        public long? FirstId { get; private set; }

        public long? LastId { get; private set; }

        internal void Include(long id)
        {
            FirstId ??= id;
            LastId = id;
        }

        public void Dispose()
        {
            if (ReferenceEquals(CurrentSend.Value, this))
                CurrentSend.Value = null;
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
