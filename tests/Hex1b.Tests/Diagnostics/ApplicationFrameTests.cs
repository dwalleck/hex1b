using System.Diagnostics;
using System.Text;
using Hex1b.Diagnostics;
using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A diagnostics-enabled Hex1b app publishes an immutable projection after every completed
/// render pass, and the shared engine returns the latest one without driving the app loop.
/// </summary>
[TestClass]
public class ApplicationFrameTests
{
    [TestMethod]
    public async Task PublishesEveryCompletedPass()
    {
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget($"count {counter}"));

        for (var i = 0; i < 10; i++)
        {
            var changed = await harness.NextPassAsync(() => counter++);
            var withChange = harness.Capture();
            Assert.AreEqual(changed, withChange.Frame!.FrameId, "frame identity is not the completed-pass count");
            Assert.IsTrue(withChange.Frame.WroteOutput, $"pass {changed} changed text but reports no output");
            Assert.AreEqual(changed, withChange.Identity!.ApplicationFrame);

            var unchanged = await harness.NextPassAsync(change: null);
            var withoutChange = harness.Capture();
            Assert.AreEqual(unchanged, withoutChange.Frame!.FrameId, $"frame {unchanged} not published; latest is {withoutChange.Frame.FrameId}");
            Assert.IsFalse(withoutChange.Frame.WroteOutput, $"pass {unchanged} changed nothing but reports output");
        }
    }

    [TestMethod]
    public async Task CaptureNeverAdvancesFrames()
    {
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget("idle"));
        await harness.WaitForQuietAsync();
        var before = harness.App.FrameCount;

        for (var i = 0; i < 100; i++)
            Assert.AreEqual(DiagnosticOutcome.Captured, harness.Capture().Outcome);
        await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.AreEqual(before, harness.App.FrameCount, $"passes advanced {before} -> {harness.App.FrameCount}");
        Assert.AreEqual(before, harness.Capture().Frame!.FrameId);
    }

    [TestMethod]
    public async Task NoProjectionWithoutDiagnostics()
    {
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new TextBlockWidget($"n {counter}"), publish: false);
        for (var i = 0; i < 10; i++)
            await harness.NextPassAsync(() => counter++);

        var source = (IApplicationFrameSource)harness.App;
        Assert.IsNull(source.LatestFrame, $"an app without diagnostics projected frame {source.LatestFrame?.FrameId}");
        var result = harness.Capture();
        Assert.AreEqual(DiagnosticOutcome.Unavailable, result.Outcome);
        Assert.AreEqual("application-frame-publication-disabled", result.Problem!.Code);
    }

    [TestMethod]
    public async Task UnavailableReasonsAreDistinct()
    {
        // Publication enabled but the first pass is still building: no frame yet.
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(20, 3).Build();
        using var cts = new CancellationTokenSource();
        using var app = new Hex1bApp(async _ => { await gate.Task; return new TextBlockWidget("late"); },
            new Hex1bAppOptions { WorkloadAdapter = workload });
        var run = app.RunAsync(cts.Token);
        for (var i = 0; i < 100 && workload.ApplicationFrameSource is null; i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);

        var notYet = new TerminalDiagnostics(terminal, "gated").CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());
        gate.SetResult();
        await cts.CancelAsync();
        try { await run; } catch (OperationCanceledException) { }

        await using var disabled = await AppHarness.StartAsync(_ => new TextBlockWidget("off"), publish: false);
        var off = disabled.Capture();

        var rawWorkload = new Hex1bAppWorkloadAdapter();
        await using var raw = Hex1bTerminal.CreateBuilder().WithWorkload(rawWorkload).WithHeadless().WithDimensions(20, 3).Build();
        var noApp = new TerminalDiagnostics(raw, "raw").CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());

        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-application-frame-yet"), (notYet.Outcome, notYet.Problem!.Code));
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "application-frame-publication-disabled"), (off.Outcome, off.Problem!.Code));
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-application-layer"), (noApp.Outcome, noApp.Problem!.Code));
    }

    [TestMethod]
    public async Task NoApplicationLayer_CapabilitiesMarkTheLayerUnavailable()
    {
        await using var raw = Hex1bTerminal.CreateBuilder().WithWorkload(new Hex1bAppWorkloadAdapter()).WithHeadless().WithDimensions(20, 3).Build();
        var diagnostics = new TerminalDiagnostics(raw, "raw");

        var result = diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest { Authorizations = [DiagnosticAuthorization.EditorText] });
        var layer = diagnostics.GetCapabilities().Layers.Single(l => l.Layer == DiagnosticLayer.ApplicationFrame);

        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-application-layer"), (result.Outcome, result.Problem!.Code));
        Assert.IsNull(result.Frame, "a workload without an application layer returned a frame");
        Assert.IsFalse(layer.Available);
        Assert.AreEqual(result.Problem.Message, layer.Reason, "capabilities and the operation disagree on why there is no frame");
    }

    [TestMethod]
    public async Task Clipping_VisibleBoundsFollowEnclosingClipRegions()
    {
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            // Inner area is rows 1-2. row-1 wraps onto rows 2-3, straddling the clip edge.
            new BorderWidget(new VStackWidget(
            [
                new TextBlockWidget("row-0"),
                new TextBlockWidget($"row-1 {new string('w', 60)}", TextOverflow.Wrap),
                .. Enumerable.Range(2, 8).Select(i => (Hex1bWidget)new TextBlockWidget($"row-{i}")),
            ])).FixedHeight(4),
        ]), columns: 40, rows: 10);

        var frame = harness.Capture().Frame!;
        var nodes = new List<(DiagnosticFrameNode Node, Rect Expected)>();
        Walk(frame.Root!, new Rect(0, 0, frame.Columns, frame.Rows), nodes);

        foreach (var (node, expected) in nodes)
        {
            var visible = node.VisibleBounds;
            Assert.AreEqual(Describe(expected), $"{visible.X},{visible.Y},{visible.Width},{visible.Height}",
                $"{node.Type} visible {Describe(visible)} != expected {Describe(expected)} for bounds {Describe(node.Bounds)}");
            var expectedState = expected.Width <= 0 || expected.Height <= 0 ? DiagnosticClipState.FullyClipped
                : expected.Width == node.Bounds.Width && expected.Height == node.Bounds.Height ? DiagnosticClipState.Visible
                : DiagnosticClipState.PartiallyClipped;
            if (node.Bounds.Width > 0 && node.Bounds.Height > 0)
                Assert.AreEqual(expectedState, node.ClipState, $"{node.Type} at {Describe(node.Bounds)}");
        }

        var rows = nodes.Where(n => n.Node.Text?.StartsWith("row-", StringComparison.Ordinal) == true).Select(n => n.Node).ToList();
        Assert.IsTrue(rows.Any(r => r.ClipState == DiagnosticClipState.PartiallyClipped), "no row was partially clipped by the border");
        Assert.IsTrue(rows.Any(r => r.ClipState == DiagnosticClipState.FullyClipped), "no row was clipped away by the border");
        var border = nodes.Single(n => n.Node.Type == nameof(BorderNode)).Node;
        Assert.AreEqual("clip", border.ClipMode);
        Assert.IsNotNull(border.ClipRect);
    }

    [TestMethod]
    public async Task ProjectionFailure_ReportsTheFailedFrameAndRecovers()
    {
        var armed = new StrongBox<bool>();
        var counter = 0;
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            new TextBlockWidget($"n {counter}"),
            new ArmableProjectionFailureWidget(armed),
        ]));

        armed.Value = true;
        var failedPass = await harness.NextPassAsync(() => counter++);
        var failed = harness.Capture();
        armed.Value = false;

        Assert.AreEqual(DiagnosticOutcome.Failed, failed.Outcome,
            $"captured frame {failed.Frame?.FrameId} while frame {failedPass} failed");
        Assert.AreEqual("application-frame-projection-failed", failed.Problem!.Code);
        StringAssert.Contains(failed.Problem.Message, $"frame {failedPass}");
        await harness.WaitForTextAsync($"n {counter}");

        var recovered = await harness.NextPassAsync(() => counter++);
        var result = harness.Capture();
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.AreEqual(recovered, result.Frame!.FrameId);
    }

    // Multiline text with a surrogate pair (U+1D11E) on the middle line.
    private const string MultilineText = "alpha\nbe\U0001D11Eta gamma\nz";

    [TestMethod]
    public async Task EditorMetadata_TextBoxReportsCaretAndSelectionsBothDirections()
    {
        var state = new TextBoxState();
        await using var harness = await AppHarness.StartAsync(_ => new TextBoxWidget().State(state).Multiline(), columns: 40, rows: 6);

        var anchor = MultilineText.IndexOf("ta", StringComparison.Ordinal);
        var cursor = 2;
        await harness.NextPassAsync(() =>
        {
            state.Text = MultilineText;
            state.CursorPosition = cursor;
            state.SelectionAnchor = anchor; // backward: cursor before anchor, spanning lines
        });
        var backward = harness.Capture().Frame!;

        await harness.NextPassAsync(() =>
        {
            state.CursorPosition = MultilineText.Length;
            state.SelectionAnchor = 1; // forward
        });
        var forward = harness.Capture().Frame!;

        foreach (var (frame, caret, start, end) in new[]
        {
            (backward, cursor, cursor, anchor),
            (forward, MultilineText.Length, 1, MultilineText.Length),
        })
        {
            var editor = frame.FocusedEditor;
            Assert.IsNotNull(editor, $"frame {frame.FrameId} reports no focused editor");
            Assert.AreEqual("text-box", editor.Kind);
            Assert.AreEqual(MultilineText.Length, editor.Length);
            Assert.AreEqual(3, editor.LineCount);
            AssertBoundsOf(frame, nameof(TextBoxNode), editor.Bounds);
            Assert.HasCount(1, editor.Carets, $"expected 1 caret, got {editor.Carets.Count}");
            AssertCaret(MultilineText, caret, editor.Carets[0]);
            Assert.HasCount(1, editor.Selections, $"expected 1 selection, got {editor.Selections.Count}");
            AssertCaret(MultilineText, start, editor.Selections[0].Start);
            AssertCaret(MultilineText, end, editor.Selections[0].End);
        }
    }

    [TestMethod]
    public async Task EditorMetadata_EditorReportsEveryCursorAndSelection()
    {
        var state = new EditorState(new Hex1bDocument(MultilineText));
        await using var harness = await AppHarness.StartAsync(_ => new EditorWidget(state), columns: 40, rows: 6);

        var second = MultilineText.IndexOf("gamma", StringComparison.Ordinal);
        await harness.NextPassAsync(() =>
        {
            state.Cursors.Primary.Position = new DocumentOffset(3);
            state.Cursors.Primary.SelectionAnchor = new DocumentOffset(1);
            state.Cursors.Add(new DocumentOffset(second + 2), new DocumentOffset(second));
            state.Cursors.Add(new DocumentOffset(MultilineText.Length), null);
        });
        var frame = harness.Capture().Frame!;

        var editor = frame.FocusedEditor;
        Assert.IsNotNull(editor, "no focused editor");
        Assert.AreEqual("editor", editor.Kind);
        Assert.AreEqual(MultilineText.Length, editor.Length);
        Assert.AreEqual(3, editor.LineCount);
        AssertBoundsOf(frame, nameof(EditorNode), editor.Bounds);

        var expectedCarets = state.Cursors.Select(c => c.Position.Value).ToArray();
        Assert.HasCount(3, expectedCarets);
        Assert.AreEqual(expectedCarets.Length, editor.Carets.Count, $"expected {expectedCarets.Length} carets, got {editor.Carets.Count}");
        for (var i = 0; i < expectedCarets.Length; i++)
            AssertCaret(MultilineText, expectedCarets[i], editor.Carets[i]);

        var expectedSelections = state.Cursors.Where(c => c.HasSelection)
            .Select(c => (c.SelectionStart.Value, c.SelectionEnd.Value)).ToArray();
        Assert.HasCount(2, expectedSelections);
        Assert.AreEqual(expectedSelections.Length, editor.Selections.Count, $"expected {expectedSelections.Length} selections, got {editor.Selections.Count}");
        for (var i = 0; i < expectedSelections.Length; i++)
        {
            AssertCaret(MultilineText, expectedSelections[i].Item1, editor.Selections[i].Start);
            AssertCaret(MultilineText, expectedSelections[i].Item2, editor.Selections[i].End);
        }
    }

    [TestMethod]
    public async Task EditorMetadata_NonEditorFocusReportsNoEditor()
    {
        await using var harness = await AppHarness.StartAsync(_ => new ButtonWidget("press"));
        var result = harness.Capture(DiagnosticAuthorization.EditorText);

        Assert.AreEqual(nameof(ButtonNode), result.Frame!.Focus.FocusedNodeType);
        Assert.IsNull(result.Frame.FocusedEditor);
        Assert.IsTrue(result.UnavailableFields.Any(f => f.Field == "frame.focusedEditor"), "absent focused editor is not explained");
        var coverage = result.ContentCoverage.Single(c => c.Content == DiagnosticContentClass.EditorText);
        Assert.AreEqual(DiagnosticCoverageState.Unavailable, coverage.State);
    }

    private const string SentinelA = "SENTINEL-A-7f3e";
    private const string SentinelB = "SENTINEL-B-91c2";

    [TestMethod]
    public async Task EditorText_DefaultCaptureWithholdsAllEditorText()
    {
        await using var harness = await StartSentinelAppAsync();
        var result = harness.Capture();
        var json = Serialize(result);

        Assert.IsFalse(json.Contains(SentinelA, StringComparison.Ordinal), "sentinel A in default capture");
        Assert.IsFalse(json.Contains(SentinelB, StringComparison.Ordinal), "sentinel B in default capture");
        Assert.IsNull(result.Frame!.FocusedEditor!.Text);
        var coverage = result.ContentCoverage.Single(c => c.Content == DiagnosticContentClass.EditorText);
        Assert.AreEqual(DiagnosticCoverageState.Excluded, coverage.State);
    }

    [TestMethod]
    public async Task EditorText_AuthorizedCaptureIncludesOnlyTheFocusedEditor()
    {
        await using var harness = await StartSentinelAppAsync();
        var result = harness.Capture(DiagnosticAuthorization.EditorText);
        var json = Serialize(result);

        Assert.IsTrue(json.Contains(SentinelA, StringComparison.Ordinal), "focused editor's sentinel A missing from editor-text capture");
        Assert.IsFalse(json.Contains(SentinelB, StringComparison.Ordinal), "sentinel B present: an unfocused editor's text leaked");
        Assert.AreEqual(SentinelA, result.Frame!.FocusedEditor!.Text);
        var coverage = result.ContentCoverage.Single(c => c.Content == DiagnosticContentClass.EditorText);
        Assert.AreEqual(DiagnosticCoverageState.Included, coverage.State);
    }

    [TestMethod]
    public async Task EditorText_OtherAuthorizationsNeverAddEditorText()
    {
        await using var harness = await StartSentinelAppAsync();
        foreach (var authorization in Enum.GetValues<DiagnosticAuthorization>().Where(a => a != DiagnosticAuthorization.EditorText))
        {
            var json = Serialize(harness.Capture(authorization));
            Assert.IsFalse(json.Contains(SentinelA, StringComparison.Ordinal), $"sentinel A in {authorization} capture");
            Assert.IsFalse(json.Contains(SentinelB, StringComparison.Ordinal), $"sentinel B in {authorization} capture");
        }
    }

    [TestMethod]
    public async Task ReturnedFrameIsImmutable()
    {
        var editorState = new EditorState(new Hex1bDocument("first line\nsecond"));
        var boxState = new TextBoxState();
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            new EditorWidget(editorState).FixedHeight(3),
            new TextBoxWidget().State(boxState),
            new TextBlockWidget($"lines {editorState.Document.LineCount}"),
        ]), columns: 40, rows: 8);
        await harness.NextPassAsync(() =>
        {
            editorState.Cursors.Primary.Position = new DocumentOffset(2);
            editorState.Cursors.Add(new DocumentOffset(12), new DocumentOffset(11));
        });

        var retained = harness.Capture(DiagnosticAuthorization.EditorText);
        Assert.AreEqual("editor", retained.Frame!.FocusedEditor?.Kind, "fixture: the editor should have focus");
        var first = Serialize(retained);

        // Later passes change editor text and cursors, focus, and geometry.
        await harness.NextPassAsync(() =>
        {
            editorState.InsertText("typed ");
            editorState.Cursors.Add(new DocumentOffset(0), null);
        });
        await harness.NextPassAsync(() => harness.App.FocusWhere(node => node is TextBoxNode));
        var resized = harness.App.FrameCount + 1;
        harness.Resize(30, 6);
        await harness.WaitForPassAsync(resized);
        var later = harness.Capture(DiagnosticAuthorization.EditorText);
        Assert.AreNotEqual(first, Serialize(later), "fixture: later passes should produce a different frame");

        Assert.AreEqual(first, Serialize(retained), "result changed after later frames");
    }

    [TestMethod]
    public async Task ConcurrentChanges_FramesStayConsistent()
    {
        var box = new TextBoxState();
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
        [
            new BorderWidget(new VStackWidget(
            [
                new TextBoxWidget().State(box),
                new TextBlockWidget($"typed {box.Text.Length} {new string('x', box.Text.Length % 50)}", TextOverflow.Wrap),
                new ButtonWidget("ok"),
                new TextBoxWidget("second"),
            ])).FixedHeight(6),
            new TextBlockWidget("footer"),
        ]), columns: 40, rows: 10);

        using var stop = new CancellationTokenSource();
        var driver = Task.Run(async () =>
        {
            var sizes = new[] { (40, 10), (24, 8), (60, 12), (30, 5) };
            for (var i = 0; !stop.IsCancellationRequested; i++)
            {
                var input = new Hex1bTerminalInputSequenceBuilder().Type("ab");
                if (i % 3 == 0)
                    input = input.Tab();
                await input.Build().ApplyAsync(harness.Terminal, stop.Token);
                if (i % 4 == 0)
                {
                    var (w, h) = sizes[i / 4 % sizes.Length];
                    harness.Resize(w, h);
                }
                await Task.Delay(2, stop.Token);
            }
        }, stop.Token);

        var frameIds = new HashSet<long>();
        var sizesSeen = new HashSet<(int, int)>();
        var focusSeen = new HashSet<string>();
        try
        {
            var elapsed = Stopwatch.StartNew();
            for (var capture = 0; capture < 200 || (frameIds.Count < 40 && elapsed.Elapsed < TimeSpan.FromSeconds(8)); capture++)
            {
                var result = harness.Capture(DiagnosticAuthorization.EditorText);
                Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, result.Problem?.Message);
                var frame = result.Frame!;
                CheckInvariants(frame);
                frameIds.Add(frame.FrameId);
                sizesSeen.Add((frame.Columns, frame.Rows));
                focusSeen.Add(frame.Focus.FocusedNodeType ?? "none");
                await Task.Delay(capture % 2 == 0 ? 1 : 0, TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            await stop.CancelAsync();
            try { await driver; } catch (OperationCanceledException) { }
        }

        Assert.IsGreaterThanOrEqualTo(40, frameIds.Count, $"only {frameIds.Count} distinct frames observed; the driver did not exercise concurrency");
        Assert.IsGreaterThan(1, sizesSeen.Count, "no resize was observed");
        Assert.IsGreaterThan(1, focusSeen.Count, "no focus change was observed");
    }

    [TestMethod]
    public async Task TimingsAndPopups_TimingsPresentWhenEnabled()
    {
        await using var harness = await AppHarness.StartAsync(_ => new VStackWidget(
            [new TextBlockWidget("a"), new ButtonWidget("b")]));
        var result = harness.Capture();

        var timings = result.Frame!.Timings;
        Assert.IsNotNull(timings, "frame timings missing with timing enabled");
        Assert.IsTrue(timings.BuildMs >= 0 && timings.ReconcileMs >= 0 && timings.RenderMs >= 0);
        var nodes = new List<(DiagnosticFrameNode Node, Rect Expected)>();
        Walk(result.Frame.Root!, new Rect(0, 0, result.Frame.Columns, result.Frame.Rows), nodes);
        foreach (var (node, _) in nodes)
            Assert.IsNotNull(node.Timing, $"{node.Type} has no node timing");
        Assert.IsTrue(nodes.Any(n => n.Node.Timing!.LastRenderedMsAgo >= 0), "no node reports a last render");
        Assert.IsFalse(result.UnavailableFields.Any(f => f.Field == "frame.timings"));
    }

    [TestMethod]
    public void TimingsAndPopups_TimingsExplainedWhenDisabled()
    {
        var frame = ApplicationFrameProjector.Project(new TextBlockNode(), new FocusRing(), 1, 10, 2, wroteOutput: true, timings: null);
        var source = new FixedFrameSource(new PublishedApplicationFrame(1, frame, null, 0, 0, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow));
        var workload = new Hex1bAppWorkloadAdapter { ApplicationFrameSource = source };
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(10, 2).Build();

        var result = new TerminalDiagnostics(terminal, "untimed").CaptureApplicationFrame(new DiagnosticApplicationFrameRequest());

        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome);
        Assert.IsNull(result.Frame!.Timings);
        Assert.IsNull(result.Frame.Root!.Timing);
        Assert.IsTrue(result.UnavailableFields.Any(f => f.Field == "frame.timings"), "absent timings are not explained");
    }

    [TestMethod]
    public async Task TimingsAndPopups_PopupsKeepTheirFields()
    {
        await using var harness = await AppHarness.StartAsync(_ => new ZStackWidget(
            [new VStackWidget([new TextBlockWidget("base"), new ButtonWidget("anchor")])]), columns: 40, rows: 10);

        await harness.NextPassAsync(() =>
        {
            var zstack = FindNode<ZStackNode>(harness.App.RootNode)!;
            var anchor = FindNode<ButtonNode>(harness.App.RootNode)!;
            zstack.Popups.PushAnchored(anchor, AnchorPosition.Below, new TextBlockWidget("popped"), focusRestoreNode: anchor).AsBarrier();
        });
        await harness.NextPassAsync(change: null);
        var frame = harness.Capture().Frame!;

        Assert.HasCount(1, frame.Popups);
        var popup = frame.Popups[0];
        var button = FindFrameNode(frame.Root!, nameof(ButtonNode));
        Assert.AreEqual(0, popup.Index);
        Assert.AreEqual(nameof(AnchoredNode), popup.ContentType);
        Assert.IsNotNull(popup.ContentBounds);
        Assert.IsTrue(popup.IsBarrier, "barrier flag lost");
        Assert.IsTrue(popup.IsAnchored, "anchored flag lost");
        Assert.AreEqual(nameof(ButtonNode), popup.FocusRestoreNodeType);
        Assert.AreEqual(nameof(ButtonNode), popup.AnchorNodeType);
        Assert.AreEqual(Describe(button.Bounds), Describe(popup.AnchorBounds!));
        Assert.IsFalse(popup.AnchorIsStale);
        Assert.AreEqual(nameof(AnchorPosition.Below), popup.AnchorPosition);
    }

    // === Helpers ===

    private static Task<AppHarness> StartSentinelAppAsync() => AppHarness.StartAsync(_ => new VStackWidget(
    [
        new TextBoxWidget(SentinelA),
        new TextBoxWidget(SentinelB),
    ]));

    private static string Serialize(DiagnosticApplicationFrameResult result) =>
        System.Text.Json.JsonSerializer.Serialize(result, DiagnosticsJsonContext.Default.DiagnosticApplicationFrameResult);

    // Independent line/column scan: 0-based, columns count UTF-16 code units, lines split on '\n'.
    private static (int Line, int Column) Scan(string text, int offset)
    {
        int line = 0, column = 0;
        for (var i = 0; i < offset; i++)
        {
            if (text[i] == '\n') { line++; column = 0; }
            else column++;
        }
        return (line, column);
    }

    private static void AssertCaret(string text, int offset, DiagnosticCaret actual)
    {
        var (line, column) = Scan(text, offset);
        Assert.AreEqual($"{offset}@{line}:{column}", $"{actual.Offset}@{actual.Line}:{actual.Column}", "offset@line:column");
    }

    private static void AssertBoundsOf(DiagnosticApplicationFrame frame, string type, DiagnosticRect bounds)
    {
        var node = FindFrameNode(frame.Root!, type);
        Assert.AreEqual(Describe(node.Bounds), Describe(bounds), $"{type} editor bounds");
        Assert.IsGreaterThan(0, bounds.Width * bounds.Height, "editor has no area");
    }

    private static DiagnosticFrameNode FindFrameNode(DiagnosticFrameNode node, string type) =>
        node.Type == type ? node : node.Children.Select(child => FindFrameNodeOrNull(child, type)).FirstOrDefault(n => n is not null)
            ?? throw new AssertFailedException($"no {type} in the frame");

    private static DiagnosticFrameNode? FindFrameNodeOrNull(DiagnosticFrameNode node, string type) =>
        node.Type == type ? node : node.Children.Select(child => FindFrameNodeOrNull(child, type)).FirstOrDefault(n => n is not null);

    private static T? FindNode<T>(Hex1bNode? node) where T : Hex1bNode =>
        node is null ? null : node as T ?? node.GetChildren().Select(FindNode<T>).FirstOrDefault(n => n is not null);

    // Invariants recomputed from the result alone, without projector code.
    private static void CheckInvariants(DiagnosticApplicationFrame frame)
    {
        var nodes = new List<(DiagnosticFrameNode Node, Rect Expected)>();
        Walk(frame.Root!, new Rect(0, 0, frame.Columns, frame.Rows), nodes);
        foreach (var (node, expected) in nodes)
        {
            Assert.AreEqual(Describe(expected), Describe(node.VisibleBounds),
                $"frame {frame.FrameId}: {node.Type} visible rect outside its bounds or enclosing clips");
        }

        var treeFocused = nodes.Where(n => n.Node.IsFocused && n.Node.IsFocusable).Select(n => n.Node).ToList();
        var ring = frame.Focus;
        Assert.AreEqual(ring.Focusables.Count(f => f.IsFocused), treeFocused.Count, $"frame {frame.FrameId}: focus ring and tree disagree on focus");
        if (ring.CurrentIndex >= 0)
        {
            var entry = ring.Focusables[ring.CurrentIndex];
            Assert.IsTrue(entry.IsFocused, $"frame {frame.FrameId}: current focus entry is not focused");
            Assert.AreEqual(ring.FocusedNodeType, entry.Type);
            Assert.IsTrue(treeFocused.Any(n => n.Type == entry.Type && Describe(n.Bounds) == Describe(entry.Bounds)),
                $"frame {frame.FrameId}: focused {entry.Type} at {Describe(entry.Bounds)} is not the tree's focused node");
        }

        if (frame.FocusedEditor is { } editor)
        {
            Assert.AreEqual(ring.FocusedNodeType == nameof(TextBoxNode) ? "text-box" : "editor", editor.Kind, $"frame {frame.FrameId}");
            Assert.AreEqual(editor.Length, editor.Text!.Length, $"frame {frame.FrameId}: editor length differs from its text");
            Assert.AreEqual(editor.Text.Count(c => c == '\n') + 1, editor.LineCount, $"frame {frame.FrameId}: line count");
            foreach (var caret in editor.Carets.Concat(editor.Selections.SelectMany(s => new[] { s.Start, s.End })))
            {
                Assert.IsTrue(caret.Offset >= 0 && caret.Offset <= editor.Length, $"frame {frame.FrameId}: caret {caret.Offset} outside length {editor.Length}");
                var (line, column) = Scan(editor.Text, caret.Offset);
                Assert.AreEqual((line, column), (caret.Line, caret.Column), $"frame {frame.FrameId}: caret {caret.Offset}");
            }
            foreach (var selection in editor.Selections)
                Assert.IsLessThanOrEqualTo(selection.End.Offset, selection.Start.Offset, $"frame {frame.FrameId}: inverted selection");
        }
    }

    private static void Walk(DiagnosticFrameNode node, Rect clip, List<(DiagnosticFrameNode, Rect)> into)
    {
        var bounds = new Rect(node.Bounds.X, node.Bounds.Y, node.Bounds.Width, node.Bounds.Height);
        into.Add((node, Overlap(bounds, clip)));
        var childClip = node.ClipMode == "clip" && node.ClipRect is { } own
            ? Overlap(clip, new Rect(own.X, own.Y, own.Width, own.Height))
            : clip;
        foreach (var child in node.Children)
            Walk(child, childClip, into);
    }

    // Independent rectangle arithmetic for the oracle.
    private static Rect Overlap(Rect a, Rect b)
    {
        int left = Math.Max(a.X, b.X), top = Math.Max(a.Y, b.Y);
        int right = Math.Min(a.X + a.Width, b.X + b.Width), bottom = Math.Min(a.Y + a.Height, b.Y + b.Height);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : new Rect(left, top, 0, 0);
    }

    private static string Describe(Rect r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private static string Describe(DiagnosticRect r) => $"{r.X},{r.Y},{r.Width},{r.Height}";

    private sealed class FixedFrameSource(PublishedApplicationFrame frame) : IApplicationFrameSource
    {
        public bool FramePublicationEnabled => true;

        public PublishedApplicationFrame? LatestFrame => frame;
    }

    internal sealed class StrongBox<T>
    {
        public T? Value { get; set; }
    }

    internal sealed record ArmableProjectionFailureWidget(StrongBox<bool> Armed) : Hex1bWidget
    {
        internal override Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
        {
            var node = existingNode as ArmableProjectionFailureNode ?? new ArmableProjectionFailureNode();
            node.Armed = Armed;
            return Task.FromResult<Hex1bNode>(node);
        }

        internal override Type GetExpectedNodeType() => typeof(ArmableProjectionFailureNode);
    }

    // Rendering never reads HitTestBounds, so arming it makes only the diagnostic projection fail.
    internal sealed class ArmableProjectionFailureNode : Hex1bNode
    {
        public StrongBox<bool> Armed { get; set; } = new();

        public override Rect HitTestBounds =>
            Armed.Value ? throw new InvalidOperationException("armed projection failure") : Bounds;

        protected override Size MeasureCore(Constraints constraints) => constraints.Constrain(new Size(1, 1));

        public override void Render(Hex1bRenderContext context)
        {
        }
    }

    internal sealed class AppHarness : IAsyncDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private Task _run = Task.CompletedTask;

        private AppHarness(Hex1bTerminal terminal, Hex1bApp app)
        {
            Terminal = terminal;
            App = app;
            Diagnostics = new TerminalDiagnostics(terminal, "frames");
        }

        public Hex1bTerminal Terminal { get; }
        public Hex1bApp App { get; }
        public TerminalDiagnostics Diagnostics { get; }

        public static async Task<AppHarness> StartAsync(Func<RootContext, Hex1bWidget> build, bool publish = true,
            int columns = 40, int rows = 6)
        {
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = publish };
            var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(columns, rows).Build();
            var app = new Hex1bApp(build, new Hex1bAppOptions { WorkloadAdapter = workload });
            var harness = new AppHarness(terminal, app);
            harness._run = app.RunAsync(harness._cts.Token);
            await harness.WaitForPassAsync(1);
            return harness;
        }

        // Resizes the way a real presentation does, so the app sees the new size.
        public void Resize(int columns, int rows) =>
            ((HeadlessPresentationAdapter)Terminal.PresentationAdapter).TriggerResize(columns, rows);

        public DiagnosticApplicationFrameResult Capture(params DiagnosticAuthorization[] authorizations) =>
            Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest { Authorizations = authorizations });

        public async Task<long> NextPassAsync(Action? change)
        {
            var target = App.FrameCount + 1;
            change?.Invoke();
            App.Invalidate();
            await WaitForPassAsync(target);
            return App.FrameCount;
        }

        public async Task WaitForPassAsync(long pass)
        {
            for (var i = 0; i < 500 && App.FrameCount < pass; i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.IsTrue(App.FrameCount >= pass, $"pass {pass} never completed (at {App.FrameCount})");
        }

        public async Task WaitForQuietAsync()
        {
            var last = -1L;
            while (App.FrameCount != last)
            {
                last = App.FrameCount;
                await Task.Delay(150, TestContext.Current.CancellationToken);
            }
        }

        public Task WaitForTextAsync(string text) => new Hex1bTerminalInputSequenceBuilder()
            .WaitUntil(s => s.ContainsText(text), TimeSpan.FromSeconds(5), $"'{text}' rendered")
            .Build().ApplyAsync(Terminal, TestContext.Current.CancellationToken);

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _run; } catch (OperationCanceledException) { }
            App.Dispose();
            await Terminal.DisposeAsync();
            _cts.Dispose();
        }
    }
}
