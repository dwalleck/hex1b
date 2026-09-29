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
    /// workload). A model that had applied output takes a <c>text-state/1</c> start instead (<see cref="StartCheckpoint"/>).
    /// </summary>
    internal static DiagnosticCaseCheckpoint Describe(bool authorized, string? unsupported, DiagnosticCaseModelConfiguration configuration)
    {
        if (!authorized)
        {
            return new DiagnosticCaseCheckpoint
            {
                Status = DiagnosticCaseCheckpointStatus.Excluded,
                Reason = "Requires the reapplication-data authorization.",
            };
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
