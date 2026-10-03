using System.CommandLine;
using System.Diagnostics;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Hosting;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Terminal;

/// <summary>
/// Starts a hosted terminal by spawning a detached host process.
/// </summary>
internal sealed class TerminalStartCommand : BaseCommand
{
    private static readonly Option<int> s_widthOption = new("--width") { DefaultValueFactory = _ => 120, Description = "Terminal width" };
    private static readonly Option<int> s_heightOption = new("--height") { DefaultValueFactory = _ => 30, Description = "Terminal height" };
    private static readonly Option<string?> s_cwdOption = new("--cwd") { Description = "Working directory" };
    private static readonly Option<string?> s_recordOption = new("--record") { Description = "Record to asciinema file" };
    private static readonly Option<bool> s_attachOption = new("--attach") { Description = "Immediately attach to the terminal after starting" };
    private static readonly Option<bool> s_passthruOption = new("--passthru") { Description = "Run in passthru mode: PTY bridges directly to the current terminal with no chrome" };
    private static readonly Option<int?> s_portOption = new("--port") { Description = "Port for WebSocket diagnostics listener" };
    private static readonly Option<string?> s_bindOption = new("--bind") { Description = "Bind address for the WebSocket listener (default: 127.0.0.1, use 0.0.0.0 for containers)" };
    private static readonly Option<int?> s_scrollbackOption = new("--scrollback") { Description = "Rows of scrollback the terminal retains (1-1,000,000; default none)" };
    private static readonly Option<bool> s_recordCaseOption = new("--record-case")
    {
        Description = "Record a bounded diagnostic case from the terminal's construction (see 'capture case')"
    };
    private static readonly Option<long?> s_caseMaxBytesOption = new("--case-max-bytes") { Description = "With --record-case: largest artifact, in bytes (1 MiB-1 GiB; default 64 MiB)" };
    private static readonly Option<int?> s_caseMaxSecondsOption = new("--case-max-seconds") { Description = "With --record-case: longest recording, in seconds (1-86400; default 600)" };
    private static readonly Option<string[]> s_caseAuthorizeOption = new("--case-authorize")
    {
        Description = "With --record-case: payloads beyond metadata (repeatable or comma-separated): reapplication-data, raw-input, editor-text, native-output"
    };
    private static readonly Option<string?> s_caseDirOption = new("--case-dir") { Description = "With --record-case: owner-only root for the case directory" };
    private static readonly Argument<string[]> s_commandArgument = new("command")
    {
        Description = "Command and arguments to run (after --). Defaults to PowerShell on Windows or bash on Linux/macOS."
    };

    private readonly DiagnosticsSocketClient _client;

    public TerminalStartCommand(
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<TerminalStartCommand> logger)
        : base("start", "Start a hosted terminal", formatter, logger)
    {
        _client = client;

        Options.Add(s_widthOption);
        Options.Add(s_heightOption);
        Options.Add(s_cwdOption);
        Options.Add(s_recordOption);
        Options.Add(s_attachOption);
        Options.Add(s_passthruOption);
        Options.Add(s_portOption);
        Options.Add(s_bindOption);
        Options.Add(s_scrollbackOption);
        Options.Add(s_recordCaseOption);
        Options.Add(s_caseMaxBytesOption);
        Options.Add(s_caseMaxSecondsOption);
        Options.Add(s_caseAuthorizeOption);
        Options.Add(s_caseDirOption);
        Arguments.Add(s_commandArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var width = parseResult.GetValue(s_widthOption);
        var height = parseResult.GetValue(s_heightOption);
        var cwd = parseResult.GetValue(s_cwdOption);
        var record = parseResult.GetValue(s_recordOption);
        var passthru = parseResult.GetValue(s_passthruOption);
        var port = parseResult.GetValue(s_portOption);
        var bind = parseResult.GetValue(s_bindOption);
        var scrollback = parseResult.GetValue(s_scrollbackOption);
        var command = parseResult.GetValue(s_commandArgument) is { Length: > 0 } cmd
            ? cmd
            : TerminalHostPlatformDefaults.GetDefaultCommandLine();
        command = TerminalHostPlatformDefaults.NormalizeCommandLine(command);

        if (passthru && parseResult.GetValue(s_attachOption))
        {
            Formatter.WriteError("--passthru and --attach are mutually exclusive");
            return 1;
        }

        if (scrollback is { } rows && TerminalDiagnostics.ScrollbackProblem(rows) is { } invalidScrollback)
        {
            Formatter.WriteError($"--scrollback: {invalidScrollback}");
            return 1;
        }

        // Case options are parsed and their bounds checked here, before a host is spawned; the host checks storage.
        var recordCase = parseResult.GetValue(s_recordCaseOption);
        var caseAuthorizations = parseResult.GetValue(s_caseAuthorizeOption) ?? [];
        var caseDir = parseResult.GetValue(s_caseDirOption);
        var caseMaxBytes = parseResult.GetValue(s_caseMaxBytesOption);
        var caseMaxSeconds = parseResult.GetValue(s_caseMaxSecondsOption);
        if (!recordCase && (caseAuthorizations.Length > 0 || caseDir != null || caseMaxBytes != null || caseMaxSeconds != null))
        {
            Formatter.WriteError("--case-max-bytes, --case-max-seconds, --case-authorize and --case-dir require --record-case");
            return 1;
        }

        var (caseRequest, invalidCase) = recordCase
            ? DiagnosticContractNames.ParseCaseStartRequest(caseMaxBytes, caseMaxSeconds, caseAuthorizations, caseDir)
            : (null, null);
        if (invalidCase != null)
        {
            Formatter.WriteError($"invalid-request ({invalidCase.Problem!.Code}): {invalidCase.Problem.Message}");
            return 1;
        }

        if (passthru)
        {
            return await RunPassthruAsync(parseResult, width, height, cwd, record, port, bind, scrollback, caseRequest, command, cancellationToken);
        }

        var hostArgs = HostArguments(width, height, cwd, record, port, bind, scrollback, caseRequest, command);

        // Find our own executable
        var selfExe = Environment.ProcessPath ?? "dotnet";
        var isSelfContained = !string.IsNullOrEmpty(Environment.ProcessPath) &&
                              !Environment.ProcessPath.EndsWith("dotnet", StringComparison.OrdinalIgnoreCase);

        var psi = new ProcessStartInfo
        {
            FileName = isSelfContained ? selfExe : "dotnet",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        if (!isSelfContained)
        {
            // Running via `dotnet run` or `dotnet hex1b` — need to pass the DLL
            var assemblyLocation = typeof(TerminalStartCommand).Assembly.Location;
            psi.ArgumentList.Add(assemblyLocation);
        }

        foreach (var arg in hostArgs)
        {
            psi.ArgumentList.Add(arg);
        }

        Logger.LogDebug("Spawning host: {FileName} {Args}", psi.FileName, string.Join(" ", psi.ArgumentList));

        var process = Process.Start(psi);
        if (process == null)
        {
            Formatter.WriteError("Failed to start host process");
            return 1;
        }

        // Wait for the socket to appear
        var socketDir = McpDiagnosticsPresentationFilter.GetSocketDirectory();
        var expectedSocket = Path.Combine(socketDir, $"{process.Id}.diagnostics.socket");

        var timeout = TimeSpan.FromSeconds(10);
        var sw = Stopwatch.StartNew();

        while (sw.Elapsed < timeout)
        {
            if (process.HasExited)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
                Formatter.WriteError($"Host process exited with code {process.ExitCode}: {stderr.Trim()}");
                return 1;
            }

            if (File.Exists(expectedSocket))
            {
                // Probe to confirm it's ready
                var info = await _client.TryProbeAsync(expectedSocket, cancellationToken);
                if (info is { Success: true })
                {
                    // If WebSocket port is configured, verify it's also listening
                    if (port.HasValue)
                    {
                        var wsReady = await TryProbeWebSocketAsync(port.Value, cancellationToken);
                        if (!wsReady)
                        {
                            // Not ready yet — keep polling
                            await Task.Delay(100, cancellationToken);
                            continue;
                        }
                    }

                    var id = process.Id.ToString();
                    var startedCase = caseRequest != null
                        ? await _client.GetCaseStatusAsync(expectedSocket, cancellationToken)
                        : null;

                    if (parseResult.GetValue(s_attachOption))
                    {
                        // Attach immediately. Claim leadership so future local resizes flow to the
                        // remote session, but keep the host's initial size so shells like pwsh
                        // don't boot into a giant mostly-empty screen.
                        var transport = new UnixSocketAttachTransport(expectedSocket);
                        return await TerminalAttachCommand.RunAttachAsync(
                            transport, id, _client,
                            resize: false, lead: true, cancellationToken);
                    }

                    if (parseResult.GetValue(RootCommand.JsonOption))
                    {
                        var jsonOutput = new Dictionary<string, object?>
                        {
                            ["id"] = id,
                            ["pid"] = process.Id,
                            ["name"] = info.AppName,
                            ["width"] = info.Width,
                            ["height"] = info.Height,
                            ["socketPath"] = expectedSocket
                        };
                        if (port.HasValue)
                        {
                            jsonOutput["port"] = port.Value;
                            jsonOutput["wsUrl"] = $"ws://localhost:{port.Value}/ws/attach";
                        }
                        if (startedCase != null)
                        {
                            jsonOutput["case"] = JsonSerializer.SerializeToElement(startedCase, DiagnosticsJsonContext.Default.DiagnosticCaseResult);
                        }
                        Formatter.WriteJson(jsonOutput);
                    }
                    else
                    {
                        var portInfo = port.HasValue ? $", ws://localhost:{port.Value}/ws/attach" : "";
                        Formatter.WriteLine($"Terminal started: {id} ({info.AppName}, {info.Width}x{info.Height}{portInfo})");
                        if (startedCase != null)
                        {
                            Formatter.WriteLine(startedCase.Outcome == DiagnosticOutcome.Captured
                                ? $"Recording case {startedCase.CaseId} to {startedCase.Path} (checkpoint {DiagnosticContractNames.Of(startedCase.Checkpoint!.Status)})"
                                : $"Case status {DiagnosticContractNames.Of(startedCase.Outcome)} ({startedCase.Problem?.Code}): {startedCase.Problem?.Message}");
                        }
                    }

                    return 0;
                }
            }

            await Task.Delay(100, cancellationToken);
        }

        Formatter.WriteError("Timeout waiting for host process to start");
        try { process.Kill(); } catch { /* best effort */ }
        return 1;
    }

    /// <summary>
    /// The arguments of the spawned <c>terminal host</c> process: every option the host takes, forwarded as given.
    /// </summary>
    internal static List<string> HostArguments(
        int width, int height,
        string? cwd, string? record,
        int? port, string? bind,
        int? scrollback,
        DiagnosticCaseStartRequest? caseRequest,
        string[] command)
    {
        var hostArgs = new List<string> { "terminal", "host", "--width", width.ToString(), "--height", height.ToString() };
        if (cwd != null)
        {
            hostArgs.AddRange(["--cwd", cwd]);
        }
        if (record != null)
        {
            hostArgs.AddRange(["--record", record]);
        }
        if (port.HasValue)
        {
            hostArgs.AddRange(["--port", port.Value.ToString()]);
        }
        if (bind != null)
        {
            hostArgs.AddRange(["--bind", bind]);
        }
        if (scrollback.HasValue)
        {
            hostArgs.AddRange(["--scrollback", scrollback.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }
        if (caseRequest != null)
        {
            hostArgs.Add("--record-case");
            if (caseRequest.MaxBytes is { } maxBytes)
                hostArgs.AddRange(["--case-max-bytes", maxBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            if (caseRequest.MaxSeconds is { } maxSeconds)
                hostArgs.AddRange(["--case-max-seconds", maxSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
            foreach (var authorization in caseRequest.Authorizations ?? [])
                hostArgs.AddRange(["--case-authorize", DiagnosticContractNames.Of(authorization)]);
            if (caseRequest.Directory != null)
                hostArgs.AddRange(["--case-dir", caseRequest.Directory]);
        }
        hostArgs.Add("--");
        hostArgs.AddRange(command);
        return hostArgs;
    }

    private async Task<int> RunPassthruAsync(
        ParseResult parseResult,
        int width, int height,
        string? cwd, string? record,
        int? port, string? bind,
        int? scrollback,
        DiagnosticCaseStartRequest? caseRequest,
        string[] command,
        CancellationToken cancellationToken)
    {
        // Warn if explicit dimensions were provided — they're ignored in passthru mode
        if (parseResult.Tokens.Any(t => t.Value is "--width" or "--height"))
        {
            Logger.LogWarning("--width and --height are ignored in passthru mode (terminal size is determined by the outer terminal)");
        }

        var config = new TerminalHostConfig
        {
            Command = command[0],
            Arguments = command.Length > 1 ? command[1..] : [],
            Passthru = true,
            Port = port,
            BindAddress = bind,
            WorkingDirectory = cwd,
            RecordPath = record,
            Scrollback = scrollback,
            DiagnosticCase = caseRequest
        };

        Logger.LogInformation("Starting passthru terminal: {Command}", config.Command);

        return await TerminalHost.RunPassthruAsync(config, cancellationToken);
    }

    /// <summary>
    /// Probes the WebSocket listener's HTTP info endpoint to confirm it's ready.
    /// </summary>
    private static async Task<bool> TryProbeWebSocketAsync(int port, CancellationToken ct)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            var response = await http.GetAsync($"http://localhost:{port}/api/info", ct);
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }
}
