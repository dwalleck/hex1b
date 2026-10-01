using System.CommandLine;

namespace Hex1b.Tool.Commands.Capture;

/// <summary>
/// Parent command grouping bounded diagnostic case operations (start, stop, status, mark, recover, inspect, reapply).
/// </summary>
internal sealed class CaptureCaseCommand : Command
{
    public CaptureCaseCommand(
        CaptureCaseStartCommand startCommand,
        CaptureCaseStopCommand stopCommand,
        CaptureCaseStatusCommand statusCommand,
        CaptureCaseInspectCommand inspectCommand,
        CaptureCaseMarkCommand markCommand,
        CaptureCaseRecoverCommand recoverCommand,
        CaptureCaseReapplyCommand reapplyCommand)
        : base("case", "Record a bounded diagnostic case: the terminal's original input and correlated evidence, to a local artifact")
    {
        Subcommands.Add(startCommand);
        Subcommands.Add(stopCommand);
        Subcommands.Add(statusCommand);
        Subcommands.Add(markCommand);
        Subcommands.Add(recoverCommand);
        Subcommands.Add(inspectCommand);
        Subcommands.Add(reapplyCommand);
    }
}
