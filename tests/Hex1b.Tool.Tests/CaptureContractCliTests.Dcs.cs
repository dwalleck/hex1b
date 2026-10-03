using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Hosting;

namespace Hex1b.Tool.Tests;

public partial class CaptureContractCliTests
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(90));
        var release = root.Path + ".release";
        Hex1bTerminal? target = null;
        Hex1bAppWorkloadAdapter? workload = null;
        Task? host = null;
        try
        {
            if (attached)
            {
                (target, workload) = await StartDcsAttachedAsync();
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await workload.WriteRequiredIfGeometry("CLI-DCS-READY" + prefix, 40, 5));
            }
            else
            {
                await WaitForSocketReleaseAsync(cts.Token);
                host = TerminalHost.RunAsync(DcsHostConfig(prefix, suffix, release), cts.Token);
                await WaitForSocketAsync(cts.Token);
                Assert.IsNotNull(await WaitForCliTextAsync("CLI-DCS-READY", cts.Token));
                await WaitForDcsPrefixAsync(release, cts.Token);
            }

            var (startExit, start, startErr) = await RunCliAsync("capture", "case", "start", Pid,
                "--dir", root.Path, "--authorize", "reapplication-data", "--json");
            Assert.AreEqual(0, startExit, startErr + start);
            var checkpoint = JsonDocument.Parse(start).RootElement.GetProperty("checkpoint");
            Assert.AreEqual(("text-state/3", "complete"),
                (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()), start);

            if (attached)
            {
                // Geometry changes while the checkpoint owns the prefix; the suffix is one original application.
                target!.Resize(35, 6);
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await workload!.WriteRequiredIfGeometry(suffix + "CLI-DCS-DONE", 35, 6));
            }
            else
            {
                File.WriteAllText(release, "release");
            }
            Assert.IsNotNull(await WaitForCliTextAsync("CLI-DCS-DONE", cts.Token));
            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr + stop);
            var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
            var startLine = File.ReadLines(Path.Combine(path, "events.jsonl"))
                .Select(line => JsonDocument.Parse(line[(line.IndexOf('\t') + 1)..]).RootElement)
                .First(item => item.TryGetProperty("checkpoint", out var cp) && cp.GetProperty("trigger").GetString() == "start");
            var dcs = startLine.GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("dcs");
            Assert.AreEqual(state, dcs.GetProperty("state").GetString());
            Assert.AreEqual(Convert.ToBase64String(Encoding.UTF8.GetBytes(retained)), dcs.GetProperty("retainedBytes").GetString());
            if (state == "escape")
                Assert.AreEqual("payload", dcs.GetProperty("stateBeforeEscape").GetString());

            var (matchedExit, matched, matchedErr) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--json");
            Assert.AreEqual(0, matchedExit, matchedErr + matched);
            Assert.AreEqual("matched", JsonDocument.Parse(matched).RootElement.GetProperty("comparison").GetString(), matched);
            foreach (var (fault, faultPath) in new[]
            {
                ("dcs-bytes", "pendingInput.dcs.retainedBytes"),
                ("dcs-state", "pendingInput.dcs.state"),
            })
            {
                // The target is the open-DCS start, not the ground state after its suffix was consumed.
                var (exit, json, err) = await RunCliAsync("capture", "case", "reapply", path, "--to", "start", "--inject-fault", fault, "--json");
                Assert.AreEqual(2, exit, err + json);
                var result = JsonDocument.Parse(json).RootElement;
                Assert.AreEqual(("different", true),
                    (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), json);
                CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
                    .Select(difference => difference.GetProperty("path").GetString()).ToList(), faultPath);
                var (_, refused, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--inject-fault", fault, "--json");
                var refusal = JsonDocument.Parse(refused).RootElement;
                Assert.AreEqual("unavailable", refusal.GetProperty("comparison").GetString(), refused);
                StringAssert.StartsWith(refusal.GetProperty("comparisonReason").GetString(), "fault-not-applicable");
            }
        }
        finally
        {
            await cts.CancelAsync();
            if (target is not null)
                await target.DisposeAsync();
            if (host is not null)
                try { await host; } catch (OperationCanceledException) { }
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
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(60));
        var release = root.Path + ".release";
        Hex1bTerminal? target = null;
        Task? host = null;
        try
        {
            if (attached)
            {
                var pair = await StartDcsAttachedAsync();
                target = pair.Terminal;
                Assert.AreEqual(NativeDeliveryOutcome.Applied,
                    await pair.Workload.WriteRequiredIfGeometry("CLI-DCS-READY\u001bPq", 40, 5));
            }
            else
            {
                await WaitForSocketReleaseAsync(cts.Token);
                host = TerminalHost.RunAsync(DcsHostConfig("\u001bPq", "\u0018", release), cts.Token);
                await WaitForSocketAsync(cts.Token);
                Assert.IsNotNull(await WaitForCliTextAsync("CLI-DCS-READY", cts.Token));
                await WaitForDcsPrefixAsync(release, cts.Token);
            }
            var (exit, json, err) = await RunCliAsync("capture", "case", "start", Pid,
                "--dir", root.Path, "--authorize", "reapplication-data", "--json");
            Assert.AreEqual(0, exit, "Ordinary recording must remain available: " + err + json);
            var checkpoint = JsonDocument.Parse(json).RootElement.GetProperty("checkpoint");
            Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), json);
            CollectionAssert.Contains(checkpoint.GetProperty("unsupportedSurfaces").EnumerateArray()
                .Select(surface => surface.GetString()).ToList(), "sixel-continuation");
            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr + stop);
            var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
            var (reapplyExit, result, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "start", "--json");
            Assert.AreEqual(1, reapplyExit, result);
            Assert.AreEqual("no-valid-interval", JsonDocument.Parse(result).RootElement.GetProperty("problem").GetProperty("code").GetString(), result);
        }
        finally
        {
            await cts.CancelAsync();
            if (target is not null)
                await target.DisposeAsync();
            if (host is not null)
                try { await host; } catch (OperationCanceledException) { }
            File.Delete(release);
            File.Delete(release + ".written");
        }
    }

    private static async Task WaitForDcsPrefixAsync(string release, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        while (!File.Exists(release + ".written"))
            await Task.Delay(20, timeout.Token);
        long? previous = null;
        var stable = 0;
        while (stable < 3)
        {
            var (exit, json, error) = await RunCliAsync("capture", "screenshot", Pid, "--json");
            Assert.AreEqual(0, exit, error + json);
            var sequence = JsonDocument.Parse(json).RootElement.GetProperty("identity").GetProperty("modelSequence").GetInt64();
            stable = previous == sequence ? stable + 1 : 0;
            previous = sequence;
            await Task.Delay(100, timeout.Token);
        }
    }

    private static TerminalHostConfig DcsHostConfig(string prefix, string suffix, string release)
    {
        var config = new TerminalHostConfig { Width = 40, Height = 5 };
        if (OperatingSystem.IsWindows())
        {
            static string Decode(string value) => "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" +
                Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
            config.Command = "powershell";
            config.Arguments = ["-NoProfile", "-Command",
                "[Console]::Write(('CLI-DCS'+'-READY')+" + Decode(prefix) + "); [IO.File]::WriteAllText('" +
                (release + ".written").Replace("'", "''", StringComparison.Ordinal) + "','ready'); while (!(Test-Path -LiteralPath '" +
                release.Replace("'", "''", StringComparison.Ordinal) + "')) { Start-Sleep -Milliseconds 50 }; [Console]::Write(" +
                Decode(suffix) + "+('CLI-DCS'+'-DONE')); Start-Sleep 60"];
        }
        else
        {
            static string Octal(string value) => string.Concat(Encoding.UTF8.GetBytes(value).Select(b => "\\" + Convert.ToString(b, 8).PadLeft(3, '0')));
            config.Command = "/bin/sh";
            config.Arguments = ["-c", "x=CLI-DCS; printf '%s" + Octal(prefix) + "' \"${x}-READY\"; printf ready > '" +
                (release + ".written").Replace("'", "'\\''", StringComparison.Ordinal) + "'; while [ ! -f '" +
                release.Replace("'", "'\\''", StringComparison.Ordinal) + "' ]; do sleep 0.05; done; printf '" +
                Octal(suffix) + "'; printf '%s' \"${x}-DONE\"; exec sleep 60"];
        }
        return config;
    }

    private static async Task<(Hex1bTerminal Terminal, Hex1bAppWorkloadAdapter Workload)> StartDcsAttachedAsync()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var workload = new Hex1bAppWorkloadAdapter();
        var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 5)
            .WithDiagnostics(appName: "CliDcsAttached", forceEnable: true).Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        return (terminal, workload);
    }
}
