using System.Text.Json;
using Hex1b.Automation;
using Hex1b.Diagnostics;
using Hex1b.Theming;
using Hex1b.Tokens;
using Hex1b.Tool.Hosting;
using Hex1b.Widgets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tool.Tests;

/// <summary>
/// Drives the real CLI entrypoint (host, dependency injection, command parsing) against real
/// diagnostics targets: a Hex1b application with diagnostics (attached) and a PTY terminal run by
/// the CLI's own terminal host (local). Returned content is validated by applying it to a model.
/// </summary>
[DoNotParallelize]
[TestClass]
public class CaptureContractCliTests
{
    private static readonly Hex1bColor Styled = Hex1bColor.FromRgb(200, 30, 40);

    [TestMethod]
    public async Task Screenshot_AttachedApplication_ReturnsStyledAnsiWithContractMetadata()
    {
        await using var target = await StartAttachedAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync(
            "capture", "screenshot", Pid, "--format", "ansi", "--scrollback", "4", "--json");

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        var root = json.RootElement;
        Assert.AreEqual("captured", root.GetProperty("outcome").GetString());
        Assert.AreEqual("ansi", root.GetProperty("format").GetString());

        using var model = ApplyToModel(root.GetProperty("content").GetString()!, 40, 9);
        var cell = FindCell(model, "STYLED");
        Assert.AreEqual((200, 30, 40), (cell.Foreground!.Value.R, cell.Foreground.Value.G, cell.Foreground.Value.B),
            "ANSI capture lost the application's foreground color");

        var identity = root.GetProperty("identity");
        Assert.AreEqual(Environment.ProcessId, identity.GetProperty("processId").GetInt32());
        Assert.AreEqual("terminal-model", identity.GetProperty("sourceLayer").GetString());
        Assert.AreEqual("CliAttached", identity.GetProperty("applicationName").GetString());
        Assert.AreEqual("hex1b-application", identity.GetProperty("configuration").GetProperty("workload").GetString());
        Assert.AreEqual(target.DiagnosticSessionId.ToString("N"), identity.GetProperty("sessionId").GetString());
        Assert.IsFalse(string.IsNullOrEmpty(identity.GetProperty("hex1bVersion").GetString()));

        var history = root.GetProperty("history");
        Assert.AreEqual(4, history.GetProperty("requestedRows").GetInt32());
        Assert.AreEqual(50, history.GetProperty("retentionCapacity").GetInt32());

        AssertUnavailable(root, "identity.applicationFrame", "not yet published");
        Assert.IsTrue(identity.GetProperty("modelSequence").GetInt64() > 0, "modelSequence missing from the CLI result");
        AssertCoverage(root, "hyperlink-targets", "excluded");
        AssertCoverage(root, "window-title", "excluded");
        AssertCoverage(root, "editor-text", "excluded");
        AssertCoverage(root, "raw-input", "excluded");
    }

    [TestMethod]
    public async Task Screenshot_LocalHostedPty_ReturnsStyledAnsiFromChildProcess()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = new TerminalHostConfig { Width = 40, Height = 6 };
        if (OperatingSystem.IsWindows())
        {
            config.Command = "powershell";
            config.Arguments = ["-NoProfile", "-Command", "Write-Host LOCALGREEN -ForegroundColor Green; Start-Sleep 60"];
        }
        else
        {
            config.Command = "/bin/sh";
            // Stay alive like a real hosted shell; a child that exits at once can race its PTY output.
            config.Arguments = ["-c", "printf '\\033[1;32mLOCALGREEN\\033[0m plain\\n'; exec sleep 60"];
        }

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        try
        {
            await WaitForSocketAsync(cts.Token);
            var text = await WaitForCliTextAsync("LOCALGREEN", cts.Token);
            Assert.IsNotNull(text, "hosted PTY output never reached the model");

            var (exitCode, stdout, stderr) = await RunCliAsync("capture", "screenshot", Pid, "--format", "ansi", "--json");

            Assert.AreEqual(0, exitCode, stderr);
            using var json = JsonDocument.Parse(stdout);
            var root = json.RootElement;
            Assert.AreEqual("pty-process", root.GetProperty("identity").GetProperty("configuration").GetProperty("workload").GetString());
            Assert.AreEqual(config.Command, root.GetProperty("identity").GetProperty("applicationName").GetString());
            AssertUnavailable(root, "identity.applicationFrame", "not a Hex1b application");
            AssertUnavailable(root, "history.availableRows", "not configured");
            Assert.IsTrue(root.GetProperty("identity").GetProperty("modelSequence").GetInt64() > 0,
                "PTY output reached the hosted model without advancing its sequence");
            Assert.IsFalse(root.GetProperty("synchronizedUpdate").GetProperty("active").GetBoolean());

            using var model = ApplyToModel(root.GetProperty("content").GetString()!, 40, 6);
            var cell = FindCell(model, "LOCALGREEN");
            Assert.IsNotNull(cell.Foreground, "ANSI capture of the PTY child lost its color");
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(Hex1bColorKind.Standard, cell.Foreground.Value.Kind);
                Assert.AreEqual(2, cell.Foreground.Value.AnsiIndex);
                Assert.IsTrue((cell.Attributes & CellAttributes.Bold) != 0);
            }
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task Screenshot_ReportsModelSequenceAndSyncDisclosure()
    {
        // A raw-workload terminal on a stopped clock: no app frame or timer can end the update.
        await using var target = await StartRawAttachedAsync(new FakeTimeProvider(DateTimeOffset.UtcNow));
        var engine = new TerminalDiagnostics(target, "CliRaw");

        var (staticExit, staticOut, staticErr) = await RunCliAsync("capture", "screenshot", Pid, "--json");
        var engineStatic = engine.Capture(new DiagnosticCaptureRequest());

        Assert.AreEqual(0, staticExit, staticErr);
        Assert.IsFalse(staticErr.Contains("partially applied", StringComparison.Ordinal), "a complete model was called partial");
        using (var json = JsonDocument.Parse(staticOut))
        {
            Assert.AreEqual(engineStatic.Identity!.ModelSequence,
                json.RootElement.GetProperty("identity").GetProperty("modelSequence").GetInt64(),
                "CLI and the in-process engine disagree on a static model");
            Assert.IsFalse(json.RootElement.GetProperty("synchronizedUpdate").GetProperty("active").GetBoolean());
        }

        target.ApplyTokens(AnsiTokenizer.Tokenize("\x1b[?2026h"));
        var enginePending = engine.Capture(new DiagnosticCaptureRequest());
        var (pendingExit, pendingOut, pendingErr) = await RunCliAsync("capture", "screenshot", Pid, "--json");

        Assert.AreEqual(0, pendingExit, pendingErr);
        StringAssert.Contains(pendingErr, "partially applied", "--json capture did not warn on stderr about partial content");
        var (textExit, _, textErr) = await RunCliAsync("capture", "screenshot", Pid);
        Assert.AreEqual(0, textExit, textErr);
        StringAssert.Contains(textErr, "partially applied", "text capture printed partial content without saying so");
        using (var json = JsonDocument.Parse(pendingOut))
        {
            Assert.IsTrue(json.RootElement.TryGetProperty("synchronizedUpdate", out var sync), "missing synchronizedUpdate");
            Assert.IsTrue(sync.GetProperty("active").GetBoolean(), "CLI did not disclose the pending synchronized update");
            Assert.AreEqual(enginePending.SynchronizedUpdate!.StartedAtSequence, sync.GetProperty("startedAtSequence").GetInt64());
        }
    }

    [TestMethod]
    public async Task Screenshot_TextFormat_WritesPlainContentAndDefaultIsNotStyled()
    {
        await using var target = await StartAttachedAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync("capture", "screenshot", Pid);

        Assert.AreEqual(0, exitCode, stderr);
        StringAssert.Contains(stdout, "STYLED");
        Assert.IsFalse(stdout.Contains('\x1b'), "default text capture contains escape sequences");
    }

    [TestMethod]
    public async Task Screenshot_InvalidFormatAndAuthorization_ReportInvalidRequestOutcome()
    {
        await using var target = await StartAttachedAppAsync();

        var (formatExit, formatOut, formatErr) = await RunCliAsync("capture", "screenshot", Pid, "--format", "bmp", "--json");
        Assert.AreEqual(1, formatExit);
        using (var json = JsonDocument.Parse(formatOut))
        {
            Assert.AreEqual("invalid-request", json.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("unsupported-format", json.RootElement.GetProperty("problem").GetProperty("code").GetString());
        }
        StringAssert.Contains(formatErr, "unsupported-format");

        var (authExit, authOut, _) = await RunCliAsync("capture", "screenshot", Pid, "--authorize", "everything", "--json");
        Assert.AreEqual(1, authExit);
        using (var json = JsonDocument.Parse(authOut))
            Assert.AreEqual("unsupported-authorization", json.RootElement.GetProperty("problem").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Screenshot_AuthorizedNonScreenMetadata_ReturnsTitle()
    {
        await using var target = await StartAttachedAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync(
            "capture", "screenshot", Pid, "--authorize", "non-screen-metadata", "--json");

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        AssertCoverage(json.RootElement, "window-title", "included");
        Assert.AreEqual(JsonValueKind.Object, json.RootElement.GetProperty("nonScreenMetadata").ValueKind);
    }

    [TestMethod]
    public async Task Screenshot_AuthorizeBeforeIdAndCommaSeparated_ParseAsAuthorizations()
    {
        await using var target = await StartAttachedAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync(
            "capture", "screenshot", "--authorize", "non-screen-metadata,editor-text", Pid, "--json");

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        AssertCoverage(json.RootElement, "window-title", "included");
        AssertCoverage(json.RootElement, "editor-text", "unavailable");
    }

    [TestMethod]
    public async Task Screenshot_JsonWithOutput_SavesContentAndOmitsItFromJson()
    {
        await using var target = await StartAttachedAppAsync();
        var outputPath = Path.Combine(Path.GetTempPath(), $"hex1b-cli-{Guid.NewGuid():N}.ansi");
        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync(
                "capture", "screenshot", Pid, "--format", "ansi", "--output", outputPath, "--json");

            Assert.AreEqual(0, exitCode, stderr);
            using var json = JsonDocument.Parse(stdout);
            Assert.AreEqual("captured", json.RootElement.GetProperty("outcome").GetString());
            Assert.IsFalse(json.RootElement.TryGetProperty("content", out _), "saved content repeated on stdout");
            StringAssert.Contains(stderr, outputPath);
            using var model = ApplyToModel(await File.ReadAllTextAsync(outputPath), 40, 5);
            Assert.AreEqual(200, FindCell(model, "STYLED").Foreground!.Value.R);
        }
        finally
        {
            File.Delete(outputPath);
        }
    }

    [TestMethod]
    public async Task Screenshot_UnreachableSocket_ReportsUnavailableOutcome()
    {
        var socketPath = Path.Combine(McpDiagnosticsPresentationFilter.GetSocketDirectory(), "999999999.diagnostics.socket");
        Directory.CreateDirectory(Path.GetDirectoryName(socketPath)!);
        await File.WriteAllTextAsync(socketPath, "");
        try
        {
            var (exitCode, stdout, stderr) = await RunCliAsync("capture", "screenshot", "999999999", "--json");

            Assert.AreEqual(1, exitCode);
            using var json = JsonDocument.Parse(stdout);
            Assert.AreEqual("unavailable", json.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("target-unreachable", json.RootElement.GetProperty("problem").GetProperty("code").GetString());
            StringAssert.Contains(stderr, "target-unreachable");
        }
        finally
        {
            File.Delete(socketPath);
        }
    }

    [TestMethod]
    public async Task Capabilities_DescribeContractAndUnavailableLayers()
    {
        await using var target = await StartAttachedAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync("capture", "capabilities", Pid);

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        var capture = json.RootElement.GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("operation").GetString() == "capture");
        CollectionAssert.AreEquivalent(new[] { "text", "ansi", "svg", "html" },
            capture.GetProperty("formats").EnumerateArray().Select(f => f.GetString()).ToArray());
        CollectionAssert.AreEqual(new[] { "immediate" },
            capture.GetProperty("timing").EnumerateArray().Select(f => f.GetString()).ToArray());
        var frame = json.RootElement.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("layer").GetString() == "application-frame");
        Assert.IsFalse(frame.GetProperty("available").GetBoolean());
    }

    [TestMethod]
    public async Task Assert_UsesSharedCaptureContract()
    {
        await using var target = await StartAttachedAppAsync();

        var (present, _, presentErr) = await RunCliAsync("assert", Pid, "--text-present", "STYLED", "--timeout", "5");
        var (absent, _, _) = await RunCliAsync("assert", Pid, "--text-present", "NOT-THERE", "--timeout", "1");

        Assert.AreEqual(0, present, presentErr);
        Assert.AreEqual(1, absent);
    }

    // === Helpers ===

    private static string Pid => Environment.ProcessId.ToString();

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCliAsync(params string[] args)
    {
        using var app = await Program.BuildApplication(args);
        var root = app.Services.GetRequiredService<Hex1b.Tool.Commands.RootCommand>();
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var originalOut = Console.Out;
        var originalError = Console.Error;
        Console.SetOut(stdout);
        Console.SetError(stderr);
        try
        {
            var exitCode = await root.Parse(args).InvokeAsync(cancellationToken: TestContext.Current.CancellationToken);
            return (exitCode, stdout.ToString(), stderr.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
            Console.SetError(originalError);
        }
    }

    private static async Task<Hex1bTerminal> StartAttachedAppAsync()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithHeadless()
            .WithScrollback(50)
            .WithHex1bApp(_ => new ThemePanelWidget(
                theme => theme.Set(GlobalTheme.ForegroundColor, Styled),
                new TextBlockWidget("STYLED")))
            .WithDiagnostics(appName: "CliAttached", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("STYLED"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        return terminal;
    }

    private static async Task<Hex1bTerminal> StartRawAttachedAsync(TimeProvider clock)
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithHeadless()
            .WithDimensions(40, 5)
            .WithTimeProvider(clock)
            .WithDiagnostics(appName: "CliRaw", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        terminal.ApplyTokens(AnsiTokenizer.Tokenize("STATIC"));
        return terminal;
    }

    private static async Task<string?> WaitForCliTextAsync(string text, CancellationToken ct)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            var (exitCode, stdout, _) = await RunCliAsync("capture", "screenshot", Pid);
            if (exitCode == 0 && stdout.Contains(text, StringComparison.Ordinal))
                return stdout;
            await Task.Delay(100, ct);
        }

        return null;
    }

    private static async Task WaitForSocketAsync(CancellationToken ct)
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        var client = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(socketPath) && await client.TryProbeAsync(socketPath, ct) is { Success: true })
                return;
            await Task.Delay(50, ct);
        }

        Assert.Fail("diagnostics socket never became available");
    }

    // Targets in this process share one socket path; a previous target's teardown must finish
    // unlinking it before the next target binds, or the late unlink removes the new socket.
    private static async Task WaitForSocketReleaseAsync(CancellationToken ct)
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50, ct);
    }

    private static Hex1bTerminal ApplyToModel(string ansi, int width, int height)
    {
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithHeadless()
            .WithDimensions(width, height)
            .Build();
        terminal.ApplyTokens(AnsiTokenizer.Tokenize(ansi));
        return terminal;
    }

    private static TerminalCell FindCell(Hex1bTerminal terminal, string text)
    {
        using var snapshot = terminal.CreateSnapshot();
        for (var y = 0; y < snapshot.Height; y++)
        {
            var line = snapshot.GetLine(y);
            var column = line.IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
                return snapshot.GetCell(column, y);
        }

        Assert.Fail($"'{text}' not found in reapplied capture:\n{snapshot.GetText()}");
        return default;
    }

    private static void AssertCoverage(JsonElement root, string content, string state)
    {
        var entry = root.GetProperty("contentCoverage").EnumerateArray()
            .Single(c => c.GetProperty("content").GetString() == content);
        Assert.AreEqual(state, entry.GetProperty("state").GetString(), content);
    }

    private static void AssertUnavailable(JsonElement root, string field, string reasonFragment)
    {
        var entry = root.GetProperty("unavailableFields").EnumerateArray()
            .Single(f => f.GetProperty("field").GetString() == field);
        StringAssert.Contains(entry.GetProperty("reason").GetString(), reasonFragment);
    }
}
