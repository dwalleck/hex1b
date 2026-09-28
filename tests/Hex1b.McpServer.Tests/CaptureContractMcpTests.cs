using System.Text.Json;
using Hex1b.Automation;
using Hex1b.Diagnostics;
using Hex1b.Nodes;
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
        AssertUnavailable(capture, "identity.applicationFrame", "is not an application frame");
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
            var partialWait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = attached, ["text"] = "NEVER", ["timeoutSeconds"] = 1 });
            Assert.IsFalse(partialWait.GetProperty("found").GetBoolean());
            StringAssert.Contains(partialWait.GetProperty("message").GetString(), "partially applied",
                "wait_for_terminal_text returned partial currentText without saying so");

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
            Assert.IsTrue(attachedCaps.GetProperty("layers").EnumerateArray()
                .Single(l => l.GetProperty("layer").GetString() == "application-frame").GetProperty("available").GetBoolean(),
                "a diagnostics-enabled app publishes application frames");
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

    private const string FrameSentinel = "SENTINEL-MCP-3b8e";

    [TestMethod]
    public async Task ApplicationFrame_AttachedApp_EqualsTheEnginesResultThroughBothTools()
    {
        await using var terminal = await StartFrameAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        var engine = new TerminalDiagnostics(terminal, "McpFrames");

        foreach (var (tool, arguments) in new (string, Dictionary<string, object?>)[]
        {
            ("capture_application_frame", new() { ["sessionId"] = sessionId }),
            ("get_hex1b_tree", new() { ["processId"] = Environment.ProcessId }),
        })
        {
            var (result, direct) = await CaptureStableAsync(engine, () => CallAsync(client, tool, arguments));

            Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
            var frame = result.GetProperty("applicationFrame");
            Assert.AreEqual("application-frame", frame.GetProperty("identity").GetProperty("sourceLayer").GetString(), tool);
            Assert.AreEqual(1, frame.GetProperty("frame").GetProperty("popups").GetArrayLength(), "fixture: the popup is missing");
            Assert.AreEqual("text-box", frame.GetProperty("frame").GetProperty("focusedEditor").GetProperty("kind").GetString(),
                $"{tool}: focused editor missing");
            Assert.IsTrue(JsonElement.DeepEquals(ToJson(direct), frame),
                $"{tool} result differs from the engine's.\nengine: {ToJson(direct)}\nmcp:    {frame}");
        }
    }

    [TestMethod]
    public async Task ApplicationFrame_EditorTextOnlyWhenAuthorized()
    {
        await using var terminal = await StartFrameAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        var plain = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = sessionId });
        var plainTree = await CallAsync(client, "get_hex1b_tree", new() { ["processId"] = Environment.ProcessId });
        var authorized = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = sessionId, ["authorize"] = "editor-text" });
        var invalid = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = sessionId, ["authorize"] = "everything" });

        Assert.IsFalse(plain.ToString().Contains(FrameSentinel, StringComparison.Ordinal), "sentinel in default MCP frame");
        Assert.IsFalse(plainTree.ToString().Contains(FrameSentinel, StringComparison.Ordinal), "sentinel in default GetHex1bTree");
        var frame = authorized.GetProperty("applicationFrame");
        Assert.AreEqual(FrameSentinel, frame.GetProperty("frame").GetProperty("focusedEditor").GetProperty("text").GetString());
        AssertCoverage(frame, "editor-text", "included");
        Assert.AreEqual("invalid-request", invalid.GetProperty("applicationFrame").GetProperty("outcome").GetString());
    }

    [TestMethod]
    public async Task ApplicationFrame_LocalSession_ReportsNoApplicationLayer()
    {
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var local = await StartLocalSessionAsync(client);
        try
        {
            var result = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = local });
            var missing = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = "no-such-session" });

            Assert.IsFalse(result.GetProperty("success").GetBoolean());
            var frame = result.GetProperty("applicationFrame");
            Assert.AreEqual("unavailable", frame.GetProperty("outcome").GetString());
            Assert.AreEqual("no-application-layer", frame.GetProperty("problem").GetProperty("code").GetString());
            Assert.AreEqual("session-not-found", missing.GetProperty("applicationFrame").GetProperty("problem").GetProperty("code").GetString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }
    }

    [TestMethod]
    public async Task ApplicationFrame_DeepTreeCrossesMcp()
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);
        Hex1bWidget deep = new TextBlockWidget("LEAF");
        for (var i = 0; i < 40; i++)
            deep = new VStackWidget([deep]);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithHeadless()
            .WithHex1bApp(_ => deep)
            .WithDiagnostics(appName: "McpDeep", forceEnable: true)
            .Build();
        _ = terminal.RunAsync();
        var probe = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(socketPath) && await probe.TryProbeAsync(socketPath) is { Success: true })
                break;
            await Task.Delay(50);
        }
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("LEAF"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal);
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        var result = await CallAsync(client, "capture_application_frame", new() { ["sessionId"] = sessionId });

        Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
        StringAssert.Contains(result.GetProperty("applicationFrame").GetProperty("frame").GetProperty("root").GetRawText(), "\"LEAF\"");
    }

    // === Helpers ===

    private async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestCancellationToken);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.IsNotNull(text, $"{tool} returned no text content");
        // Application frames nest deeper than the default reader depth of 64.
        return JsonSerializer.Deserialize<JsonElement>(text, new JsonSerializerOptions { MaxDepth = 1024 });
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

    // A static app whose anchored popup holds the focused TextBox with the sentinel (an open popup
    // owns focus). The popup is pushed from the app's own build, on the app loop, once the anchor
    // node exists.
    private static async Task<Hex1bTerminal> StartFrameAppAsync()
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);

        Hex1bApp? app = null;
        var pushed = false;
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 8)
            .WithHeadless()
            .WithHex1bApp(_ => { }, built =>
            {
                app = built;
                return _ =>
                {
                    if (!pushed && FindNode<ZStackNode>(built.RootNode) is { } host && FindNode<ButtonNode>(built.RootNode) is { } anchor)
                    {
                        host.Popups.PushAnchored(anchor, AnchorPosition.Below,
                            new VStackWidget([new TextBlockWidget("POPPED"), new TextBoxWidget(FrameSentinel)]), focusRestoreNode: anchor);
                        built.RequestFocus(node => node is TextBoxNode);
                        pushed = true;
                    }

                    return new ZStackWidget([new VStackWidget([new TextBlockWidget("FRAMEAPP"), new ButtonWidget("anchor")])]);
                };
            })
            .WithDiagnostics(appName: "McpFrames", forceEnable: true)
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
            .WaitUntil(s => s.ContainsText("FRAMEAPP"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal);
        app!.Invalidate();
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("POPPED"), TimeSpan.FromSeconds(10), "popup rendered")
            .Build().ApplyAsync(terminal);
        return terminal;
    }

    // Engine and MCP observe the same published frame only while the app is idle; retry until an
    // engine capture before and after the tool call agree.
    private static async Task<(JsonElement Result, DiagnosticApplicationFrameResult Engine)> CaptureStableAsync(
        TerminalDiagnostics engine, Func<Task<JsonElement>> call)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var before = engine.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
            var result = await call();
            var after = engine.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
            if (before.Frame?.FrameId == after.Frame?.FrameId)
                return (result, before);
            await Task.Delay(100);
        }

        Assert.Fail("the application never stayed idle across an MCP call");
        return default;
    }

    private static JsonElement ToJson(DiagnosticApplicationFrameResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult);

    private static T? FindNode<T>(Hex1bNode? node) where T : Hex1bNode =>
        node is null ? null : node as T ?? node.GetChildren().Select(FindNode<T>).FirstOrDefault(n => n is not null);

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
