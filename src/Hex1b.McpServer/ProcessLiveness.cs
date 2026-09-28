using System.Diagnostics;

namespace Hex1b.McpServer;

/// <summary>
/// Checks whether a diagnostics target's process is still running.
/// </summary>
internal static class ProcessLiveness
{
    public static bool IsRunning(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }
}
