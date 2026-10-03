using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            var delivery = attachedCaps.GetProperty("operations").EnumerateArray()
                .Single(o => o.GetProperty("operation").GetString() == "delivery");
            Assert.AreEqual("native-delivery", delivery.GetProperty("layer").GetString(), "the delivery operation names its layer");
            CollectionAssert.Contains(delivery.GetProperty("authorizations").EnumerateArray().Select(a => a.GetString()).ToList(), "native-output");
            // The operations are the same; only milestone availability depends on the target.
            Assert.AreEqual(
                WithoutMilestoneAvailability(attachedCaps.GetProperty("operations")),
                WithoutMilestoneAvailability(localCaps.GetProperty("operations")),
                "local and attached targets must describe the same capture operation");
            CollectionAssert.AreEquivalent(new[] { "input-accepted", "input-processed", "frame-published", "model-applied" },
                AvailableMilestones(attachedCaps), "a diagnostics-enabled app supports every milestone");
            CollectionAssert.AreEquivalent(new[] { "input-accepted" }, AvailableMilestones(localCaps),
                "a PTY session can only report acceptance");
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }

        static string WithoutMilestoneAvailability(JsonElement operations)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(operations.GetRawText())!;
            foreach (var operation in node.AsArray())
                foreach (var milestone in operation!["milestones"]!.AsArray())
                {
                    milestone!.AsObject().Remove("available");
                    milestone.AsObject().Remove("reason");
                }
            return node.ToJsonString();
        }

        static string[] AvailableMilestones(JsonElement capabilities) => capabilities.GetProperty("operations").EnumerateArray()
            .Single(o => o.GetProperty("operation").GetString() == "capture").GetProperty("milestones").EnumerateArray()
            .Where(m => m.GetProperty("available").GetBoolean()).Select(m => m.GetProperty("milestone").GetString()!).ToArray();

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

    [TestMethod]
    public async Task Milestone_AttachedApp_SendThenCaptureEachStage()
    {
        await using var terminal = await StartFrameAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        // send_terminal_key is registered twice (pre-existing); send through the Hex1b input tool.
        var send = await CallAsync(client, "send_input_to_hex1b_terminal", new() { ["processId"] = Environment.ProcessId, ["input"] = "q" });
        Assert.IsTrue(send.GetProperty("success").GetBoolean(), send.ToString());
        var accepted = send.GetProperty("acceptedInput");
        var inputId = accepted.GetProperty("lastId").GetInt64();
        StringAssert.Contains(accepted.GetProperty("meaning").GetString(), "Queued for the application");

        var processed = await CallAsync(client, "capture_terminal_screen", new()
        {
            ["sessionId"] = sessionId,
            ["milestone"] = "input-processed",
            ["inputId"] = inputId,
        });
        var framed = await CallAsync(client, "capture_application_frame", new()
        {
            ["sessionId"] = sessionId,
            ["milestone"] = "frame-published",
            ["inputId"] = inputId,
        });
        var applied = await CallAsync(client, "capture_terminal_screen", new()
        {
            ["sessionId"] = sessionId,
            ["milestone"] = "model-applied",
            ["inputId"] = inputId,
            ["authorize"] = "raw-input",
        });

        Assert.IsTrue(processed.GetProperty("capture").GetProperty("milestone").GetProperty("met").GetBoolean(), processed.ToString());
        var engine = await new TerminalDiagnostics(terminal, "McpFrames").CaptureAsync(new DiagnosticCaptureRequest
        {
            Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = inputId },
        });
        var engineMilestone = JsonSerializer.SerializeToElement(engine.Milestone!, DiagnosticsJsonContext.Default.Options);
        var mcpMilestone = processed.GetProperty("capture").GetProperty("milestone");
        Assert.IsTrue(JsonElement.DeepEquals(WithoutWallClock(engineMilestone), WithoutWallClock(mcpMilestone)),
            $"MCP milestone differs from the engine's.\nengine: {engineMilestone}\nmcp:    {mcpMilestone}");
        StringAssert.Contains(processed.GetProperty("message").GetString(), $"Milestone input-processed for input {inputId}: met");
        Assert.IsFalse(processed.GetProperty("capture").GetProperty("milestone").GetProperty("input").TryGetProperty("payload", out _),
            "raw key payload in a default MCP milestone result");
        var frame = framed.GetProperty("applicationFrame").GetProperty("milestone").GetProperty("frame");
        Assert.IsGreaterThanOrEqualTo(inputId, frame.GetProperty("processedInput").GetInt64());
        var appliedMilestone = applied.GetProperty("capture").GetProperty("milestone");
        Assert.IsTrue(appliedMilestone.GetProperty("met").GetBoolean(), applied.ToString());
        StringAssert.Contains(appliedMilestone.GetProperty("input").GetProperty("payload").GetString(), "\"q\"",
            "raw-input did not include the typed payload");
    }

    [TestMethod]
    public async Task Milestone_LocalSession_ReportsAcceptanceOnly()
    {
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var local = await StartLocalSessionAsync(client);
        try
        {
            var send = await CallAsync(client, "send_terminal_input", new() { ["sessionId"] = local, ["text"] = "echo hi" });
            var accepted = send.GetProperty("acceptedInput");
            var inputId = accepted.GetProperty("lastId").GetInt64();
            var processed = await CallAsync(client, "capture_terminal_screen", new()
            {
                ["sessionId"] = local,
                ["milestone"] = "input-processed",
                ["inputId"] = inputId,
            });
            var acceptedCapture = await CallAsync(client, "capture_terminal_screen", new()
            {
                ["sessionId"] = local,
                ["milestone"] = "input-accepted",
                ["inputId"] = inputId,
            });

            StringAssert.Contains(accepted.GetProperty("meaning").GetString(), "child process");
            var capture = processed.GetProperty("capture");
            Assert.AreEqual("unavailable", capture.GetProperty("outcome").GetString());
            Assert.AreEqual("input-consumption-unobservable", capture.GetProperty("problem").GetProperty("code").GetString());
            Assert.IsTrue(acceptedCapture.GetProperty("success").GetBoolean(), acceptedCapture.ToString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }
    }

    [TestMethod]
    public async Task Delivery_AttachedMatchesTheEngineAndLocalSessionIsUnavailable()
    {
        await using var terminal = await StartDeliveryAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);

        var plain = await CallAsync(client, "capture_native_delivery", new() { ["sessionId"] = sessionId });
        var raw = await CallAsync(client, "capture_native_delivery", new() { ["sessionId"] = sessionId, ["authorize"] = "native-output" });
        var engine = new TerminalDiagnostics(terminal, "McpDelivery").CaptureDelivery(new DiagnosticDeliveryRequest());

        Assert.IsTrue(plain.GetProperty("success").GetBoolean(), plain.ToString());
        var delivery = plain.GetProperty("delivery");
        Assert.AreEqual("websocket", delivery.GetProperty("deliveryLayer").GetString());
        Assert.IsTrue(delivery.GetProperty("records").EnumerateArray().All(r => !r.TryGetProperty("content", out _)),
            "bytes returned without native-output");
        var engineJson = JsonSerializer.SerializeToElement(engine, DiagnosticsJsonContext.Default.DiagnosticDeliveryResult);
        Assert.IsTrue(JsonElement.DeepEquals(WithoutAcquisition(engineJson), WithoutAcquisition(delivery)),
            $"MCP delivery differs from the engine's.\nengine: {engineJson}\nmcp:    {delivery}");
        var contents = raw.GetProperty("delivery").GetProperty("records").EnumerateArray()
            .Where(r => r.TryGetProperty("content", out _))
            .Select(r => Encoding.UTF8.GetString(Convert.FromBase64String(r.GetProperty("content").GetString()!)));
        Assert.IsTrue(contents.Any(c => c.Contains("DELIVERY-SENTINEL", StringComparison.Ordinal)), "native-output did not return the written bytes");

        var local = await StartLocalSessionAsync(client);
        try
        {
            var unavailable = await CallAsync(client, "capture_native_delivery", new() { ["sessionId"] = local });
            Assert.IsFalse(unavailable.GetProperty("success").GetBoolean());
            Assert.AreEqual("no-native-presentation",
                unavailable.GetProperty("delivery").GetProperty("problem").GetProperty("code").GetString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = local });
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_AttachedStartStatusStopInspectMatchTheEngine()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        var engine = new TerminalDiagnostics(terminal, "McpAttached");

        var start = await CallAsync(client, "start_diagnostic_case", new()
        {
            ["sessionId"] = sessionId,
            ["directory"] = root.Path,
            ["authorize"] = "reapplication-data",
            ["maxSeconds"] = 300,
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        AssertCaseEquals(engine.GetCaseStatus(), start.GetProperty("case"), "start", "elapsedSeconds", "bytesWritten", "streams");

        JsonElement status = default;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var before = engine.GetCaseStatus() with { ElapsedSeconds = null };
            status = (await CallAsync(client, "get_diagnostic_case_status", new() { ["sessionId"] = sessionId })).GetProperty("case");
            if (JsonSerializer.Serialize(before, DiagnosticsJsonContext.Default.DiagnosticCaseResult)
                == JsonSerializer.Serialize(engine.GetCaseStatus() with { ElapsedSeconds = null }, DiagnosticsJsonContext.Default.DiagnosticCaseResult))
                break;
            await Task.Delay(100);
        }
        AssertCaseEquals(engine.GetCaseStatus(), status, "status", "elapsedSeconds");

        var recorder = terminal.DiagnosticCase!;
        var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
        Assert.IsTrue(stop.GetProperty("success").GetBoolean(), stop.ToString());
        Assert.AreEqual("requested", stop.GetProperty("case").GetProperty("stopReason").GetString());
        AssertCaseEquals(recorder.Describe(), stop.GetProperty("case"), "stop", "elapsedSeconds");

        var path = stop.GetProperty("case").GetProperty("path").GetString()!;
        var inspect = await CallAsync(client, "inspect_diagnostic_case", new() { ["path"] = path, ["limit"] = 50 });
        var expected = JsonSerializer.SerializeToElement(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 50 }),
            DiagnosticsJsonContext.Default.DiagnosticCaseInspection);
        Assert.IsTrue(JsonElement.DeepEquals(expected, inspect.GetProperty("inspection")),
            $"MCP inspect differs from the inspector.\ninspector: {expected}\nmcp:       {inspect.GetProperty("inspection")}");

        var none = await CallAsync(client, "get_diagnostic_case_status", new() { ["sessionId"] = sessionId });
        var unknown = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = "no-such-session" });
        Assert.AreEqual("no-active-case", none.GetProperty("case").GetProperty("problem").GetProperty("code").GetString());
        Assert.AreEqual("session-not-found", unknown.GetProperty("case").GetProperty("problem").GetProperty("code").GetString());
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_LocalSessionRecordsFromTheFirstByte()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        var loose = Path.Combine(root.Path, "loose");
        Directory.CreateDirectory(loose, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await StartServerAsync();
        await using var client = await CreateClientAsync();

        var refused = await CallAsync(client, "start_bash_terminal", new() { ["recordCase"] = true, ["caseDirectory"] = loose });
        var orphan = await CallAsync(client, "start_bash_terminal", new() { ["caseDirectory"] = root.Path });
        Assert.AreEqual((false, "storage-refused"), (refused.GetProperty("success").GetBoolean(),
            refused.GetProperty("case").GetProperty("problem").GetProperty("code").GetString()), refused.ToString());
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(loose), "a refused start wrote to the loose root");
        Assert.IsEmpty(SessionManager.GetAllSessions(), "a refused case left a session");
        Assert.AreEqual((false, "invalid-request"), (orphan.GetProperty("success").GetBoolean(),
            orphan.GetProperty("case").GetProperty("problem").GetProperty("code").GetString()), orphan.ToString());

        var start = await CallAsync(client, "start_bash_terminal", new()
        {
            ["width"] = 60,
            ["height"] = 10,
            ["workingDirectory"] = Path.GetTempPath(),
            ["recordCase"] = true,
            ["caseDirectory"] = root.Path,
            ["caseAuthorize"] = "reapplication-data",
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        var sessionId = start.GetProperty("sessionId").GetString()!;
        try
        {
            var started = start.GetProperty("case");
            Assert.AreEqual(("construction", "complete"), (started.GetProperty("startPath").GetString(),
                started.GetProperty("checkpoint").GetProperty("status").GetString()), started.ToString());

            await CallAsync(client, "send_terminal_input", new() { ["sessionId"] = sessionId, ["text"] = "echo CASE-MCP-$((20+22))\r" });
            var wait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = sessionId, ["text"] = "CASE-MCP-42", ["timeoutSeconds"] = 10 });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());

            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(stop.GetProperty("success").GetBoolean(), stop.ToString());
            var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest
            {
                Path = stop.GetProperty("case").GetProperty("path").GetString()!,
                Limit = 4096,
            });
            var model = inspection.Events.Where(e => e.Stream == "model").ToList();
            Assert.AreEqual(1L, model[0].ModelSequence, "the case missed the shell's first output");
            var bytes = model.Where(e => e.Data is not null).SelectMany(e => Convert.FromBase64String(e.Data!)).ToArray();
            StringAssert.Contains(Encoding.UTF8.GetString(bytes), "CASE-MCP-42");
            Assert.AreEqual((DiagnosticCaseCompletionState.Complete, true), (inspection.CompletionState, inspection.Intervals.Single().Valid));
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ResizeTerminal_LocalSessionUpdatesCaptureAndPtyAndReappliesCase()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage and PTY geometry are verified on Linux.");
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var start = await CallAsync(client, "start_bash_terminal", new()
        {
            ["width"] = 60,
            ["height"] = 10,
            ["workingDirectory"] = Path.GetTempPath(),
            ["recordCase"] = true,
            ["caseDirectory"] = root.Path,
            ["caseAuthorize"] = "reapplication-data",
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        var sessionId = start.GetProperty("sessionId").GetString()!;
        try
        {
            var resize = await CallAsync(client, "resize_terminal", new()
            {
                ["sessionId"] = sessionId,
                ["width"] = 45,
                ["height"] = 8,
            });
            Assert.IsTrue(resize.GetProperty("success").GetBoolean(), resize.ToString());

            // Completion of resize_terminal must include the model resize, without waiting for child output.
            var capture = await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(capture.GetProperty("success").GetBoolean(), capture.ToString());
            var geometry = capture.GetProperty("capture").GetProperty("geometry");
            Assert.AreEqual((45, 8), (geometry.GetProperty("columns").GetInt32(), geometry.GetProperty("rows").GetInt32()),
                capture.ToString());
            var listed = await CallAsync(client, "list_terminals", new());
            var session = TestSeq.Single(listed.GetProperty("sessions").EnumerateArray(),
                s => s.GetProperty("sessionId").GetString() == sessionId);
            Assert.AreEqual((45, 8), (session.GetProperty("width").GetInt32(), session.GetProperty("height").GetInt32()),
                listed.ToString());

            // The complete marker is computed by bash, so echoed command text cannot satisfy the wait.
            var nonce = Guid.NewGuid().ToString("N")[..8];
            var input = await CallAsync(client, "send_terminal_input", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = $"printf '\\nPTY-{nonce}-%s:%s\\n' \"$((20+22))\" \"$(stty size)\"\r",
            });
            Assert.IsTrue(input.GetProperty("success").GetBoolean(), input.ToString());
            var wait = await CallAsync(client, "wait_for_terminal_text", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = $"PTY-{nonce}-42:8 45",
                ["timeoutSeconds"] = 10,
            });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());

            var mark = await CallAsync(client, "mark_diagnostic_case", new()
            {
                ["sessionId"] = sessionId,
                ["label"] = "resized",
            });
            Assert.IsTrue(mark.GetProperty("success").GetBoolean(), mark.ToString());
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(stop.GetProperty("success").GetBoolean(), stop.ToString());
            var path = stop.GetProperty("case").GetProperty("path").GetString()!;
            var inspect = await CallAsync(client, "inspect_diagnostic_case", new() { ["path"] = path, ["limit"] = 4096 });
            Assert.IsTrue(inspect.GetProperty("success").GetBoolean(), inspect.ToString());
            var modelResize = TestSeq.Single(inspect.GetProperty("inspection").GetProperty("events").EnumerateArray(),
                e => e.GetProperty("stream").GetString() == "model" && e.GetProperty("kind").GetString() == "resize");
            Assert.AreEqual((45, 8), (modelResize.GetProperty("width").GetInt32(), modelResize.GetProperty("height").GetInt32()),
                modelResize.ToString());
            var reapplied = await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "resized" });
            Assert.IsTrue(reapplied.GetProperty("success").GetBoolean(), reapplied.ToString());
            Assert.AreEqual("matched", reapplied.GetProperty("reapplication").GetProperty("comparison").GetString(), reapplied.ToString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task ResizeTerminal_ExitedSessionUpdatesCaptureAndReportedGeometry()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("The local bash session is verified on Linux.");
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await StartLocalSessionAsync(client);
        try
        {
            var liveResize = await CallAsync(client, "resize_terminal", new()
            {
                ["sessionId"] = sessionId,
                ["width"] = 45,
                ["height"] = 8,
            });
            Assert.IsTrue(liveResize.GetProperty("success").GetBoolean(), liveResize.ToString());
            var retained = SessionManager.GetSession(sessionId);
            Assert.IsNotNull(retained);
            var exit = await CallAsync(client, "send_terminal_input", new() { ["sessionId"] = sessionId, ["text"] = "exit 0\r" });
            Assert.IsTrue(exit.GetProperty("success").GetBoolean(), exit.ToString());
            using var exitTimeout = CancellationTokenSource.CreateLinkedTokenSource(TestCancellationToken);
            exitTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            Assert.AreEqual(0, await retained.WaitForExitAsync(exitTimeout.Token));

            var resize = await CallAsync(client, "resize_terminal", new()
            {
                ["sessionId"] = sessionId,
                ["width"] = 40,
                ["height"] = 7,
            });
            Assert.IsTrue(resize.GetProperty("success").GetBoolean(), resize.ToString());
            var capture = await CallAsync(client, "capture_terminal_screen", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(capture.GetProperty("success").GetBoolean(), capture.ToString());
            var geometry = capture.GetProperty("capture").GetProperty("geometry");
            Assert.AreEqual((40, 7), (geometry.GetProperty("columns").GetInt32(), geometry.GetProperty("rows").GetInt32()),
                capture.ToString());
            var listed = await CallAsync(client, "list_terminals", new());
            var session = TestSeq.Single(listed.GetProperty("sessions").EnumerateArray(),
                s => s.GetProperty("sessionId").GetString() == sessionId);
            Assert.IsTrue(session.GetProperty("hasExited").GetBoolean(), listed.ToString());
            Assert.AreEqual((40, 7), (session.GetProperty("width").GetInt32(), session.GetProperty("height").GetInt32()),
                listed.ToString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(1)]
    [DataRow(1_000_000)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task StartBash_ScrollbackParameter(int? scrollback)
    {
        // The session's terminal has the requested capacity (absent: none), as the case's recorded configuration
        // shows; out-of-range values are refused as invalid requests without a session.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();

        foreach (var invalid in new[] { 0, 1_000_001, -1 })
        {
            var refused = await CallAsync(client, "start_bash_terminal", new() { ["scrollback"] = invalid });
            Assert.IsFalse(refused.GetProperty("success").GetBoolean(), refused.ToString());
            StringAssert.Contains(refused.GetProperty("message").GetString(), "invalid-request: scrollback must be 1 to 1,000,000 rows", refused.ToString());
        }
        Assert.IsEmpty(SessionManager.GetAllSessions(), "a refused scrollback left a session");

        var arguments = new Dictionary<string, object?>
        {
            ["workingDirectory"] = Path.GetTempPath(),
            ["recordCase"] = true,
            ["caseDirectory"] = root.Path,
            ["caseAuthorize"] = "reapplication-data",
        };
        if (scrollback is { } rows)
            arguments["scrollback"] = rows;
        var start = await CallAsync(client, "start_bash_terminal", arguments);
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        var sessionId = start.GetProperty("sessionId").GetString()!;
        try
        {
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            Assert.IsTrue(stop.GetProperty("success").GetBoolean(), stop.ToString());
            var path = stop.GetProperty("case").GetProperty("path").GetString()!;
            var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json"))).RootElement
                .GetProperty("checkpoint").GetProperty("configuration");
            Assert.AreEqual(scrollback, configuration.TryGetProperty("scrollbackCapacity", out var recorded) ? recorded.GetInt32() : null,
                configuration.ToString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_RecoverAndReapplyFromMatchTheEngineAndTheReapplier()
    {
        // Ticket 13: recover_diagnostic_case equals the engine's recovery and the checkpoint line the target wrote, and
        // its refusals the engine's; reapply_diagnostic_case's `from` equals the reapplier's, origin included; the tool
        // descriptions name the new origin and the `from` refusals.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var terminal = await StartAttachedAppAsync(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        var engine = new TerminalDiagnostics(terminal, "McpRecover");

        var unknown = await CallAsync(client, "recover_diagnostic_case", new() { ["sessionId"] = "no-such-session" });
        Assert.AreEqual("session-not-found", unknown.GetProperty("recovery").GetProperty("problem").GetProperty("code").GetString());
        var tooLong = new string('x', 65);
        var invalid = await CallAsync(client, "recover_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = tooLong });
        AssertJsonEquals(engine.RecoverCase(tooLong), invalid.GetProperty("recovery"), "invalid label");
        var mark = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = "mcp-mark" });
        Assert.IsTrue(mark.GetProperty("success").GetBoolean(), mark.ToString());
        // A model event between the mark and the recovery, so the mark is before the recovery's interval.
        var marked = terminal.CurrentModelSequence;
        terminal.Resize(42, 6);
        for (var i = 0; i < 500 && terminal.CurrentModelSequence == marked; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.AreNotEqual(marked, terminal.CurrentModelSequence, "fixture: the resize raised no model event");
        var recovery = await CallAsync(client, "recover_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = "mcp-recover" });
        Assert.IsTrue(recovery.GetProperty("success").GetBoolean(), recovery.ToString());
        StringAssert.Contains(recovery.GetProperty("message").GetString(), "a new origin");
        var recorder = terminal.DiagnosticCase!;
        var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
        var path = stop.GetProperty("case").GetProperty("path").GetString()!;
        var none = await CallAsync(client, "recover_diagnostic_case", new() { ["sessionId"] = sessionId });
        AssertJsonEquals(engine.RecoverCase(), none.GetProperty("recovery"), "recover without a case");
        var line = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 4096 }).Events
            .Single(e => e.Checkpoint?.Label == "mcp-recover");
        Assert.AreEqual("recovery", line.Checkpoint!.Trigger);
        AssertJsonEquals(new DiagnosticCaseRecoverResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = recorder.CaseId,
            Label = "mcp-recover",
            CheckpointOrdinal = line.Checkpoint.Ordinal,
            ModelSequence = line.ModelSequence,
            Status = "complete",
        }, recovery.GetProperty("recovery"), "recovery");

        foreach (var (arguments, request, code, origin) in new (Dictionary<string, object?>, DiagnosticCaseReapplyRequest, string?, string?)[]
        {
            (new() { ["path"] = path, ["to"] = "stop" }, new() { Path = path, ToLabel = "stop" }, null, "start"),
            (new() { ["path"] = path, ["to"] = "stop", ["from"] = "mcp-recover" }, new() { Path = path, ToLabel = "stop", From = "mcp-recover" }, null, "mcp-recover"),
            (new() { ["path"] = path, ["to"] = "stop", ["from"] = $"case:{line.CaseSequence}" }, new() { Path = path, ToLabel = "stop", From = $"case:{line.CaseSequence}" }, null, "mcp-recover"),
            (new() { ["path"] = path, ["to"] = "stop", ["from"] = "mcp-mark" }, new() { Path = path, ToLabel = "stop", From = "mcp-mark" }, "not-an-origin", null),
            (new() { ["path"] = path, ["to"] = "stop", ["from"] = "nobody" }, new() { Path = path, ToLabel = "stop", From = "nobody" }, "unknown-label", null),
            (new() { ["path"] = path, ["to"] = "mcp-mark", ["from"] = "mcp-recover" }, new() { Path = path, ToLabel = "mcp-mark", From = "mcp-recover" }, "beyond-interval", null),
        })
        {
            var result = await CallAsync(client, "reapply_diagnostic_case", arguments);
            var expected = DiagnosticCaseReapplier.Reapply(request);
            Assert.AreEqual((code, origin), (expected.Problem?.Code, expected.Origin?.Label ?? expected.Origin?.Trigger), $"fixture: {string.Join(" ", arguments.Values)}: {expected.Problem?.Message}");
            Assert.AreEqual(expected.Outcome == DiagnosticOutcome.Captured, result.GetProperty("success").GetBoolean(), result.ToString());
            AssertJsonEquals(expected, result.GetProperty("reapplication"), string.Join(" ", arguments.Values), "runPath");
        }

        var tools = await client.ListToolsAsync();
        StringAssert.Contains(tools.Single(t => t.Name == "recover_diagnostic_case").Description!, "new origin", "the recover description");
        var fromParameter = tools.Single(t => t.Name == "reapply_diagnostic_case").JsonSchema.GetProperty("properties").GetProperty("from").GetProperty("description").GetString()!;
        foreach (var term in new[] { "start", "not-an-origin", "unknown-label", "unknown-case-sequence", "unknown-checkpoint", "beyond-interval" })
            StringAssert.Contains(fromParameter, term, "the from description");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_PendingInputFaults()
    {
        // A bash session prints the first byte of a scalar, pauses, then the rest; start_diagnostic_case runs during
        // the pause. The start is complete and holds the byte; re-application matches; the pending-* faults differ at
        // the start and are not applicable where the holder is empty (ticket 12).
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var session = await CallAsync(client, "start_bash_terminal", new() { ["workingDirectory"] = Path.GetTempPath() });
        Assert.IsTrue(session.GetProperty("success").GetBoolean(), session.ToString());
        var sessionId = session.GetProperty("sessionId").GetString()!;
        string path;
        try
        {
            // Both markers are assembled at run time so the echoed command line contains neither: a wait would otherwise
            // be satisfied by the echo, before the pause.
            await CallAsync(client, "send_terminal_input", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = "x=MCP-PEN; printf \"${x}DING-\\346\"; sleep 12; printf \"\\274\\242 ${x}DING-DONE\\n\"\r",
            });
            var wait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = sessionId, ["text"] = "MCP-PENDING-", ["timeoutSeconds"] = 10 });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());
            var start = await CallAsync(client, "start_diagnostic_case", new()
            {
                ["sessionId"] = sessionId,
                ["directory"] = root.Path,
                ["authorize"] = "reapplication-data",
            });
            Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
            var checkpoint = start.GetProperty("case").GetProperty("checkpoint");
            Assert.AreEqual("complete", checkpoint.GetProperty("status").GetString(), $"fixture: the start was not taken during the pause: {checkpoint}");
            var done = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = sessionId, ["text"] = "MCP-PENDING-DONE", ["timeoutSeconds"] = 30 });
            Assert.IsTrue(done.GetProperty("found").GetBoolean(), done.ToString());
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            path = stop.GetProperty("case").GetProperty("path").GetString()!;
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }

        // The start line (checksum, tab, JSON) must hold the pending byte: under load the pause can end before the start.
        var startLine = File.ReadLines(Path.Combine(path, "events.jsonl")).Select(l => JsonDocument.Parse(l[(l.IndexOf('\t') + 1)..]).RootElement)
            .First(e => e.TryGetProperty("checkpoint", out var c) && c.GetProperty("trigger").GetString() == "start");
        Assert.AreEqual("5g==", startLine.GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("utf8").GetString(),
            "fixture: the start was not taken during the pause (a 12 s window)");
        var matched = (await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "stop" })).GetProperty("reapplication");
        Assert.AreEqual("matched", matched.GetProperty("comparison").GetString(), matched.ToString());
        var result = (await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "start", ["injectFault"] = "pending-input" }))
            .GetProperty("reapplication");
        Assert.AreEqual(("different", true), (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), result.ToString());
        CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
            .Select(d => d.GetProperty("path").GetString()).ToList(), "pendingInput.utf8");
        var refused = (await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "start", ["injectFault"] = "pending-escape" }))
            .GetProperty("reapplication");
        Assert.AreEqual("unavailable", refused.GetProperty("comparison").GetString(), refused.ToString());
        StringAssert.StartsWith(refused.GetProperty("comparisonReason").GetString(), "fault-not-applicable");

        // The tool descriptions: the reapply tool names the faults, and the start tool no longer lists pending input among
        // the surfaces a start cannot restore (review XR#1).
        var tools = await client.ListToolsAsync();
        var reapplyTool = tools.Single(t => t.Name == "reapply_diagnostic_case");
        var faultParameter = reapplyTool.JsonSchema.GetProperty("properties").GetProperty("injectFault").GetProperty("description").GetString()!;
        foreach (var fault in new[] { "pending-input", "pending-escape", "pending-ground-escape", "pending-framer" })
            StringAssert.Contains(faultParameter, fault, "the injectFault description");
        var startDescription = tools.Single(t => t.Name == "start_diagnostic_case").Description!;
        StringAssert.Contains(startDescription, "a DCS in progress, graphics", "the start description's unsupported surfaces");
        Assert.IsFalse(startDescription.Contains("pending input, a DCS", StringComparison.Ordinal), "the start description still lists pending input as unsupported");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_TitleAndMarkFaults()
    {
        // A bash session recorded from its first byte pushes a title and emits OSC 133 marks: reapply_diagnostic_case's
        // title-stack and command-mark faults differ at the paths they name and are labelled (ticket 11).
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var start = await CallAsync(client, "start_bash_terminal", new()
        {
            ["workingDirectory"] = Path.GetTempPath(),
            ["recordCase"] = true,
            ["caseDirectory"] = root.Path,
            ["caseAuthorize"] = "reapplication-data",
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        var sessionId = start.GetProperty("sessionId").GetString()!;
        string path;
        try
        {
            await CallAsync(client, "send_terminal_input", new()
            {
                ["sessionId"] = sessionId,
                ["text"] = "printf '\\033]22;\\007\\033]133;A\\007$ MCP-FAULTS-%s\\n\\033]133;D;0\\007' $((1+1))\r",
            });
            var wait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = sessionId, ["text"] = "MCP-FAULTS-2", ["timeoutSeconds"] = 10 });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());
            var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
            path = stop.GetProperty("case").GetProperty("path").GetString()!;
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = sessionId });
        }

        foreach (var (fault, faultPath) in new[] { ("title-stack", "titles.stack[0].window"), ("command-mark", "commandMarks[0].column") })
        {
            var result = (await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "stop", ["injectFault"] = fault }))
                .GetProperty("reapplication");
            Assert.AreEqual(("different", true), (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), result.ToString());
            CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
                .Select(d => d.GetProperty("path").GetString()).ToList(), faultPath, fault);
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task CaseLiveStart_ReapplyFromTheStartMatchesTheReapplier()
    {
        // Ticket 09: a case started over MCP on a running application owns a text-state/2 start, and re-applies from
        // it to matched through reapply_diagnostic_case, equal to the reapplier's own result.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var terminal = await StartAttachedAppAsync();
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        var start = await CallAsync(client, "start_diagnostic_case", new()
        {
            ["sessionId"] = sessionId,
            ["directory"] = root.Path,
            ["authorize"] = "reapplication-data",
        });
        Assert.IsTrue(start.GetProperty("success").GetBoolean(), start.ToString());
        var checkpoint = start.GetProperty("case").GetProperty("checkpoint");
        Assert.AreEqual(("text-state/2", "complete"), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()),
            checkpoint.ToString());
        terminal.Resize(30, 6);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.Width == 30 && s.ContainsText("STYLED"), TimeSpan.FromSeconds(10), "re-rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        var mark = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = "resized" });
        Assert.IsTrue(mark.GetProperty("success").GetBoolean(), mark.ToString());
        var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
        var path = stop.GetProperty("case").GetProperty("path").GetString()!;

        var inspect = await CallAsync(client, "inspect_diagnostic_case", new() { ["path"] = path });
        var expected = JsonSerializer.SerializeToElement(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }),
            DiagnosticsJsonContext.Default.DiagnosticCaseInspection);
        Assert.IsTrue(JsonElement.DeepEquals(expected, inspect.GetProperty("inspection")), "MCP inspect differs from the inspector");
        foreach (var label in new[] { "start", "resized", "stop" })
        {
            var result = await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = label });
            var reapplied = DiagnosticCaseReapplier.Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = label });
            Assert.AreEqual("matched", reapplied.Comparison, $"{label}: {reapplied.ComparisonReason} {reapplied.Problem?.Message}");
            Assert.IsTrue(result.GetProperty("success").GetBoolean(), result.ToString());
            AssertJsonEquals(reapplied, result.GetProperty("reapplication"), label, "runPath");
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_MarkAndReapplyMatchTheEngineAndTheReapplier()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        // Recorded from construction, so the case is re-applicable.
        await using var terminal = await StartAttachedAppAsync(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        await StartServerAsync();
        await using var client = await CreateClientAsync();
        var sessionId = await ConnectAttachedAsync(client);
        var engine = new TerminalDiagnostics(terminal, "McpMark");

        var unknown = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = "no-such-session" });
        Assert.AreEqual("session-not-found", unknown.GetProperty("mark").GetProperty("problem").GetProperty("code").GetString());
        var tooLong = new string('x', 65);
        var invalid = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = tooLong });
        AssertJsonEquals(engine.MarkCase(tooLong), invalid.GetProperty("mark"), "invalid label");
        var mark = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = sessionId, ["label"] = "mcp-mark" });
        Assert.IsTrue(mark.GetProperty("success").GetBoolean(), mark.ToString());
        var recorder = terminal.DiagnosticCase!;
        var stop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = sessionId });
        var path = stop.GetProperty("case").GetProperty("path").GetString()!;
        var none = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = sessionId });
        AssertJsonEquals(engine.MarkCase(), none.GetProperty("mark"), "mark without a case");
        var line = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 4096 }).Events
            .Single(e => e.Checkpoint?.Label == "mcp-mark");
        AssertJsonEquals(new DiagnosticCaseMarkResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = recorder.CaseId,
            Label = "mcp-mark",
            CheckpointOrdinal = line.Checkpoint!.Ordinal,
            ModelSequence = line.ModelSequence,
            StateRecorded = true,
        }, mark.GetProperty("mark"), "mark");

        foreach (var (arguments, request) in new (Dictionary<string, object?>, DiagnosticCaseReapplyRequest)[]
        {
            (new() { ["path"] = path, ["to"] = "mcp-mark" }, new() { Path = path, ToLabel = "mcp-mark" }),
            (new() { ["path"] = path, ["to"] = "mcp-mark", ["preview"] = "ansi" }, new() { Path = path, ToLabel = "mcp-mark", Previews = ["ansi"] }),
            (new() { ["path"] = path, ["to"] = "stop", ["injectFault"] = "cell-text,title", ["maxDifferences"] = 1 },
                new() { Path = path, ToLabel = "stop", Faults = ["cell-text", "title"], MaxDifferences = 1 }),
            (new() { ["path"] = path, ["to"] = $"case:{line.CaseSequence}", ["preview"] = "text,html" },
                new() { Path = path, ToCaseSequence = line.CaseSequence, Previews = ["text", "html"] }),
            (new() { ["path"] = path, ["to"] = "999999" }, new() { Path = path, ToModelSequence = 999_999 }),
        })
        {
            var result = await CallAsync(client, "reapply_diagnostic_case", arguments);
            var expected = DiagnosticCaseReapplier.Reapply(request);
            Assert.AreNotEqual("no-valid-interval", expected.Problem?.Code, "fixture: the case is not re-applicable");
            Assert.AreEqual(expected.Outcome == DiagnosticOutcome.Captured, result.GetProperty("success").GetBoolean(), result.ToString());
            AssertJsonEquals(expected, result.GetProperty("reapplication"), string.Join(" ", arguments.Values), "runPath");
            if (expected.Outcome == DiagnosticOutcome.Captured)
                StringAssert.Contains(result.GetProperty("message").GetString(), $"({TerminalDiagnostics.Hex1bBuild}), same build", result.ToString());
        }

        // Ticket 14: the message names a different recording build; a refusal's message names the failed check with the
        // case's and this build's values.
        var manifestFile = Path.Combine(path, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!.AsObject();
        manifest["identity"]!["hex1bBuild"] = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        var other = await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "mcp-mark" });
        Assert.IsTrue(other.GetProperty("success").GetBoolean(), other.ToString());
        StringAssert.Contains(other.GetProperty("message").GetString(), "(0123456789abcdef0123456789abcdef), different builds", other.ToString());
        manifest["formatVersion"] = 3;
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        var incompatible = await CallAsync(client, "reapply_diagnostic_case", new() { ["path"] = path, ["to"] = "mcp-mark" });
        Assert.IsFalse(incompatible.GetProperty("success").GetBoolean(), incompatible.ToString());
        StringAssert.Contains(incompatible.GetProperty("message").GetString(), "[formatVersion: the case declares 3; this build 2]", incompatible.ToString());

        // A local session records from its first byte, marks, and re-applies to matched.
        var local = await CallAsync(client, "start_bash_terminal", new()
        {
            ["width"] = 60,
            ["height"] = 10,
            ["workingDirectory"] = Path.GetTempPath(),
            ["recordCase"] = true,
            ["caseDirectory"] = root.Path,
            ["caseAuthorize"] = "reapplication-data",
        });
        var localId = local.GetProperty("sessionId").GetString()!;
        try
        {
            await CallAsync(client, "send_terminal_input", new() { ["sessionId"] = localId, ["text"] = "echo MARK-$((40+2))\r" });
            var wait = await CallAsync(client, "wait_for_terminal_text", new() { ["sessionId"] = localId, ["text"] = "MARK-42", ["timeoutSeconds"] = 10 });
            Assert.IsTrue(wait.GetProperty("found").GetBoolean(), wait.ToString());
            var localMark = await CallAsync(client, "mark_diagnostic_case", new() { ["sessionId"] = localId, ["label"] = "echoed" });
            Assert.IsTrue(localMark.GetProperty("success").GetBoolean(), localMark.ToString());
            var localStop = await CallAsync(client, "stop_diagnostic_case", new() { ["sessionId"] = localId });
            var reapplied = await CallAsync(client, "reapply_diagnostic_case", new()
            {
                ["path"] = localStop.GetProperty("case").GetProperty("path").GetString(),
                ["to"] = "echoed",
            });
            Assert.AreEqual("matched", reapplied.GetProperty("reapplication").GetProperty("comparison").GetString(), reapplied.ToString());
        }
        finally
        {
            await CallAsync(client, "remove_session", new() { ["sessionId"] = localId });
        }
    }

    // The tool's JSON equals the contract object's, apart from fields that differ per run.
    private static void AssertJsonEquals<T>(T expected, JsonElement actual, string what, params string[] ignored)
    {
        var expectedNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(expected, DiagnosticsJsonContext.Default.GetTypeInfo(typeof(T))!))!.AsObject();
        var actualNode = System.Text.Json.Nodes.JsonNode.Parse(actual.GetRawText())!.AsObject();
        foreach (var field in ignored)
        {
            expectedNode.Remove(field);
            actualNode.Remove(field);
        }
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(expectedNode, actualNode), $"{what}: MCP differs.\nexpected: {expectedNode}\nmcp:      {actualNode}");
    }

    private static void AssertCaseEquals(DiagnosticCaseResult engine, JsonElement client, string operation, params string[] volatileFields)
    {
        var expected = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(engine, DiagnosticsJsonContext.Default.DiagnosticCaseResult))!.AsObject();
        var actual = System.Text.Json.Nodes.JsonNode.Parse(client.GetRawText())!.AsObject();
        foreach (var field in volatileFields)
        {
            expected.Remove(field);
            actual.Remove(field);
        }

        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), $"MCP {operation} differs from the engine's.\nengine: {expected}\nmcp:    {actual}");
    }

    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    private sealed class CaseRoot : IDisposable
    {
        public CaseRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hex1b-mcp-case-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    // An attached app presented over a WebSocket, so its writes are observable delivery.
    private static async Task<Hex1bTerminal> StartDeliveryAppAsync()
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);
        var socket = new ControlledWebSocket();
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithPresentation(new WebSocketPresentationAdapter(socket, 40, 5))
            .WithHex1bApp(_ => new TextBlockWidget("DELIVERY-SENTINEL"))
            .WithDiagnostics(appName: "McpDelivery", forceEnable: true)
            .Build();
        _ = terminal.RunAsync();
        var client = new DiagnosticsSocketClient();
        for (var attempt = 0; attempt < 100; attempt++)
        {
            if (File.Exists(socketPath) && await client.TryProbeAsync(socketPath) is { Success: true })
                break;
            await Task.Delay(50);
        }

        for (var i = 0; i < 300; i++)
        {
            lock (socket.Sent)
                if (socket.Sent.Any(b => Encoding.UTF8.GetString(b).Contains("DELIVERY-SENTINEL", StringComparison.Ordinal)))
                    break;
            await Task.Delay(20);
        }

        await Task.Delay(300);
        return terminal;
    }

    private static JsonElement WithoutAcquisition(JsonElement result)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText())!;
        (node["identity"] as System.Text.Json.Nodes.JsonObject)?.Remove("acquisition");
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    // === Helpers ===

    // Wall-clock record times differ in serialized precision; compare everything else exactly.
    private static JsonElement WithoutWallClock(JsonElement milestone)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(milestone.GetRawText())!;
        if (node["input"] is System.Text.Json.Nodes.JsonObject input)
        {
            input.Remove("acceptedAt");
            input.Remove("processedAt");
        }
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private async Task<JsonElement> CallAsync(McpClient client, string tool, Dictionary<string, object?> arguments)
    {
        var result = await client.CallToolAsync(tool, arguments, cancellationToken: TestCancellationToken);
        var text = result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text;
        Assert.IsNotNull(text, $"{tool} returned no text content");
        Assert.IsTrue(text.TrimStart().StartsWith('{'), $"{tool} returned a non-JSON reply: {text}");
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

    private static async Task<Hex1bTerminal> StartAttachedAppAsync(DiagnosticCaseStartRequest? recordCase = null)
    {
        var socketPath = McpDiagnosticsPresentationFilter.GetSocketPath();
        for (var attempt = 0; attempt < 100 && File.Exists(socketPath); attempt++)
            await Task.Delay(50);

        var builder = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithHeadless()
            .WithScrollback(50)
            .WithHex1bApp(_ => new ThemePanelWidget(
                theme => theme.Set(GlobalTheme.ForegroundColor, Styled),
                new TextBlockWidget("STYLED")))
            .WithDiagnostics(appName: "McpAttached", forceEnable: true);
        var terminal = (recordCase is null ? builder : builder.WithDiagnosticCase(recordCase)).Build();
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
