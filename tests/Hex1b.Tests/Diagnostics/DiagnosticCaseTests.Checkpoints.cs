using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Hex1b.Reflow;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Checkpoints recorded during a case (ticket 08): at every stop but a collector failure, and at marks,
// with the model's state only under reapplication-data, bounded, and never taken between them.
public partial class DiagnosticCaseTests
{
    [TestMethod]
    public async Task Checkpoint_AtEachStop()
    {
        using var root = new CaseRoot();

        // Requested.
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "requested stop");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }
        AssertStopCheckpoint(Artifact.Read(path), "requested", "requested stop");

        // Time limit, on a fake clock.
        var clock = new FakeTimeProvider();
        workload = new ScriptedWorkload();
        (terminal, path) = Checkpointed(root, workload, maxSeconds: 1, clock: clock);
        await using (terminal)
        {
            await workload.WriteAndWaitAsync(terminal, "time limit");
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
        }
        AssertStopCheckpoint(Artifact.Read(path), "time-limit", "time limit");

        // Size limit: a small model's state fits in the tier written as the case closes.
        workload = new ScriptedWorkload();
        (terminal, path) = Checkpointed(root, workload, width: 10, height: 2, scrollback: null, maxBytes: 1024 * 1024);
        await using (terminal)
        {
            var chunk = Enumerable.Repeat((byte)'s', 16 * 1024).ToArray();
            for (var i = 0; i < 128 && new TerminalDiagnostics(terminal).GetCaseStatus().Outcome == DiagnosticOutcome.Captured; i++)
                await workload.WriteAndWaitAsync(terminal, chunk);
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
        }
        var sized = Artifact.Read(path);
        AssertStopCheckpoint(sized, "size-limit", "ssssssssss");
        Assert.IsLessThanOrEqualTo(1024L * 1024, Directory.GetFiles(path).Sum(f => new FileInfo(f).Length));

        // A collector failure takes none.
        DiagnosticCaseRecorder.WriterFaultForTesting.Value = new IOException("injected storage failure");
        try
        {
            workload = new ScriptedWorkload();
            (terminal, path) = Checkpointed(root, workload);
            await using (terminal)
            {
                await workload.WriteAndWaitAsync(terminal, "fails");
                await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
            }
        }
        finally
        {
            DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
        }
        var failed = Artifact.Read(path);
        Assert.AreEqual("collector-failed", failed.Completion!.Value.GetProperty("stopReason").GetString());
        Assert.IsEmpty(Checkpoints(failed), "a collector failure recorded a checkpoint");
        Assert.AreEqual(0, failed.Completion.Value.GetProperty("checkpoints").GetProperty("offered").GetInt64());
    }

    [TestMethod]
    public async Task Checkpoint_BeforeDisposalResets()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        await workload.WriteAndWaitAsync(terminal, "main text\u001b[?1049h\u001b[HALT TEXT");
        await terminal.DisposeAsync();
        await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));

        var artifact = Artifact.Read(path);
        Assert.AreEqual("target-disposed", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var state = StopCheckpoint(artifact).GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual("alternate", state.GetProperty("activeBuffer").GetString(), "the checkpoint was taken after disposal reset the screens");
        StringAssert.StartsWith(RowText(state.GetProperty("screen")[0]), "ALT TEXT");
        StringAssert.StartsWith(RowText(state.GetProperty("savedMainScreen")[0]), "main text");
    }

    [TestMethod]
    public async Task Mark_RecordsAtTheCurrentModelSequence()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        var diagnostics = new TerminalDiagnostics(terminal);
        DiagnosticCaseMarkResult between;
        DiagnosticCaseMarkResult resized;
        var inLock = new List<(long Sequence, string State)>();
        await using (terminal)
        {
            Assert.AreEqual("invalid-label", diagnostics.MarkCase("").Problem?.Code);
            Assert.AreEqual("invalid-label", diagnostics.MarkCase("tab\there").Problem?.Code);
            Assert.AreEqual("invalid-label", diagnostics.MarkCase(new string('x', 65)).Problem?.Code);
            Assert.AreEqual("invalid-label", diagnostics.MarkCase("caf\u00e9").Problem?.Code);

            await workload.WriteAndWaitAsync(terminal, "first ");
            between = diagnostics.MarkCase("between");
            Assert.AreEqual(DiagnosticOutcome.Captured, between.Outcome, between.Problem?.Message);
            Assert.AreEqual(terminal.CurrentModelSequence, between.ModelSequence);
            Assert.AreEqual("between", between.Label);
            Assert.IsTrue(between.StateRecorded);
            await workload.WriteAndWaitAsync(terminal, "second");

            // A model change inside the mark's hold of the lock (re-entered by the test) is in its state.
            DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value = () => terminal.Resize(33, 9);
            try
            {
                resized = diagnostics.MarkCase("resized");
            }
            finally
            {
                DiagnosticCaseRecorder.BeforeMarkCaptureForTesting.Value = null;
            }
            Assert.AreEqual(terminal.CurrentModelSequence, resized.ModelSequence);

            // Marks while another thread floods output: each checkpoint is the model at its own model sequence,
            // read in the same hold of the lock as the test's own reading.
            DiagnosticCaseRecorder.AfterMarkCaptureForTesting.Value = () =>
                inLock.Add((terminal.CurrentModelSequence, JsonSerializer.Serialize(terminal.CaptureModelState(), DiagnosticsJsonContext.Default.DiagnosticModelState)));
            try
            {
                using var stop = new CancellationTokenSource();
                var flood = Task.Run(async () =>
                {
                    for (var i = 0; !stop.IsCancellationRequested; i++)
                    {
                        workload.Enqueue(System.Text.Encoding.UTF8.GetBytes($"flood {i}\r\n"));
                        await Task.Yield();
                    }
                });
                for (var i = 0; i < 20; i++)
                {
                    Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.MarkCase().Outcome);
                    await Task.Delay(2, TestContext.Current.CancellationToken);
                }
                stop.Cancel();
                await flood;
            }
            finally
            {
                DiagnosticCaseRecorder.AfterMarkCaptureForTesting.Value = null;
            }
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual("no-active-case", diagnostics.MarkCase().Problem?.Code);
        }

        var artifact = Artifact.Read(path);
        var marks = Checkpoints(artifact).Where(c => c.GetProperty("checkpoint").GetProperty("trigger").GetString() == "mark").ToList();
        Assert.HasCount(22, marks);
        var resizedLine = marks.Single(m => m.GetProperty("checkpoint").GetProperty("label").GetString() == "resized");
        var resizedState = resizedLine.GetProperty("checkpoint").GetProperty("state");
        Assert.AreEqual(resized.ModelSequence, resizedLine.GetProperty("modelSequence").GetInt64());
        Assert.AreEqual(resized.ModelSequence, resizedState.GetProperty("modelSequence").GetInt64(), "the state was not taken at the mark's model sequence");
        Assert.AreEqual((33, 9), (resizedState.GetProperty("width").GetInt32(), resizedState.GetProperty("height").GetInt32()));
        marks.Remove(resizedLine);
        var first = marks[0];
        Assert.AreEqual(between.ModelSequence, first.GetProperty("modelSequence").GetInt64());
        Assert.AreEqual("between", first.GetProperty("checkpoint").GetProperty("label").GetString());
        Assert.AreEqual(between.CheckpointOrdinal, first.GetProperty("checkpoint").GetProperty("ordinal").GetInt64());
        StringAssert.StartsWith(RowText(first.GetProperty("checkpoint").GetProperty("state").GetProperty("screen")[0]), "first  ");

        // In file order after its model event, and never before one it follows.
        var index = artifact.Events.IndexOf(first);
        Assert.IsTrue(artifact.Events.Take(index).Any(e => e.GetProperty("stream").GetString() == "model"
            && e.GetProperty("modelSequence").GetInt64() == between.ModelSequence), "the checkpoint precedes its model event");

        var flooded = marks.Skip(1).ToList();
        Assert.HasCount(20, inLock);
        var advanced = 0;
        for (var i = 0; i < flooded.Count; i++)
        {
            var checkpoint = flooded[i];
            Assert.AreEqual(inLock[i].Sequence, checkpoint.GetProperty("modelSequence").GetInt64(), $"mark {i}: a different model sequence");
            Assert.AreEqual(inLock[i].State, checkpoint.GetProperty("checkpoint").GetProperty("state").GetRawText(), $"mark {i}: a different state");
            if (i > 0 && inLock[i].Sequence > inLock[i - 1].Sequence)
                advanced++;
            var ordinal = checkpoint.GetProperty("checkpoint").GetProperty("ordinal").GetInt64();
            var position = artifact.Events.IndexOf(checkpoint);
            Assert.IsFalse(artifact.Events.Skip(position).Any(e => e.GetProperty("stream").GetString() == "model"
                && e.GetProperty("modelSequence").GetInt64() <= checkpoint.GetProperty("modelSequence").GetInt64()),
                $"checkpoint {ordinal} precedes a model event it follows");
        }
        Assert.IsGreaterThan(5, advanced, "fixture: the flood did not interleave with the marks");
    }

    [TestMethod]
    public async Task Checkpoint_RequiresReapplicationData()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload, authorized: false);
        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            await workload.WriteAndWaitAsync(terminal, "boundary only");
            var mark = diagnostics.MarkCase("plain label with spaces");
            Assert.AreEqual(DiagnosticOutcome.Captured, mark.Outcome);
            Assert.IsFalse(mark.StateRecorded);
            Assert.AreEqual("requires reapplication-data", mark.StateReason);
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual(0, terminal.ModelStateCapturesForTesting, "state was taken without reapplication-data");
        }

        var checkpoints = Checkpoints(Artifact.Read(path));
        Assert.HasCount(2, checkpoints);
        foreach (var line in checkpoints)
        {
            var checkpoint = line.GetProperty("checkpoint");
            Assert.AreEqual("unavailable", checkpoint.GetProperty("status").GetString());
            Assert.AreEqual("requires reapplication-data", checkpoint.GetProperty("reason").GetString());
            Assert.IsFalse(checkpoint.TryGetProperty("state", out _), "a boundary-only checkpoint carries state");
            Assert.IsTrue(line.TryGetProperty("modelSequence", out _), "the boundary has no model sequence");
        }
    }

    [TestMethod]
    public async Task Checkpoint_TooLargeIsMissing()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload, width: 20, height: 5, scrollback: 50_000, maxBytes: 1024 * 1024);
        var diagnostics = new TerminalDiagnostics(terminal);
        await using (terminal)
        {
            for (var chunk = 0; chunk < 11; chunk++)
                await workload.WriteAndWaitAsync(terminal, string.Concat(Enumerable.Range(0, 5_000).Select(i => $"{chunk}:{i}\r\n")));
            Assert.AreEqual(50_000, terminal.ScrollbackCount, "fixture: history is not full");

            // A mark too large to write stays a boundary; the case keeps recording.
            var mark = diagnostics.MarkCase("large");
            Assert.AreEqual(DiagnosticOutcome.Captured, mark.Outcome);
            await workload.WriteAndWaitAsync(terminal, "after the mark");
            await Task.Delay(200, TestContext.Current.CancellationToken);
            Assert.AreEqual(DiagnosticCaseState.Recording, diagnostics.GetCaseStatus().State, "a checkpoint too large to write stopped the case");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(path);
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var checkpoints = Checkpoints(artifact);
        Assert.HasCount(2, checkpoints);
        foreach (var line in checkpoints)
        {
            var checkpoint = line.GetProperty("checkpoint");
            Assert.AreEqual("missing", checkpoint.GetProperty("status").GetString());
            Assert.AreEqual("size-limit", checkpoint.GetProperty("reason").GetString());
            Assert.IsFalse(checkpoint.TryGetProperty("state", out _));
        }
        var counts = artifact.Completion.Value.GetProperty("checkpoints");
        Assert.AreEqual((2L, 2L, 0L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64(), counts.GetProperty("dropped").GetInt64()));
        Assert.IsLessThanOrEqualTo(1024L * 1024, Directory.GetFiles(path).Sum(f => new FileInfo(f).Length));
    }

    [TestMethod]
    public async Task Mark_BoundedWhileTheWriterIsHeld()
    {
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var workload = new ScriptedWorkload();
            var (terminal, path) = Checkpointed(root, workload);
            await using (terminal)
            {
                var diagnostics = new TerminalDiagnostics(terminal);
                await workload.WriteAndWaitAsync(terminal, "held");
                var results = Enumerable.Range(0, 70).Select(_ => diagnostics.MarkCase()).ToList();
                Assert.AreEqual(64, results.Count(r => r.Outcome == DiagnosticOutcome.Captured));
                Assert.AreEqual(6, results.Count(r => r.Problem?.Code == "busy"));
                Assert.IsTrue(results.Skip(64).All(r => r.Problem?.Code == "busy"), "a mark after the bound was accepted");
                Assert.AreEqual(64, terminal.ModelStateCapturesForTesting, "a refused mark took the model's state");

                gate.Set();
                await WaitAsync(() => WrittenCheckpointLines(path) == 64);
                Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.MarkCase().Outcome, "the bound was not released as marks were written");
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
            var counts = Artifact.Read(path).Completion!.Value.GetProperty("checkpoints");
            Assert.AreEqual((66L, 66L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64()));
        }
        finally
        {
            DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            gate.Set();
        }
    }

    [TestMethod]
    public async Task Checkpoint_NoProjectionUntilMarkOrStop()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var (terminal, path) = Checkpointed(root, workload);
        await using (terminal)
        {
            for (var i = 0; i < 50; i++)
                await workload.WriteAndWaitAsync(terminal, $"event {i}\r\n");
            terminal.Resize(30, 8);
            Assert.AreEqual(0, terminal.ModelStateCapturesForTesting, "a case took the model's state without a mark or stop");
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual(1, terminal.ModelStateCapturesForTesting);
        }
        var checkpoint = StopCheckpoint(Artifact.Read(path)).GetProperty("checkpoint");
        Assert.IsGreaterThanOrEqualTo(0.0, checkpoint.GetProperty("captureMilliseconds").GetDouble());
    }

    [TestMethod]
    public async Task Configuration_RecordedStructurally()
    {
        using var root = new CaseRoot();
        var presentation = new HeadlessPresentationAdapter(41, 7, TerminalCapabilities.Modern).WithReflow(KittyReflowStrategy.Instance);
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = new ScriptedWorkload(),
            Width = 41,
            Height = 7,
            ScrollbackCapacity = null,
            CommandMarkHistoryCapacity = 2,
        };
        options.Graphics.MaximumImagesPerScreen = 17;
        var (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options,
            new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
        await using (terminal)
            await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var manifest = Artifact.Read(started.Path!).Manifest;
        Assert.AreEqual(2, manifest.GetProperty("formatVersion").GetInt32());
        var recorded = JsonSerializer.Deserialize(manifest.GetProperty("checkpoint").GetProperty("configuration").GetRawText(),
            DiagnosticsJsonContext.Default.DiagnosticCaseModelConfiguration)!;
        Assert.AreEqual("kitty", recorded.ReflowStrategy);
        Assert.IsTrue(recorded.ReflowEnabled);
        Assert.AreEqual((41, 7, (int?)null, 2), (recorded.Width, recorded.Height, recorded.ScrollbackCapacity, recorded.CommandMarkHistoryCapacity));
        Assert.AreEqual(17, recorded.Graphics!.MaximumImagesPerScreen);
        Assert.AreEqual(TerminalCapabilities.Modern, CaseConfiguration.TerminalCapabilities(recorded.Capabilities!, out var problem), problem);
        Assert.AreSame(KittyReflowStrategy.Instance, CaseConfiguration.CreateReflowStrategy(recorded.ReflowStrategy));

        // Every capability and graphics option is recorded by name, and every capability value survives the trip.
        var capabilityNames = typeof(DiagnosticCaseCapabilities).GetProperties().Select(p => p.Name).ToHashSet();
        var unrecorded = typeof(TerminalCapabilities).GetProperties().Where(p => p.CanWrite && !capabilityNames.Contains(p.Name)).Select(p => p.Name).ToList();
        Assert.IsEmpty(unrecorded, "capabilities not recorded: " + string.Join(", ", unrecorded));
        var limitNames = typeof(DiagnosticCaseGraphicsLimits).GetProperties().Select(p => p.Name).ToHashSet();
        var unrecordedLimits = typeof(Hex1bTerminalGraphicsOptions).GetProperties().Where(p => p.CanWrite && !limitNames.Contains(p.Name)).Select(p => p.Name).ToList();
        Assert.IsEmpty(unrecordedLimits, "graphics limits not recorded: " + string.Join(", ", unrecordedLimits));
        var varied = TerminalCapabilities.Minimal with
        {
            SixelSupport = Hex1b.Sixel.SixelPresentationSupport.Native,
            SixelCellMetrics = new Hex1b.Sixel.SixelCellMetrics(9.5, 19, Hex1b.Sixel.SixelCellMetricsSource.Direct, Hex1b.Sixel.SixelCellMetricsReliability.Authoritative),
            ActualCellPixelWidth = 9.5,
            DefaultForeground = 0x123456,
            SupportsRetroactiveVariationSelectors = false,
        };
        Assert.AreEqual(varied, CaseConfiguration.TerminalCapabilities(CaseConfiguration.Capabilities(varied), out _));

        // Values and fields this build does not know are named, never defaulted.
        var unknownField = JsonSerializer.Deserialize("{\"supportsHolograms\":true,\"sixelSupport\":\"None\"}", DiagnosticsJsonContext.Default.DiagnosticCaseCapabilities)!;
        Assert.IsNull(CaseConfiguration.TerminalCapabilities(unknownField, out problem));
        Assert.AreEqual("capabilities.supportsHolograms: unknown field", problem);
        Assert.IsNull(CaseConfiguration.TerminalCapabilities(recorded.Capabilities! with { SixelSupport = "Hologram" }, out problem));
        StringAssert.StartsWith(problem, "capabilities.sixelSupport");
        Assert.IsNull(CaseConfiguration.CreateReflowStrategy("custom:Some.Strategy"));

        // Automatic detection is named by what it detected; a presentation's own provider is custom.
        Assert.AreEqual(CaseConfiguration.ReflowStrategyId(new HeadlessPresentationAdapter(10, 2).WithReflow(AutoReflowStrategy.Instance.DetectedStrategy)),
            CaseConfiguration.ReflowStrategyId(new HeadlessPresentationAdapter(10, 2).WithReflow(AutoReflowStrategy.Instance)));
        Assert.AreEqual("none", CaseConfiguration.ReflowStrategyId(new HeadlessPresentationAdapter(10, 2)));
    }

    private static (Hex1bTerminal Terminal, string Path) Checkpointed(CaseRoot root, ScriptedWorkload workload, bool authorized = true,
        int width = 40, int height = 10, int? scrollback = 100, long? maxBytes = null, int? maxSeconds = null, TimeProvider? clock = null)
    {
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(width, height),
            WorkloadAdapter = workload,
            Width = width,
            Height = height,
            ScrollbackCapacity = scrollback,
            TimeProvider = clock ?? TimeProvider.System,
        };
        var (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options, new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = maxBytes,
            MaxSeconds = maxSeconds,
            Authorizations = authorized ? [DiagnosticAuthorization.ReapplicationData] : [],
        });
        Assert.AreEqual(DiagnosticOutcome.Captured, started.Outcome, started.Problem?.Message);
        return (terminal, started.Path!);
    }

    // Checkpoint lines the writer has finished, counted while it may still be writing the next one.
    private static int WrittenCheckpointLines(string path)
    {
        var events = Path.Combine(path, "events.jsonl");
        if (!File.Exists(events))
            return 0;
        using var stream = new FileStream(events, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var text = new StreamReader(stream).ReadToEnd();
        return text.Split('\n')[..^1].Count(line => line.Contains("\"kind\":\"checkpoint\"", StringComparison.Ordinal));
    }

    private static List<JsonElement> Checkpoints(Artifact artifact) =>
        artifact.Events.Where(e => e.GetProperty("stream").GetString() == "case" && e.GetProperty("kind").GetString() == "checkpoint").ToList();

    private static JsonElement StopCheckpoint(Artifact artifact) =>
        Checkpoints(artifact).Single(c => c.GetProperty("checkpoint").GetProperty("trigger").GetString() == "stop");

    private static string RowText(JsonElement row) =>
        string.Concat(row.GetProperty("cells").EnumerateArray().Select(c => c.GetProperty("t").GetString()));

    // The stop checkpoint carries the state at the stop: its model sequence is the last recorded model event's.
    private static void AssertStopCheckpoint(Artifact artifact, string reason, string text)
    {
        Assert.AreEqual(reason, artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var line = StopCheckpoint(artifact);
        var checkpoint = line.GetProperty("checkpoint");
        Assert.AreEqual("recorded", checkpoint.GetProperty("status").GetString(), $"{reason}: {checkpoint}");
        Assert.AreEqual("stop", checkpoint.GetProperty("label").GetString());
        var state = checkpoint.GetProperty("state");
        Assert.AreEqual(line.GetProperty("modelSequence").GetInt64(), state.GetProperty("modelSequence").GetInt64());
        StringAssert.StartsWith(RowText(state.GetProperty("screen")[0]), text, $"{reason}: the state is not the model's");
        var counts = artifact.Completion.Value.GetProperty("checkpoints");
        Assert.AreEqual((1L, 1L, 0L), (counts.GetProperty("offered").GetInt64(), counts.GetProperty("written").GetInt64(), counts.GetProperty("dropped").GetInt64()));
    }
}
