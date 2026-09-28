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
        lock (_sync)
        {
            if (!write(evt))
                return false;
            var id = ++_accepted;
            var tracked = new Tracked(id, DateTimeOffset.UtcNow);
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

    /// <summary>Records that <paramref name="applicationInstanceId"/>'s loop consumed <paramref name="evt"/>.</summary>
    internal void Processed(Hex1bEvent evt, string applicationInstanceId)
    {
        if (!_tracked.TryGetValue(evt, out var tracked))
            return;
        lock (_sync)
        {
            tracked.ProcessedBy = applicationInstanceId;
            tracked.ProcessedAt = DateTimeOffset.UtcNow;
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
        if (HostsFlow)
            return;
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
    private WaitStatus? Evaluate(DiagnosticMilestone milestone, long inputId)
    {
        switch (milestone)
        {
            case DiagnosticMilestone.InputAccepted:
                return WaitStatus.Met;
            case DiagnosticMilestone.InputProcessed:
                if (_processed >= inputId)
                    return WaitStatus.Met;
                break;
            default:
                return WaitStatus.Unavailable("milestone-not-supported",
                    $"Milestone {DiagnosticContractNames.Of(milestone)} is not supported by this target.");
        }

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
            if (Evaluate(waiter.Milestone, waiter.InputId) is { } status)
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

        public string? ProcessedBy { get; set; }

        public DateTimeOffset? ProcessedAt { get; set; }
    }

    private sealed class Waiter(DiagnosticMilestone milestone, long inputId)
    {
        public DiagnosticMilestone Milestone { get; } = milestone;

        public long InputId { get; } = inputId;

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

        public bool IsMet => Outcome is null;

        public static WaitStatus TimedOut(string message) => new(DiagnosticOutcome.TimedOut, "milestone-timed-out", message);

        public static WaitStatus Failed(string code, string message) => new(DiagnosticOutcome.Failed, code, message);

        public static WaitStatus Unavailable(string code, string message) => new(DiagnosticOutcome.Unavailable, code, message);

        public static WaitStatus Invalid(string code, string message) => new(DiagnosticOutcome.InvalidRequest, code, message);
    }
}
