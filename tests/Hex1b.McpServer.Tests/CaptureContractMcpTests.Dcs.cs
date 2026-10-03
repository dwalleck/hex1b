using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using ModelContextProtocol.Client;

namespace Hex1b.McpServer.Tests;

public partial class CaptureContractMcpTests
{
    [TestMethod]
    [DataRow(false, "\u001bP$qm\u001b", "\\", "escape", "$qm")]
    [DataRow(true, "\u001bP$qm\u001b", "\\", "escape", "$qm")]
    [DataRow(false, "\u001bP1;2zignored", "\u001b\\", "payload", "1;2zignored")]
    [DataRow(true, "\u001bP1;2zignored", "\u001b\\", "payload", "1;2zignored")]
    public async Task Case_DcsLiveStart_LocalAndAttachedRestoresContinuation(
        bool attached, string prefix, string suffix, string state, string retained)
    {
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var release = root.Path + ".release";
        Hex1bTerminal? terminal = null;
        Hex1bAppWorkloadAdapter? workload = null;
        string? sessionId = null;
        try
        {
            if (attached)
            {
                (terminal, workload) = await StartDcsAttachedAsync();
                sessionId = await ConnectAttachedAsync(client);
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await workload.WriteRequiredIfGeometry("MCP-DCS-READY" + prefix, 40, 5));
            }
            else
            {
                sessionId = await StartLocalSessionAsync(client);
                var sent = await CallAsync(client, "send_terminal_input", new()
                {
                    ["sessionId"] = sessionId,
                    ["text"] = DcsShellCommand(prefix, suffix, release) + "\r",
                });
                Assert.IsTrue(sent.GetProperty("success").GetBoolean(), sent.ToString());
                await WaitForDcsTextAsync(client, sessionId, "MCP-DCS-READY");
                await WaitForDcsPrefixAsync(client, sessionId, release);
            }

            var start = await CallAsync(client, "start_diagnostic_case", new()
            {
                ["sessionId"] = sessionId,
                ["directory"] = root.Path,
                ["authorize"] = "reapplication-data",
            });
            Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
            var checkpoint = start.GetProperty("case").GetProperty("checkpoint");
            Assert.AreEqual(("text-state/3", "complete"),
                (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()), start.ToString());
            if (attached)
            {
                terminal!.Resize(35, 6);
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await workload!.WriteRequiredIfGeometry(suffix + "MCP-DCS-DONE", 35, 6));
            }
            else
            {
                File.WriteAllText(release, "release");
            }
            await WaitForDcsTextAsync(client, sessionId, "MCP-DCS-DONE");
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(stop.GetProperty("success").GetBoolean(), stop.ToString());
            var path = stop.GetProperty("case").GetProperty("path").GetString()!;
            var startLine = File.ReadLines(Path.Combine(path, "events.jsonl"))
                .Select(line => JsonDocument.Parse(line[(line.IndexOf('\t') + 1)..]).RootElement)
                .First(item => item.TryGetProperty("checkpoint", out var cp) && cp.GetProperty("trigger").GetString() == "start");
            var dcs = startLine.GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("dcs");
            Assert.AreEqual(state, dcs.GetProperty("state").GetString());
            Assert.AreEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes(retained)), dcs.GetProperty("retainedBytes").GetString());
            if (state == "escape")
                Assert.AreEqual("payload", dcs.GetProperty("stateBeforeEscape").GetString());
            var matched = (await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "stop" }))
                .GetProperty("reapplication");
            Assert.AreEqual("matched", matched.GetProperty("comparison").GetString(), matched.ToString());
            foreach (var (fault, faultPath) in new[]
            {
                ("dcs-bytes", "pendingInput.dcs.retainedBytes"),
                ("dcs-state", "pendingInput.dcs.state"),
            })
            {
                var result = (await CallAsync(client, "reapply_diagnostic_case", new()
                {
                    ["path"] = path,
                    ["to"] = "start",
                    ["injectFault"] = fault,
                })).GetProperty("reapplication");
                Assert.AreEqual(("different", true),
                    (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), result.ToString());
                CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
                    .Select(difference => difference.GetProperty("path").GetString()).ToList(), faultPath);
                var refusal = (await CallAsync(client, "reapply_diagnostic_case", new()
                {
                    ["path"] = path,
                    ["to"] = "stop",
                    ["injectFault"] = fault,
                })).GetProperty("reapplication");
                Assert.AreEqual("unavailable", refusal.GetProperty("comparison").GetString(), refusal.ToString());
                StringAssert.StartsWith(refusal.GetProperty("comparisonReason").GetString(), "fault-not-applicable");
            }
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (sessionId is not null)
                await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
            if (terminal is not null)
                await terminal.DisposeAsync();
            File.Delete(release);
            File.Delete(release + ".written");
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Case_SixelLiveStart_LocalAndAttachedNamesRefusal(bool attached)
    {
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var release = root.Path + ".release";
        Hex1bTerminal? terminal = null;
        string? sessionId = null;
        try
        {
            if (attached)
            {
                var pair = await StartDcsAttachedAsync();
                terminal = pair.Terminal;
                sessionId = await ConnectAttachedAsync(client);
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await pair.Workload.WriteRequiredIfGeometry("MCP-DCS-READY\u001bPq", 40, 5));
            }
            else
            {
                sessionId = await StartLocalSessionAsync(client);
                await CallAsync(client, "send_terminal_input", new()
                {
                    ["sessionId"] = sessionId,
                    ["text"] = DcsShellCommand("\u001bPq", "\u0018", release) + "\r",
                });
                await WaitForDcsTextAsync(client, sessionId, "MCP-DCS-READY");
                await WaitForDcsPrefixAsync(client, sessionId, release);
            }
            var start = await CallAsync(client, "start_diagnostic_case", new()
            {
                ["sessionId"] = sessionId,
                ["directory"] = root.Path,
                ["authorize"] = "reapplication-data",
            });
            Assert.IsTrue(start.GetProperty("success").GetBoolean(), "Ordinary recording must remain available: " + start);
            var checkpoint = start.GetProperty("case").GetProperty("checkpoint");
            Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), start.ToString());
            CollectionAssert.Contains(checkpoint.GetProperty("unsupportedSurfaces").EnumerateArray()
                .Select(surface => surface.GetString()).ToList(), "sixel-continuation");
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            var result = (await CallAsync(client, "reapply_diagnostic_case", new()
            {
                ["path"] = stop.GetProperty("case").GetProperty("path").GetString(),
                ["to"] = "start",
            })).GetProperty("reapplication");
            Assert.AreEqual("no-valid-interval", result.GetProperty("problem").GetProperty("code").GetString(), result.ToString());
        }
        finally
        {
            File.WriteAllText(release, "release");
            if (sessionId is not null)
                await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
            if (terminal is not null)
                await terminal.DisposeAsync();
            File.Delete(release);
            File.Delete(release + ".written");
        }
    }

    private async Task WaitForDcsPrefixAsync(McpClient client, string sessionId, string release)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(release + ".written"))
            await Task.Delay(20, timeout.Token);
        long? previous = null;
        var stable = 0;
        while (stable < 3)
        {
            var capture = (await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = sessionId }))
                .GetProperty("capture");
            var sequence = capture.GetProperty("identity").GetProperty("modelSequence").GetInt64();
            stable = previous == sequence ? stable + 1 : 0;
            previous = sequence;
            await Task.Delay(100, timeout.Token);
        }
    }

    private static string DcsShellCommand(string prefix, string suffix, string release)
    {
        if (OperatingSystem.IsWindows())
        {
            // send_terminal_input decodes backslash escapes; PowerShell accepts forward-slash file paths.
            release = release.Replace('\\', '/');
            static string Decode(string value) => "[Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "')";
            // Flush the raw control bytes before publishing the prefix-written marker.
            return "[Console]::Write(('MCP-DCS'+'-READY')); $o=[Console]::OpenStandardOutput(); $b=" + Decode(prefix) +
                "; $o.Write($b,0,$b.Length); $o.Flush(); [IO.File]::WriteAllText('" +
                (release + ".written").Replace("'", "''", StringComparison.Ordinal) + "','ready'); while (!(Test-Path -LiteralPath '" +
                release.Replace("'", "''", StringComparison.Ordinal) + "')) { Start-Sleep -Milliseconds 50 }; $b=" + Decode(suffix) +
                "; $o.Write($b,0,$b.Length); $o.Flush(); [Console]::Write(('MCP-DCS'+'-DONE'))";
        }
        static string Octal(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
        return "x=MCP-DCS; printf '%s" + Octal(prefix) + "' \"${x}-READY\"; printf ready > '" +
            (release + ".written").Replace("'", "'\\''", StringComparison.Ordinal) + "'; while [ ! -f '" +
            release.Replace("'", "'\\''", StringComparison.Ordinal) + "' ]; do sleep 0.05; done; printf '" + Octal(suffix) +
            "'; printf '%s' \"${x}-DONE\"";
    }

    private async Task WaitForDcsTextAsync(McpClient client, string sessionId, string text)
    {
        var wait = await CallAsync(client, "wait_for_terminal_text", new()
        {
            ["sessionId"] = sessionId,
            ["text"] = text,
            ["timeoutSeconds"] = 30,
        });
        Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());
    }

    private static async Task<(Hex1bTerminal Terminal, Hex1bAppWorkloadAdapter Workload)> StartDcsAttachedAsync()
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50, TestContext.Current.CancellationToken);
        var workload = new Hex1bAppWorkloadAdapter();
        var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 5)
            .WithDiagnostics(appName: "McpDcsAttached", forceEnable: true).Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        var probe = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (await probe.TryProbeAsync(socketPath, TestContext.Current.CancellationToken) is { Success: true })
                return (terminal, workload);
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
        await terminal.DisposeAsync();
        Assert.Fail("diagnostics socket never became available");
        return default;
    }
}
