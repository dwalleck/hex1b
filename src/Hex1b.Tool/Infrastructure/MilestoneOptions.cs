using System.CommandLine;
using System.Text.Json;
using Hex1b.Diagnostics;

namespace Hex1b.Tool.Infrastructure;

/// <summary>
/// The capture options that wait for a named processing milestone of an input, shared by the
/// capture commands. Parsing and meaning stay in the shared diagnostics contract.
/// </summary>
internal sealed class MilestoneOptions
{
    public Option<string?> Milestone { get; } = new("--milestone")
    {
        Description = "Wait for a milestone of an input before capturing: input-accepted, input-processed, frame-published, or model-applied",
    };

    public Option<long?> InputId { get; } = new("--input-id")
    {
        Description = "The input id to wait for (the last id a send printed)",
    };

    public Option<int?> Timeout { get; } = new("--milestone-timeout")
    {
        Description = "Maximum milestone wait in milliseconds (1-60000; default 5000)",
    };

    public void AddTo(Command command)
    {
        command.Options.Add(Milestone);
        command.Options.Add(InputId);
        command.Options.Add(Timeout);
    }

    public (DiagnosticMilestoneRequest? Milestone, string? Invalid) Parse(ParseResult parseResult) =>
        DiagnosticContractNames.ParseMilestone(
            parseResult.GetValue(Milestone), parseResult.GetValue(InputId), parseResult.GetValue(Timeout));

    /// <summary>One line describing what a milestone capture observed.</summary>
    public static string Describe(DiagnosticMilestoneResult milestone)
    {
        var frame = milestone.Frame is { } f ? $"; frame {f.FrameId} of {f.ApplicationInstanceId} (processed input {f.ProcessedInput}, wroteOutput={f.WroteOutput})" : "";
        var model = milestone.ModelSequence is { } sequence ? $"; model sequence {sequence}" : "";
        return $"Milestone {DiagnosticContractNames.Of(milestone.Milestone)} for input {milestone.InputId}: {(milestone.Met ? "met" : "not met")}; " +
            $"accepted {milestone.AcceptedInput}, processed {milestone.ProcessedInput}{frame}{model}";
    }

    /// <summary>Prints the ids a send was assigned, as text or JSON; nothing when the target did not assign any.</summary>
    public static void WriteAccepted(OutputFormatter formatter, DiagnosticAcceptedInput? accepted, bool json)
    {
        if (accepted is null)
            return;
        if (json)
            Console.WriteLine(JsonSerializer.Serialize(accepted, DiagnosticsJsonContext.Default.DiagnosticAcceptedInput));
        else
            formatter.WriteLine(accepted.FirstId == accepted.LastId
                ? $"Accepted input {accepted.LastId}: {accepted.Meaning}"
                : $"Accepted inputs {accepted.FirstId}-{accepted.LastId}: {accepted.Meaning}");
    }

    /// <summary>
    /// Prints the accepted ranges of a command's sends: one send as one JSON object, several as
    /// one JSON array, so <c>--json</c> output is always a single document.
    /// </summary>
    public static void WriteAccepted(OutputFormatter formatter, IReadOnlyList<DiagnosticAcceptedInput?> sends, bool json)
    {
        if (sends.Count == 1 || !json)
        {
            foreach (var accepted in sends)
                WriteAccepted(formatter, accepted, json);
            return;
        }

        var tracked = sends.OfType<DiagnosticAcceptedInput>().ToArray();
        if (tracked.Length > 0)
            Console.WriteLine(JsonSerializer.Serialize(tracked, DiagnosticsJsonContext.Default.DiagnosticAcceptedInputArray));
    }
}
