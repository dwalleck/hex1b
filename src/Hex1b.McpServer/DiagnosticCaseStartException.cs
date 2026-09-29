using Hex1b.Diagnostics;

namespace Hex1b.McpServer;

/// <summary>
/// A session could not start because its construction-time diagnostic case was refused; carries the
/// engine's result.
/// </summary>
public sealed class DiagnosticCaseStartException(DiagnosticCaseResult result)
    : InvalidOperationException($"The diagnostic case could not start: {result.Problem?.Code}: {result.Problem?.Message}")
{
    /// <summary>The engine's result for the refused start.</summary>
    public DiagnosticCaseResult Result { get; } = result;
}
