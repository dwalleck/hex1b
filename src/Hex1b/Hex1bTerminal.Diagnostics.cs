namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // Correlates observations of this model across diagnostic requests and clients.
    internal Guid DiagnosticSessionId { get; } = Guid.NewGuid();

    // Counts model-input events: output application batches and geometry changes. Advanced and
    // read only under _bufferLock, so a model read names exactly the state it copied.
    private long _modelSequence;

    private void AdvanceModelSequenceUnsafe() => _modelSequence++;

    internal bool IsDisposed => _disposed;

    internal IHex1bTerminalPresentationAdapter PresentationAdapter => _presentation;

    internal int HistoryRetentionCapacity => _scrollbackBuffer?.Capacity ?? 0;
}
