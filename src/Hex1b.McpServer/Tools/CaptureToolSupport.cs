using System.Text.Json;
using Hex1b.Diagnostics;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// Translates MCP tool arguments to the shared diagnostic capture contract and back. Capture
/// policy, permissions, and outcomes stay in <see cref="TerminalDiagnostics"/>; this adapter
/// only parses invocation text and optionally moves content into a file.
/// </summary>
internal static class CaptureToolSupport
{
    public const string AuthorizeDescription =
        "Optional comma-separated opt-ins beyond the rendered screen: non-screen-metadata (titles, hyperlink targets), editor-text, raw-input. Default content can still contain secrets.";

    public const string HistoryRowsDescription =
        "Rows of retained terminal-model history to include above the screen (default 0). This is model history, not native scrollback; see capture.history for requested/available/returned rows.";

    public static async Task<CaptureToolResult> CaptureAsync(
        Func<DiagnosticCaptureRequest, CancellationToken, Task<DiagnosticCaptureResult>> capture,
        string format,
        int historyRows,
        string? authorize,
        string? savePath,
        CancellationToken ct,
        string? sessionId = null,
        int? processId = null)
    {
        var authorizations = authorize?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (request, invalid) = DiagnosticContractNames.ParseCaptureRequest(format, historyRows, authorizations);
        var result = invalid ?? await capture(request!, ct);

        if (result.Outcome != DiagnosticOutcome.Captured)
            return Create(result, sessionId, processId,
                $"Capture {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}");

        var geometry = result.Geometry is { } size ? $"{size.Columns}x{size.Rows}" : "unknown-size";
        var partial = TerminalDiagnostics.DescribePartialContent(result) is { } note ? " " + note : "";
        if (string.IsNullOrEmpty(savePath))
            return Create(result, sessionId, processId, $"Captured {geometry} terminal model as {format.ToLowerInvariant()}.{partial}");

        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(savePath, result.Content, ct);

        return Create(result with { Content = null }, sessionId, processId,
            $"Captured {geometry} terminal model to {savePath}; content is in the file, not capture.content.{partial}", savePath);
    }

    /// <summary>
    /// Serializes with the contract's own serializer so host serializer settings (for example
    /// the MCP SDK's enum converter) cannot change the contract's wire names.
    /// </summary>
    public static JsonElement ToJson(DiagnosticCaptureResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult);

    public static JsonElement ToJson(DiagnosticCapabilities capabilities) =>
        JsonSerializer.SerializeToElement(capabilities, DiagnosticsJsonContext.Default.DiagnosticCapabilities);

    private static CaptureToolResult Create(
        DiagnosticCaptureResult capture, string? sessionId, int? processId, string message, string? savedPath = null) => new()
    {
        Success = capture.Outcome == DiagnosticOutcome.Captured,
        SessionId = sessionId,
        ProcessId = processId,
        Message = message,
        SavedPath = savedPath,
        Capture = ToJson(capture),
    };
}
