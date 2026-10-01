using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Re-applies a recorded case offline, without the process that wrote it: a detached model rebuilt from the
/// case's configuration applies its recorded events up to a target and is compared with the checkpoint
/// recorded there. It restores from the case's origin (its start, or a recovery checkpoint taken after recording
/// loss) and applies only the events after it. Each run writes its own directory inside the case.
/// </summary>
internal sealed class CaptureCaseReapplyCommand : BaseCommand
{
    private static readonly Argument<string> s_pathArgument = new("path") { Description = "Case directory" };
    private static readonly Option<string> s_toOption = new("--to")
    {
        Description = "Target: a model sequence (12), a case sequence (case:34), or a checkpoint label (label:name, or the bare name; stop is the stop checkpoint, start a live start's checkpoint; a label several checkpoints share is ambiguous, so name one by case:<n>)",
        Required = true,
    };
    private static readonly Option<string?> s_fromOption = new("--from")
    {
        Description = "Origin to restore from: start, or a recovery checkpoint by label (label:name, or the bare name), case sequence (case:34) or ordinal (checkpoint:2); default the earliest origin whose re-applicable interval covers the target (a mark is not-an-origin; a target outside the named origin's interval is beyond-interval)"
    };
    private static readonly Option<string[]> s_faultOption = new("--inject-fault")
    {
        Description = "Inject a declared fault into the reconstructed state before comparing, as kind or kind:target (cell-text:3/5, mode:wraparound, history-row:12, title-stack, command-mark, pending-input, pending-escape, pending-ground-escape, pending-framer; repeatable or comma-separated); the result is labelled"
    };
    private static readonly Option<int?> s_maxDifferencesOption = new("--max-differences")
    {
        Description = "Most differences listed (1-100000; default 1000); every difference is counted"
    };
    private static readonly Option<string[]> s_previewOption = new("--preview")
    {
        Description = "Previews to write (repeatable or comma-separated): text, ansi, svg, html"
    };

    public CaptureCaseReapplyCommand(OutputFormatter formatter, ILogger<CaptureCaseReapplyCommand> logger)
        : base("reapply", "Re-apply a diagnostic case offline to a boundary and compare it with the recorded checkpoint", formatter, logger)
    {
        Arguments.Add(s_pathArgument);
        Options.Add(s_toOption);
        Options.Add(s_fromOption);
        Options.Add(s_faultOption);
        Options.Add(s_maxDifferencesOption);
        Options.Add(s_previewOption);
    }

    protected override Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var json = parseResult.GetValue(RootCommand.JsonOption);
        var (request, invalid) = DiagnosticContractNames.ParseCaseReapplyRequest(parseResult.GetValue(s_pathArgument)!,
            parseResult.GetValue(s_toOption), parseResult.GetValue(s_faultOption), parseResult.GetValue(s_maxDifferencesOption),
            parseResult.GetValue(s_previewOption), parseResult.GetValue(s_fromOption));
        return Task.FromResult(CaseCommandOutput.Write(Formatter, invalid ?? DiagnosticCaseReapplier.Reapply(request!), json));
    }
}
