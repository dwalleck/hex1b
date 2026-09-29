namespace Hex1b.Diagnostics;

/// <summary>
/// Re-applies recorded diagnostic cases offline, in a detached model whose pumps never run. It reads the case's
/// files and writes only its own run directory inside the case.
/// </summary>
public static class DiagnosticCaseReapplier
{
    /// <summary>Most differences a request may list.</summary>
    public const int MaxDifferences = Cases.ModelStateComparer.MaxMaxDifferences;

    /// <summary>The declared fault kinds.</summary>
    public static IReadOnlyList<string> FaultKinds => Cases.ModelStateFault.Kinds;

    /// <summary>Re-applies a case to its target boundary and compares it with the checkpoint recorded there.</summary>
    public static DiagnosticCaseReapplyResult Reapply(DiagnosticCaseReapplyRequest request) => Cases.CaseReapplier.Reapply(request);
}
