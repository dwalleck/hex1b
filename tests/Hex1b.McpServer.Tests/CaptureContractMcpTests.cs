using System.Text.Json;
using Hex1b.Automation;
using Hex1b.Diagnostics;
using Hex1b.Theming;
using Hex1b.Tokens;
using Hex1b.Widgets;
using Microsoft.Extensions.Time.Testing;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Hex1b.McpServer.Tests;

/// <summary>
/// Drives real MCP tool calls against a local PTY session and an attached Hex1b application.
/// Returned capture content is validated by applying it to a terminal model.
/// </summary>
[DoNotParallelize]
[TestClass]
public class CaptureContractMcpTests : McpServerTestBase
{
    private static readonly Hex1bColor Styled = Hex1bColor.FromRgb(20, 180, 90);

    private static string StartTerminalToolName => OperatingSystem.IsWindows() ? "start_pwsh_terminal" : "start_bash_terminal";

    [TestMethod]
    public async Task CaptureTerminalScreen_LocalSession_ReturnsStyledAnsiFromChildProcess()
    {
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await StartLocalSessionAsync(client);
        try
        {
            await CallAsync(client, "send_terminal_input", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = OperatingSystem.IsWindows()
                    ? "Write-Host ('MCP' + 'LOCAL') -ForegroundColor Magenta\r"
                    : "printf '\\033[1;35mMCP%s\\033[0m\\n' LOCAL\r"
            });
            var wait = await CallAsync(client, "wait_for_terminal_text", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = "MCPLOCAL",
                ["timeoutSeconds"] = 10
            });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());

            var result = await CallAsync(client, "capture_terminal_screen", new()
            {
                ["sessionId"] = sessionId,
                ["format"] = "ansi"
            });

            Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
            var capture = result.GetProperty("capture");
            Assert.AreEqual("captured", capture.GetProperty("outcome").GetString());
            Assert.AreEqual("ansi", capture.GetProperty("format").GetString());
            var geometry = capture.GetProperty("geometry");
            using var model = ApplyToModel(capture.GetProperty("content").GetString()!,
                geometry.GetProperty("columns").GetInt32(), geometry.GetProperty("rows").GetInt32());
            var cell = FindCell(model, "MCPLOCAL");
            Assert.IsNotNull(cell.Foreground, "ANSI capture of the local session lost its style (plain-text substitution)");
            if (!OperatingSystem.IsWindows())
            {
                Assert.AreEqual(5, cell.Foreground.Value.AnsiIndex);
                Assert.IsTrue((cell.Attributes & CellAttributes.Bold) != 0);
            }

            var identity = capture.GetProperty("identity");
            Assert.AreEqual(Environment.ProcessId, identity.GetProperty("processId").GetInt32(),
                "a local session's model is hosted by the MCP server process");
            Assert.AreEqual("pty-process", identity.GetProperty("configuration").GetProperty("workload").GetString());
            AssertUnavailable(capture, "identity.applicationFrame", "not a Hex1b application");
            AssertCoverage(capture, "hyperlink-targets", "excluded");
            AssertCoverage(capture, "raw-input", "excluded");
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    public async Task CaptureTerminalScreen_AttachedApplication_ReturnsTheEnginesStyledContent()
    {
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        var result = await CallAsync(client, "capture_terminal_screen", new()
        {
            ["sessionId"] = sessionId,
            ["format"] = "ansi",
            ["historyRows"] = 3
        });

        Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
        var capture = result.GetProperty("capture");
        var content = capture.GetProperty("content").GetString()!;
        using (var model = ApplyToModel(content, 40, 5))
        {
            var cell = FindCell(model, "STYLED");
            Assert.AreEqual((20, 180, 90), (cell.Foreground!.Value.R, cell.Foreground.Value.G, cell.Foreground.Value.B));
        }

        // The MCP result carries the engine's actual observation of this terminal, not a copy of the request.
        var direct = new TerminalDiagnostics(terminal, "McpAttached").Capture(new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Ansi,
            HistoryRows = 3
        });
        Assert.AreEqual(direct.Content, content);
        var identity = capture.GetProperty("identity");
        Assert.AreEqual(terminal.DiagnosticSessionId.ToString("N"), identity.GetProperty("sessionId").GetString());
        Assert.AreEqual("McpAttached", identity.GetProperty("applicationName").GetString());
        Assert.AreEqual("hex1b-application", identity.GetProperty("configuration").GetProperty("workload").GetString());
        Assert.AreEqual(3, capture.GetProperty("history").GetProperty("requestedRows").GetInt32());
        Assert.AreEqual(50, capture.GetProperty("history").GetProperty("retentionCapacity").GetInt32());
        AssertUnavailable(capture, "identity.applicationFrame", "not yet published");
        Assert.AreEqual(direct.Identity!.ModelSequence, identity.GetProperty("modelSequence").GetInt64(),
            "MCP and the in-process engine disagree on the model state of a static terminal");
        CollectionAssert.AreEqual(
            direct.ContentCoverage.Select(c => $"{DiagnosticContractNames.Of(c.Content)}:{DiagnosticContractNames.Of(c.State)}").ToArray(),
            capture.GetProperty("contentCoverage").EnumerateArray()
                .Select(c => $"{c.GetProperty("content").GetString()}:{c.GetProperty("state").GetString()}").ToArray());
    }

    [TestMethod]
    public async Task CaptureTerminalScreen_ReportsModelSequenceAndSyncDisclosure()
    {
        // A raw-workload terminal on a stopped clock: no app frame or timer can end the update.
        await using var terminal = await StartRawAttachedAsync(new FakeTimeProvider(DateTimeOffset.UtcNow));
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var attached = await ConnectAttachedAsync(client);
        var local = await StartLocalSessionAsync(client);
        var engine = new TerminalDiagnostics(terminal, "McpRaw");
        try
        {
            var staticResult = await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = attached });
            Assert.IsFalse(staticResult.GetProperty("message").GetString()!.Contains("partially applied"), "a complete model was called partial");
            var staticCapture = staticResult.GetProperty("capture");
            Assert.AreEqual(engine.Capture(new DiagnosticCaptureRequest()).Identity!.ModelSequence,
                staticCapture.GetProperty("identity").GetProperty("modelSequence").GetInt64(),
                "MCP and the in-process engine disagree on a static model");

            terminal.ApplyTokens(AnsiTokenizer.Tokenize("\x1b[?2026h"));
            var enginePending = engine.Capture(new DiagnosticCaptureRequest());
            var pendingResult = await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = attached });
            StringAssert.Contains(pendingResult.GetProperty("message").GetString(), "partially applied",
                "the MCP message presented partial content as a screen");
            var pending = pendingResult.GetProperty("capture");
            Assert.IsTrue(pending.TryGetProperty("synchronizedUpdate", out var sync), "missing synchronizedUpdate");
            Assert.IsTrue(sync.GetProperty("active").GetBoolean(), "MCP did not disclose the pending synchronized update");
            Assert.AreEqual(enginePending.SynchronizedUpdate!.StartedAtSequence, sync.GetProperty("startedAtSequence").GetInt64());

            var beforeOutput = (await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = local }))
                .GetProperty("capture").GetProperty("identity").GetProperty("modelSequence").GetInt64();
            await CallAsync(client, "send_terminal_input", new()
            {
                ["sessionId"] = local,
                ["text"] = OperatingSystem.IsWindows() ? "Write-Host ('SEQ' + 'OUT')\r" : "echo SEQ''OUT\r"
            });
            var wait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = local, ["text"] = "SEQOUT", ["timeoutSeconds"] = 10 });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());
            var localCapture = (await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = local })).GetProperty("capture");
            Assert.IsTrue(localCapture.GetProperty("identity").GetProperty("modelSequence").GetInt64() > beforeOutput,
                "PTY output reached the local session's model without advancing its sequence");
            Assert.IsTrue(localCapture.TryGetProperty("synchronizedUpdate", out _), "missing synchronizedUpdate for the local session");
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }
    }

    [TestMethod]
    public async Task CaptureHex1bTerminal_SavesStyledContentAndReturnsContractMetadata()
    {
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var savePath = Path.Combine(Path.GetTempPath(), $"hex1b-capture-{Guid.NewGuid():N}.ansi");
        try
        {
            var result = await CallAsync(client, "capture_hex1b_terminal", new()
            {
                ["processId"] = Environment.ProcessId,
                ["savePath"] = savePath,
                ["format"] = "ansi"
            });

            Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
            Assert.AreEqual(savePath, result.GetProperty("savedPath").GetString());
            var capture = result.GetProperty("capture");
            Assert.IsFalse(capture.TryGetProperty("content", out var inline) && inline.ValueKind == JsonValueKind.String,
                "saved content must not also be returned inline");
            Assert.AreEqual("captured", capture.GetProperty("outcome").GetString());
            using var model = ApplyToModel(await File.ReadAllTextAsync(savePath), 40, 5);
            Assert.AreEqual(20, FindCell(model, "STYLED").Foreground!.Value.R);
        }
        finally
        {
            File.Delete(savePath);
        }
    }

    [TestMethod]
    public async Task CaptureTerminalScreen_AuthorizationControlsNonScreenMetadata()
    {
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        var defaults = (await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = sessionId })).GetProperty("capture");
        var authorized = (await CallAsync(client, "capture_terminal_screen", new()
        {
            ["sessionId"] = sessionId,
            ["authorize"] = "non-screen-metadata, editor-text"
        })).GetProperty("capture");

        AssertCoverage(defaults, "window-title", "excluded");
        Assert.IsFalse(defaults.TryGetProperty("nonScreenMetadata", out var none) && none.ValueKind == JsonValueKind.Object);
        AssertCoverage(authorized, "window-title", "included");
        AssertCoverage(authorized, "editor-text", "unavailable");
        Assert.AreEqual(JsonValueKind.Object, authorized.GetProperty("nonScreenMetadata").ValueKind);
    }

    [TestMethod]
    public async Task CaptureTools_InvalidRequestAndMissingSession_UseContractOutcomes()
    {
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await StartLocalSessionAsync(client);
        try
        {
            var invalid = (await CallAsync(client, "capture_terminal_screen", new()
            {
                ["sessionId"] = sessionId,
                ["format"] = "bmp"
            })).GetProperty("capture");
            Assert.AreEqual("invalid-request", invalid.GetProperty("outcome").GetString());
            Assert.AreEqual("unsupported-format", invalid.GetProperty("problem").GetProperty("code").GetString());

            var missing = (await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = "nope" })).GetProperty("capture");
            Assert.AreEqual("unavailable", missing.GetProperty("outcome").GetString());
            Assert.AreEqual("session-not-found", missing.GetProperty("problem").GetProperty("code").GetString());

            var text = await CallAsync(client, "capture_terminal_text", new() { ["sessionId"] = sessionId });
            Assert.AreEqual("text", text.GetProperty("capture").GetProperty("format").GetString());
            Assert.AreEqual("captured", text.GetProperty("capture").GetProperty("outcome").GetString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    public async Task WaitForTerminalText_UnreachableAttachedTarget_ReportsFailureNotNotFound()
    {
        var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        await terminal.DisposeAsync();
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);

        var started = System.Diagnostics.Stopwatch.StartNew();
        var wait = await CallAsync(client, "wait_for_terminal_text", new()
        {
            ["sessionId"] = sessionId,
            ["text"] = "STYLED",
            ["timeoutSeconds"] = 10
        });

        Assert.IsFalse(wait.GetProperty("success").GetBoolean(), wait.ToString());
        StringAssert.Contains(wait.GetProperty("message").GetString(), "target-unreachable");
        Assert.IsTrue(started.Elapsed < TimeSpan.FromSeconds(5), "an unreachable target was polled until the deadline");
    }

    [TestMethod]
    public async Task GetTerminalDiagnosticCapabilities_LocalAndAttachedDiscloseTheirLayers()
    {
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var attached = await ConnectAttachedAsync(client);
        var local = await StartLocalSessionAsync(client);
        try
        {
            var attachedCaps = (await CallAsync(client, "get_terminal_diagnostic_capabilities", new() { ["sessionId"] = attached }))
                .GetProperty("capabilities");
            var localCaps = (await CallAsync(client, "get_terminal_diagnostic_capabilities", new() { ["sessionId"] = local }))
                .GetProperty("capabilities");

            Assert.AreEqual("captured", attachedCaps.GetProperty("outcome").GetString());
            StringAssert.Contains(FrameLayerReason(attachedCaps), "not yet published");
            StringAssert.Contains(FrameLayerReason(localCaps), "not a Hex1b application");
            Assert.AreEqual(
                attachedCaps.GetProperty("operations").ToString(),
                localCaps.GetProperty("operations").ToString(),
                "local and attached targets must describe the same capture operation");
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }

        static string FrameLayerReason(JsonElement capabilities) => capabilities.GetProperty("layers").EnumerateArray()
            .Single(l => l.GetProperty("layer").GetString() == "application-frame").GetProperty("reason").GetString()!;
    }

    // === Helpers ===

    private async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestCancellationToken);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.IsNotNull(text, $"{tool} returned no text content");
        return JsonSerializer.Deserialize<JsonElement>(text);
    }

    private async Task<string> StartLocalSessionAsync(McpClient client)
    {
        var start = await CallAsync(client, StartTerminalToolName, new()
        {
            ["width"] = 60,
            ["height"] = 10,
            ["workingDirectory"] = Path.GetTempPath()
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        return start.GetProperty("sessionId").GetString()!;
    }

    private async Task<string> ConnectAttachedAsync(McpClient client)
    {
        var connect = await CallAsync(client, "connect_to_hex1b_stack", new() { ["processId"] = Environment.ProcessId });
        Assert.IsTrue(connect.GetProperty("success").GetBoolean(), connect.ToString());
        return connect.GetProperty("sessionId").GetString()!;
    }

    private static async Task<Hex1bTerminal> StartAttachedAppAsync()
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);

        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithHeadless()
            .WithScrollback(50)
            .WithHex1bApp(_ => new ThemePanelWidget(
                theme => theme.Set(GlobalTheme.ForegroundColor, Styled),
                new TextBlockWidget("STYLED")))
            .WithDiagnostics(appName: "McpAttached", forceEnable: true)
            .Build();
        _ = terminal.RunAsync();

        var client = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(socketPath) && await client.TryProbeAsync(socketPath) is { Success: true })
                break;
            await Task.Delay(50);
        }

        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("STYLED"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal);
        return terminal;
    }

    private static async Task<Hex1bTerminal> StartRawAttachedAsync(TimeProvider clock)
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);

        var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(new Hex1bAppWorkloadAdapter())
            .WithHeadless()
            .WithDimensions(40, 5)
            .WithTimeProvider(clock)
            .WithDiagnostics(appName: "McpRaw", forceEnable: true)
            .Build();
        _ = terminal.RunAsync();
        var client = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(socketPath) && await client.TryProbeAsync(socketPath) is { Success: true })
                break;
            await Task.Delay(50);
        }

        terminal.ApplyTokens(AnsiTokenizer.Tokenize("STATIC"));
        return terminal;
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
            var column = snapshot.GetLine(y).IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
                return snapshot.GetCell(column, y);
        }

        Assert.Fail($"'{text}' not found in reapplied capture:\n{snapshot.GetText()}");
        return default;
    }

    private static void AssertCoverage(JsonElement capture, string content, string state)
    {
        var entry = capture.GetProperty("contentCoverage").EnumerateArray()
            .Single(c => c.GetProperty("content").GetString() == content);
        Assert.AreEqual(state, entry.GetProperty("state").GetString(), content);
    }

    private static void AssertUnavailable(JsonElement capture, string field, string reasonFragment)
    {
        var entry = capture.GetProperty("unavailableFields").EnumerateArray()
            .Single(f => f.GetProperty("field").GetString() == field);
        StringAssert.Contains(entry.GetProperty("reason").GetString(), reasonFragment);
    }
}
