using System.CommandLine;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Describes the diagnostic operations, formats, authorizations, and evidence layers a
/// terminal supports, with their exact limitations.
/// </summary>
internal sealed class CaptureCapabilitiesCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };

    public CaptureCapabilitiesCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCapabilitiesCommand> logger)
        : base("capabilities", "Describe the diagnostic capabilities and limits of a terminal (JSON)", formatter, logger)
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

        var capabilities = await _client.GetCapabilitiesAsync(resolved.SocketPath!, cancellationToken);
        Console.WriteLine(JsonSerializer.Serialize(capabilities,
            DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCapabilities))));
        if (capabilities.Outcome == DiagnosticOutcome.Captured)
            return 0;

        Formatter.WriteError($"{DiagnosticContractNames.Of(capabilities.Outcome)} ({capabilities.Problem?.Code}): {capabilities.Problem?.Message}");
        return 1;
    }
}
