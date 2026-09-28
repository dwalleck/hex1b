using System.ComponentModel;
using Hex1b.Diagnostics;
using ModelContextProtocol.Server;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP tools for capturing terminal output.
/// </summary>
[McpServerToolType]
public class CaptureTools(TerminalSessionManager sessionManager)
{
    /// <summary>
    /// Captures the current terminal screen as text.
    /// </summary>
    [McpServerTool, Description("Capture the current terminal screen content as plain text. Returns the shared diagnostic capture result (capture.content, geometry, history coverage, identity, content coverage).")]
    public Task<CaptureToolResult> CaptureTerminalText(
        [Description("The session ID returned by start_terminal")] string sessionId,
        [Description(CaptureToolSupport.HistoryRowsDescription)] int historyRows = 0,
        CancellationToken ct = default)
        => CaptureSessionAsync(sessionId, "text", historyRows, authorize: null, savePath: null, ct);

    /// <summary>
    /// Captures the current terminal screen as an SVG image.
    /// </summary>
    [McpServerTool, Description("Capture the current terminal screen as an SVG image and save to a file. The returned capture carries geometry, history coverage, identity, and content coverage; its content is in the file.")]
    public Task<CaptureToolResult> CaptureTerminalScreenshot(
        [Description("The session ID returned by start_terminal")] string sessionId,
        [Description("File path to save the SVG screenshot (required).")] string savePath,
        [Description(CaptureToolSupport.HistoryRowsDescription)] int historyRows = 0,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(savePath))
        {
            return Task.FromResult(new CaptureToolResult
            {
                Success = false,
                SessionId = sessionId,
                Message = "savePath is required. Please provide a file path to save the screenshot.",
                Capture = CaptureToolSupport.ToJson(TerminalDiagnostics.Problem(DiagnosticOutcome.InvalidRequest, "missing-save-path", "savePath is required."))
            });
        }

        if (!savePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            savePath = Path.ChangeExtension(savePath, ".svg");

        return CaptureSessionAsync(sessionId, "svg", historyRows, authorize: null, savePath, ct);
    }

    private async Task<CaptureToolResult> CaptureSessionAsync(
        string sessionId, string format, int historyRows, string? authorize, string? savePath, CancellationToken ct)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            return new CaptureToolResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found.",
                Capture = CaptureToolSupport.ToJson(TerminalDiagnostics.Problem(DiagnosticOutcome.Unavailable, "session-not-found", $"Session '{sessionId}' not found."))
            };
        }

        try
        {
            var result = await CaptureToolSupport.CaptureAsync(
                (request, _) => Task.FromResult(session.Diagnostics.Capture(request)),
                format, historyRows, authorize, savePath, ct, sessionId);
            return new CaptureToolResult
            {
                Success = result.Success,
                SessionId = sessionId,
                Message = result.Message,
                SavedPath = result.SavedPath,
                HasExited = session.HasExited,
                ExitCode = session.HasExited ? session.ExitCode : null,
                Capture = result.Capture
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CaptureToolResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Failed to save capture: {ex.Message}",
                Capture = CaptureToolSupport.ToJson(TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "save-failed", ex.Message))
            };
        }
    }

    /// <summary>
    /// Waits for specific text to appear on the terminal screen.
    /// </summary>
    [McpServerTool, Description("Wait for specific text to appear on the terminal screen. Useful for waiting for prompts or output.")]
    public async Task<WaitForTextResult> WaitForTerminalText(
        [Description("The session ID returned by start_terminal")] string sessionId,
        [Description("The text to wait for")] string text,
        [Description("Maximum seconds to wait (default: 10)")] int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            return new WaitForTextResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found.",
                Found = false
            };
        }

        try
        {
            var timeout = TimeSpan.FromSeconds(Math.Max(1, Math.Min(timeoutSeconds, 60)));
            var found = await session.WaitForTextAsync(text, timeout, ct);

            if (found)
            {
                return new WaitForTextResult
                {
                    Success = true,
                    SessionId = sessionId,
                    Message = $"Text '{text}' found on terminal.",
                    Found = true
                };
            }
            else
            {
                return new WaitForTextResult
                {
                    Success = true,
                    SessionId = sessionId,
                    Message = $"Text '{text}' not found within {timeoutSeconds} seconds.",
                    Found = false,
                    CurrentText = session.Diagnostics.Capture(new DiagnosticCaptureRequest()).Content
                };
            }
        }
        catch (Exception ex)
        {
            return new WaitForTextResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Failed to wait for text: {ex.Message}",
                Found = false
            };
        }
    }
}
