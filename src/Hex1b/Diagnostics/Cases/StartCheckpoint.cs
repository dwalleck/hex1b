namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The <c>text-state/1</c> start checkpoint of a case started on a terminal that has already applied output:
/// which state surfaces a start may hold. The active text buffer, its continuation, the retained history, titles and
/// the title stack, command marks, pending input (an unfinished escape sequence or UTF-8 scalar, a held ESC) and, on the
/// alternate screen, the saved main screen are restored; a DCS in progress and graphics are refused while present (a DCS
/// in progress until its ticket extends the surface; graphics are outside the text profile). A start whose configuration
/// cannot be rebuilt is never complete.
/// </summary>
internal static class StartCheckpoint
{
    /// <summary>
    /// The manifest checkpoint of a start, or the classification of a recovery checkpoint, taken at
    /// <paramref name="modelSequence"/>: complete when its state holds only restorable surfaces; otherwise unsupported,
    /// naming the surfaces or why no state was taken.
    /// </summary>
    internal static DiagnosticCaseCheckpoint Describe(DiagnosticCaseModelConfiguration configuration, long modelSequence,
        DiagnosticCaseRecorder.CheckpointCapture start)
    {
        var described = new DiagnosticCaseCheckpoint
        {
            Profile = DiagnosticCaseCheckpointProfiles.TextState,
            Configuration = configuration,
            ModelSequence = modelSequence,
            UnsupportedSurfaces = [],
        };
        // A start the reapplier would refuse for its configuration is never complete.
        var raw = System.Text.Json.JsonSerializer.SerializeToNode(configuration, DiagnosticsJsonContext.Default.DiagnosticCaseModelConfiguration)!.AsObject();
        if (CaseConfiguration.RebuildProblem(raw, configuration) is { } configurationProblem)
            return described with { Status = DiagnosticCaseCheckpointStatus.Unsupported, Reason = $"configuration: {configurationProblem}" };
        if (start.State is not { } state)
            return described with { Status = DiagnosticCaseCheckpointStatus.Unsupported, Reason = start.Reason };
        var surfaces = Unsupported(state);
        return surfaces.Count > 0
            ? described with
            {
                Status = DiagnosticCaseCheckpointStatus.Unsupported,
                Reason = $"unsupported-surfaces: the checkpoint held {string.Join(", ", surfaces)}, which it cannot restore yet.",
                UnsupportedSurfaces = surfaces,
            }
            : described with { Status = DiagnosticCaseCheckpointStatus.Complete, CoveredSurfaces = FreshModelCheckpoint.CoveredSurfaces };
    }

    /// <summary>
    /// Whether a recorded start fits the case: the writer writes it first after the manifest, so the manifest's bytes,
    /// the line's own fields and the state's exact bytes must stay within the events tier.
    /// </summary>
    internal static bool Fits(long manifestBytes, DiagnosticCaseRecorder.CheckpointCapture start, long maxBytes) =>
        manifestBytes + DiagnosticCaseRecorder.StartLineAllowance + start.StateJsonBytes <= maxBytes - DiagnosticCaseRecorder.EventReserve;

    /// <summary>A complete start that does not fit with its manifest (<see cref="Fits"/>): unsupported, at the size limit.</summary>
    internal static DiagnosticCaseCheckpoint TooLarge(DiagnosticCaseCheckpoint complete, long manifestBytes, long stateJsonBytes) => complete with
    {
        Status = DiagnosticCaseCheckpointStatus.Unsupported,
        Reason = $"size-limit: the start state ({stateJsonBytes} bytes) and the manifest ({manifestBytes} bytes) are larger than the case's size bound leaves for its events",
        CoveredSurfaces = [],
    };

    /// <summary>
    /// The surfaces a start state holds that its restore cannot represent, in a fixed order; empty when it can be
    /// restored. It reads only the projection.
    /// </summary>
    internal static IReadOnlyList<string> Unsupported(DiagnosticModelState state)
    {
        // Pending input is restored since ticket 12; the projection names the remaining surfaces itself (a DCS in
        // progress, graphics).
        return state.Unsupported;
    }
}
