using System.Text;

namespace Hex1b.Tests;

[TestClass]
public class WindowsPtyArgumentTests
{
    [TestMethod]
    [TestCategory("Windows")]
    [DataRow("", "middle", "last", "ARGS[\"\"][middle][last]END")]
    [DataRow("first", "", "last", "ARGS[first][\"\"][last]END")]
    [DataRow("first", "middle", "", "ARGS[first][middle][\"\"]END")]
    public async Task StartAsync_EmptyArgument_PreservesItsPosition(
        string first, string second, string third, string expected)
    {
        if (!OperatingSystem.IsWindows())
            Assert.Inconclusive("Requires native Windows command-line parsing and ConPTY.");

        var directory = Directory.CreateTempSubdirectory("hex1b-arguments-");
        try
        {
            // %1 retains the quotes for an empty argument; a missing argument expands to nothing.
            // Report before waiting for input so process exit cannot race output consumption.
            await File.WriteAllTextAsync(Path.Combine(directory.FullName, "args.cmd"),
                "@echo off\r\necho ARGS[%1][%2][%3]END\r\npause >nul\r\n", TestContext.Current.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(20));
            var ct = cancellation.Token;
            await using var pty = new WindowsProxyPtyHandle();
            await pty.StartAsync("cmd.exe", ["/d", "/c", "args.cmd", first, second, third],
                directory.FullName, [], 100, 24, ct);

            var output = new StringBuilder();
            while (!output.ToString().Contains("END", StringComparison.Ordinal))
            {
                var bytes = await pty.ReadAsync(ct);
                Assert.IsFalse(bytes.IsEmpty, $"Child exited before reporting arguments: {output}");
                output.Append(Encoding.UTF8.GetString(bytes.Span));
            }

            Assert.Contains(expected, output.ToString());
            await pty.WriteAsync("\r"u8.ToArray(), ct);
            Assert.AreEqual(0, await pty.WaitForExitAsync(ct));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }
}
