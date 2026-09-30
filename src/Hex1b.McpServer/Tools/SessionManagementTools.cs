using System.ComponentModel;
using Hex1b.Diagnostics;
using ModelContextProtocol.Server;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP tools for managing terminal session lifecycle.
/// </summary>
[McpServerToolType]
public class SessionManagementTools(TerminalSessionManager sessionManager)
{
    /// <summary>
    /// Starts a new bash terminal session.
    /// </summary>
    [McpServerTool, Description("Start a new bash terminal session. Use this on Linux and macOS. Returns the session ID for use with other terminal tools.")]
    public async Task<StartTerminalResult> StartBashTerminal(
        [Description("Optional working directory for the bash session")] string? workingDirectory = null,
        [Description("Terminal width in columns (default: 80)")] int width = 80,
        [Description("Terminal height in rows (default: 24)")] int height = 24,
        [Description("Optional path to save an asciinema recording file (.cast extension recommended)")] string? asciinemaFilePath = null,
        [Description("Record a bounded diagnostic case from the session's first byte (see start_diagnostic_case); the result's 'case' reports it.")] bool recordCase = false,
        [Description("With recordCase: " + DiagnosticCaseTools.AuthorizeDescription)] string? caseAuthorize = null,
        [Description("With recordCase: largest artifact in bytes (1 MiB to 1 GiB; default 64 MiB).")] long? caseMaxBytes = null,
        [Description("With recordCase: longest recording in seconds (1 to 86400; default 600).")] int? caseMaxSeconds = null,
        [Description("With recordCase: owner-only root directory for the case.")] string? caseDirectory = null,
        [Description("Rows of scrollback the terminal retains (1 to 1,000,000; default none).")] int? scrollback = null,
        CancellationToken ct = default)
    {
        return await StartShellAsync("bash", [], workingDirectory, width, height, asciinemaFilePath, scrollback,
            CaseOptions(recordCase, caseAuthorize, caseMaxBytes, caseMaxSeconds, caseDirectory), ct);
    }

    /// <summary>
    /// Starts a new PowerShell (pwsh) terminal session.
    /// </summary>
    [McpServerTool, Description("Start a new PowerShell (pwsh) terminal session. Use this on Windows or when PowerShell is preferred. Returns the session ID for use with other terminal tools.")]
    public async Task<StartTerminalResult> StartPwshTerminal(
        [Description("Optional working directory for the PowerShell session")] string? workingDirectory = null,
        [Description("Terminal width in columns (default: 80)")] int width = 80,
        [Description("Terminal height in rows (default: 24)")] int height = 24,
        [Description("Optional path to save an asciinema recording file (.cast extension recommended)")] string? asciinemaFilePath = null,
        [Description("Record a bounded diagnostic case from the session's first byte (see start_diagnostic_case); the result's 'case' reports it.")] bool recordCase = false,
        [Description("With recordCase: " + DiagnosticCaseTools.AuthorizeDescription)] string? caseAuthorize = null,
        [Description("With recordCase: largest artifact in bytes (1 MiB to 1 GiB; default 64 MiB).")] long? caseMaxBytes = null,
        [Description("With recordCase: longest recording in seconds (1 to 86400; default 600).")] int? caseMaxSeconds = null,
        [Description("With recordCase: owner-only root directory for the case.")] string? caseDirectory = null,
        [Description("Rows of scrollback the terminal retains (1 to 1,000,000; default none).")] int? scrollback = null,
        CancellationToken ct = default)
    {
        return await StartShellAsync("pwsh", [], workingDirectory, width, height, asciinemaFilePath, scrollback,
            CaseOptions(recordCase, caseAuthorize, caseMaxBytes, caseMaxSeconds, caseDirectory), ct);
    }

    // The case options when recordCase is set: the request, or why it is invalid. Other case options without
    // recordCase are refused, rather than silently starting a session without a case.
    private static (bool Record, DiagnosticCaseStartRequest? Request, DiagnosticCaseResult? Invalid) CaseOptions(
        bool recordCase, string? authorize, long? maxBytes, int? maxSeconds, string? directory)
    {
        if (!recordCase)
        {
            return authorize is null && maxBytes is null && maxSeconds is null && directory is null
                ? (false, null, null)
                : (false, null, TerminalDiagnostics.CaseProblem(DiagnosticOutcome.InvalidRequest, "invalid-request",
                    "caseAuthorize, caseMaxBytes, caseMaxSeconds and caseDirectory require recordCase."));
        }

        var (request, invalid) = DiagnosticCaseTools.ParseStart(maxBytes, maxSeconds, authorize, directory);
        return (true, request, invalid);
    }

    private async Task<StartTerminalResult> StartShellAsync(
        string command,
        string[] arguments,
        string? workingDirectory,
        int width,
        int height,
        string? asciinemaFilePath,
        int? scrollback,
        (bool Record, DiagnosticCaseStartRequest? Request, DiagnosticCaseResult? Invalid) diagnosticCase,
        CancellationToken ct)
    {
        if (scrollback is { } rows && TerminalDiagnostics.ScrollbackProblem(rows) is { } invalidScrollback)
            return Failed(command, arguments, workingDirectory, width, height, $"Failed to start terminal: invalid-request: {invalidScrollback}", null);
        if (diagnosticCase.Invalid is { } invalid)
            return Failed(command, arguments, workingDirectory, width, height, $"Failed to start terminal: {invalid.Problem!.Code}: {invalid.Problem.Message}", invalid);

        try
        {
            var session = await sessionManager.StartSessionAsync(
                command,
                arguments,
                workingDirectory,
                environment: null,
                width,
                height,
                asciinemaFilePath,
                diagnosticCase.Request,
                scrollback,
                ct);

            return new StartTerminalResult
            {
                Success = true,
                SessionId = session.Id,
                ProcessId = session.ProcessId,
                Message = asciinemaFilePath != null 
                    ? $"Terminal session started successfully. Recording to: {asciinemaFilePath}"
                    : "Terminal session started successfully.",
                Command = session.Command,
                Arguments = session.Arguments.ToArray(),
                WorkingDirectory = session.WorkingDirectory,
                Width = session.Width,
                Height = session.Height,
                AsciinemaFilePath = session.AsciinemaFilePath,
                Case = diagnosticCase.Record ? DiagnosticCaseTools.ToJson(session.Diagnostics.GetCaseStatus()) : null
            };
        }
        catch (DiagnosticCaseStartException ex)
        {
            return Failed(command, arguments, workingDirectory, width, height, $"Failed to start terminal: {ex.Message}", ex.Result);
        }
        catch (Exception ex)
        {
            return Failed(command, arguments, workingDirectory, width, height, $"Failed to start terminal: {ex.Message}", null);
        }
    }

    private static StartTerminalResult Failed(string command, string[] arguments, string? workingDirectory, int width, int height,
        string message, DiagnosticCaseResult? diagnosticCase) => new()
    {
        Success = false,
        SessionId = null,
        ProcessId = null,
        Message = message,
        Command = command,
        Arguments = arguments,
        WorkingDirectory = workingDirectory,
        Width = width,
        Height = height,
        AsciinemaFilePath = null,
        Case = diagnosticCase is null ? null : DiagnosticCaseTools.ToJson(diagnosticCase)
    };

    /// <summary>
    /// Stops a terminal session's process but keeps the session for inspection.
    /// </summary>
    [McpServerTool, Description("Stop a terminal session's process by its ID. The session remains available for inspection. Use remove_session to fully clean up.")]
    public StopTerminalResult StopTerminal(
        [Description("The session ID returned by start_bash_terminal or start_pwsh_terminal")] string sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            return new StopTerminalResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found."
            };
        }

        var hadExited = session.HasExited;
        var exitCode = hadExited ? session.ExitCode : (int?)null;
        var asciinemaPath = session.AsciinemaFilePath;

        if (!hadExited)
        {
            sessionManager.StopSession(sessionId);
        }

        var message = hadExited 
            ? $"Process had already exited with code {exitCode}." 
            : "Process stopped. Use remove_session to clean up the session.";
        
        if (asciinemaPath != null)
        {
            message += $" Asciinema recording: {asciinemaPath}";
        }

        return new StopTerminalResult
        {
            Success = true,
            SessionId = sessionId,
            Message = message,
            HadAlreadyExited = hadExited,
            ExitCode = exitCode,
            AsciinemaFilePath = asciinemaPath
        };
    }

    /// <summary>
    /// Lists all active terminal sessions.
    /// </summary>
    [McpServerTool, Description("List all active terminal sessions with their status and information.")]
    public ListTerminalsResult ListTerminals()
    {
        var sessions = sessionManager.ListSessions();

        return new ListTerminalsResult
        {
            SessionCount = sessions.Count,
            Sessions = sessions.Select(s => new TerminalSessionInfo
            {
                SessionId = s.Id,
                ProcessId = s.ProcessId,
                Command = s.Command,
                Arguments = s.Arguments.ToArray(),
                WorkingDirectory = s.WorkingDirectory,
                Width = s.Width,
                Height = s.Height,
                StartedAt = s.StartedAt,
                HasExited = s.HasExited,
                ExitCode = s.ExitCode,
                RunningFor = s.HasExited ? null : DateTimeOffset.UtcNow - s.StartedAt,
                AsciinemaFilePath = s.AsciinemaFilePath,
                IsRecording = s.IsRecording,
                ActiveRecordingPath = s.ActiveRecordingPath
            }).ToArray()
        };
    }

    /// <summary>
    /// Removes a terminal session completely, disposing all resources.
    /// </summary>
    [McpServerTool, Description("Remove a terminal session completely, disposing all resources. Use after stop_terminal or when the process has exited.")]
    public async Task<RemoveSessionResult> RemoveSession(
        [Description("The session ID returned by start_bash_terminal or start_pwsh_terminal")] string sessionId)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            return new RemoveSessionResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found."
            };
        }

        var wasRunning = !session.HasExited;
        var removed = await sessionManager.RemoveSessionAsync(sessionId);

        return new RemoveSessionResult
        {
            Success = removed,
            SessionId = sessionId,
            Message = removed
                ? (wasRunning ? "Session removed (process was still running and has been killed)." : "Session removed.")
                : $"Failed to remove session '{sessionId}'.",
            WasRunning = wasRunning
        };
    }

    /// <summary>
    /// Resizes a terminal session.
    /// </summary>
    [McpServerTool, Description("Resize a terminal session to the specified dimensions.")]
    public async Task<ResizeTerminalResult> ResizeTerminal(
        [Description("The session ID returned by start_bash_terminal or start_pwsh_terminal")] string sessionId,
        [Description("New width in columns")] int width,
        [Description("New height in rows")] int height,
        CancellationToken ct = default)
    {
        var session = sessionManager.GetSession(sessionId);
        if (session == null)
        {
            return new ResizeTerminalResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Session '{sessionId}' not found."
            };
        }

        if (width < 1 || height < 1)
        {
            return new ResizeTerminalResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Invalid dimensions: width and height must be at least 1."
            };
        }

        if (width > 500 || height > 200)
        {
            return new ResizeTerminalResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Dimensions too large: max 500x200."
            };
        }

        try
        {
            var oldWidth = session.Width;
            var oldHeight = session.Height;
            await session.ResizeAsync(width, height, ct);

            return new ResizeTerminalResult
            {
                Success = true,
                SessionId = sessionId,
                Message = $"Resized terminal from {oldWidth}x{oldHeight} to {width}x{height}.",
                OldWidth = oldWidth,
                OldHeight = oldHeight,
                NewWidth = width,
                NewHeight = height
            };
        }
        catch (Exception ex)
        {
            return new ResizeTerminalResult
            {
                Success = false,
                SessionId = sessionId,
                Message = $"Failed to resize: {ex.Message}"
            };
        }
    }
}
