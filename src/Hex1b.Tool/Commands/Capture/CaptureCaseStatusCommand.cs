using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Reports a terminal's active case: state, bounds, progress and per-stream counts.
/// </summary>
internal sealed class CaptureCaseStatusCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };

    public CaptureCaseStatusCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCaseStatusCommand> logger)
        : base("status", "Show the active diagnostic case's progress", formatter, logger)
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

        return CaseCommandOutput.Write(Formatter, await _client.GetCaseStatusAsync(resolved.SocketPath!, cancellationToken),
            parseResult.GetValue(RootCommand.JsonOption), "is");
    }
}
