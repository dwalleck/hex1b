using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Reads a case artifact offline, without the process that wrote it: completion, coverage, re-applicable
/// intervals, and a page of verified events.
/// </summary>
internal sealed class CaptureCaseInspectCommand : BaseCommand
{
    private static readonly Argument<string> s_pathArgument = new("path") { Description = "Case directory" };
    private static readonly Option<long?> s_sinceOption = new("--since") { Description = "Only events after this case sequence" };
    private static readonly Option<int?> s_limitOption = new("--limit") { Description = "Most events to return (1-4096; default none, coverage only)" };

    public CaptureCaseInspectCommand(OutputFormatter formatter, ILogger<CaptureCaseInspectCommand> logger)
        : base("inspect", "Inspect a diagnostic case artifact offline", formatter, logger)
    {
        Arguments.Add(s_pathArgument);
        Options.Add(s_sinceOption);
        Options.Add(s_limitOption);
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest
        {
            Path = parseResult.GetValue(s_pathArgument)!,
            Since = parseResult.GetValue(s_sinceOption),
            Limit = parseResult.GetValue(s_limitOption),
        });
        return Task.FromResult(CaseCommandOutput.Write(Formatter, inspection, parseResult.GetValue(RootCommand.JsonOption)));
    }
}
