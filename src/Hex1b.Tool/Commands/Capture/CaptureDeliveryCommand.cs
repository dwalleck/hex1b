using System.CommandLine;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Commands.App;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Reads a terminal's native delivery record: every write the terminal made to its presentation,
/// and whether the host accepted, refused, or failed it.
/// </summary>
internal sealed class CaptureDeliveryCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };
    private static readonly Option<long?> s_sinceOption = new("--since") { Description = "Only records after this sequence" };
    private static readonly Option<int?> s_limitOption = new("--limit") { Description = "Most records to return (1-4096; default 4096)" };
    private static readonly Option<string[]> s_authorizeOption = new("--authorize")
    {
        Description = "Opt in to content beyond metadata: native-output adds each record's written bytes (repeatable or comma-separated)"
    };

    public CaptureDeliveryCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureDeliveryCommand> logger)
        : base("delivery", "Show what the terminal's native presentation did with each write (accepted, refused, failed)", formatter, logger)
    {
        _resolver = resolver;
        _client = client;
        Arguments.Add(s_idArgument);
        Options.Add(s_sinceOption);
        Options.Add(s_limitOption);
        Options.Add(s_authorizeOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var json = parseResult.GetValue(RootCommand.JsonOption);
        var authorizations = parseResult.GetValue(s_authorizeOption)?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var (request, invalid) = DiagnosticContractNames.ParseDeliveryRequest(
            parseResult.GetValue(s_sinceOption), parseResult.GetValue(s_limitOption), authorizations);
        if (invalid != null)
            return WriteFailure(invalid, json);

        var resolved = _resolver.Resolve(parseResult.GetValue(s_idArgument)!);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        var result = await _client.CaptureDeliveryAsync(resolved.SocketPath!, request!, cancellationToken);
        if (result.Outcome != DiagnosticOutcome.Captured)
            return WriteFailure(result, json);
        if (json)
        {
            WriteResult(result);
            return 0;
        }

        var totals = result.Totals!;
        Formatter.WriteLine($"Delivery layer: {AppTreeCommand.Safe(result.DeliveryLayer)} (covered since {result.CoverageStartedAt:O})");
        Formatter.WriteLine($"Totals: {totals.Accepted} accepted, {totals.Refused} refused, {totals.Failed} failed, {totals.BytesAccepted} bytes accepted; " +
            $"{result.EvictedRecords} evicted, {result.WritesInProgress} in progress");
        foreach (var record in result.Records)
        {
            var detail = record.Outcome switch
            {
                DiagnosticDeliveryOutcome.Refused => $" reason={AppTreeCommand.Safe(record.Reason)}",
                DiagnosticDeliveryOutcome.Failed => $" error={AppTreeCommand.Safe(record.Error)}",
                _ => "",
            };
            Formatter.WriteLine($"#{record.Sequence} {DiagnosticContractNames.Of(record.Outcome)} {DiagnosticContractNames.Of(record.Source)}" +
                $" {(record.Phase is { } phase ? DiagnosticContractNames.Of(phase) : "-")} {record.Length}B" +
                $" accepted={(record.BytesAccepted?.ToString() ?? "unavailable")} model={record.ModelSequenceAtStart}" +
                $" output={(record.OutputSequence?.ToString() ?? "-")}{detail}");
        }

        foreach (var limitation in result.Limitations)
            Formatter.WriteLine($"Limitation: {AppTreeCommand.Safe(limitation)}");
        return 0;
    }

    private int WriteFailure(DiagnosticDeliveryResult result, bool json)
    {
        if (json)
            WriteResult(result);
        Formatter.WriteError($"{DiagnosticContractNames.Of(result.Outcome)} ({AppTreeCommand.Safe(result.Problem?.Code)}): {AppTreeCommand.Safe(result.Problem?.Message)}");
        return 1;
    }

    private static void WriteResult(DiagnosticDeliveryResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticDeliveryResult))));
}
