using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hex1b.Diagnostics;
using ModelContextProtocol.Server;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// Unified MCP tools for interacting with terminal targets (both local and remote).
/// </summary>
[McpServerToolType]
public class UnifiedTerminalTools(TerminalSessionManager sessionManager)
{
    /// <summary>
    /// Connects to a remote Hex1b application with diagnostics enabled.
    /// </summary>
    [McpServerTool, Description("Connects to a remote Hex1b application by process ID. The application must have diagnostics enabled via WithDiagnostics(). Returns a session ID for use with other terminal tools. Use GetHex1bSkill to get comprehensive Hex1b MCP documentation.")]
    public async Task<ConnectRemoteResult> ConnectToHex1bStack(
        [Description("Process ID of the Hex1b application to connect to")] int processId,
        CancellationToken ct = default)
    {
        try
        {
            var target = await sessionManager.ConnectRemoteAsync(processId, ct);
            
            return new ConnectRemoteResult
            {
                Success = true,
                SessionId = target.Id,
                Message = $"Connected to {target.Name} (PID: {target.ProcessId}, {target.Width}x{target.Height})",
                AppName = target.Name,
                ProcessId = target.ProcessId,
                Width = target.Width,
                Height = target.Height
            };
        }
        catch (Exception ex)
        {
            return new ConnectRemoteResult
            {
                Success = false,
                SessionId = null,
                Message = $"Failed to connect: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Discovers and connects to all Hex1b applications with diagnostics enabled.
    /// </summary>
    [McpServerTool, Description("Discovers and connects to all Hex1b applications that have diagnostics enabled. Returns session IDs for each connected application.")]
    public async Task<DiscoverAndConnectResult> DiscoverHex1bStacks(
        CancellationToken ct = default)
    {
        try
        {
            var targets = await sessionManager.DiscoverAndConnectRemotesAsync(ct);
            
            return new DiscoverAndConnectResult
            {
                Success = true,
                Message = targets.Count > 0 
                    ? $"Connected to {targets.Count} Hex1b stack(s)."
                    : "No new Hex1b stacks found to connect.",
                ConnectedCount = targets.Count,
                Sessions = targets.Select(t => new ConnectedSessionInfo
                {
                    SessionId = t.Id,
                    AppName = t.Name,
                    ProcessId = t.ProcessId,
                    Width = t.Width,
                    Height = t.Height
                }).ToArray()
            };
        }
        catch (Exception ex)
        {
            return new DiscoverAndConnectResult
            {
                Success = false,
                Message = $"Discovery failed: {ex.Message}",
                ConnectedCount = 0,
                Sessions = []
            };
        }
    }

    /// <summary>
    /// Lists all connected terminal targets (both local and remote).
    /// </summary>
    [McpServerTool, Description("Lists all terminal targets - both local terminals started by the MCP server and remote Hex1b applications connected via diagnostics.")]
    public ListTargetsResult ListAllTerminalTargets()
    {
        var targets = sessionManager.ListTargets();
        
        return new ListTargetsResult
        {
            Success = true,
            TargetCount = targets.Count,
            Targets = targets.Select(t => new TerminalTargetInfo
            {
                SessionId = t.Id,
                TargetType = t.TargetType.ToString(),
                Name = t.Name,
                ProcessId = t.ProcessId,
                Width = t.Width,
                Height = t.Height,
                IsAlive = t.IsAlive,
                StartedAt = t.StartedAt
            }).ToArray()
        };
    }

    /// <summary>
    /// Sends a mouse click to a terminal target.
    /// </summary>
    [McpServerTool, Description("Sends a mouse click to a terminal target at the specified cell position. Works with both local and remote terminals.")]
    public async Task<SendMouseClickResult> SendTerminalMouseClick(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("Column position (0-based)")] int x,
        [Description("Row position (0-based)")] int y,
        [Description("Mouse button: 'left', 'middle', or 'right' (default: 'left')")] string button = "left",
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        if (target == null)
        {
            return new SendMouseClickResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found. Use list_all_terminal_targets to see available sessions."
            };
        }

        var mouseButton = button.ToLowerInvariant() switch
        {
            "middle" => MouseButton.Middle,
            "right" => MouseButton.Right,
            _ => MouseButton.Left
        };

        try
        {
            await target.SendMouseClickAsync(x, y, mouseButton, ct);
            
            return new SendMouseClickResult
            {
                Success = true,
                SessionId = sessionId,
                Message = $"Clicked {button} at ({x}, {y})",
                X = x,
                Y = y,
                Button = button
            };
        }
        catch (Exception ex)
        {
            return new SendMouseClickResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Failed to send mouse click: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Sends a key press to a terminal target.
    /// </summary>
    [McpServerTool, Description("Sends a key press to a terminal target. Supports special keys like Enter, Tab, Escape, arrow keys, F1-F12, etc. Works with both local and remote terminals.")]
    public async Task<SendKeyResult> SendTerminalKey(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("Key to send: Enter, Tab, Escape, Up, Down, Left, Right, Backspace, Delete, Home, End, PageUp, PageDown, F1-F12, or a single character")] string key,
        [Description("Optional modifiers: Ctrl, Alt, Shift (comma-separated)")] string? modifiers = null,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        if (target == null)
        {
            return new SendKeyResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found."
            };
        }

        var modifierArray = string.IsNullOrEmpty(modifiers) 
            ? null 
            : modifiers.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        try
        {
            await target.SendKeyAsync(key, modifierArray, ct);
            
            var modStr = modifierArray != null ? $" with {string.Join("+", modifierArray)}" : "";
            return new SendKeyResult
            {
                Success = true,
                SessionId = sessionId,
                Message = $"Sent key '{key}'{modStr}",
                Key = key,
                Modifiers = modifierArray
            };
        }
        catch (Exception ex)
        {
            return new SendKeyResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Failed to send key: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Captures the terminal model of any target through the shared diagnostic contract.
    /// </summary>
    [McpServerTool, Description("Captures the terminal screen from any connected target (local or remote) through the shared diagnostic contract. ANSI preserves cell styles. Returns capture.outcome, content, geometry, history coverage, identity (process/session/build/configuration/acquisition clock), content coverage (included/excluded/unavailable), and limitations. Use get_terminal_diagnostic_capabilities to discover supported formats and limits.")]
    public async Task<CaptureToolResult> CaptureTerminalScreen(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("Capture format: 'text', 'ansi', 'svg', or 'html' (default: 'text')")] string format = "text",
        [Description("Optional file path to save the content. When provided, content is written to the file and omitted from capture.content.")] string? savePath = null,
        [Description(CaptureToolSupport.HistoryRowsDescription)] int historyRows = 0,
        [Description(CaptureToolSupport.AuthorizeDescription)] string? authorize = null,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        if (target == null)
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
            return await CaptureToolSupport.CaptureAsync(target.CaptureAsync, format, historyRows, authorize, savePath, ct, sessionId);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
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
    /// Returns the latest application frame of any target through the shared contract.
    /// </summary>
    [McpServerTool, Description("Returns the latest frame a Hex1b application published at the end of a completed render pass, through the shared diagnostic contract: node tree with bounds, effective visible rects and clip state, focus ring, popups, focused-editor carets/selections, timings, and the frame's identity. Capturing never drives a render. Targets that are not Hex1b applications (for example local PTY sessions) report outcome 'unavailable' with code 'no-application-layer'.")]
    public async Task<ApplicationFrameToolResult> CaptureApplicationFrame(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description(CaptureToolSupport.FrameAuthorizeDescription)] string? authorize = null,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        if (target == null)
        {
            return CaptureToolSupport.FrameResult(
                TerminalDiagnostics.FrameProblem(DiagnosticOutcome.Unavailable, "session-not-found", $"Session '{sessionId}' not found."),
                sessionId, processId: null);
        }

        return await CaptureToolSupport.CaptureApplicationFrameAsync(target.CaptureApplicationFrameAsync, authorize, ct, sessionId);
    }

    /// <summary>
    /// Describes the diagnostic capabilities of any target.
    /// </summary>
    [McpServerTool, Description("Describes what diagnostic observations a terminal target supports: operations, formats, timing, authorizations, evidence layers (terminal model, application frame, native delivery, native presentation) with reasons for unavailable layers, and exact limitations.")]
    public async Task<DiagnosticCapabilitiesToolResult> GetTerminalDiagnosticCapabilities(
        [Description("Session ID of the terminal target")] string sessionId,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        var capabilities = target is null
            ? TerminalDiagnostics.CapabilitiesProblem(DiagnosticOutcome.Unavailable, "session-not-found", $"Session '{sessionId}' not found.")
            : await target.GetDiagnosticCapabilitiesAsync(ct);

        return new DiagnosticCapabilitiesToolResult
        {
            Success = capabilities.Outcome == DiagnosticOutcome.Captured,
            SessionId = sessionId,
            Message = capabilities.Problem?.Message ?? "Described diagnostic capabilities.",
            Capabilities = CaptureToolSupport.ToJson(capabilities)
        };
    }

    /// <summary>
    /// Waits for specific text to appear on the terminal screen.
    /// </summary>
    [McpServerTool, Description("Waits for specific text to appear on the terminal screen (1-60 seconds). Useful for waiting for prompts or specific output. Works with both local and remote terminals; an unobservable target fails instead of reporting the text as not found.")]
    public async Task<WaitForTextResult> WaitForTerminalText(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("The text to wait for")] string text,
        [Description("Maximum seconds to wait (default: 10)")] int timeoutSeconds = 10,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        if (target == null)
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
            var timeout = TimeSpan.FromSeconds(Math.Clamp(timeoutSeconds, 1, 60));
            var found = await target.WaitForTextAsync(text, timeout, ct);
            string? currentText = null;
            var partial = "";
            if (!found)
            {
                var current = await target.CaptureAsync(new DiagnosticCaptureRequest { Format = DiagnosticCaptureFormat.Text }, ct);
                currentText = current.Content;
                if (TerminalDiagnostics.DescribePartialContent(current) is { } note)
                    partial = " " + note;
            }

            return new WaitForTextResult
            {
                Success = true,
                SessionId = sessionId,
                Message = found ? $"Found text '{text}'" : $"Text '{text}' not found within {timeout.TotalSeconds:0}s.{partial}",
                Found = found,
                CurrentText = currentText
            };
        }
        catch (Exception ex)
        {
            return new WaitForTextResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Wait failed: {ex.Message}",
                Found = false
            };
        }
    }
}

// === Result Types ===

public class ConnectRemoteResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("appName")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? AppName { get; init; }

    [JsonPropertyName("processId")]
    public int? ProcessId { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
}

public class DiscoverAndConnectResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("connectedCount")]
    public required int ConnectedCount { get; init; }

    [JsonPropertyName("sessions")]
    public required ConnectedSessionInfo[] Sessions { get; init; }
}

public class ConnectedSessionInfo
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("appName")]
    public required string AppName { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }
}

public class ListTargetsResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("targetCount")]
    public required int TargetCount { get; init; }

    [JsonPropertyName("targets")]
    public required TerminalTargetInfo[] Targets { get; init; }
}

public class TerminalTargetInfo
{
    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("targetType")]
    public required string TargetType { get; init; }

    [JsonPropertyName("name")]
    public required string Name { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }

    [JsonPropertyName("isAlive")]
    public required bool IsAlive { get; init; }

    [JsonPropertyName("startedAt")]
    public required DateTimeOffset StartedAt { get; init; }
}

public class SendMouseClickResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("x")]
    public int X { get; init; }

    [JsonPropertyName("y")]
    public int Y { get; init; }

    [JsonPropertyName("button")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Button { get; init; }
}

public class SendKeyResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    [JsonPropertyName("modifiers")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string[]? Modifiers { get; init; }
}

public class DiagnosticCapabilitiesToolResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("capabilities")]
    public required JsonElement Capabilities { get; init; }
}

// WaitForTextResult is defined in ToolResults.cs
