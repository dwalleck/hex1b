namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The <c>text-state/1</c> start checkpoint of a case started on a terminal that has already applied output:
/// which state surfaces a start may hold. The active text buffer, its continuation and, on the alternate screen, the
/// saved main screen are restored; retained history, titles, command marks, pending input and graphics are refused
/// while present, until their tickets extend the surface (10, 11, 12; graphics are outside the text profile).
/// </summary>
internal static class StartCheckpoint
{
    /// <summary>
    /// The manifest checkpoint of a start taken at <paramref name="modelSequence"/>: complete when its state holds
    /// only restorable surfaces; otherwise unsupported, naming the surfaces or why no state was taken.
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
        if (start.State is not { } state)
            return described with { Status = DiagnosticCaseCheckpointStatus.Unsupported, Reason = start.Reason };
        var surfaces = Unsupported(state);
        return surfaces.Count > 0
            ? described with
            {
                Status = DiagnosticCaseCheckpointStatus.Unsupported,
                Reason = $"unsupported-surfaces: the start held {string.Join(", ", surfaces)}, which this checkpoint cannot restore yet.",
                UnsupportedSurfaces = surfaces,
            }
            : described with { Status = DiagnosticCaseCheckpointStatus.Complete, CoveredSurfaces = FreshModelCheckpoint.CoveredSurfaces };
    }

    /// <summary>
    /// The surfaces a start state holds that its restore cannot represent, in a fixed order; empty when it can be
    /// restored. It reads only the projection.
    /// </summary>
    internal static IReadOnlyList<string> Unsupported(DiagnosticModelState state)
    {
        var surfaces = new List<string>();
        if (state.History is { Rows.Count: > 0 })
            surfaces.Add("retained-history");
        if (state.Titles.Window.Length > 0 || state.Titles.Icon.Length > 0 || state.Titles.Stack.Count > 0)
            surfaces.Add("titles");
        if (state.CommandMarks.Count > 0)
            surfaces.Add("command-marks");
        var pending = state.PendingInput;
        if (pending.EscapePrefix.Length > 0 || pending.Utf8.Length > 0 || pending.GroundEscape || pending.FramerUtf8Remaining != 0)
            surfaces.Add("pending-input");
        // The projection names these itself.
        foreach (var surface in state.Unsupported)
        {
            if (!surfaces.Contains(surface))
                surfaces.Add(surface);
        }
        return surfaces;
    }
}
