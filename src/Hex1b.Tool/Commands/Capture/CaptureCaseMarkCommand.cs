using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Marks a boundary in a terminal's active case: a checkpoint of the model's state at its current model
/// sequence (the state only with reapplication-data), which a re-application can target and compare.
/// </summary>
internal sealed class CaptureCaseMarkCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };
    private static readonly Option<string?> s_labelOption = new("--label")
    {
        Description = "The checkpoint's label (1-64 letters, digits, '.', '_', ':' or '-'; not 'stop'; default mark-<ordinal>)"
    };

    public CaptureCaseMarkCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCaseMarkCommand> logger)
        : base("mark", "Mark a boundary (a checkpoint) in the active diagnostic case", formatter, logger)
    {
        _resolver = resolver;
        _client = client;
        Arguments.Add(s_idArgument);
        Options.Add(s_labelOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var resolved = _resolver.Resolve(parseResult.GetValue(s_idArgument)!);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        return CaseCommandOutput.Write(Formatter, await _client.MarkCaseAsync(resolved.SocketPath!, parseResult.GetValue(s_labelOption), cancellationToken),
            parseResult.GetValue(RootCommand.JsonOption));
    }
}
