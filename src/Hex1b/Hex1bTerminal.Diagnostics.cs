namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // Correlates observations of this model across diagnostic requests and clients.
    internal Guid DiagnosticSessionId { get; } = Guid.NewGuid();

    /// <summary>The model sequence now, read under the model lock.</summary>
    internal long CurrentModelSequence
    {
        get { lock (_bufferLock) return _modelSequence; }
    }

    /// <summary>The session's input milestone tracker; present only when diagnostics are enabled.</summary>
    internal Diagnostics.InputMilestoneTracker? InputMilestones { get; private set; }

    /// <summary>
    /// Gives a terminal whose workload is not a Hex1b application an acceptance-only tracker, so
    /// diagnostic sends receive ids; called when a diagnostics engine attaches.
    /// </summary>
    internal void EnsureAcceptanceTracker()
    {
        if (InputMilestones is null && _workload is not Hex1bAppWorkloadAdapter)
            InputMilestones = new Diagnostics.InputMilestoneTracker(acceptanceOnly: true);
    }

    /// <summary>
    /// The session's native delivery recorder; present only when a diagnostics engine is attached and
    /// the presentation can report its writes.
    /// </summary>
    internal Diagnostics.NativeDeliveryRecorder? NativeDelivery => Volatile.Read(ref _nativeDelivery);

    private Diagnostics.NativeDeliveryRecorder? _nativeDelivery;

    /// <summary>
    /// Arms native delivery recording when the presentation is observable; called when a diagnostics
    /// engine attaches. Writes made before this are not covered, and a terminal already disposed is
    /// not armed: an empty record would claim coverage of a session that has ended.
    /// </summary>
    internal void EnsureNativeDeliveryRecorder()
    {
        // An impact-aware presentation receives cell impacts rather than bytes through the helpers,
        // so its writes would not be recorded; it is reported unobservable instead.
        if (NativeDelivery is not null || _presentation is not IObservableNativePresentation observable
            || _presentation is ICellImpactAwarePresentationAdapter)
            return;

        var layer = observable.DeliveryLayer;
        // Disposal sets _disposed under the same lock, so arming either precedes it (and its exit
        // writes are recorded) or does not happen.
        lock (_bufferLock)
        {
            if (!_disposed && _nativeDelivery is null)
                Volatile.Write(ref _nativeDelivery, new Diagnostics.NativeDeliveryRecorder(layer));
        }
    }

    // Counts model events: output application batches, geometry changes, and synchronized-update
    // timeout releases. Advanced and read only under _bufferLock, so a model read names exactly
    // the state it copied.
    private long _modelSequence;

    private void AdvanceModelSequenceUnsafe() => _modelSequence++;

    internal bool IsDisposed => _disposed;

    internal IHex1bTerminalPresentationAdapter PresentationAdapter => _presentation;

    internal int HistoryRetentionCapacity => _scrollbackBuffer?.Capacity ?? 0;
}
