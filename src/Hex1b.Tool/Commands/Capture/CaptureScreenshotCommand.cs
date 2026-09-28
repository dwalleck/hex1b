using System.CommandLine;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Captures a terminal screen screenshot in various formats through the shared diagnostic
/// capture contract.
/// </summary>
internal sealed class CaptureScreenshotCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };
    private static readonly Option<string> s_formatOption = new("--format") { DefaultValueFactory = _ => "text", Description = "Output format: text, ansi, svg, html, or png" };
    private static readonly Option<string?> s_outputOption = new("--output") { Description = "Save to file instead of stdout (required for png)" };
    private static readonly Option<string?> s_waitOption = new("--wait") { Description = "Wait for text to appear before capturing" };
    private static readonly Option<int> s_timeoutOption = new("--timeout") { DefaultValueFactory = _ => 30, Description = "Timeout in seconds for --wait" };
    private static readonly Option<int> s_scrollbackOption = new("--scrollback") { DefaultValueFactory = _ => 0, Description = "Rows of retained terminal-model history to include (not native scrollback)" };
    private static readonly Option<string[]> s_authorizeOption = new("--authorize")
    {
        Description = "Opt in to content beyond the rendered screen: non-screen-metadata, editor-text, or raw-input (repeatable or comma-separated)"
    };

    public CaptureScreenshotCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<CaptureScreenshotCommand> logger)
        : base("screenshot", "Capture a terminal screen screenshot", formatter, logger)
    {
        _resolver = resolver;
        _client = client;

        Arguments.Add(s_idArgument);
        Options.Add(s_formatOption);
        Options.Add(s_outputOption);
        Options.Add(s_waitOption);
        Options.Add(s_timeoutOption);
        Options.Add(s_scrollbackOption);
        Options.Add(s_authorizeOption);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var id = parseResult.GetValue(s_idArgument)!;
        var format = parseResult.GetValue(s_formatOption)!;
        var outputPath = parseResult.GetValue(s_outputOption);
        var waitText = parseResult.GetValue(s_waitOption);
        var timeout = parseResult.GetValue(s_timeoutOption);
        var scrollback = parseResult.GetValue(s_scrollbackOption);
        var authorizations = parseResult.GetValue(s_authorizeOption)?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var json = parseResult.GetValue(RootCommand.JsonOption);

        var isPng = string.Equals(format, "png", StringComparison.OrdinalIgnoreCase);

        if (isPng && outputPath == null)
        {
            Formatter.WriteError("--output is required when using --format png");
            return 1;
        }

        // PNG is rasterized locally from an SVG capture rendered with the font embedded in this tool.
        var (request, invalid) = DiagnosticContractNames.ParseCaptureRequest(
            isPng ? "svg" : format, scrollback, authorizations, isPng ? SvgToPngConverter.EmbeddedFontFamily : null);
        if (invalid != null)
            return WriteFailure(invalid, json);

        var resolved = _resolver.Resolve(id);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        if (waitText != null)
        {
            var waitFailure = await WaitForTextAsync(resolved.SocketPath!, waitText, timeout, cancellationToken);
            if (waitFailure != null)
                return WriteFailure(waitFailure, json);
        }

        var result = await _client.CaptureAsync(resolved.SocketPath!, request!, cancellationToken);
        if (result.Outcome != DiagnosticOutcome.Captured)
            return WriteFailure(result, json);

        if (isPng)
        {
            var pngBytes = SvgToPngConverter.Convert(result.Content!);
            await File.WriteAllBytesAsync(outputPath!, pngBytes, cancellationToken);
        }
        else if (outputPath != null)
        {
            await File.WriteAllTextAsync(outputPath, result.Content, cancellationToken);
        }

        if (outputPath != null)
        {
            // Saved content is not repeated on stdout, matching the MCP tools. PNG is rasterized
            // here from the SVG capture that the result describes.
            var note = isPng ? $"Saved PNG rasterized from the SVG capture to {outputPath}" : $"Saved to {outputPath}";
            if (json)
            {
                WriteResult(result with { Content = null });
                Formatter.WriteError(note);
            }
            else
            {
                Formatter.WriteLine(note);
            }
        }
        else if (json)
        {
            WriteResult(result);
        }
        else
        {
            Console.Write(result.Content);
        }

        return 0;
    }

    private async Task<DiagnosticCaptureResult?> WaitForTextAsync(string socketPath, string waitText, int timeoutSeconds, CancellationToken cancellationToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));
        var textRequest = new DiagnosticCaptureRequest { Format = DiagnosticCaptureFormat.Text };
        try
        {
            while (true)
            {
                var text = await _client.CaptureAsync(socketPath, textRequest, timeoutCts.Token);
                if (text.Outcome != DiagnosticOutcome.Captured)
                    return text;
                if (text.Content!.Contains(waitText, StringComparison.Ordinal))
                    return null;

                await Task.Delay(250, timeoutCts.Token);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return TerminalDiagnostics.Problem(DiagnosticOutcome.Failed, "timeout",
                $"Text '{waitText}' did not appear within {timeoutSeconds}s.");
        }
    }

    private int WriteFailure(DiagnosticCaptureResult result, bool json)
    {
        if (json)
            WriteResult(result);
        Formatter.WriteError($"{DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}");
        return 1;
    }

    private static void WriteResult(DiagnosticCaptureResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticCaptureResult))));
}
