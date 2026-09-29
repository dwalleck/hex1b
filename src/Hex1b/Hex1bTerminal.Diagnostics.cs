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
    internal Diagnostics.NativeDeliveryRecorder? NativeDelivery { get; private set; }

    /// <summary>
    /// Arms native delivery recording when the presentation is observable; called when a diagnostics
    /// engine attaches. Writes made before this are not covered.
    /// </summary>
    internal void EnsureNativeDeliveryRecorder()
    {
        if (NativeDelivery is null && _presentation is IObservableNativePresentation observable)
            NativeDelivery = new Diagnostics.NativeDeliveryRecorder(observable.DeliveryLayer);
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
