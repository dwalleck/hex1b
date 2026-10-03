namespace Hex1b.Diagnostics.Cases;

/// <summary>The <c>fresh-model/1</c> checkpoint: a fresh model is fully determined by its configuration.</summary>
internal static class FreshModelCheckpoint
{
    /// <summary>The state surfaces a fresh model's configuration determines (shared spec checkpoint table).</summary>
    internal static readonly IReadOnlyList<string> CoveredSurfaces =
    [
        "geometry-and-text-buffers",
        "retained-model-history",
        "cell-rendition-and-text-metadata",
        "cursor-and-saved-cursor",
        "modes-and-layout-controls",
        "decoder-and-parser-continuation",
        "text-continuation",
        "titles-and-icon-metadata",
        "command-marks",
        "graphics-placements-and-resources",
    ];

    /// <summary>
    /// The checkpoint of a case armed on a fresh model, or of one that may not hold state (unauthorized, or an HMP1
    /// workload). A model that had applied output takes a <c>text-state/3</c> start instead (<see cref="StartCheckpoint"/>).
    /// </summary>
    internal static DiagnosticCaseCheckpoint Describe(bool fresh, bool authorized, string? unsupported, DiagnosticCaseModelConfiguration configuration)
    {
        // A model that is not fresh, authorized and not HMP1 is a text-state/3 start; this cannot describe it.
        if (!fresh && authorized && unsupported is null)
            throw new ArgumentException($"A model that has applied output takes a {DiagnosticCaseCheckpointProfiles.TextState} start, not a fresh-model/1 checkpoint.", nameof(fresh));
        if (!authorized)
        {
            return new DiagnosticCaseCheckpoint
            {
                Status = DiagnosticCaseCheckpointStatus.Excluded,
                Reason = "Requires the reapplication-data authorization.",
            };
        }

        if (unsupported is null)
        {
            var raw = System.Text.Json.JsonSerializer.SerializeToNode(configuration, DiagnosticsJsonContext.Default.DiagnosticCaseModelConfiguration)!.AsObject();
            if (CaseConfiguration.RebuildProblem(raw, configuration) is { } configurationProblem)
                unsupported = $"configuration: {configurationProblem}";
        }

        if (unsupported is not null)
        {
            return new DiagnosticCaseCheckpoint
            {
                Status = DiagnosticCaseCheckpointStatus.Unsupported,
                Reason = unsupported,
                Configuration = configuration,
            };
        }

        return new DiagnosticCaseCheckpoint
        {
            Status = DiagnosticCaseCheckpointStatus.Complete,
            Configuration = configuration,
            CoveredSurfaces = CoveredSurfaces,
        };
    }
}
