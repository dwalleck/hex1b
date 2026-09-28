using Hex1b.Reflow;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // Correlates observations of this model across diagnostic requests and clients.
    internal Guid DiagnosticSessionId { get; } = Guid.NewGuid();

    internal bool IsDisposed => _disposed;

    internal IHex1bTerminalPresentationAdapter PresentationAdapter => _presentation;

    internal bool ReflowEnabled => _presentation is ITerminalReflowProvider { ReflowEnabled: true };

    internal int HistoryRetentionCapacity => _scrollbackBuffer?.Capacity ?? 0;
}
