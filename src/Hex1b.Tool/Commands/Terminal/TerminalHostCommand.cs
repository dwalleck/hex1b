using System.CommandLine;
using Hex1b.Tool.Hosting;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Terminal;

/// <summary>
/// Internal host command — runs a headless terminal with PTY and MCP diagnostics.
/// Not meant to be invoked directly; spawned by <see cref="TerminalStartCommand"/>.
/// </summary>
internal sealed class TerminalHostCommand : BaseCommand
{
    private static readonly Option<int> s_widthOption = new("--width") { DefaultValueFactory = _ => 120, Description = "Terminal width" };
    private static readonly Option<int> s_heightOption = new("--height") { DefaultValueFactory = _ => 30, Description = "Terminal height" };
    private static readonly Option<string?> s_cwdOption = new("--cwd") { Description = "Working directory" };
    private static readonly Option<string?> s_recordOption = new("--record") { Description = "Record to asciinema file" };
    private static readonly Option<int?> s_portOption = new("--port") { Description = "Port for WebSocket diagnostics listener" };
    private static readonly Option<string?> s_bindOption = new("--bind") { Description = "Bind address for the WebSocket listener (default: 127.0.0.1, use 0.0.0.0 for containers)" };
    private static readonly Option<int?> s_scrollbackOption = new("--scrollback") { Description = "Rows of scrollback the terminal retains" };
    private static readonly Option<bool> s_recordCaseOption = new("--record-case") { Description = "Record a bounded diagnostic case from construction" };
    private static readonly Option<long?> s_caseMaxBytesOption = new("--case-max-bytes") { Description = "Largest case artifact, in bytes" };
    private static readonly Option<int?> s_caseMaxSecondsOption = new("--case-max-seconds") { Description = "Longest case, in seconds" };
    private static readonly Option<string[]> s_caseAuthorizeOption = new("--case-authorize") { Description = "Case payload authorizations" };
    private static readonly Option<string?> s_caseDirOption = new("--case-dir") { Description = "Owner-only root for the case directory" };
    private static readonly Argument<string[]> s_commandArgument = new("command")
    {
        Description = "Command and arguments to run. Defaults to PowerShell on Windows or bash on Linux/macOS."
    };

    public TerminalHostCommand(
        OutputFormatter formatter,
        ILogger<TerminalHostCommand> logger)
        : base("host", "Run as a terminal host process (internal)", formatter, logger)
    {
        Hidden = true;

        Options.Add(s_widthOption);
        Options.Add(s_heightOption);
        Options.Add(s_cwdOption);
        Options.Add(s_recordOption);
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
        var (config, error) = Config(parseResult);
        if (config is null)
        {
            Formatter.WriteError(error!);
            return 1;
        }

        Logger.LogInformation("Starting terminal host: {Command} ({Width}x{Height})", config.Command, config.Width, config.Height);

        return await TerminalHost.RunAsync(config, cancellationToken);
    }

    /// <summary>
    /// The host's configuration from its parsed arguments (as <see cref="TerminalStartCommand.HostArguments"/> writes
    /// them), or why they are invalid.
    /// </summary>
    internal static (TerminalHostConfig? Config, string? Error) Config(ParseResult parseResult)
    {
        var command = parseResult.GetValue(s_commandArgument) is { Length: > 0 } cmd
            ? cmd
            : TerminalHostPlatformDefaults.GetDefaultCommandLine();
        command = TerminalHostPlatformDefaults.NormalizeCommandLine(command);

        var scrollback = parseResult.GetValue(s_scrollbackOption);
        if (scrollback is { } rows && Diagnostics.TerminalDiagnostics.ScrollbackProblem(rows) is { } invalidScrollback)
            return (null, $"--scrollback: {invalidScrollback}");

        Diagnostics.DiagnosticCaseStartRequest? caseRequest = null;
        if (parseResult.GetValue(s_recordCaseOption))
        {
            var (request, invalid) = Diagnostics.DiagnosticContractNames.ParseCaseStartRequest(
                parseResult.GetValue(s_caseMaxBytesOption), parseResult.GetValue(s_caseMaxSecondsOption),
                parseResult.GetValue(s_caseAuthorizeOption), parseResult.GetValue(s_caseDirOption));
            if (invalid != null)
                return (null, $"{invalid.Problem!.Code}: {invalid.Problem.Message}");

            caseRequest = request;
        }

        return (new TerminalHostConfig
        {
            Command = command[0],
            Arguments = command.Length > 1 ? command[1..] : [],
            Width = parseResult.GetValue(s_widthOption),
            Height = parseResult.GetValue(s_heightOption),
            WorkingDirectory = parseResult.GetValue(s_cwdOption),
            RecordPath = parseResult.GetValue(s_recordOption),
            Port = parseResult.GetValue(s_portOption),
            BindAddress = parseResult.GetValue(s_bindOption),
            Scrollback = scrollback,
            DiagnosticCase = caseRequest
        }, null);
    }
}
