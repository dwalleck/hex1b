using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Automation;
using Hex1b.Diagnostics;
using Hex1b.Flow;
using Hex1b.Nodes;
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
public partial class CaptureContractCliTests
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

        AssertUnavailable(root, "identity.applicationFrame", "is not an application frame");
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
        Assert.IsTrue(frame.GetProperty("available").GetBoolean(), "a diagnostics-enabled app publishes application frames");
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

    private const string FrameSentinel = "SENTINEL-CLI-5d1a";

    [TestMethod]
    public async Task AppTree_Json_EqualsTheEnginesApplicationFrame()
    {
        await using var target = await StartFrameAppAsync();

        var (cli, engine) = await CaptureStableAsync(target, "app", "tree", Pid, "--json");

        Assert.AreEqual("captured", cli.GetProperty("outcome").GetString());
        var identity = cli.GetProperty("identity");
        Assert.AreEqual("application-frame", identity.GetProperty("sourceLayer").GetString());
        Assert.AreEqual(engine.Frame!.FrameId, identity.GetProperty("applicationFrame").GetInt64());
        var frame = cli.GetProperty("frame");
        Assert.AreEqual(1, frame.GetProperty("popups").GetArrayLength(), "fixture: the popup is missing");
        Assert.AreEqual("text-box", frame.GetProperty("focusedEditor").GetProperty("kind").GetString(), "fixture: the TextBox should have focus");
        Assert.IsTrue(JsonElement.DeepEquals(ToJson(engine), cli),
            $"CLI result differs from the engine's.\nengine: {ToJson(engine)}\ncli:    {cli}");
    }

    [TestMethod]
    public async Task AppTree_EditorText_IncludedOnlyWhenAuthorized()
    {
        await using var target = await StartFrameAppAsync();

        var (_, plainJson, _) = await RunCliAsync("app", "tree", Pid, "--json");
        var (_, plainText, _) = await RunCliAsync("app", "tree", Pid, "--focus", "--popups");
        var (authorizedExit, authorizedJson, authorizedErr) = await RunCliAsync("app", "tree", Pid, "--json", "--authorize", "editor-text");
        var (_, authorizedText, _) = await RunCliAsync("app", "tree", Pid, "--focus", "--authorize", "editor-text");

        Assert.IsFalse(plainJson.Contains(FrameSentinel, StringComparison.Ordinal), "sentinel in default CLI JSON");
        Assert.IsFalse(plainText.Contains(FrameSentinel, StringComparison.Ordinal), "sentinel in default CLI text");
        Assert.AreEqual(0, authorizedExit, authorizedErr);
        using var json = JsonDocument.Parse(authorizedJson);
        Assert.AreEqual(FrameSentinel, json.RootElement.GetProperty("frame").GetProperty("focusedEditor").GetProperty("text").GetString());
        AssertCoverage(json.RootElement, "editor-text", "included");
        StringAssert.Contains(authorizedText, FrameSentinel);
    }

    [TestMethod]
    public async Task AppTree_Text_ShowsFrameTreeFocusAndPopups()
    {
        await using var target = await StartFrameAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync("app", "tree", Pid, "--focus", "--popups");

        Assert.AreEqual(0, exitCode, stderr);
        StringAssert.StartsWith(stdout, "Frame ");
        StringAssert.Contains(stdout, "TextBoxNode [FOCUSED]");
        StringAssert.Contains(stdout, "\"FRAMEAPP\"");
        StringAssert.Contains(stdout, "Popups:");
        StringAssert.Contains(stdout, "anchor=ButtonNode");
        StringAssert.Contains(stdout, "Focus: TextBoxNode");
        StringAssert.Contains(stdout, "Editor: text-box");
    }

    [TestMethod]
    public async Task AppTree_LocalHostedPty_ReportsNoApplicationLayer()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = new TerminalHostConfig { Width = 40, Height = 6 };
        if (OperatingSystem.IsWindows())
        {
            config.Command = "powershell";
            config.Arguments = ["-NoProfile", "-Command", "Write-Host HOSTED; Start-Sleep 60"];
        }
        else
        {
            config.Command = "/bin/sh";
            config.Arguments = ["-c", "echo HOSTED; exec sleep 60"];
        }

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        try
        {
            await WaitForSocketAsync(cts.Token);

            var (exitCode, stdout, stderr) = await RunCliAsync("app", "tree", Pid, "--json");

            Assert.AreEqual(1, exitCode, stdout);
            using var json = JsonDocument.Parse(stdout);
            Assert.AreEqual("unavailable", json.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("no-application-layer", json.RootElement.GetProperty("problem").GetProperty("code").GetString());
            StringAssert.Contains(stderr, "no-application-layer");
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task AppTree_FlowStep_ReturnsTheActiveStepsFrame()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget("FLOWSTEP"), new TextBoxWidget("step input")]));
                await step.WaitForCompletionAsync(TestContext.Current.CancellationToken);
            })
            .WithHeadless()
            .WithDimensions(40, 8)
            .WithDiagnostics(appName: "CliFlow", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("FLOWSTEP"), TimeSpan.FromSeconds(10), "flow step rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);

        var (exitCode, stdout, stderr) = await RunCliAsync("app", "tree", Pid, "--json");

        Assert.AreEqual(0, exitCode, $"{stderr}\n{stdout}");
        using var json = JsonDocument.Parse(stdout);
        var frame = json.RootElement.GetProperty("frame");
        StringAssert.Contains(frame.GetProperty("root").ToString(), "\"FLOWSTEP\"", "the active step's tree is missing");
        Assert.AreEqual("text-box", frame.GetProperty("focusedEditor").GetProperty("kind").GetString());
        Assert.IsTrue(frame.TryGetProperty("timings", out _), "a diagnostics-enabled flow step reports timings");
    }

    [TestMethod]
    public async Task AppTree_DeepTreeAndControlCharacters_CrossTheSocketSafely()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        Hex1bWidget deep = new ListWidget(["\u001b]0;pwned\u0007item\u202e\u2028\U000E0041", "second"]);
        for (var i = 0; i < 40; i++)
            deep = new VStackWidget([deep]);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 6)
            .WithHeadless()
            .WithHex1bApp(_ => new VStackWidget([new TextBlockWidget("DEEPAPP"), deep]))
            .WithDiagnostics(appName: "CliDeep", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("DEEPAPP"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);

        var (jsonExit, json, jsonErr) = await RunCliAsync("app", "tree", Pid, "--json");
        var (textExit, text, textErr) = await RunCliAsync("app", "tree", Pid);

        Assert.AreEqual(0, jsonExit, jsonErr);
        StringAssert.Contains(json, "ListNode");
        Assert.AreEqual(0, textExit, textErr);
        Assert.IsFalse(text.Contains('\u001b'), "application text reached the terminal with a raw ESC");
        Assert.IsFalse(text.Contains('\u202e'), "a bidirectional override reached the terminal unescaped");
        Assert.IsFalse(text.Contains('\u2028'), "a line separator reached the terminal unescaped");
        Assert.IsFalse(text.Contains("\U000E0041", StringComparison.Ordinal), "a tag character reached the terminal unescaped");
        StringAssert.Contains(text, "\\u001b]0;pwned");
    }

    [TestMethod]
    public async Task Milestone_AttachedApp_SendThenCaptureEachStageLikeTheEngine()
    {
        await using var target = await StartFrameAppAsync();

        var (sendExit, sendOut, sendErr) = await RunCliAsync("keys", Pid, "--key", "Q", "--ctrl", "--json");
        Assert.AreEqual(0, sendExit, sendErr);
        using var accepted = JsonDocument.Parse(sendOut);
        var inputId = accepted.RootElement.GetProperty("lastId").GetInt64();
        StringAssert.Contains(accepted.RootElement.GetProperty("meaning").GetString(), "Queued for the application");

        foreach (var stage in new[] { "input-processed", "frame-published", "model-applied" })
        {
            var (exitCode, stdout, stderr) = await RunCliAsync("capture", "screenshot", Pid, "--json",
                "--milestone", stage, "--input-id", inputId.ToString());
            Assert.AreEqual(0, exitCode, $"{stage}: {stderr}");
            using var json = JsonDocument.Parse(stdout);
            var milestone = json.RootElement.GetProperty("milestone");
            Assert.AreEqual(stage, milestone.GetProperty("milestone").GetString());
            Assert.IsTrue(milestone.GetProperty("met").GetBoolean(), stage);
            StringAssert.Contains(stderr, $"Milestone {stage} for input {inputId}: met");

            var engine = await new TerminalDiagnostics(target, "CliFrames").CaptureAsync(new DiagnosticCaptureRequest
            {
                Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticContractNames.TryParse<DiagnosticMilestone>(stage, out var parsed) ? parsed : default, InputId = inputId },
            });
            var engineMilestone = System.Text.Json.JsonSerializer.SerializeToElement(engine.Milestone!, DiagnosticsJsonContext.Default.Options);
            Assert.IsTrue(JsonElement.DeepEquals(WithoutTimestamps(engineMilestone), WithoutTimestamps(milestone)),
                $"{stage}: CLI milestone differs from the engine's.\nengine: {engineMilestone}\ncli:    {milestone}");
        }

        var (_, plain, _) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "input-processed", "--input-id", inputId.ToString());
        var (_, raw, _) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "input-processed", "--input-id", inputId.ToString(),
            "--authorize", "raw-input");
        // Compare parsed values: the JSON encoder escapes '+', so a raw string search proves nothing.
        var plainInput = JsonDocument.Parse(plain).RootElement.GetProperty("milestone").GetProperty("input");
        var rawInput = JsonDocument.Parse(raw).RootElement.GetProperty("milestone").GetProperty("input");
        Assert.IsFalse(plainInput.TryGetProperty("payload", out _), $"raw key payload in a default CLI milestone result: {plainInput}");
        Assert.AreEqual("Q+Control", rawInput.GetProperty("payload").GetString(), "raw-input did not include the key payload");

        var (treeExit, tree, treeErr) = await RunCliAsync("app", "tree", Pid, "--json", "--milestone", "frame-published", "--input-id", inputId.ToString());
        Assert.AreEqual(0, treeExit, treeErr);
        using var treeJson = JsonDocument.Parse(tree);
        Assert.IsGreaterThanOrEqualTo(inputId, treeJson.RootElement.GetProperty("milestone").GetProperty("frame").GetProperty("processedInput").GetInt64());
    }

    [TestMethod]
    public async Task Milestone_KeysWithTextAndKey_PrintsOneJsonDocument()
    {
        await using var target = await StartFrameAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync("keys", Pid, "--text", "a", "--key", "Q", "--json");

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.ValueKind, $"two sends did not print one JSON array: {stdout}");
        var sends = json.RootElement.EnumerateArray().ToArray();
        Assert.HasCount(2, sends);
        Assert.IsGreaterThan(sends[0].GetProperty("lastId").GetInt64(), sends[1].GetProperty("firstId").GetInt64());
    }

    [TestMethod]
    public async Task Milestone_AbandonedSocketWaitReleasesItsPendingSlot()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(30, 4)
            .WithHeadless()
            .WithHex1bApp(_ => new ButtonWidget("GATE").OnClick(async _ =>
            {
                entered.TrySetResult();
                await gate.Task;
            }))
            .WithDiagnostics(appName: "CliAbandon", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        try
        {
            await WaitForSocketAsync(TestContext.Current.CancellationToken);
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("GATE"), TimeSpan.FromSeconds(10), "application rendered")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            var engine = new TerminalDiagnostics(terminal, "CliAbandon");
            await engine.TrackSendAsync(() => terminal.SendEventAsync(new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.Enter, '\r', Hex1b.Input.Hex1bModifiers.None)), "key");
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            var queued = (await engine.TrackSendAsync(() => terminal.SendEventAsync(
                new Hex1b.Input.Hex1bKeyEvent(Hex1b.Input.Hex1bKey.X, 'x', Hex1b.Input.Hex1bModifiers.None)), "key"))!.LastId;
            var tracker = terminal.InputMilestones!;

            using (var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix, System.Net.Sockets.SocketType.Stream,
                       System.Net.Sockets.ProtocolType.Unspecified))
            {
                await socket.ConnectAsync(new System.Net.Sockets.UnixDomainSocketEndPoint(McpDiagnosticsPresentationFilter.GetSocketPath()),
                    TestContext.Current.CancellationToken);
                var request = JsonSerializer.Serialize(new DiagnosticsRequest
                {
                    Method = TerminalDiagnostics.CaptureOperation,
                    Capture = new DiagnosticCaptureRequest
                    {
                        Milestone = new DiagnosticMilestoneRequest { Milestone = DiagnosticMilestone.InputProcessed, InputId = queued, TimeoutMs = 60_000 },
                    },
                }, DiagnosticsJsonContext.Default.DiagnosticsRequest);
                await socket.SendAsync(System.Text.Encoding.UTF8.GetBytes(request + "\n"), TestContext.Current.CancellationToken);
                for (var i = 0; i < 300 && tracker.PendingWaits < 1; i++)
                    await Task.Delay(10, TestContext.Current.CancellationToken);
                Assert.AreEqual(1, tracker.PendingWaits, "fixture: the socket wait never became pending");
            }

            for (var i = 0; i < 300 && tracker.PendingWaits > 0; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.AreEqual(0, tracker.PendingWaits, "a disconnected client's wait kept its pending slot");

            // The timeout bound through the CLI: timed out after the requested 300 ms, not the default 5 s.
            var stopwatch = System.Diagnostics.Stopwatch.StartNew();
            var (exitCode, stdout, _) = await RunCliAsync("capture", "screenshot", Pid, "--json",
                "--milestone", "input-processed", "--input-id", queued.ToString(), "--milestone-timeout", "300");
            var elapsed = stopwatch.Elapsed;
            Assert.AreEqual(1, exitCode);
            using var timedOut = JsonDocument.Parse(stdout);
            Assert.AreEqual("timed-out", timedOut.RootElement.GetProperty("outcome").GetString());
            Assert.IsFalse(timedOut.RootElement.GetProperty("milestone").GetProperty("met").GetBoolean());
            Assert.IsTrue(elapsed >= TimeSpan.FromMilliseconds(280) && elapsed < TimeSpan.FromSeconds(4),
                $"the CLI milestone wait took {elapsed.TotalMilliseconds:0} ms for a 300 ms timeout");
        }
        finally
        {
            gate.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Milestone_KeysReportsDeliveredTextWhenTheKeyFails()
    {
        await using var target = await StartFrameAppAsync();

        var (exitCode, stdout, stderr) = await RunCliAsync("keys", Pid, "--text", "a", "--key", "NoSuchKey", "--json");

        Assert.AreEqual(1, exitCode, "fixture: the key send must fail");
        using var json = JsonDocument.Parse(stdout);
        // The command makes two sends, so its JSON is an array whether or not the second fails.
        Assert.AreEqual(JsonValueKind.Array, json.RootElement.ValueKind, stdout);
        Assert.IsGreaterThan(0L, json.RootElement[0].GetProperty("lastId").GetInt64(), $"the delivered text's ids were not reported: {stdout} {stderr}");
    }

    [TestMethod]
    public async Task Milestone_SendToAnExitedPtyChildFailsWithoutAnId()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithPtyProcess("sh", "-c", "exit 0")
            .WithHeadless()
            .WithDimensions(30, 4)
            .WithDiagnostics(appName: "CliExited", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 200 && (terminal.Workload as Hex1bTerminalChildProcess)?.HasExited != true; i++)
            await Task.Delay(25, TestContext.Current.CancellationToken);
        Assert.IsTrue((terminal.Workload as Hex1bTerminalChildProcess)?.HasExited, "fixture: the child did not exit");

        var (textExit, textOut, textErr) = await RunCliAsync("keys", Pid, "--text", "hello", "--json");
        var (keyExit, keyOut, _) = await RunCliAsync("keys", Pid, "--key", "Enter", "--json");

        Assert.AreEqual(1, textExit, $"a send to an exited child reported success: {textOut}");
        StringAssert.Contains(textErr, "has exited");
        Assert.AreEqual(1, keyExit, $"a key to an exited child reported success: {keyOut}");
        Assert.IsFalse(textOut.Contains("lastId", StringComparison.Ordinal) || keyOut.Contains("lastId", StringComparison.Ordinal),
            "an undelivered send to an exited child was given an id");
    }

    [TestMethod]
    public async Task Milestone_InvalidNameIsAnInvalidRequest()
    {
        await using var target = await StartFrameAppAsync();

        var (exitCode, stdout, _) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "whenever", "--input-id", "1");

        Assert.AreEqual(1, exitCode);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreEqual("invalid-request", json.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual("invalid-milestone", json.RootElement.GetProperty("problem").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Milestone_LocalHostedPty_ReportsAcceptanceOnly()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = new TerminalHostConfig { Width = 40, Height = 6 };
        if (OperatingSystem.IsWindows())
        {
            config.Command = "powershell";
            config.Arguments = ["-NoProfile", "-Command", "Start-Sleep 60"];
        }
        else
        {
            config.Command = "/bin/sh";
            config.Arguments = ["-c", "exec sleep 60"];
        }

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        try
        {
            await WaitForSocketAsync(cts.Token);
            var (sendExit, sendOut, sendErr) = await RunCliAsync("keys", Pid, "--text", "hi", "--json");
            Assert.AreEqual(0, sendExit, sendErr);
            using var accepted = JsonDocument.Parse(sendOut);
            var inputId = accepted.RootElement.GetProperty("lastId").GetInt64().ToString();
            StringAssert.Contains(accepted.RootElement.GetProperty("meaning").GetString(), "child process");

            var (acceptedExit, _, acceptedErr) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "input-accepted", "--input-id", inputId);
            var (processedExit, processedOut, _) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "input-processed", "--input-id", inputId);

            Assert.AreEqual(0, acceptedExit, acceptedErr);
            Assert.AreEqual(1, processedExit);
            using var processed = JsonDocument.Parse(processedOut);
            Assert.AreEqual("unavailable", processed.RootElement.GetProperty("outcome").GetString());
            Assert.AreEqual("input-consumption-unobservable", processed.RootElement.GetProperty("problem").GetProperty("code").GetString());
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    public async Task Milestone_FlowStep_FrameMetAndModelApplicationUnobservable()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(_ => new VStackWidget([new TextBlockWidget("MSTEP"), new TextBoxWidget("")]));
                await step.WaitForCompletionAsync(TestContext.Current.CancellationToken);
            })
            .WithHeadless()
            .WithDimensions(40, 8)
            .WithDiagnostics(appName: "CliMilestoneFlow", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("MSTEP"), TimeSpan.FromSeconds(10), "flow step rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);

        var (_, sendOut, _) = await RunCliAsync("keys", Pid, "--key", "Q", "--json");
        var inputId = JsonDocument.Parse(sendOut).RootElement.GetProperty("lastId").GetInt64().ToString();
        var (treeExit, tree, treeErr) = await RunCliAsync("app", "tree", Pid, "--json", "--milestone", "frame-published", "--input-id", inputId);
        var (modelExit, model, _) = await RunCliAsync("capture", "screenshot", Pid, "--json", "--milestone", "model-applied", "--input-id", inputId);

        Assert.AreEqual(0, treeExit, treeErr);
        using var treeJson = JsonDocument.Parse(tree);
        Assert.AreEqual(treeJson.RootElement.GetProperty("frame").GetProperty("applicationInstanceId").GetString(),
            treeJson.RootElement.GetProperty("milestone").GetProperty("frame").GetProperty("applicationInstanceId").GetString());
        Assert.AreEqual(1, modelExit);
        Assert.AreEqual("model-application-unobservable",
            JsonDocument.Parse(model).RootElement.GetProperty("problem").GetProperty("code").GetString());
    }

    [TestMethod]
    public async Task Delivery_AttachedAppMatchesTheEngineAndHidesBytesByDefault()
    {
        var (target, _) = await StartDeliveryAppAsync();
        await using var owned = target;

        var (exitCode, stdout, stderr) = await RunCliAsync("capture", "delivery", Pid, "--json");
        var (rawExit, raw, rawErr) = await RunCliAsync("capture", "delivery", Pid, "--json", "--authorize", "native-output");
        var engine = new TerminalDiagnostics(target, "CliDelivery").CaptureDelivery(new DiagnosticDeliveryRequest());

        Assert.AreEqual(0, exitCode, stderr);
        using var json = JsonDocument.Parse(stdout);
        Assert.AreEqual("captured", json.RootElement.GetProperty("outcome").GetString());
        Assert.AreEqual("websocket", json.RootElement.GetProperty("deliveryLayer").GetString());
        var records = json.RootElement.GetProperty("records").EnumerateArray().ToList();
        Assert.IsNotEmpty(records, "fixture: the app's output produced no delivery records");
        Assert.IsTrue(records.All(r => !r.TryGetProperty("content", out _)), "bytes returned without native-output");
        var engineJson = JsonSerializer.SerializeToElement(engine, DiagnosticsJsonContext.Default.DiagnosticDeliveryResult);
        Assert.IsTrue(JsonElement.DeepEquals(WithoutAcquisition(engineJson), WithoutAcquisition(json.RootElement)),
            $"CLI delivery differs from the engine's.\nengine: {engineJson}\ncli:    {json.RootElement}");

        Assert.AreEqual(0, rawExit, rawErr);
        var contents = JsonDocument.Parse(raw).RootElement.GetProperty("records").EnumerateArray()
            .Where(r => r.TryGetProperty("content", out _))
            .Select(r => System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(r.GetProperty("content").GetString()!)));
        Assert.IsTrue(contents.Any(c => c.Contains("DELIVERY-SENTINEL", StringComparison.Ordinal)), "native-output did not return the written bytes");
    }

    [TestMethod]
    public async Task Delivery_TextOutputUnavailableAndInvalid()
    {
        var (target, _) = await StartDeliveryAppAsync();
        await using (target)
        {
            var (exitCode, text, stderr) = await RunCliAsync("capture", "delivery", Pid);
            var (invalidExit, _, invalidErr) = await RunCliAsync("capture", "delivery", Pid, "--limit", "0");

            Assert.AreEqual(0, exitCode, stderr);
            StringAssert.Contains(text, "Delivery layer: websocket");
            StringAssert.Contains(text, "accepted workload-output");
            Assert.AreEqual(1, invalidExit);
            StringAssert.Contains(invalidErr, "invalid-limit");
        }

        await using var headless = await StartAttachedAppAsync();
        var (headlessExit, _, headlessErr) = await RunCliAsync("capture", "delivery", Pid, "--json");
        Assert.AreEqual(1, headlessExit);
        StringAssert.Contains(headlessErr, "no-native-presentation");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_AttachedStartStatusStopInspectMatchTheEngine()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var target = await StartAttachedAppAsync();
        var engine = new TerminalDiagnostics(target, "CliAttached");

        var (startExit, start, startErr) = await RunCliAsync("capture", "case", "start", Pid, "--dir", root.Path,
            "--authorize", "reapplication-data", "--max-seconds", "300", "--json");
        Assert.AreEqual(0, startExit, startErr);
        var started = JsonDocument.Parse(start).RootElement.Clone();
        Assert.AreEqual("recording", started.GetProperty("state").GetString());
        Assert.AreEqual("live", started.GetProperty("startPath").GetString());
        AssertCaseEquals(engine.GetCaseStatus(), started, "start", "elapsedSeconds", "bytesWritten", "streams");

        // A live start writes its checkpoint asynchronously; compare status only after that write is complete.
        for (var attempt = 0; attempt < 500 && engine.GetCaseStatus().Checkpoints?.Written != 1; attempt++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.AreEqual(1L, engine.GetCaseStatus().Checkpoints!.Written, "fixture: the start checkpoint was never written");
        var status = await StableStatusAsync(engine, async () => (await RunCliAsync("capture", "case", "status", Pid, "--json")).Stdout);
        AssertCaseEquals(engine.GetCaseStatus(), status, "status", "elapsedSeconds");

        var recorder = target.DiagnosticCase!;
        var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
        Assert.AreEqual(0, stopExit, stopErr);
        var stopped = JsonDocument.Parse(stop).RootElement.Clone();
        Assert.AreEqual("requested", stopped.GetProperty("stopReason").GetString());
        AssertCaseEquals(recorder.Describe(), stopped, "stop", "elapsedSeconds");

        var path = stopped.GetProperty("path").GetString()!;
        var (inspectExit, inspect, inspectErr) = await RunCliAsync("capture", "case", "inspect", path, "--limit", "50", "--json");
        Assert.AreEqual(0, inspectExit, inspectErr);
        var expected = JsonSerializer.SerializeToElement(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 50 }),
            DiagnosticsJsonContext.Default.DiagnosticCaseInspection);
        var inspected = JsonDocument.Parse(inspect).RootElement;
        Assert.IsTrue(JsonElement.DeepEquals(expected, inspected), $"CLI inspect differs from the inspector.\ninspector: {expected}\ncli:       {inspected}");
        Assert.AreEqual("complete", inspected.GetProperty("completionState").GetString());
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_RefusalsAndTextOutput()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        var loose = Path.Combine(root.Path, "loose");
        Directory.CreateDirectory(loose, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        await using var target = await StartAttachedAppAsync();

        var none = await RunCliAsync("capture", "case", "status", Pid);
        var bogus = await RunCliAsync("capture", "case", "start", Pid, "--authorize", "bogus", "--dir", root.Path);
        var bounds = await RunCliAsync("capture", "case", "start", Pid, "--max-bytes", "5", "--dir", root.Path);
        var refused = await RunCliAsync("capture", "case", "start", Pid, "--dir", loose, "--json");
        var badPath = await RunCliAsync("capture", "case", "start", Pid, "--dir", "bad\0path");
        Assert.AreEqual((1, true), (badPath.ExitCode, badPath.Stderr.Contains("invalid-directory", StringComparison.Ordinal)), badPath.Stderr);
        Assert.AreEqual((1, true), (none.ExitCode, none.Stderr.Contains("no-active-case", StringComparison.Ordinal)), none.Stderr);
        Assert.AreEqual((1, true), (bogus.ExitCode, bogus.Stderr.Contains("unsupported-authorization", StringComparison.Ordinal)), bogus.Stderr);
        Assert.AreEqual((1, true), (bounds.ExitCode, bounds.Stderr.Contains("invalid-bounds", StringComparison.Ordinal)), bounds.Stderr);
        Assert.AreEqual(1, refused.ExitCode);
        Assert.AreEqual("storage-refused", JsonDocument.Parse(refused.Stdout).RootElement.GetProperty("problem").GetProperty("code").GetString());
        Assert.IsEmpty(Directory.EnumerateFileSystemEntries(loose), "a refused start wrote to the loose root");

        var (startExit, started, startErr) = await RunCliAsync("capture", "case", "start", Pid, "--dir", root.Path);
        Assert.AreEqual(0, startExit, startErr);
        StringAssert.Contains(started, "started: recording");
        StringAssert.Contains(started, "Checkpoint: fresh-model/1 excluded");
        var again = await RunCliAsync("capture", "case", "start", Pid, "--dir", root.Path);
        Assert.AreEqual((1, true), (again.ExitCode, again.Stderr.Contains("case-active", StringComparison.Ordinal)), again.Stderr);
        var (statusExit, status, _) = await RunCliAsync("capture", "case", "status", Pid);
        Assert.AreEqual(0, statusExit);
        StringAssert.Contains(status, "  model: ");

        var (stopExit, stopped, stopErr) = await RunCliAsync("capture", "case", "stop", Pid);
        Assert.AreEqual(0, stopExit, stopErr);
        StringAssert.Contains(stopped, "stopped: stopped (requested)");
        var path = Directory.GetDirectories(root.Path).Single(d => d != loose);
        var (inspectExit, inspected, _) = await RunCliAsync("capture", "case", "inspect", path);
        Assert.AreEqual(0, inspectExit);
        StringAssert.Contains(inspected, "complete (requested)");
        StringAssert.Contains(inspected, "Not re-applicable: checkpoint excluded");
        var missing = await RunCliAsync("capture", "case", "inspect", Path.Combine(root.Path, "absent"));
        Assert.AreEqual((1, true), (missing.ExitCode, missing.Stderr.Contains("case-not-found", StringComparison.Ordinal)), missing.Stderr);

        // Launch-time options are validated before any host is spawned.
        var orphan = await RunCliAsync("terminal", "start", "--case-dir", root.Path, "--", "/bin/true");
        var badLaunch = await RunCliAsync("terminal", "start", "--record-case", "--case-authorize", "bogus", "--", "/bin/true");
        Assert.AreEqual((1, true), (orphan.ExitCode, orphan.Stderr.Contains("require --record-case", StringComparison.Ordinal)), orphan.Stderr);
        Assert.AreEqual((1, true), (badLaunch.ExitCode, badLaunch.Stderr.Contains("unsupported-authorization", StringComparison.Ordinal)), badLaunch.Stderr);
        var badBounds = await RunCliAsync("terminal", "start", "--record-case", "--case-max-bytes", "5", "--", "/bin/true");
        Assert.AreEqual((1, true), (badBounds.ExitCode, badBounds.Stderr.Contains("invalid-bounds", StringComparison.Ordinal)), badBounds.Stderr);
        Assert.IsFalse(badBounds.Stderr.Contains("Host process", StringComparison.Ordinal), "invalid bounds spawned a host before being refused");

        // Every client splits comma-separated authorizations the same way, the internal host command included.
        var hostList = await RunCliAsync("terminal", "host", "--record-case", "--case-authorize", "bogus,reapplication-data", "--", "/bin/true");
        Assert.AreEqual(1, hostList.ExitCode, hostList.Stderr);
        StringAssert.Contains(hostList.Stderr, "'bogus'");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_MarkAndReapplyMatchTheEngineAndTheReapplier()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        // Recorded from construction, so the case is re-applicable.
        await using var target = await StartAttachedAppAsync(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        var engine = new TerminalDiagnostics(target, "CliMark");

        // Refusals equal the engine's own.
        var tooLong = new string('x', 65);
        var (badExit, bad, _) = await RunCliAsync("capture", "case", "mark", Pid, "--label", tooLong, "--json");
        Assert.AreEqual(1, badExit);
        AssertJsonEquals(engine.MarkCase(tooLong), bad, "invalid label");

        // A mark equals the checkpoint the target wrote.
        var (markExit, mark, markErr) = await RunCliAsync("capture", "case", "mark", Pid, "--label", "cli-mark", "--json");
        Assert.AreEqual(0, markExit, markErr);
        var (textExit, text, _) = await RunCliAsync("capture", "case", "mark", Pid, "--label", "cli-text");
        Assert.AreEqual(0, textExit);
        StringAssert.Contains(text, "marked 'cli-text'");
        var recorder = target.DiagnosticCase!;
        var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
        Assert.AreEqual(0, stopExit, stopErr);
        var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
        var (noneExit, none, _) = await RunCliAsync("capture", "case", "mark", Pid, "--json");
        Assert.AreEqual(1, noneExit);
        AssertJsonEquals(engine.MarkCase(), none, "mark without a case");
        var line = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 4096 }).Events
            .Single(e => e.Checkpoint?.Label == "cli-mark");
        AssertJsonEquals(new DiagnosticCaseMarkResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = recorder.CaseId,
            Label = "cli-mark",
            CheckpointOrdinal = line.Checkpoint!.Ordinal,
            ModelSequence = line.ModelSequence,
            StateRecorded = true,
        }, mark, "mark");

        // Re-application equals the reapplier's for the same request, apart from each run's own directory.
        foreach (var (args, request) in new (string[], DiagnosticCaseReapplyRequest)[]
        {
            (["--to", "cli-mark"], new() { Path = path, ToLabel = "cli-mark" }),
            (["--to", "label:stop", "--inject-fault", "cell-text,mode", "--max-differences", "1"],
                new() { Path = path, ToLabel = "stop", Faults = ["cell-text", "mode"], MaxDifferences = 1 }),
            (["--to", line.ModelSequence!.Value.ToString(), "--preview", "text", "--preview", "svg"],
                new() { Path = path, ToModelSequence = line.ModelSequence, Previews = ["text", "svg"] }),
            (["--to", $"case:{line.CaseSequence}"], new() { Path = path, ToCaseSequence = line.CaseSequence }),
            (["--to", "999999"], new() { Path = path, ToModelSequence = 999_999 }),
        })
        {
            var (exit, json, err) = await RunCliAsync(["capture", "case", "reapply", path, .. args, "--json"]);
            var expected = DiagnosticCaseReapplier.Reapply(request);
            Assert.AreNotEqual("no-valid-interval", expected.Problem?.Code, "fixture: the case is not re-applicable");
            Assert.AreEqual(expected.Outcome != DiagnosticOutcome.Captured ? 1 : expected.Comparison == "matched" ? 0 : 2, exit, err);
            AssertJsonEquals(expected, json, string.Join(" ", args), "runPath");
            if (request.ToLabel == "cli-mark")
                Assert.AreEqual("matched", expected.Comparison, expected.ComparisonReason);
        }

        var (matchedExit, matched, matchedErr) = await RunCliAsync("capture", "case", "reapply", path, "--to", "cli-mark");
        Assert.AreEqual(0, matchedExit, matchedErr + matched);
        StringAssert.Contains(matched, "('cli-mark'): matched");
        var (faultExit, faulted, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "cli-mark", "--inject-fault", "cursor");
        Assert.AreEqual(2, faultExit);
        StringAssert.Contains(faulted, "Fault injected: cursor at cursor.x");
        var (unknownExit, _, unknown) = await RunCliAsync("capture", "case", "reapply", path, "--to", "999999");
        Assert.AreEqual(1, unknownExit);
        StringAssert.Contains(unknown, "unknown-model-sequence");
        var (invalidExit, _, invalid) = await RunCliAsync("capture", "case", "reapply", path, "--to", "case:x");
        Assert.AreEqual((1, true), (invalidExit, invalid.Contains("invalid-target", StringComparison.Ordinal)), invalid);

        // An interrupted case's unknown tail: past its end is beyond-interval, with the last valid boundary.
        File.Delete(Path.Combine(path, "completion.json"));
        var (beyondExit, _, beyond) = await RunCliAsync("capture", "case", "reapply", path, "--to", "999999");
        Assert.AreEqual(1, beyondExit);
        StringAssert.Contains(beyond, "beyond-interval");
        StringAssert.Contains(beyond, "Re-applicable through model sequence");

        // Ticket 14: the human output names both builds (the CLI's Hex1b is this build's copy: one compilation of a
        // single-TFM library, so its module version id is the test host's; a multi-targeted Hex1b would break this
        // assumption, not the product), and a refusal names the failed check with the case's and this build's values.
        var build = typeof(Hex1bTerminal).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        StringAssert.Contains(matched, $"({build}): same build", matched);
        var manifestFile = Path.Combine(path, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(manifestFile))!.AsObject();
        manifest["identity"]!["hex1bBuild"] = "0123456789abcdef0123456789abcdef";
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        var (otherExit, other, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "cli-mark");
        Assert.AreEqual(0, otherExit, other);
        StringAssert.Contains(other, "(0123456789abcdef0123456789abcdef); re-applied by", other);
        StringAssert.Contains(other, $"({build}): different builds", other);
        manifest["identity"]!.AsObject().Remove("hex1bBuild");
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        var (noneExit2, none2, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "cli-mark");
        Assert.AreEqual(0, noneExit2, none2);
        StringAssert.Contains(none2, "(no build id); re-applied by", none2);
        StringAssert.Contains(none2, "build ids not compared", none2);
        manifest["formatVersion"] = 3;
        File.WriteAllText(manifestFile, manifest.ToJsonString());
        var (incompatibleExit, _, incompatible) = await RunCliAsync("capture", "case", "reapply", path, "--to", "cli-mark");
        Assert.AreEqual(1, incompatibleExit, incompatible);
        StringAssert.Contains(incompatible, "unavailable (incompatible): formatVersion: the artifact declares 3; this build reads 2.", incompatible);
        StringAssert.Contains(incompatible, "Incompatible: formatVersion (the case declares 3; this build 2)", incompatible);
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task CaseLiveStart_InspectAndReapplyFromTheStart()
    {
        // Ticket 09: a case started through the CLI on a running application owns a cumulative text-state/3 start (the
        // application is on the alternate screen), and re-applies from it to matched.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var target = await StartAttachedAppAsync();
        var (startExit, start, startErr) = await RunCliAsync("capture", "case", "start", Pid, "--dir", root.Path,
            "--authorize", "reapplication-data", "--json");
        Assert.AreEqual(0, startExit, startErr);
        var checkpoint = JsonDocument.Parse(start).RootElement.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/3", "complete"), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()),
            checkpoint.ToString());
        var startSequence = checkpoint.GetProperty("modelSequence").GetInt64();
        target.Resize(30, 6);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.Width == 30 && s.ContainsText("STYLED"), TimeSpan.FromSeconds(10), "re-rendered")
            .Build().ApplyAsync(target, TestContext.Current.CancellationToken);
        var (markExit, _, markErr) = await RunCliAsync("capture", "case", "mark", Pid, "--label", "resized", "--json");
        Assert.AreEqual(0, markExit, markErr);
        var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
        Assert.AreEqual(0, stopExit, stopErr);
        var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;

        var (inspectExit, inspect, inspectErr) = await RunCliAsync("capture", "case", "inspect", path, "--json");
        Assert.AreEqual(0, inspectExit, inspectErr);
        var expected = JsonSerializer.SerializeToElement(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }),
            DiagnosticsJsonContext.Default.DiagnosticCaseInspection);
        Assert.IsTrue(JsonElement.DeepEquals(expected, JsonDocument.Parse(inspect).RootElement), "CLI inspect differs from the inspector");
        var (_, inspectText, _) = await RunCliAsync("capture", "case", "inspect", path);
        StringAssert.Contains(inspectText, $"checkpoint text-state/3 complete at model sequence {startSequence}");
        StringAssert.Contains(inspectText, $"Re-applicable: model {startSequence}..");

        foreach (var label in new[] { "start", "resized", "stop" })
        {
            var (exit, json, err) = await RunCliAsync("capture", "case", "reapply", path, "--to", label, "--json");
            var reapplied = DiagnosticCaseReapplier.Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = label });
            Assert.AreEqual("matched", reapplied.Comparison, $"{label}: {reapplied.ComparisonReason} {reapplied.Problem?.Message}");
            Assert.AreEqual(0, exit, err);
            AssertJsonEquals(reapplied, json, label, "runPath");
        }
        var (textExit, text, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop");
        Assert.AreEqual(0, textExit);
        StringAssert.Contains(text, $"Restored from the text-state/3 start at model sequence {startSequence}");

        // A start that held refused surfaces: the text names them.
        var refused = Path.Combine(root.Path, "refused");
        Directory.CreateDirectory(refused, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        foreach (var file in Directory.GetFiles(path))
            File.Copy(file, Path.Combine(refused, Path.GetFileName(file)));
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(refused, "manifest.json")))!;
        manifest["checkpoint"]!["status"] = "unsupported";
        manifest["checkpoint"]!["reason"] = "unsupported-surfaces: the start held retained-history, titles.";
        manifest["checkpoint"]!["unsupportedSurfaces"] = new System.Text.Json.Nodes.JsonArray("retained-history", "titles");
        File.WriteAllText(Path.Combine(refused, "manifest.json"), manifest.ToJsonString());
        var (_, refusedText, _) = await RunCliAsync("capture", "case", "inspect", refused);
        StringAssert.Contains(refusedText, "unsupported surfaces: retained-history, titles");
    }

    // The client's JSON equals the contract object's, apart from fields that differ per run.
    private static void AssertJsonEquals<T>(T expected, string actualJson, string what, params string[] ignored)
    {
        var expectedNode = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(expected, DiagnosticsJsonContext.Default.GetTypeInfo(typeof(T))!))!.AsObject();
        var actualNode = System.Text.Json.Nodes.JsonNode.Parse(actualJson)!.AsObject();
        foreach (var field in ignored)
        {
            expectedNode.Remove(field);
            actualNode.Remove(field);
        }
        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(expectedNode, actualNode), $"{what}: the CLI differs.\nexpected: {expectedNode}\ncli:      {actualNode}");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_HostedPtyRecordsFromConstruction()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = new TerminalHostConfig
        {
            Width = 40,
            Height = 6,
            Command = "/bin/sh",
            Arguments = ["-c", "printf 'CASE-FROM-FIRST-BYTE\\n'; exec sleep 60"],
            DiagnosticCase = new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] },
        };

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        try
        {
            await WaitForSocketAsync(cts.Token);
            Assert.IsNotNull(await WaitForCliTextAsync("CASE-FROM-FIRST-BYTE", cts.Token), "hosted PTY output never reached the model");

            var (statusExit, status, statusErr) = await RunCliAsync("capture", "case", "status", Pid, "--json");
            Assert.AreEqual(0, statusExit, statusErr);
            var root_ = JsonDocument.Parse(status).RootElement;
            Assert.AreEqual(("construction", "complete"), (root_.GetProperty("startPath").GetString(),
                root_.GetProperty("checkpoint").GetProperty("status").GetString()));

            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr);
            var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest
            {
                Path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!,
                Limit = 4096,
            });
            var model = inspection.Events.Where(e => e.Stream == "model").ToList();
            Assert.AreEqual(1L, model[0].ModelSequence, "the case missed the child's first output");
            var bytes = model.Where(e => e.Data is not null).SelectMany(e => Convert.FromBase64String(e.Data!)).ToArray();
            StringAssert.Contains(System.Text.Encoding.UTF8.GetString(bytes), "CASE-FROM-FIRST-BYTE");
            Assert.AreEqual((true, 0L), (inspection.Intervals.Single().Valid, inspection.Intervals.Single().FromModelSequence));
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow(1)]
    [DataRow(1_000_000)]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task TerminalStart_ScrollbackOption(int? scrollback)
    {
        // The host gives the terminal the requested capacity (absent: none), as the case's recorded configuration
        // shows; out-of-range values are refused by 'terminal start' and 'terminal host' before a host runs.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        foreach (var command in new[] { "start", "host" })
        {
            foreach (var invalid in new[] { "0", "1000001", "-1" })
            {
                var (exit, _, error) = await RunCliAsync("terminal", command, "--scrollback", invalid, "--", "/bin/true");
                Assert.AreEqual(1, exit, $"terminal {command} --scrollback {invalid}");
                StringAssert.Contains(error, "scrollback must be 1 to 1,000,000 rows", $"terminal {command} --scrollback {invalid}");
            }
        }

        // The host runs as 'terminal start' spawns it: the start command's host arguments, parsed by the CLI into the
        // host command's configuration.
        using var root = new CaseRoot();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var hostArgs = Hex1b.Tool.Commands.Terminal.TerminalStartCommand.HostArguments(40, 6, null, null, null, null, scrollback,
            new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] },
            ["/bin/sh", "-c", "printf 'SCROLLBACK-HOST\\n'; exec sleep 60"]);
        Assert.AreEqual(scrollback is not null, hostArgs.Contains("--scrollback"), string.Join(' ', hostArgs));
        using var parser = await Program.BuildApplication([.. hostArgs]);
        var (config, configError) = Hex1b.Tool.Commands.Terminal.TerminalHostCommand.Config(
            parser.Services.GetRequiredService<Hex1b.Tool.Commands.RootCommand>().Parse(hostArgs));
        Assert.IsNotNull(config, configError);
        Assert.AreEqual(scrollback, config.Scrollback, "the host did not read the forwarded --scrollback");
        Assert.AreEqual((40, 6, "/bin/sh", 2, root.Path, 1), (config.Width, config.Height, config.Command, config.Arguments.Length,
            config.DiagnosticCase?.Directory, config.DiagnosticCase?.Authorizations?.Count), "the host's other forwarded options");

        // Every option the host takes survives the round trip (not run: a port or a recording would open a listener or
        // write a file).
        var allArgs = Hex1b.Tool.Commands.Terminal.TerminalStartCommand.HostArguments(81, 7, "/work", "/rec.cast", 4321, "0.0.0.0", 250,
            new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                MaxBytes = 2 * 1024 * 1024,
                MaxSeconds = 30,
                Authorizations = [DiagnosticAuthorization.ReapplicationData, DiagnosticAuthorization.RawInput],
            },
            ["/bin/echo", "a", "b"]);
        using var allParser = await Program.BuildApplication([.. allArgs]);
        var (all, allError) = Hex1b.Tool.Commands.Terminal.TerminalHostCommand.Config(
            allParser.Services.GetRequiredService<Hex1b.Tool.Commands.RootCommand>().Parse(allArgs));
        Assert.IsNotNull(all, allError);
        Assert.AreEqual((81, 7, "/work", "/rec.cast", (int?)4321, "0.0.0.0", (int?)250, "/bin/echo", "a b"),
            (all.Width, all.Height, all.WorkingDirectory, all.RecordPath, all.Port, all.BindAddress, all.Scrollback, all.Command, string.Join(' ', all.Arguments)),
            string.Join(' ', allArgs));
        Assert.AreEqual((root.Path, (long?)(2 * 1024 * 1024), (int?)30, "ReapplicationData,RawInput"),
            (all.DiagnosticCase?.Directory, all.DiagnosticCase?.MaxBytes, all.DiagnosticCase?.MaxSeconds, string.Join(',', all.DiagnosticCase?.Authorizations ?? [])),
            "the forwarded case options");

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        try
        {
            await WaitForSocketAsync(cts.Token);
            Assert.IsNotNull(await WaitForCliTextAsync("SCROLLBACK-HOST", cts.Token), "hosted PTY output never reached the model");
            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr);
            var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
            var configuration = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json"))).RootElement
                .GetProperty("checkpoint").GetProperty("configuration");
            Assert.AreEqual(scrollback, configuration.TryGetProperty("scrollbackCapacity", out var recorded) ? recorded.GetInt32() : null,
                configuration.ToString());
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }
    }

    [TestMethod]
    [DataRow("ls", "--width=3")]
    [DataRow("sh", "--record-case")]
    [DataRow("prog", "--")]
    public async Task TerminalStart_CommandArgumentsAreNotHostOptions(string command, string argument)
    {
        var hostArgs = Hex1b.Tool.Commands.Terminal.TerminalStartCommand.HostArguments(
            81, 7, null, null, null, null, null, null, [command, argument]);
        using var parser = await Program.BuildApplication([.. hostArgs]);
        var parsed = parser.Services.GetRequiredService<Hex1b.Tool.Commands.RootCommand>().Parse(hostArgs);
        Assert.AreEqual(0, parsed.Errors.Count, string.Join("; ", parsed.Errors));

        var (config, error) = Hex1b.Tool.Commands.Terminal.TerminalHostCommand.Config(parsed);
        Assert.IsNotNull(config, error);
        Assert.AreEqual(command, config.Command);
        CollectionAssert.AreEqual(new[] { argument }, config.Arguments);
        Assert.AreEqual((81, 7), (config.Width, config.Height), "child arguments changed host geometry");
        Assert.IsNull(config.DiagnosticCase, "a child argument enabled host case recording");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_RecoverAndReapplyFromMatchTheEngineAndTheReapplier()
    {
        // Ticket 13: `capture case recover` equals the engine's recovery and the checkpoint line the target wrote, and
        // its refusals the engine's; `reapply --from` equals the reapplier's for the same request, origin included.
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        await using var target = await StartAttachedAppAsync(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        var engine = new TerminalDiagnostics(target, "CliRecover");

        var tooLong = new string('x', 65);
        var (badExit, bad, _) = await RunCliAsync("capture", "case", "recover", Pid, "--label", tooLong, "--json");
        Assert.AreEqual(1, badExit);
        AssertJsonEquals(engine.RecoverCase(tooLong), bad, "invalid label");

        var (markExit, _, markErr) = await RunCliAsync("capture", "case", "mark", Pid, "--label", "cli-mark", "--json");
        Assert.AreEqual(0, markExit, markErr);
        // A model event between the mark and the recovery, so the mark is before the recovery's interval.
        var marked = target.CurrentModelSequence;
        target.Resize(42, 6);
        for (var i = 0; i < 500 && target.CurrentModelSequence == marked; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.AreNotEqual(marked, target.CurrentModelSequence, "fixture: the resize raised no model event");
        var (recoverExit, recovered, recoverErr) = await RunCliAsync("capture", "case", "recover", Pid, "--label", "cli-recover", "--json");
        Assert.AreEqual(0, recoverExit, recoverErr);
        var (textExit, text, _) = await RunCliAsync("capture", "case", "recover", Pid);
        Assert.AreEqual(0, textExit);
        StringAssert.Contains(text, "recovered 'recovery-");
        StringAssert.Contains(text, "a new origin");
        var recorder = target.DiagnosticCase!;
        var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
        Assert.AreEqual(0, stopExit, stopErr);
        var path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
        var (noneExit, none, _) = await RunCliAsync("capture", "case", "recover", Pid, "--json");
        Assert.AreEqual(1, noneExit);
        AssertJsonEquals(engine.RecoverCase(), none, "recover without a case");
        var line = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path, Limit = 4096 }).Events
            .Single(e => e.Checkpoint?.Label == "cli-recover");
        Assert.AreEqual("recovery", line.Checkpoint!.Trigger);
        AssertJsonEquals(new DiagnosticCaseRecoverResult
        {
            Outcome = DiagnosticOutcome.Captured,
            CaseId = recorder.CaseId,
            Label = "cli-recover",
            CheckpointOrdinal = line.Checkpoint.Ordinal,
            ModelSequence = line.ModelSequence,
            Status = "complete",
        }, recovered, "recovery");

        // Re-application with --from equals the reapplier's for the same request, apart from each run's own directory.
        foreach (var (args, request, code, origin) in new (string[], DiagnosticCaseReapplyRequest, string?, string?)[]
        {
            (["--to", "stop"], new() { Path = path, ToLabel = "stop" }, null, "start"),
            (["--to", "stop", "--from", "cli-recover"], new() { Path = path, ToLabel = "stop", From = "cli-recover" }, null, "cli-recover"),
            (["--to", "stop", "--from", $"checkpoint:{line.Checkpoint.Ordinal}"], new() { Path = path, ToLabel = "stop", From = $"checkpoint:{line.Checkpoint.Ordinal}" }, null, "cli-recover"),
            (["--to", "stop", "--from", "cli-mark"], new() { Path = path, ToLabel = "stop", From = "cli-mark" }, "not-an-origin", null),
            (["--to", "stop", "--from", "nobody"], new() { Path = path, ToLabel = "stop", From = "nobody" }, "unknown-label", null),
            (["--to", "cli-mark", "--from", "cli-recover"], new() { Path = path, ToLabel = "cli-mark", From = "cli-recover" }, "beyond-interval", null),
        })
        {
            var (exit, json, err) = await RunCliAsync(["capture", "case", "reapply", path, .. args, "--json"]);
            var expected = DiagnosticCaseReapplier.Reapply(request);
            Assert.AreEqual((code, origin), (expected.Problem?.Code, expected.Origin?.Label ?? expected.Origin?.Trigger), $"fixture: {string.Join(" ", args)}: {expected.Problem?.Message}");
            Assert.AreEqual(expected.Outcome != DiagnosticOutcome.Captured ? 1 : expected.Comparison == "matched" ? 0 : 2, exit, err);
            AssertJsonEquals(expected, json, string.Join(" ", args), "runPath");
        }

        var (fromExit, fromText, fromErr) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--from", "cli-recover");
        Assert.AreEqual(0, fromExit, fromErr + fromText);
        StringAssert.Contains(fromText, $"Restored from recovery 'cli-recover' (checkpoint {line.Checkpoint.Ordinal}) at model sequence {line.ModelSequence}");
        var (inspectExit, inspectText, _) = await RunCliAsync("capture", "case", "inspect", path);
        Assert.AreEqual(0, inspectExit);
        StringAssert.Contains(inspectText, "from the start");
        StringAssert.Contains(inspectText, $"from recovery 'cli-recover' (checkpoint {line.Checkpoint.Ordinal})");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_PendingInputFaults()
    {
        // A hosted PTY prints the first byte of a scalar, pauses, then the rest; the case starts during the pause.
        // The start is complete and holds the byte; re-application matches; the CLI's pending-* faults differ at the
        // start and are not applicable where the holder is empty (ticket 12).
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var config = new TerminalHostConfig
        {
            Width = 40,
            Height = 6,
            Command = "/bin/sh",
            Arguments = ["-c", "printf 'PENDING-\\346'; sleep 12; printf '\\274\\242 PENDING-DONE\\n'; exec sleep 60"],
        };

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        string path;
        try
        {
            await WaitForSocketAsync(cts.Token);
            Assert.IsNotNull(await WaitForCliTextAsync("PENDING-", cts.Token), "hosted PTY output never reached the model");
            var (startExit, start, startErr) = await RunCliAsync("capture", "case", "start", Pid, "--dir", root.Path, "--authorize", "reapplication-data", "--json");
            Assert.AreEqual(0, startExit, startErr);
            var checkpoint = JsonDocument.Parse(start).RootElement.GetProperty("checkpoint");
            Assert.AreEqual("complete", checkpoint.GetProperty("status").GetString(), $"fixture: the start was not taken during the pause: {checkpoint}");
            // The shared helper gives up after about 10 s; the pause is 12 s.
            var done = false;
            for (var attempt = 0; attempt < 300 && !done; attempt++)
            {
                var (exitCode, stdout, _) = await RunCliAsync("capture", "screenshot", Pid);
                done = exitCode == 0 && stdout.Contains("PENDING-DONE", StringComparison.Ordinal);
                if (!done)
                    await Task.Delay(100, cts.Token);
            }
            Assert.IsTrue(done, "the rest of the output never reached the model");
            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr);
            path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }

        // The start line (checksum, tab, JSON) holds the pending byte.
        var startLine = File.ReadLines(Path.Combine(path, "events.jsonl")).Select(l => JsonDocument.Parse(l[(l.IndexOf('\t') + 1)..]).RootElement)
            .First(e => e.TryGetProperty("checkpoint", out var c) && c.GetProperty("trigger").GetString() == "start");
        Assert.AreEqual("5g==", startLine.GetProperty("checkpoint").GetProperty("state").GetProperty("pendingInput").GetProperty("utf8").GetString(),
            "fixture: the start was not taken during the pause (a 12 s window)");
        var (matchedExit, matched, matchedErr) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--json");
        Assert.AreEqual(0, matchedExit, matchedErr + matched);
        Assert.AreEqual("matched", JsonDocument.Parse(matched).RootElement.GetProperty("comparison").GetString(), matched);

        var (exit, json, err) = await RunCliAsync("capture", "case", "reapply", path, "--to", "start", "--inject-fault", "pending-input", "--json");
        Assert.AreEqual(2, exit, err + json);
        var result = JsonDocument.Parse(json).RootElement;
        Assert.AreEqual(("different", true), (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), json);
        CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
            .Select(d => d.GetProperty("path").GetString()).ToList(), "pendingInput.utf8");
        var (_, text, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "start", "--inject-fault", "pending-input");
        StringAssert.Contains(text, "Fault injected: pending-input at pendingInput.utf8");
        foreach (var fault in new[] { "pending-escape", "pending-ground-escape" })
        {
            var (_, refused, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "start", "--inject-fault", fault, "--json");
            var refusal = JsonDocument.Parse(refused).RootElement;
            Assert.AreEqual("unavailable", refusal.GetProperty("comparison").GetString(), refused);
            StringAssert.StartsWith(refusal.GetProperty("comparisonReason").GetString(), "fault-not-applicable", fault);
        }
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Case_TitleAndMarkFaults()
    {
        // A hosted PTY that pushes a title and emits OSC 133 marks, recorded from construction: the CLI's title-stack and
        // command-mark faults differ at the paths they name and are labelled (ticket 11).
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Owner-only case storage is verified on Linux.");
        using var root = new CaseRoot();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var config = new TerminalHostConfig
        {
            Width = 40,
            Height = 6,
            Command = "/bin/sh",
            Arguments = ["-c", "printf '\\033]0;one\\007\\033]22;\\007\\033]0;two\\007\\033]133;A\\007$ \\033]133;B\\007cmd\\r\\n\\033]133;D;0\\007FAULTS-READY\\n'; exec sleep 60"],
            DiagnosticCase = new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] },
        };

        await WaitForSocketReleaseAsync(cts.Token);
        var host = TerminalHost.RunAsync(config, cts.Token);
        string path;
        try
        {
            await WaitForSocketAsync(cts.Token);
            Assert.IsNotNull(await WaitForCliTextAsync("FAULTS-READY", cts.Token), "hosted PTY output never reached the model");
            var (stopExit, stop, stopErr) = await RunCliAsync("capture", "case", "stop", Pid, "--json");
            Assert.AreEqual(0, stopExit, stopErr);
            path = JsonDocument.Parse(stop).RootElement.GetProperty("path").GetString()!;
        }
        finally
        {
            await cts.CancelAsync();
            try { await host; } catch (OperationCanceledException) { }
        }

        foreach (var (fault, faultPath) in new[] { ("title-stack", "titles.stack[0].window"), ("command-mark", "commandMarks[0].column") })
        {
            var (exit, json, err) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--inject-fault", fault, "--json");
            Assert.AreEqual(2, exit, err + json);
            var result = JsonDocument.Parse(json).RootElement;
            Assert.AreEqual(("different", true), (result.GetProperty("comparison").GetString(), result.GetProperty("faultInjected").GetBoolean()), json);
            CollectionAssert.Contains(result.GetProperty("differences").GetProperty("differences").EnumerateArray()
                .Select(d => d.GetProperty("path").GetString()).ToList(), faultPath, fault);
            var (_, text, _) = await RunCliAsync("capture", "case", "reapply", path, "--to", "stop", "--inject-fault", fault);
            StringAssert.Contains(text, $"Fault injected: {fault} at {faultPath}");
        }
    }

    // Status of a settled case: engine counts equal twice in a row around the client's read.
    private static async Task<JsonElement> StableStatusAsync(TerminalDiagnostics engine, Func<Task<string>> read)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            var before = Progress(engine.GetCaseStatus());
            var json = JsonDocument.Parse(await read()).RootElement.Clone();
            if (before == Progress(engine.GetCaseStatus()))
                return json;
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("fixture: the case never settled");
        return default;

        static string Progress(DiagnosticCaseResult result) =>
            $"{result.BytesWritten}:{string.Join(",", result.Streams.Select(s => $"{s.Offered}/{s.Written}/{s.Dropped}"))}";
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

        Assert.IsTrue(System.Text.Json.Nodes.JsonNode.DeepEquals(expected, actual), $"CLI {operation} differs from the engine's.\nengine: {expected}\ncli:    {actual}");
    }

    private sealed class CaseRoot : IDisposable
    {
        public CaseRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hex1b-cli-case-" + Guid.NewGuid().ToString("N"));
            if (!OperatingSystem.IsWindows())
                Directory.CreateDirectory(Path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    // An attached app presented over a WebSocket, so its writes are observable delivery.
    private static async Task<(Hex1bTerminal Terminal, ControlledWebSocket Socket)> StartDeliveryAppAsync()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var socket = new ControlledWebSocket();
        var terminal = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithPresentation(new WebSocketPresentationAdapter(socket, 40, 5))
            .WithHex1bApp(_ => new TextBlockWidget("DELIVERY-SENTINEL"))
            .WithDiagnostics(appName: "CliDelivery", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        for (var i = 0; i < 300; i++)
        {
            lock (socket.Sent)
                if (socket.Sent.Any(b => System.Text.Encoding.UTF8.GetString(b).Contains("DELIVERY-SENTINEL", StringComparison.Ordinal)))
                    break;
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }

        await Task.Delay(300, TestContext.Current.CancellationToken);
        return (terminal, socket);
    }

    private static JsonElement WithoutAcquisition(JsonElement result)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(result.GetRawText())!;
        (node["identity"] as System.Text.Json.Nodes.JsonObject)?.Remove("acquisition");
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
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

    private static async Task<Hex1bTerminal> StartAttachedAppAsync(DiagnosticCaseStartRequest? recordCase = null)
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
        var builder = Hex1bTerminal.CreateBuilder()
            .WithDimensions(40, 5)
            .WithHeadless()
            .WithScrollback(50)
            .WithHex1bApp(_ => new ThemePanelWidget(
                theme => theme.Set(GlobalTheme.ForegroundColor, Styled),
                new TextBlockWidget("STYLED")))
            .WithDiagnostics(appName: "CliAttached", forceEnable: true);
        var terminal = (recordCase is null ? builder : builder.WithDiagnosticCase(recordCase)).Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("STYLED"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        return terminal;
    }

    // A static app whose anchored popup holds the focused TextBox with the sentinel (an open popup
    // owns focus). The popup is pushed from the app's own build, on the app loop, once the anchor
    // node exists.
    private static async Task<Hex1bTerminal> StartFrameAppAsync()
    {
        await WaitForSocketReleaseAsync(TestContext.Current.CancellationToken);
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

                    return new ZStackWidget([new VStackWidget(
                    [
                        new TextBlockWidget("FRAMEAPP"),
                        new ButtonWidget("anchor"),
                    ])]);
                };
            })
            .WithDiagnostics(appName: "CliFrames", forceEnable: true)
            .Build();
        _ = terminal.RunAsync(TestContext.Current.CancellationToken);
        await WaitForSocketAsync(TestContext.Current.CancellationToken);
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("FRAMEAPP"), TimeSpan.FromSeconds(10), "application rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        app!.Invalidate();
        await new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText("POPPED"), TimeSpan.FromSeconds(10), "popup rendered")
            .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        return terminal;
    }

    // Engine and CLI observe the same published frame only while the app is idle; retry until an
    // engine capture before and after the CLI call agree.
    private static async Task<(JsonElement Cli, DiagnosticApplicationFrameResult Engine)> CaptureStableAsync(
        Hex1bTerminal target, params string[] args)
    {
        var engine = new TerminalDiagnostics(target, "CliFrames");
        for (var attempt = 0; attempt < 10; attempt++)
        {
            var before = engine.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
            var (exitCode, stdout, stderr) = await RunCliAsync(args);
            var after = engine.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
            Assert.AreEqual(0, exitCode, stderr);
            if (before.Frame?.FrameId == after.Frame?.FrameId)
                return (JsonDocument.Parse(stdout).RootElement.Clone(), before);
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }

        Assert.Fail("the application never stayed idle across a CLI capture");
        return default;
    }

    // Record timestamps differ in precision between serializers; compare the rest exactly.
    private static JsonElement WithoutTimestamps(JsonElement milestone)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(milestone.GetRawText())!;
        if (node["input"] is System.Text.Json.Nodes.JsonObject input)
        {
            input.Remove("acceptedAt");
            input.Remove("processedAt");
        }
        return JsonDocument.Parse(node.ToJsonString()).RootElement.Clone();
    }

    private static JsonElement ToJson(DiagnosticApplicationFrameResult result) =>
        JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult);

    private static T? FindNode<T>(Hex1bNode? node) where T : Hex1bNode =>
        node is null ? null : node as T ?? node.GetChildren().Select(FindNode<T>).FirstOrDefault(n => n is not null);

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
