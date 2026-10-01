using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Takes a recovery checkpoint in a terminal's active case: the model's full text state at its current model
/// sequence, a new origin that re-application restores from for targets after recording loss.
/// </summary>
internal sealed class CaptureCaseRecoverCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };
    private static readonly Option<string?> s_labelOption = new("--label")
    {
        Description = "The checkpoint's label (1-64 printable ASCII characters; default recovery-<ordinal>; labels need not be unique)"
    };

    public CaptureCaseRecoverCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCaseRecoverCommand> logger)
        : base("recover", "Take a recovery checkpoint in the active diagnostic case: a new origin to re-apply from after recording loss", formatter, logger)
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

        return CaseCommandOutput.Write(Formatter, await _client.RecoverCaseAsync(resolved.SocketPath!, parseResult.GetValue(s_labelOption), cancellationToken),
            parseResult.GetValue(RootCommand.JsonOption));
    }
}
