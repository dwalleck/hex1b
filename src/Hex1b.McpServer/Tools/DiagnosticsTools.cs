using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Hex1b.Diagnostics;
using ModelContextProtocol.Server;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP tools for discovering and capturing Hex1b terminals that have diagnostics enabled.
/// All socket access goes through the shared diagnostics client.
/// </summary>
[McpServerToolType]
public class DiagnosticsTools
{
    private static readonly DiagnosticsSocketClient Client = new();

    /// <summary>
    /// Lists all Hex1b terminals that have diagnostics enabled via WithDiagnostics().
    /// Connects to each socket to get info about the running application.
    /// </summary>
    [McpServerTool, Description("Lists all Hex1b terminals that have diagnostics enabled via WithDiagnostics(). Returns information about each running application including name, process ID, and dimensions. Tip: Use GetHex1bSkill to get comprehensive documentation about all Hex1b MCP tools.")]
    public async Task<GetHex1bStacksResult> GetHex1bStacksWithDiagnosticsEnabled(
        CancellationToken ct = default)
    {
        var socketDir = McpDiagnosticsPresentationFilter.GetSocketDirectory();
        var stacks = new List<Hex1bStackInfo>();

        if (!Directory.Exists(socketDir))
        {
            return new GetHex1bStacksResult
            {
                Success = true,
                Message = "No Hex1b stacks with diagnostics enabled found.",
                StackCount = 0,
                Stacks = []
            };
        }

        foreach (var socketPath in Directory.GetFiles(socketDir, "*.diagnostics.socket"))
        {
            var pidStr = Path.GetFileName(socketPath).Replace(".diagnostics.socket", "");
            if (!int.TryParse(pidStr, out var pid))
                continue;

            if (!IsProcessRunning(pid))
            {
                // Clean up stale socket
                try { File.Delete(socketPath); }
                catch { /* ignore */ }
                continue;
            }

            var info = await Client.TryProbeAsync(socketPath, ct);
            stacks.Add(info is { Success: true }
                ? new Hex1bStackInfo
                {
                    SocketPath = socketPath,
                    AppName = info.AppName ?? "Unknown",
                    ProcessId = info.ProcessId ?? pid,
                    StartTime = info.StartTime,
                    Width = info.Width ?? 0,
                    Height = info.Height ?? 0,
                    IsResponsive = true
                }
                : new Hex1bStackInfo
                {
                    SocketPath = socketPath,
                    AppName = "Unknown",
                    ProcessId = pid,
                    Width = 0,
                    Height = 0,
                    IsResponsive = false
                });
        }

        return new GetHex1bStacksResult
        {
            Success = true,
            Message = stacks.Count > 0
                ? $"Found {stacks.Count} Hex1b stack(s) with diagnostics enabled."
                : "No Hex1b stacks with diagnostics enabled found.",
            StackCount = stacks.Count,
            Stacks = stacks.ToArray()
        };
    }

    /// <summary>
    /// Captures the terminal state from a Hex1b application with diagnostics enabled.
    /// </summary>
    [McpServerTool, Description("Captures the terminal model of a Hex1b application with diagnostics enabled and saves the content to a file (ansi, svg, html, or text). Returns the shared diagnostic capture result with geometry, history coverage, identity, and content coverage; the content itself is in the file. Use GetHex1bSkill for comprehensive MCP documentation.")]
    public async Task<CaptureToolResult> CaptureHex1bTerminal(
        [Description("Process ID of the Hex1b application to capture")] int processId,
        [Description("File path to save the capture (required).")] string savePath,
        [Description("Capture format: 'ansi', 'svg', 'html', or 'text' (default: 'ansi')")] string format = "ansi",
        [Description(CaptureToolSupport.HistoryRowsDescription)] int historyRows = 0,
        [Description(CaptureToolSupport.AuthorizeDescription)] string? authorize = null,
        CancellationToken ct = default)
    {
        var unavailable = CheckTarget(processId, out var socketPath);
        if (unavailable != null)
        {
            return new CaptureToolResult
            {
                Success = false,
                ProcessId = processId,
                Message = unavailable,
                Capture = CaptureToolSupport.ToJson(TerminalDiagnostics.Problem(DiagnosticOutcome.Unavailable, "target-unreachable", unavailable))
            };
        }

        try
        {
            return await CaptureToolSupport.CaptureAsync(
                (request, token) => Client.CaptureAsync(socketPath, request, token),
                format, historyRows, authorize, savePath, ct, processId: processId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new CaptureToolResult
            {
                Success = false,
                ProcessId = processId,
                Message = $"Failed to save capture: {ex.Message}",
                Capture = CaptureToolSupport.ToJson(TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "save-failed", ex.Message))
            };
        }
    }

    /// <summary>
    /// Sends input to a Hex1b application with diagnostics enabled.
    /// </summary>
    [McpServerTool, Description("Sends input characters to a Hex1b application with diagnostics enabled. Supports escape sequences like \\n, \\t, \\x1b. Use GetHex1bSkill for comprehensive MCP documentation.")]
    public async Task<SendInputToHex1bTerminalResult> SendInputToHex1bTerminal(
        [Description("Process ID of the Hex1b application")] int processId,
        [Description("The input to send. Supports escape sequences like \\n for newline, \\t for tab, \\x1b for escape.")] string input,
        CancellationToken ct = default)
    {
        var unavailable = CheckTarget(processId, out var socketPath);
        if (unavailable != null)
            return new SendInputToHex1bTerminalResult { Success = false, ProcessId = processId, Message = unavailable };

        try
        {
            var processedInput = ProcessEscapeSequences(input);
            var response = await Client.SendAsync(socketPath, new DiagnosticsRequest { Method = "input", Data = processedInput }, ct);
            if (!response.Success)
            {
                return new SendInputToHex1bTerminalResult
                {
                    Success = false,
                    ProcessId = processId,
                    Message = response.Error ?? "Unknown error from diagnostics socket."
                };
            }

            return new SendInputToHex1bTerminalResult
            {
                Success = true,
                ProcessId = processId,
                Message = $"Sent input to process {processId}",
                CharactersSent = processedInput.Length
            };
        }
        catch (Exception ex)
        {
            return new SendInputToHex1bTerminalResult
            {
                Success = false,
                ProcessId = processId,
                Message = $"Failed to send input: {ex.Message}"
            };
        }
    }

    /// <summary>
    /// Gets the widget/node tree from a Hex1b application for debugging.
    /// </summary>
    [McpServerTool, Description("Gets the widget/node tree, popup stack, focus ring, and frame timing information from a Hex1b application. Use this to debug hit testing, focus, and layout issues. Essential for understanding why clicks aren't working or focus is wrong. Use GetHex1bSkill for comprehensive documentation.")]
    public async Task<GetHex1bTreeResult> GetHex1bTree(
        [Description("Process ID of the Hex1b application")] int processId,
        CancellationToken ct = default)
    {
        var unavailable = CheckTarget(processId, out var socketPath);
        if (unavailable != null)
            return new GetHex1bTreeResult { Success = false, ProcessId = processId, Message = unavailable };

        try
        {
            var response = await Client.SendAsync(socketPath, new DiagnosticsRequest { Method = "tree" }, ct);
            if (!response.Success)
            {
                return new GetHex1bTreeResult
                {
                    Success = false,
                    ProcessId = processId,
                    Message = response.Error ?? "Unknown error from diagnostics socket."
                };
            }

            return new GetHex1bTreeResult
            {
                Success = true,
                ProcessId = processId,
                Message = $"Retrieved tree from {response.Width}x{response.Height} terminal",
                Width = response.Width ?? 0,
                Height = response.Height ?? 0,
                Tree = ToElement(response.Tree),
                Popups = ToElement(response.Popups),
                FocusInfo = ToElement(response.FocusInfo),
                FrameInfo = ToElement(response.FrameInfo)
            };
        }
        catch (Exception ex)
        {
            return new GetHex1bTreeResult
            {
                Success = false,
                ProcessId = processId,
                Message = $"Failed to get tree: {ex.Message}"
            };
        }
    }

    private static JsonElement? ToElement<T>(T? value) where T : class =>
        value is null ? null : JsonSerializer.SerializeToElement(value, DiagnosticsJsonOptions.Default.GetTypeInfo(typeof(T)));

    // Returns why the target cannot be reached, or null when its socket is live.
    private static string? CheckTarget(int processId, out string socketPath)
    {
        socketPath = Path.Combine(McpDiagnosticsPresentationFilter.GetSocketDirectory(), $"{processId}.diagnostics.socket");
        if (!File.Exists(socketPath))
            return $"No diagnostics socket found for process {processId}. Ensure the application is running with WithDiagnostics() enabled.";

        if (IsProcessRunning(processId))
            return null;

        try { File.Delete(socketPath); }
        catch { /* ignore */ }
        return $"Process {processId} is no longer running.";
    }

    private static string ProcessEscapeSequences(string input)
    {
        // Process common escape sequences
        return input
            .Replace("\\n", "\n")
            .Replace("\\r", "\r")
            .Replace("\\t", "\t")
            .Replace("\\x1b", "\x1b")
            .Replace("\\e", "\x1b")
            .Replace("\\\\", "\\");
    }

    private static bool IsProcessRunning(int pid)
    {
        try
        {
            var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}

// === Result Types ===

public class GetHex1bStacksResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("stackCount")]
    public required int StackCount { get; init; }

    [JsonPropertyName("stacks")]
    public required Hex1bStackInfo[] Stacks { get; init; }
}

public class Hex1bStackInfo
{
    [JsonPropertyName("socketPath")]
    public required string SocketPath { get; init; }

    [JsonPropertyName("appName")]
    public required string AppName { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("startTime")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTimeOffset? StartTime { get; init; }

    [JsonPropertyName("width")]
    public required int Width { get; init; }

    [JsonPropertyName("height")]
    public required int Height { get; init; }

    [JsonPropertyName("isResponsive")]
    public required bool IsResponsive { get; init; }
}

public class SendInputToHex1bTerminalResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("charactersSent")]
    public int CharactersSent { get; init; }
}

public class GetHex1bTreeResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("processId")]
    public required int ProcessId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("width")]
    public int Width { get; init; }

    [JsonPropertyName("height")]
    public int Height { get; init; }
    
    [JsonPropertyName("tree")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Tree { get; init; }
    
    [JsonPropertyName("popups")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? Popups { get; init; }
    
    [JsonPropertyName("focusInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? FocusInfo { get; init; }

    [JsonPropertyName("frameInfo")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public JsonElement? FrameInfo { get; init; }
}
