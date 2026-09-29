using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // At most one diagnostic case records this terminal. It is armed and cleared under the model lock,
    // so its freshness check and its first event are ordered with every model mutation.
    private DiagnosticCaseRecorder? _diagnosticCase;

    // The configuration that determines this model's fresh state, captured at construction.
    private DiagnosticCaseModelConfiguration _caseConfiguration = new();

    /// <summary>The active diagnostic case, if any.</summary>
    internal DiagnosticCaseRecorder? DiagnosticCase => Volatile.Read(ref _diagnosticCase);

    /// <summary>The time source for this terminal's clocks and timers.</summary>
    internal TimeProvider TimeProvider => _timeProvider;

    private void CaptureCaseConfiguration(Hex1bTerminalOptions options)
    {
        var reflow = _presentation as Reflow.ITerminalReflowProvider;
        _caseConfiguration = new DiagnosticCaseModelConfiguration
        {
            Width = _width,
            Height = _height,
            ScrollbackCapacity = options.ScrollbackCapacity,
            CommandMarkHistoryCapacity = _commandMarkHistoryCapacity,
            CustomMarkerLimit = _customMarkerLimit,
            EscapeSequenceTimeoutMs = _escapeTimeout.TotalMilliseconds,
            ReflowEnabled = reflow is { ReflowEnabled: true },
            ReflowProvider = reflow is { ReflowEnabled: true } ? reflow.GetType().FullName : null,
            Presentation = _presentation.GetType().FullName ?? "",
            Workload = _workload.GetType().FullName ?? "",
            Capabilities = _presentation.Capabilities.ToString(),
            // The Sixel policy record carries every per-image limit; the per-screen byte budget is separate.
            Graphics = $"{options.CreateSixelPolicy()} MaximumRetainedBytesPerScreen={options.Graphics.MaximumRetainedBytesPerScreen}",
        };
    }

    /// <summary>
    /// Arms a diagnostic case under the model lock. The model is fresh when it has applied no model
    /// event and its pump has read no output bytes. Returns the armed recorder, or why none was armed.
    /// </summary>
    internal (DiagnosticCaseRecorder? Recorder, string? ProblemCode, string? ActiveCaseId) TryArmDiagnosticCase(
        Func<bool, DiagnosticCaseModelConfiguration, DiagnosticCaseRecorder> create)
    {
        lock (_bufferLock)
        {
            if (_disposed)
                return (null, "target-disposed", null);
            if (_diagnosticCase is { } active)
                return (null, "case-active", active.CaseId);
            var recorder = create(_modelSequence == 0 && OutputBytesRead == 0, _caseConfiguration);
            Volatile.Write(ref _diagnosticCase, recorder);
            return (recorder, null, null);
        }
    }

    /// <summary>Forgets a finished case so a new one can start.</summary>
    internal void ClearDiagnosticCase(DiagnosticCaseRecorder recorder)
    {
        lock (_bufferLock)
        {
            if (ReferenceEquals(_diagnosticCase, recorder))
                Volatile.Write(ref _diagnosticCase, null);
        }
    }

    // Must hold _bufferLock, right after the model sequence advanced for this event.
    private void NotifyCaseModelEventUnsafe(string kind, int width, int height) =>
        _diagnosticCase?.RecordModelEvent(kind, _modelSequence, width, height);

    // Must hold _bufferLock: disposal ends the case before it resets any model state.
    private void NotifyCaseDisposedUnsafe() => _diagnosticCase?.RequestStop(DiagnosticCaseStopReason.TargetDisposed);
}
