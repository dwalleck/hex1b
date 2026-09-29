using System.Buffers;
using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using ModelContextProtocol.Protocol;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// Translates MCP tool arguments to the shared diagnostic capture contract and back. Capture
/// policy, permissions, and outcomes stay in <see cref="TerminalDiagnostics"/>; this adapter
/// only parses invocation text and optionally moves content into a file.
/// </summary>
internal static class CaptureToolSupport
{
    public const string AuthorizeDescription =
        "Optional comma-separated opt-ins beyond the rendered screen: non-screen-metadata (titles, hyperlink targets), editor-text, raw-input. Default content can still contain secrets.";

    public const string FrameAuthorizeDescription =
        "Optional comma-separated opt-ins: editor-text includes the focused editor's full text. Without it, editors report caret, selection, length, and line metadata only.";

    public const string HistoryRowsDescription =
        "Rows of retained terminal-model history to include above the screen (default 0). This is model history, not native scrollback; see capture.history for requested/available/returned rows.";

    public static async Task<CaptureToolResult> CaptureAsync(
        Func<DiagnosticCaptureRequest, CancellationToken, Task<DiagnosticCaptureResult>> capture,
        string format,
        int historyRows,
        string? authorize,
        string? savePath,
        CancellationToken ct,
        string? sessionId = null,
        int? processId = null,
        string? milestone = null,
        long? inputId = null,
        int? milestoneTimeoutMs = null)
    {
        var authorizations = authorize?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (request, invalid) = DiagnosticContractNames.ParseCaptureRequest(format, historyRows, authorizations);
        var (milestoneRequest, invalidMilestone) = DiagnosticContractNames.ParseMilestone(milestone, inputId, milestoneTimeoutMs);
        if (invalid is null && invalidMilestone is not null)
            invalid = TerminalDiagnostics.Problem(DiagnosticOutcome.InvalidRequest, "invalid-milestone", invalidMilestone);
        if (invalid is null && milestoneRequest is not null)
            request = new DiagnosticCaptureRequest
            {
                Format = request!.Format,
                HistoryRows = request.HistoryRows,
                FontFamily = request.FontFamily,
                Authorizations = request.Authorizations,
                Milestone = milestoneRequest,
            };
        var result = invalid ?? await capture(request!, ct);
        var observed = result.Milestone is { } milestoneResult ? " " + DescribeMilestone(milestoneResult) : "";

        if (result.Outcome != DiagnosticOutcome.Captured)
            return Create(result, sessionId, processId,
                $"Capture {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}{observed}");

        var geometry = result.Geometry is { } size ? $"{size.Columns}x{size.Rows}" : "unknown-size";
        var partial = TerminalDiagnostics.DescribePartialContent(result) is { } note ? " " + note : "";
        if (string.IsNullOrEmpty(savePath))
            return Create(result, sessionId, processId, $"Captured {geometry} terminal model as {format.ToLowerInvariant()}.{partial}{observed}");

        var directory = Path.GetDirectoryName(savePath);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(savePath, result.Content, ct);

        return Create(result with { Content = null }, sessionId, processId,
            $"Captured {geometry} terminal model to {savePath}; content is in the file, not capture.content.{partial}", savePath);
    }

    /// <summary>
    /// Serializes with the contract's own serializer so host serializer settings (for example
    /// the MCP SDK's enum converter) cannot change the contract's wire names.
    /// </summary>
    public static JsonElement ToJson(DiagnosticCaptureResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult);

    public static JsonElement ToJson(DiagnosticApplicationFrameResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult);

    public static JsonElement ToJson(DiagnosticDeliveryResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticDeliveryResult);

    public const string DeliveryAuthorizeDescription =
        "Opt in to content beyond metadata (comma-separated): native-output adds each record's written bytes (base64).";

    /// <summary>
    /// Parses the arguments, reads the native delivery record, and wraps the contract result for MCP.
    /// </summary>
    public static async Task<DeliveryToolResult> CaptureDeliveryAsync(
        Func<DiagnosticDeliveryRequest, CancellationToken, Task<DiagnosticDeliveryResult>> capture,
        long? since, int? limit, string? authorize, CancellationToken ct, string? sessionId = null)
    {
        var authorizations = authorize?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (request, invalid) = DiagnosticContractNames.ParseDeliveryRequest(since, limit, authorizations);
        var result = invalid ?? await capture(request!, ct);
        return DeliveryResult(result, sessionId);
    }

    public static DeliveryToolResult DeliveryResult(DiagnosticDeliveryResult result, string? sessionId) => new()
    {
        Success = result.Outcome == DiagnosticOutcome.Captured,
        SessionId = sessionId,
        Message = result.Outcome == DiagnosticOutcome.Captured
            ? $"Read {result.Records.Count} {result.DeliveryLayer} delivery records ({result.Totals!.Accepted} accepted, " +
              $"{result.Totals.Refused} refused, {result.Totals.Failed} failed since coverage started)."
            : $"Native delivery {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}",
        Delivery = ToJson(result),
    };

    /// <summary>
    /// Parses the authorize argument, captures the latest application frame, and wraps the
    /// contract result for MCP.
    /// </summary>
    public static async Task<ApplicationFrameToolResult> CaptureApplicationFrameAsync(
        Func<DiagnosticApplicationFrameRequest, CancellationToken, Task<DiagnosticApplicationFrameResult>> capture,
        string? authorize,
        CancellationToken ct,
        string? sessionId = null,
        int? processId = null,
        string? milestone = null,
        long? inputId = null,
        int? milestoneTimeoutMs = null)
    {
        var authorizations = authorize?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var (request, invalid) = DiagnosticContractNames.ParseApplicationFrameRequest(authorizations);
        var (milestoneRequest, invalidMilestone) = DiagnosticContractNames.ParseMilestone(milestone, inputId, milestoneTimeoutMs);
        if (invalid is null && invalidMilestone is not null)
            invalid = TerminalDiagnostics.FrameProblem(DiagnosticOutcome.InvalidRequest, "invalid-milestone", invalidMilestone);
        if (invalid is null && milestoneRequest is not null)
            request = new DiagnosticApplicationFrameRequest { Authorizations = request!.Authorizations, Milestone = milestoneRequest };
        var result = invalid ?? await capture(request!, ct);
        return FrameResult(result, sessionId, processId);
    }

    public const string MilestoneDescription =
        "Optional: wait (bounded) for a milestone of an input before capturing: input-accepted, input-processed, frame-published, or model-applied. Requires inputId (the lastId a send returned).";

    public const string InputIdDescription = "The input id to wait for; required with milestone.";

    public const string MilestoneTimeoutDescription = "Maximum milestone wait in milliseconds (1-60000; default 5000).";

    private static string DescribeMilestone(DiagnosticMilestoneResult milestone) =>
        $"Milestone {DiagnosticContractNames.Of(milestone.Milestone)} for input {milestone.InputId}: {(milestone.Met ? "met" : "not met")} " +
        $"(accepted {milestone.AcceptedInput}, processed {milestone.ProcessedInput}" +
        (milestone.Frame is { } frame ? $", frame {frame.FrameId}" : "") +
        (milestone.ModelSequence is { } sequence ? $", model sequence {sequence}" : "") + ").";

    /// <summary>
    /// Writes a frame tool result as the tool's text content. Frames nest two JSON levels per
    /// node level, deeper than the MCP host serializer's default limit of 64, so the result is
    /// written here with the contract's depth limit.
    /// </summary>
    public static CallToolResult ToCallToolResult(ApplicationFrameToolResult result)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = FrameJsonMaxDepth }))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("success", result.Success);
            if (result.SessionId is { } sessionId)
                writer.WriteString("sessionId", sessionId);
            if (result.ProcessId is { } processId)
                writer.WriteNumber("processId", processId);
            writer.WriteString("message", result.Message);
            writer.WritePropertyName("applicationFrame");
            result.ApplicationFrame.WriteTo(writer);
            writer.WriteEndObject();
        }

        return new CallToolResult { Content = [new TextContentBlock { Text = Encoding.UTF8.GetString(buffer.WrittenSpan) }] };
    }

    private const int FrameJsonMaxDepth = 1024;

    public static ApplicationFrameToolResult FrameResult(DiagnosticApplicationFrameResult result, string? sessionId, int? processId) => new()
    {
        Success = result.Outcome == DiagnosticOutcome.Captured,
        SessionId = sessionId,
        ProcessId = processId,
        Message = (result.Frame is { } frame
            ? $"Captured application frame {frame.FrameId} ({frame.Columns}x{frame.Rows})."
            : $"Application frame {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}")
            + (result.Milestone is { } milestone ? " " + DescribeMilestone(milestone) : ""),
        ApplicationFrame = ToJson(result),
    };

    public static JsonElement ToJson(DiagnosticCapabilities capabilities) =>
        JsonSerializer.SerializeToElement(capabilities, DiagnosticsJsonContext.Default.DiagnosticCapabilities);

    private static CaptureToolResult Create(
        DiagnosticCaptureResult capture, string? sessionId, int? processId, string message, string? savedPath = null) => new()
    {
        Success = capture.Outcome == DiagnosticOutcome.Captured,
        SessionId = sessionId,
        ProcessId = processId,
        Message = message,
        SavedPath = savedPath,
        Capture = ToJson(capture),
    };
}
