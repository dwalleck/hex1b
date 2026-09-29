using Hex1b.Diagnostics;

namespace Hex1b;

public sealed partial class Hex1bTerminal
{
    // Every write the terminal makes to its presentation goes through these two helpers. Without a
    // native delivery recorder they make exactly the call the terminal always made; with one, each
    // write is recorded as accepted, refused or failed, and a failure still propagates unchanged.

    private ValueTask WritePresentationAsync(IHex1bTerminalPresentationAdapter presentation, ReadOnlyMemory<byte> data,
        DiagnosticDeliverySource source, DiagnosticDeliveryPhase? phase, long outputSequence, CancellationToken ct)
    {
        if (NativeDelivery is not { } recorder || presentation is not IObservableNativePresentation observable)
            return presentation.WriteOutputAsync(data, ct);
        return WriteObservedAsync(recorder, observable, data, source, phase, outputSequence, ct);
    }

    private async ValueTask WriteObservedAsync(NativeDeliveryRecorder recorder, IObservableNativePresentation observable,
        ReadOnlyMemory<byte> data, DiagnosticDeliverySource source, DiagnosticDeliveryPhase? phase, long outputSequence,
        CancellationToken ct)
    {
        var entry = recorder.Begin(source, phase, data.Span, outputSequence > 0 ? outputSequence : null, CurrentModelSequence);
        var progress = new NativeWriteProgress();
        try
        {
            var result = await observable.WriteObservedAsync(data, progress, ct).ConfigureAwait(false);
            recorder.Complete(entry, result.Refused ? DiagnosticDeliveryOutcome.Refused : DiagnosticDeliveryOutcome.Accepted,
                result.Refused ? 0 : data.Length, result.Reason, error: null);
        }
        catch (Exception error)
        {
            recorder.Complete(entry, DiagnosticDeliveryOutcome.Failed, progress.Observed ? progress.BytesAccepted : null,
                reason: null, $"{error.GetType().FullName}: {error.Message}");
            throw;
        }
    }

    private ValueTask<NativeDeliveryOutcome> WritePresentationIfGeometryAsync(IGeometryGatedPresentationAdapter presentation,
        ReadOnlyMemory<byte> data, int expectedWidth, int expectedHeight, long outputSequence, CancellationToken ct)
    {
        if (NativeDelivery is not { } recorder || presentation is not IObservableNativePresentation observable)
            return presentation.WriteOutputIfGeometryAsync(data, expectedWidth, expectedHeight, ct);
        return WriteObservedIfGeometryAsync(recorder, observable, data, expectedWidth, expectedHeight, outputSequence, ct);
    }

    private async ValueTask<NativeDeliveryOutcome> WriteObservedIfGeometryAsync(NativeDeliveryRecorder recorder,
        IObservableNativePresentation observable, ReadOnlyMemory<byte> data, int expectedWidth, int expectedHeight,
        long outputSequence, CancellationToken ct)
    {
        // A gated batch is written before the model applies it: only a written batch is then described.
        var entry = recorder.Begin(DiagnosticDeliverySource.GatedDelivery, DiagnosticDeliveryPhase.BeforeModel, data.Span,
            outputSequence > 0 ? outputSequence : null, CurrentModelSequence);
        var progress = new NativeWriteProgress();
        try
        {
            var outcome = await observable.WriteObservedIfGeometryAsync(data, expectedWidth, expectedHeight, progress, ct)
                .ConfigureAwait(false);
            if (outcome == NativeDeliveryOutcome.GeometryChanged)
                recorder.Complete(entry, DiagnosticDeliveryOutcome.Refused, 0, "geometry-changed", error: null);
            else
                recorder.Complete(entry, DiagnosticDeliveryOutcome.Accepted, data.Length, reason: null, error: null);
            return outcome;
        }
        catch (Exception error)
        {
            recorder.Complete(entry, DiagnosticDeliveryOutcome.Failed, progress.Observed ? progress.BytesAccepted : null,
                reason: null, $"{error.GetType().FullName}: {error.Message}");
            throw;
        }
    }
}
