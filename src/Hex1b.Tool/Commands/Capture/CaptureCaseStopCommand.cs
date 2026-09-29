using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Stops a terminal's active case, returning once its artifact is finished (or its drain bound passed).
/// </summary>
internal sealed class CaptureCaseStopCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };

    public CaptureCaseStopCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCaseStopCommand> logger)
        : base("stop", "Stop the active diagnostic case and finish its artifact", formatter, logger)
    {
        _resolver = resolver;
        _client = client;
        Arguments.Add(s_idArgument);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resolved = _resolver.Resolve(parseResult.GetValue(s_idArgument)!);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        return CaseCommandOutput.Write(Formatter, await _client.StopCaseAsync(resolved.SocketPath!, cancellationToken),
            parseResult.GetValue(RootCommand.JsonOption), "stopped");
    }
}
