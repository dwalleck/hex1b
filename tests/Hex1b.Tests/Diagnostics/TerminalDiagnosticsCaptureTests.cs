using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hex1b.Diagnostics;
using Hex1b.Automation;
using Hex1b.Theming;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// Exercises the shared diagnostics capture contract against real terminal models.
/// Every CLI and MCP client returns what this engine produces.
/// </summary>
[TestClass]
public class TerminalDiagnosticsCaptureTests
{
    private const string StyledOutput = "plain \x1b[1;31mRED\x1b[0m \x1b[38;5;208mORANGE\x1b[0m \x1b[4;44mBLUE\x1b[0m";

    [TestMethod]
    public async Task Capture_Ansi_PreservesCoveredRenditionWhenReapplied()
    {
        await using var source = await StartAsync(StyledOutput, waitFor: "BLUE");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "styled");

        var result = diagnostics.Capture(new DiagnosticCaptureRequest { Format = DiagnosticCaptureFormat.Ansi });

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
        Assert.AreEqual(DiagnosticCaptureFormat.Ansi, result.Format);
        Assert.IsNotNull(result.Content);

        // Validate the returned content semantically: apply it to a fresh model and read its cells.
        await using var replica = await StartAsync(result.Content, waitFor: "BLUE");
        using var snapshot = replica.Terminal.CreateSnapshot();
        var red = FindCell(snapshot, "RED");
        Assert.AreEqual(Hex1bColorKind.Standard, red.Foreground!.Value.Kind);
        Assert.AreEqual(1, red.Foreground.Value.AnsiIndex);
        Assert.IsTrue((red.Attributes & CellAttributes.Bold) != 0, "bold lost");

        var orange = FindCell(snapshot, "ORANGE");
        Assert.AreEqual(Hex1bColorKind.Indexed, orange.Foreground!.Value.Kind);
        Assert.AreEqual(208, orange.Foreground.Value.AnsiIndex);

        var blue = FindCell(snapshot, "BLUE");
        Assert.IsTrue((blue.Attributes & CellAttributes.Underline) != 0, "underline lost");
        Assert.AreEqual(4, blue.Background!.Value.AnsiIndex);

        var plain = FindCell(snapshot, "plain");
        Assert.IsNull(plain.Foreground, "unstyled text gained a foreground");
    }

    [TestMethod]
    public async Task Capture_Text_ReturnsRenderedTextWithoutRendition()
    {
        await using var source = await StartAsync(StyledOutput, waitFor: "BLUE");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "styled");

        var result = diagnostics.Capture(new DiagnosticCaptureRequest { Format = DiagnosticCaptureFormat.Text });

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        StringAssert.StartsWith(result.Content, "plain RED ORANGE BLUE");
        Assert.IsFalse(result.Content!.Contains('\x1b'), "text capture contains escape sequences");
        Assert.AreEqual(40, result.Geometry!.Columns);
        Assert.AreEqual(6, result.Geometry.Rows);
        Assert.AreEqual(21, result.Geometry.CursorColumn);
        Assert.AreEqual(0, result.Geometry.CursorRow);
    }

    [TestMethod]
    public async Task Capture_HistoryRows_ReturnsRequestedRetainedRowsWithCoverage()
    {
        var lines = string.Concat(Enumerable.Range(1, 20).Select(i => $"line-{i:00}\r\n"));
        await using var source = await StartAsync(lines + "tail", waitFor: "tail", scrollback: 100);
        var diagnostics = new TerminalDiagnostics(source.Terminal, "history");

        var partial = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 3 });

        // 21 rows were written to a 6-row screen, so 15 rows scrolled into model history.
        Assert.AreEqual(DiagnosticOutcome.Captured, partial.Outcome);
        Assert.AreEqual(3, partial.History!.RequestedRows);
        Assert.AreEqual(15, partial.History.AvailableRows);
        Assert.AreEqual(3, partial.History.ReturnedRows);
        Assert.IsFalse(partial.History.Truncated);
        Assert.AreEqual(100, partial.History.RetentionCapacity);
        var partialRows = partial.Content!.Split('\n');
        Assert.AreEqual("line-13", partialRows[0].TrimEnd());
        Assert.AreEqual("line-15", partialRows[2].TrimEnd());
        Assert.AreEqual("line-16", partialRows[3].TrimEnd(), "active screen must follow returned history");

        var beyond = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 500 });
        Assert.AreEqual(15, beyond.History!.ReturnedRows);
        Assert.AreEqual(15, beyond.History.AvailableRows);
        Assert.IsFalse(beyond.History.Truncated, "returning everything available is not truncation");
        Assert.AreEqual("line-01", beyond.Content!.Split('\n')[0].TrimEnd());
        AssertCoverage(beyond, DiagnosticContentClass.RenderedHistory, DiagnosticCoverageState.Included);
    }

    [TestMethod]
    public async Task Capture_HistoryWithoutRetention_ReportsNoRetentionInsteadOfEmptySuccess()
    {
        await using var source = await StartAsync("a\r\nb\r\nc\r\nd\r\ne\r\nf\r\ng\r\nh", waitFor: "h");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "no-history");

        var result = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 10 });

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.AreEqual(0, result.History!.RetentionCapacity);
        Assert.AreEqual(0, result.History.ReturnedRows);
        Assert.IsNull(result.History.AvailableRows);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.History.Reason));
        AssertCoverage(result, DiagnosticContentClass.RenderedHistory, DiagnosticCoverageState.Unavailable);
        AssertUnavailableField(result, "history.availableRows");
    }

    [TestMethod]
    public async Task Capture_NoHistoryRequested_ExcludesHistoryByRequest()
    {
        await using var source = await StartAsync("x", waitFor: "x", scrollback: 10);
        var result = new TerminalDiagnostics(source.Terminal, "t").Capture(new DiagnosticCaptureRequest());

        AssertCoverage(result, DiagnosticContentClass.RenderedHistory, DiagnosticCoverageState.Excluded);
        Assert.AreEqual(0, result.History!.ReturnedRows);
    }

    [TestMethod]
    public async Task Capture_Default_ExcludesNonScreenMetadataAndPrivateContent()
    {
        const string url = "https://example.invalid/secret-target";
        await using var source = await StartAsync(
            $"\x1b]2;Secret Title\x07\x1b]8;id=1;{url}\x1b\\LINK\x1b]8;;\x1b\\ visible", waitFor: "visible");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "metadata");

        foreach (var format in Enum.GetValues<DiagnosticCaptureFormat>())
        {
            var result = diagnostics.Capture(new DiagnosticCaptureRequest { Format = format });

            Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, $"{format}: {result.Problem?.Message}");
            Assert.IsFalse(result.Content!.Contains(url, StringComparison.Ordinal), $"{format} leaked the hyperlink target");
            Assert.IsFalse(result.Content.Contains("Secret Title", StringComparison.Ordinal), $"{format} leaked the title");
            Assert.IsNull(result.NonScreenMetadata);
            AssertCoverage(result, DiagnosticContentClass.RenderedScreen, DiagnosticCoverageState.Included);
            AssertCoverage(result, DiagnosticContentClass.ConcealedText, DiagnosticCoverageState.Excluded);
            AssertCoverage(result, DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Excluded);
            AssertCoverage(result, DiagnosticContentClass.WindowTitle, DiagnosticCoverageState.Excluded);
            AssertCoverage(result, DiagnosticContentClass.EditorText, DiagnosticCoverageState.Excluded);
            AssertCoverage(result, DiagnosticContentClass.RawInput, DiagnosticCoverageState.Excluded);
            Assert.IsTrue(result.Limitations.Any(l => l.Contains("secret", StringComparison.OrdinalIgnoreCase)),
                "rendered content must not be advertised as secret-free");
        }
    }

    [TestMethod]
    public async Task Capture_AuthorizedNonScreenMetadata_IncludesTitleAndHyperlinkTargets()
    {
        const string url = "https://example.invalid/target";
        await using var source = await StartAsync(
            $"\x1b]0;Visible Title\x07\x1b]8;;{url}\x1b\\LINK\x1b]8;;\x1b\\ after", waitFor: "after");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "metadata");
        DiagnosticAuthorization[] authorized = [DiagnosticAuthorization.NonScreenMetadata];

        var ansi = diagnostics.Capture(new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Ansi,
            Authorizations = authorized
        });

        Assert.AreEqual("Visible Title", ansi.NonScreenMetadata!.WindowTitle);
        Assert.AreEqual("Visible Title", ansi.NonScreenMetadata.IconName);
        AssertCoverage(ansi, DiagnosticContentClass.WindowTitle, DiagnosticCoverageState.Included);
        AssertCoverage(ansi, DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Included);
        await using (var replica = await StartAsync(ansi.Content!, waitFor: "after"))
        {
            using var snapshot = replica.Terminal.CreateSnapshot();
            Assert.AreEqual(url, FindCell(snapshot, "LINK").HyperlinkData?.Uri, "authorized ANSI lost the hyperlink");
        }

        var html = diagnostics.Capture(new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Html,
            Authorizations = authorized
        });
        StringAssert.Contains(html.Content, url);
        AssertCoverage(html, DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Included);

        var text = diagnostics.Capture(new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Text,
            Authorizations = authorized
        });
        AssertCoverage(text, DiagnosticContentClass.HyperlinkTargets, DiagnosticCoverageState.Unavailable);
        Assert.AreEqual("Visible Title", text.NonScreenMetadata!.WindowTitle);
    }

    [TestMethod]
    public async Task Capture_ConcealedText_IsWithheldFromEveryFormat()
    {
        await using var source = await StartAsync("\x1b[8mHUNTER2\x1b[0m visible", waitFor: "visible", scrollback: 10);
        var diagnostics = new TerminalDiagnostics(source.Terminal, "concealed");

        foreach (var format in Enum.GetValues<DiagnosticCaptureFormat>())
        {
            var result = diagnostics.Capture(new DiagnosticCaptureRequest
            {
                Format = format,
                Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
            });

            Assert.IsFalse(result.Content!.Contains("HUNTER2", StringComparison.Ordinal), $"{format} leaked concealed text");
            foreach (var letter in "HUNTER2".Distinct().Except("visible"))
                Assert.IsFalse(result.Content.Contains($"{letter}", StringComparison.Ordinal) && format == DiagnosticCaptureFormat.Text,
                    $"{format} leaked a concealed glyph '{letter}'");
            AssertCoverage(result, DiagnosticContentClass.ConcealedText, DiagnosticCoverageState.Excluded);
        }

        var text = diagnostics.Capture(new DiagnosticCaptureRequest()).Content!;
        StringAssert.StartsWith(text, "        visible", "concealed cells must stay blank without shifting later text");
    }

    [TestMethod]
    public async Task Capture_WithheldHyperlinks_DoNotLeakModelReferences()
    {
        await using var source = await StartAsync(
            "\x1b]8;;https://example.invalid/r\x1b\\LINK\x1b]8;;\x1b\\\r\n" + string.Concat(Enumerable.Repeat("x\r\n", 8)),
            waitFor: "x", scrollback: 20);
        var link = FindTrackedHyperlink(source.Terminal);
        var baseline = link.RefCount;
        var diagnostics = new TerminalDiagnostics(source.Terminal, "refs");

        foreach (var format in Enum.GetValues<DiagnosticCaptureFormat>())
        {
            diagnostics.Capture(new DiagnosticCaptureRequest { Format = format, HistoryRows = 20 });
            diagnostics.Capture(new DiagnosticCaptureRequest
            {
                Format = format,
                HistoryRows = 20,
                Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
            });
        }

        Assert.AreEqual(baseline, link.RefCount, "captures changed the model's hyperlink reference count");
    }

    [TestMethod]
    public async Task Capture_HistoryCroppedToNarrowerScreen_ReportsTruncation()
    {
        await using var source = await StartAsync(
            new string('W', 38) + "END\r\n" + string.Concat(Enumerable.Repeat("x\r\n", 8)), waitFor: "x", scrollback: 20);
        source.Terminal.Resize(20, 6);
        var diagnostics = new TerminalDiagnostics(source.Terminal, "crop");

        var result = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = 20 });

        Assert.IsTrue(result.History!.CroppedRows >= 1, "cropped wide history was not reported");
        Assert.IsTrue(result.History.Truncated);
        StringAssert.Contains(result.History.Reason, "cropped");
        Assert.IsFalse(result.Content!.Contains("END", StringComparison.Ordinal), "fixture did not crop");
    }

    [TestMethod]
    public async Task Capabilities_DisposedTerminal_AreUnavailable()
    {
        var source = await StartAsync("x", waitFor: "x");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "t");
        await source.DisposeAsync();

        var capabilities = diagnostics.GetCapabilities();

        Assert.AreEqual(DiagnosticOutcome.Unavailable, capabilities.Outcome);
        Assert.AreEqual("target-disposed", capabilities.Problem!.Code);
        Assert.AreEqual(0, capabilities.Operations.Count);
    }

    [TestMethod]
    public async Task Capture_EditorAndRawInputAuthorizations_AreNotReportedAsCaptured()
    {
        await using var source = await StartAsync("x", waitFor: "x");
        var result = new TerminalDiagnostics(source.Terminal, "t").Capture(new DiagnosticCaptureRequest
        {
            Authorizations = [DiagnosticAuthorization.EditorText, DiagnosticAuthorization.RawInput]
        });

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        AssertCoverage(result, DiagnosticContentClass.EditorText, DiagnosticCoverageState.Unavailable);
        AssertCoverage(result, DiagnosticContentClass.RawInput, DiagnosticCoverageState.Unavailable);
        // Neither authorization implies non-screen metadata.
        AssertCoverage(result, DiagnosticContentClass.WindowTitle, DiagnosticCoverageState.Excluded);
        Assert.IsNull(result.NonScreenMetadata);
    }

    [TestMethod]
    public async Task Capture_Identity_ReportsCorrelationIdentitiesAndExplainsEveryAbsentField()
    {
        await using var first = await StartAsync("one", waitFor: "one");
        await using var second = await StartAsync("two", waitFor: "two");
        var diagnostics = new TerminalDiagnostics(first.Terminal, "identity-app");

        var a = diagnostics.Capture(new DiagnosticCaptureRequest());
        var b = diagnostics.Capture(new DiagnosticCaptureRequest());
        var other = new TerminalDiagnostics(second.Terminal, "identity-app").Capture(new DiagnosticCaptureRequest());

        var identity = a.Identity!;
        Assert.AreEqual(Environment.ProcessId, identity.ProcessId);
        Assert.AreEqual(DiagnosticLayer.TerminalModel, identity.SourceLayer);
        Assert.AreEqual("identity-app", identity.ApplicationName);
        Assert.AreEqual(
            typeof(Hex1bTerminal).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion,
            identity.Hex1bVersion);
        Assert.IsFalse(string.IsNullOrEmpty(identity.SessionId));
        Assert.AreEqual(identity.SessionId, b.Identity!.SessionId, "same terminal must keep its session identity");
        Assert.AreNotEqual(identity.SessionId, other.Identity!.SessionId, "distinct terminals share a session identity");
        Assert.AreEqual(nameof(RawByteWorkload), identity.Configuration.Workload);
        Assert.AreEqual("headless", identity.Configuration.Presentation);

        var acquisition = identity.Acquisition;
        Assert.AreEqual("process-monotonic", acquisition.ClockDomain);
        Assert.AreEqual(System.Diagnostics.Stopwatch.Frequency, acquisition.Frequency);
        Assert.IsTrue(acquisition.StartTimestamp <= acquisition.EndTimestamp);
        Assert.IsTrue(acquisition.WallClockStart <= acquisition.WallClockEnd);
        Assert.IsTrue(b.Identity.Acquisition.StartTimestamp >= acquisition.EndTimestamp, "acquisitions went backwards");

        Assert.IsTrue(identity.ModelSequence > 0, "modelSequence must name the observed model state");
        Assert.AreEqual(identity.ModelSequence, b.Identity.ModelSequence, "no event occurred between the two captures");
        Assert.IsFalse(a.UnavailableFields.Any(f => f.Field == "identity.modelSequence"));
        Assert.IsNull(identity.ApplicationFrame);
        AssertUnavailableField(a, "identity.applicationFrame");
        AssertEveryNullFieldIsExplained(a);
    }

    [TestMethod]
    public async Task Capture_PendingSynchronizedUpdate_ExplainsEveryAbsentField()
    {
        // A stopped clock keeps the model's 1 s synchronized-output timeout from releasing the update.
        await using var source = await StartAsync("\x1b[?2026hPART", waitFor: "PART",
            timeProvider: new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow));

        var result = new TerminalDiagnostics(source.Terminal, "pending").Capture(new DiagnosticCaptureRequest());

        Assert.IsTrue(result.SynchronizedUpdate!.Active, "fixture: the synchronized update is no longer pending");
        AssertEveryNullFieldIsExplained(result);
    }

    [TestMethod]
    public async Task Capture_InvalidRequest_ReturnsInvalidRequestWithoutContent()
    {
        await using var source = await StartAsync("x", waitFor: "x");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "t");

        var negative = diagnostics.Capture(new DiagnosticCaptureRequest { HistoryRows = -1 });
        Assert.AreEqual(DiagnosticOutcome.InvalidRequest, negative.Outcome);
        Assert.AreEqual("invalid-history-rows", negative.Problem!.Code);
        Assert.IsNull(negative.Content);

        var format = diagnostics.Capture(new DiagnosticCaptureRequest { Format = (DiagnosticCaptureFormat)42 });
        Assert.AreEqual(DiagnosticOutcome.InvalidRequest, format.Outcome);
        Assert.AreEqual("unsupported-format", format.Problem!.Code);
    }

    [TestMethod]
    public async Task Capture_DisposedTerminal_IsUnavailable()
    {
        var source = await StartAsync("x", waitFor: "x");
        var diagnostics = new TerminalDiagnostics(source.Terminal, "t");
        await source.DisposeAsync();

        var result = diagnostics.Capture(new DiagnosticCaptureRequest());

        Assert.AreEqual(DiagnosticOutcome.Unavailable, result.Outcome);
        Assert.AreEqual("target-disposed", result.Problem!.Code);
        Assert.IsNull(result.Content);
    }

    [TestMethod]
    public async Task Capture_IsObservational_SendsNothingToWorkloadOrPresentation()
    {
        var presentation = new CountingPresentationAdapter(40, 6);
        var workload = new RawByteWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 6).Build();
        await using var source = new Source(terminal, workload);
        await source.WriteAsync("\x1b[31mred\x1b[0m");
        await source.WaitForAsync("red");
        await presentation.WaitForQuietAsync();
        var writesBefore = presentation.Writes;
        var diagnostics = new TerminalDiagnostics(terminal, "t");

        foreach (var format in Enum.GetValues<DiagnosticCaptureFormat>())
            Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.Capture(new DiagnosticCaptureRequest
            {
                Format = format,
                HistoryRows = 5,
                Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
            }).Outcome);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.AreEqual(0, workload.InputWrites, "capture wrote input to the workload");
        Assert.AreEqual(writesBefore, presentation.Writes, "capture caused presentation output (repaint or host control)");
        Assert.AreEqual("presentation:CountingPresentationAdapter",
            $"presentation:{diagnostics.Capture(new DiagnosticCaptureRequest()).Identity!.Configuration.Presentation}");
    }

    [TestMethod]
    public async Task Result_RoundTripsThroughTheWireContractWithKebabCaseNames()
    {
        await using var source = await StartAsync("x", waitFor: "x");
        var result = new TerminalDiagnostics(source.Terminal, "t").Capture(new DiagnosticCaptureRequest
        {
            Format = DiagnosticCaptureFormat.Ansi,
            Authorizations = [DiagnosticAuthorization.NonScreenMetadata]
        });

        var json = JsonSerializer.Serialize(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult);
        StringAssert.Contains(json, "\"outcome\":\"captured\"");
        StringAssert.Contains(json, "\"sourceLayer\":\"terminal-model\"");
        StringAssert.Contains(json, "\"content\":\"hyperlink-targets\"");
        var roundTripped = JsonSerializer.Deserialize(json, DiagnosticsJsonContext.Default.DiagnosticCaptureResult)!;
        Assert.AreEqual(result.Content, roundTripped.Content);
        Assert.AreEqual(result.Identity!.SessionId, roundTripped.Identity!.SessionId);
        CollectionAssert.AreEqual(
            result.ContentCoverage.Select(c => (c.Content, c.State, c.Reason)).ToArray(),
            roundTripped.ContentCoverage.Select(c => (c.Content, c.State, c.Reason)).ToArray());

        var request = JsonSerializer.Deserialize(
            """{"format":"svg","historyRows":2,"authorizations":["non-screen-metadata"]}""",
            DiagnosticsJsonContext.Default.DiagnosticCaptureRequest)!;
        Assert.AreEqual(DiagnosticCaptureFormat.Svg, request.Format);
        Assert.AreEqual(DiagnosticAuthorization.NonScreenMetadata, request.Authorizations!.Single());
    }

    [TestMethod]
    public async Task Capabilities_DescribeImmediateCaptureAndExplainUnavailableLayers()
    {
        await using var source = await StartAsync("x", waitFor: "x");

        var capabilities = new TerminalDiagnostics(source.Terminal, "t").GetCapabilities();

        Assert.AreEqual(DiagnosticOutcome.Captured, capabilities.Outcome);
        var capture = capabilities.Operations.Single(o => o.Operation == "capture");
        Assert.AreEqual(DiagnosticLayer.TerminalModel, capture.Layer);
        CollectionAssert.AreEquivalent(Enum.GetValues<DiagnosticCaptureFormat>(), capture.Formats.ToArray());
        CollectionAssert.AreEqual(new[] { "immediate" }, capture.Timing.ToArray());
        Assert.IsTrue(capture.ModelHistory);
        CollectionAssert.Contains(capture.Authorizations.ToArray(), DiagnosticAuthorization.NonScreenMetadata);
        Assert.IsTrue(capture.Limitations.Count > 0);

        Assert.IsTrue(capabilities.Layers.Single(l => l.Layer == DiagnosticLayer.TerminalModel).Available);
        foreach (var layer in new[] { DiagnosticLayer.ApplicationFrame, DiagnosticLayer.NativeDelivery, DiagnosticLayer.NativePresentation })
        {
            var entry = capabilities.Layers.Single(l => l.Layer == layer);
            Assert.IsFalse(entry.Available, $"{layer} advertised without support");
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Reason), $"{layer} has no reason");
        }
    }

    // === Helpers ===

    private static void AssertCoverage(DiagnosticCaptureResult result, DiagnosticContentClass content, DiagnosticCoverageState state)
    {
        var entry = result.ContentCoverage.SingleOrDefault(c => c.Content == content);
        Assert.IsNotNull(entry, $"no coverage entry for {content}");
        Assert.AreEqual(state, entry.State, $"{content}: {entry.Reason}");
        if (state == DiagnosticCoverageState.Included)
            Assert.IsNull(entry.Reason);
        else
            Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Reason), $"{content} {state} without a reason");
    }

    private static void AssertUnavailableField(DiagnosticCaptureResult result, string field)
    {
        var entry = result.UnavailableFields.SingleOrDefault(f => f.Field == field);
        Assert.IsNotNull(entry, $"{field} is absent without an explanation");
        Assert.IsFalse(string.IsNullOrWhiteSpace(entry.Reason));
    }

    private static void AssertEveryNullFieldIsExplained(DiagnosticCaptureResult result)
    {
        var json = JsonSerializer.SerializeToElement(result, DiagnosticsJsonContext.Default.DiagnosticCaptureResult);
        foreach (var section in new[] { "identity", "history" })
        {
            foreach (var property in json.GetProperty(section).EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Null && property.Name != "reason")
                    AssertUnavailableField(result, $"{section}.{property.Name}");
            }
        }

        // startedAtSequence is conditional, not unavailable: present exactly while an update is pending.
        var sync = json.GetProperty("synchronizedUpdate");
        Assert.AreEqual(sync.GetProperty("active").GetBoolean(), sync.TryGetProperty("startedAtSequence", out _),
            "startedAtSequence must be present exactly when a synchronized update is active");

        // Fields omitted entirely by the serializer must also be accounted for.
        var identityNames = typeof(DiagnosticObservationIdentity).GetProperties()
            .Select(p => p.GetCustomAttribute<System.Text.Json.Serialization.JsonPropertyNameAttribute>()!.Name);
        foreach (var name in identityNames)
        {
            if (!json.GetProperty("identity").TryGetProperty(name, out _))
                AssertUnavailableField(result, $"identity.{name}");
        }
    }

    private static TrackedObject<HyperlinkData> FindTrackedHyperlink(Hex1bTerminal terminal)
    {
        using var snapshot = terminal.CreateSnapshot(20);
        for (var y = 0; y < snapshot.Height; y++)
            for (var x = 0; x < snapshot.Width; x++)
                if (snapshot.GetCell(x, y).TrackedHyperlink is { } link)
                    return link;

        Assert.Fail("no hyperlink in the model");
        return null!;
    }

    private static TerminalCell FindCell(Hex1bTerminalSnapshot snapshot, string text)
    {
        for (var y = 0; y < snapshot.Height; y++)
        {
            var row = new StringBuilder();
            for (var x = 0; x < snapshot.Width; x++)
                row.Append(string.IsNullOrEmpty(snapshot.GetCell(x, y).Character) ? " " : snapshot.GetCell(x, y).Character);
            var column = row.ToString().IndexOf(text, StringComparison.Ordinal);
            if (column >= 0)
                return snapshot.GetCell(column, y);
        }

        Assert.Fail($"'{text}' not found in snapshot");
        return default;
    }

    private static async Task<Source> StartAsync(string output, string waitFor, int? scrollback = null,
        TimeProvider? timeProvider = null)
    {
        var workload = new RawByteWorkload();
        var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 6);
        if (scrollback is int capacity)
            builder.WithScrollback(capacity);
        if (timeProvider is not null)
            builder.WithTimeProvider(timeProvider);
        var source = new Source(builder.Build(), workload);
        await source.WriteAsync(output);
        await source.WaitForAsync(waitFor);
        return source;
    }

    private sealed class Source(Hex1bTerminal terminal, RawByteWorkload workload) : IAsyncDisposable
    {
        public Hex1bTerminal Terminal { get; } = terminal;

        public ValueTask WriteAsync(string output) =>
            workload.Output.Writer.WriteAsync(Encoding.UTF8.GetBytes(output), TestContext.Current.CancellationToken);

        public Task WaitForAsync(string text) => new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText(text), TimeSpan.FromSeconds(5), $"'{text}' applied")
            .Build().ApplyAsync(Terminal, TestContext.Current.CancellationToken);

        public ValueTask DisposeAsync() => Terminal.DisposeAsync();
    }

    private sealed class RawByteWorkload : IHex1bTerminalWorkloadAdapter
    {
        private int _inputWrites;

        public Channel<byte[]> Output { get; } = Channel.CreateUnbounded<byte[]>();

        public int InputWrites => Volatile.Read(ref _inputWrites);

        public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        {
            try
            {
                return await Output.Reader.ReadAsync(ct);
            }
            catch (ChannelClosedException)
            {
                return ReadOnlyMemory<byte>.Empty;
            }
        }

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _inputWrites);
            return ValueTask.CompletedTask;
        }

        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;

        public event Action? Disconnected { add { } remove { } }

        public ValueTask DisposeAsync()
        {
            Output.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingPresentationAdapter(int width, int height) : IHex1bTerminalPresentationAdapter
    {
        private int _writes;
        private long _lastWrite = System.Diagnostics.Stopwatch.GetTimestamp();

        public int Writes => Volatile.Read(ref _writes);
        public int Width => width;
        public int Height => height;
        public TerminalCapabilities Capabilities => new() { Supports256Colors = true, SupportsTrueColor = true };
        public event Action<int, int>? Resized { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }

        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _writes);
            Interlocked.Exchange(ref _lastWrite, System.Diagnostics.Stopwatch.GetTimestamp());
            return ValueTask.CompletedTask;
        }

        public async Task WaitForQuietAsync()
        {
            while (System.Diagnostics.Stopwatch.GetElapsedTime(Interlocked.Read(ref _lastWrite)) < TimeSpan.FromMilliseconds(150))
                await Task.Delay(25, TestContext.Current.CancellationToken);
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
