using System.ComponentModel;
using System.Text.Json;
using Hex1b.Diagnostics;
using ModelContextProtocol.Server;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP tools for bounded diagnostic cases: start, stop, status and mark through a target, and offline inspection
/// and re-application.
/// The tools translate arguments and wrap the shared contract results; every policy stays in the engine.
/// </summary>
[McpServerToolType]
public class DiagnosticCaseTools(TerminalSessionManager sessionManager)
{
    internal const string AuthorizeDescription =
        "Opt in to payloads beyond metadata (comma-separated): reapplication-data (the model's original input bytes, needed to " +
        "re-apply the case), raw-input (sent input text), editor-text (focused editor text in frames), native-output (written bytes).";

    /// <summary>
    /// Starts a bounded diagnostic case on a running target.
    /// </summary>
    [McpServerTool, Description("Starts recording a bounded diagnostic case on a terminal target: the terminal model's events (with its original input bytes when authorized), input, application frames and native delivery, written by the target to an owner-only local directory, as JSON lines each with a checksum. Bounded by size and time; drops newest events under overload and records every loss. A case started after the terminal's first output owns a text-state/1 start checkpoint (with reapplication-data) and re-applies from it, unless the terminal holds a surface the start cannot restore yet (retained history, titles, command marks, pending input, graphics), which it names; recordCase records from the first byte. One case per terminal.")]
    public async Task<CaseToolResult> StartDiagnosticCase(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("Largest artifact in bytes (1 MiB to 1 GiB; default 64 MiB).")] long? maxBytes = null,
        [Description("Longest recording in seconds (1 to 86400; default 600).")] int? maxSeconds = null,
        [Description(AuthorizeDescription)] string? authorize = null,
        [Description("Owner-only root directory for the case (default: the per-user diagnostic case directory).")] string? directory = null,
        CancellationToken ct = default)
    {
        var (request, invalid) = ParseStart(maxBytes, maxSeconds, authorize, directory);
        if (invalid != null)
            return Result(invalid, sessionId, "start");
        var target = sessionManager.GetTarget(sessionId);
        return Result(target == null ? SessionNotFound(sessionId) : await target.StartCaseAsync(request!, ct), sessionId, "started");
    }

    /// <summary>
    /// Stops a target's active case.
    /// </summary>
    [McpServerTool, Description("Stops the terminal target's active diagnostic case and returns once its artifact is finished (at most 10 s of draining; anything unwritten then is recorded as missing). Returns the case's final state, stop reason, bytes written and per-stream counts.")]
    public async Task<CaseToolResult> StopDiagnosticCase(
        [Description("Session ID of the terminal target")] string sessionId,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        return Result(target == null ? SessionNotFound(sessionId) : await target.StopCaseAsync(ct), sessionId, "stopped");
    }

    /// <summary>
    /// Reports a target's active case.
    /// </summary>
    [McpServerTool, Description("Reports the terminal target's active diagnostic case: state, bounds, elapsed time, bytes written, checkpoint, and per-stream offered, written and dropped counts. Returns 'unavailable' with code 'no-active-case' when none is recording.")]
    public async Task<CaseToolResult> GetDiagnosticCaseStatus(
        [Description("Session ID of the terminal target")] string sessionId,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        return Result(target == null ? SessionNotFound(sessionId) : await target.GetCaseStatusAsync(ct), sessionId, "is");
    }

    /// <summary>
    /// Inspects a case artifact offline.
    /// </summary>
    [McpServerTool, Description("Reads a diagnostic case artifact from disk without the process that wrote it. Verifies every line's checksum and reports whether the case is complete, interrupted (no completion record) or truncated (with the verified prefix), per-stream coverage and missing ranges, the re-applicable model interval, and a page of events with payloads as recorded.")]
    public CaseInspectionToolResult InspectDiagnosticCase(
        [Description("The case directory (the 'path' a start or stop returned).")] string path,
        [Description("Only events after this case sequence.")] long? since = null,
        [Description("Most events to return (1-4096; default none, coverage only).")] int? limit = null)
    {
        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Since = since, Limit = limit });
        return new CaseInspectionToolResult
        {
            Success = inspection.Outcome == DiagnosticOutcome.Captured,
            Message = inspection.Outcome == DiagnosticOutcome.Captured
                ? $"Case {inspection.Manifest!.CaseId} is {DiagnosticContractNames.Of(inspection.CompletionState!.Value)}; {inspection.Events.Count} events on this page."
                : $"Inspect {DiagnosticContractNames.Of(inspection.Outcome)} ({inspection.Problem?.Code}): {inspection.Problem?.Message}",
            Inspection = JsonSerializer.SerializeToElement(inspection, DiagnosticsJsonContext.Default.DiagnosticCaseInspection),
        };
    }

    /// <summary>
    /// Marks a boundary in a target's active case.
    /// </summary>
    [McpServerTool, Description("Marks a boundary in the terminal target's active diagnostic case: a checkpoint of the terminal model's full text state at its current model sequence (with the reapplication-data authorization; otherwise the boundary only), which reapply_diagnostic_case can target and compare. Never waits for the case's writer; at most 64 marks can await it, and a mark beyond is refused 'busy'; a mark whose state would pass the 256 MiB pending-state budget records the boundary only.")]
    public async Task<CaseMarkToolResult> MarkDiagnosticCase(
        [Description("Session ID of the terminal target")] string sessionId,
        [Description("The checkpoint's label: 1-64 printable ASCII characters (default mark-<ordinal>); labels need not be unique.")] string? label = null,
        CancellationToken ct = default)
    {
        var target = sessionManager.GetTarget(sessionId);
        var result = target == null
            ? new DiagnosticCaseMarkResult
            {
                Outcome = DiagnosticOutcome.Unavailable,
                Problem = new DiagnosticProblem { Code = "session-not-found", Message = $"Session '{sessionId}' not found." },
            }
            : await target.MarkCaseAsync(label, ct);
        return new CaseMarkToolResult
        {
            Success = result.Outcome == DiagnosticOutcome.Captured,
            SessionId = sessionId,
            Message = result.Outcome == DiagnosticOutcome.Captured
                ? $"Case {result.CaseId} marked '{result.Label}' at model sequence {result.ModelSequence}" +
                  (result.StateRecorded == true ? "." : $" (boundary only: {result.StateReason}).")
                : $"Mark {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}",
            Mark = JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaseMarkResult),
        };
    }

    /// <summary>
    /// Re-applies a case artifact offline and compares it with a recorded checkpoint.
    /// </summary>
    [McpServerTool, Description("Re-applies a recorded diagnostic case offline, without the process that wrote it: a detached terminal model rebuilt from the case's recorded configuration applies the recorded events up to a target boundary (each output chunk through the raw output path, each resize, each synchronized-update timeout on a virtual clock), then compares its full text state with the checkpoint recorded there: 'matched', 'different' (typed differences by path, counted per surface), or 'unavailable' (no checkpoint, no state, or graphics). Refuses a target past the case's re-applicable interval, an old or unknown format, and a case directory that is not owner-only. Writes only its own run directory inside the case: result.json, reapplied.json, recorded.json and any previews.")]
    public CaseReapplyToolResult ReapplyDiagnosticCase(
        [Description("The case directory (the 'path' a start or stop returned).")] string path,
        [Description("Target: a model sequence (12), a case sequence (case:34), or a checkpoint label (label:name, or the bare name; 'stop' is the stop checkpoint, 'start' a live start's checkpoint; a label several checkpoints share is ambiguous, so name one by case sequence).")] string to,
        [Description("Declared faults to inject into the reconstructed state before comparing (comma-separated), as kind or kind:target: cell-text[:row/column], cell-style[:row/column], cursor, mode[:name], title, charset, tab-stop, pending-input, history-row[:index], history-rows, pending-wrap, last-printed, rendition, margins, saved-cursor, pending-grapheme, activity, synchronized-update. The result is labelled faultInjected.")] string? injectFault = null,
        [Description("Most differences listed (1-100000; default 1000). Every difference is counted.")] int? maxDifferences = null,
        [Description("Previews to write (comma-separated): text, ansi, svg, html.")] string? preview = null)
    {
        var (request, invalid) = DiagnosticContractNames.ParseCaseReapplyRequest(path, to,
            injectFault is null ? null : [injectFault], maxDifferences, preview is null ? null : [preview]);
        var result = invalid ?? DiagnosticCaseReapplier.Reapply(request!);
        return new CaseReapplyToolResult
        {
            Success = result.Outcome == DiagnosticOutcome.Captured,
            Message = result.Outcome == DiagnosticOutcome.Captured
                ? $"Re-applied to model sequence {result.Target!.ModelSequence}: {result.Comparison}" +
                  (result.ComparisonReason is { } reason ? $" ({reason})" : "") +
                  (result.Differences is { Total: > 0 } differences ? $"; {differences.Total} differences" : "") +
                  (result.FaultInjected ? "; fault injected" : "") + $". Run: {result.RunPath}."
                : $"Reapply {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}",
            Reapplication = JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaseReapplyResult),
        };
    }

    internal static (DiagnosticCaseStartRequest? Request, DiagnosticCaseResult? Invalid) ParseStart(long? maxBytes, int? maxSeconds,
        string? authorize, string? directory) =>
        DiagnosticContractNames.ParseCaseStartRequest(maxBytes, maxSeconds,
            authorize is null ? null : [authorize], directory);

    internal static JsonElement ToJson(DiagnosticCaseResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaseResult);

    private static DiagnosticCaseResult SessionNotFound(string sessionId) =>
        TerminalDiagnostics.CaseProblem(DiagnosticOutcome.Unavailable, "session-not-found", $"Session '{sessionId}' not found.");

    private static CaseToolResult Result(DiagnosticCaseResult result, string? sessionId, string verb) => new()
    {
        Success = result.Outcome == DiagnosticOutcome.Captured,
        SessionId = sessionId,
        Message = result.Outcome == DiagnosticOutcome.Captured
            ? $"Case {result.CaseId} {verb}: {DiagnosticContractNames.Of(result.State!.Value)}" +
              (result.StopReason is { } reason ? $" ({DiagnosticContractNames.Of(reason)})" : "") + $" at {result.Path}."
            : $"Case {DiagnosticContractNames.Of(result.Outcome)} ({result.Problem?.Code}): {result.Problem?.Message}",
        Case = ToJson(result),
    };
}
