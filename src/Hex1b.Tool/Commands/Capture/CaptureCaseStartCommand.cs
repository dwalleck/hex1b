using System.CommandLine;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Starts a bounded diagnostic case on a running terminal. The target validates the request and writes
/// the artifact to its own owner-only directory.
/// </summary>
internal sealed class CaptureCaseStartCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };

    internal static readonly Option<long?> MaxBytesOption = new("--max-bytes") { Description = "Largest artifact, in bytes (1 MiB-1 GiB; default 64 MiB)" };
    internal static readonly Option<int?> MaxSecondsOption = new("--max-seconds") { Description = "Longest recording, in seconds (1-86400; default 600)" };
    internal static readonly Option<string[]> AuthorizeOption = new("--authorize")
    {
        Description = "Opt in to payloads beyond metadata (repeatable or comma-separated): reapplication-data (the model's original input bytes), " +
            "raw-input, editor-text, native-output"
    };
    internal static readonly Option<string?> DirOption = new("--dir") { Description = "Owner-only root for the case directory (default: the per-user diagnostic case directory)" };

    public CaptureCaseStartCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureCaseStartCommand> logger)
        : base("start", "Start recording a bounded diagnostic case", formatter, logger)
    {
        _resolver = resolver;
        _client = client;
        Arguments.Add(s_idArgument);
        Options.Add(MaxBytesOption);
        Options.Add(MaxSecondsOption);
        Options.Add(AuthorizeOption);
        Options.Add(DirOption);
    }

    /// <summary>Builds the start request from the shared case options, or the reason it is invalid.</summary>
    internal static (DiagnosticCaseStartRequest? Request, DiagnosticCaseResult? Invalid) ParseRequest(ParseResult parseResult) =>
        DiagnosticContractNames.ParseCaseStartRequest(
            parseResult.GetValue(MaxBytesOption),
            parseResult.GetValue(MaxSecondsOption),
            parseResult.GetValue(AuthorizeOption)?
                .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)),
            parseResult.GetValue(DirOption));

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var json = parseResult.GetValue(RootCommand.JsonOption);
        var (request, invalid) = ParseRequest(parseResult);
        if (invalid != null)
            return CaseCommandOutput.Write(Formatter, invalid, json, "start");

        var resolved = _resolver.Resolve(parseResult.GetValue(s_idArgument)!);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        return CaseCommandOutput.Write(Formatter, await _client.StartCaseAsync(resolved.SocketPath!, request!, cancellationToken), json, "started");
    }
}
