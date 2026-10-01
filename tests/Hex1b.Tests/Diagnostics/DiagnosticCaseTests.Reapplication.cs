using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Automation;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Hex1b.Reflow;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

// Offline re-application (ticket 08): validated before anything is applied, reproduced event by event in a
// detached model on a virtual clock, and compared with the checkpoints the live model recorded.
public partial class DiagnosticCaseTests
{
    // Evidence P2's corpus, with resizes to the same, a smaller and a larger size, and marks between.
    private static readonly CaseStep[] ReapplicationCorpus =
    [
        new("\u001b[1;31mred bold\u001b[0m \u001b[4:3m\u001b[58;2;9;8;7mcurly\u001b[24;59m \u001b]8;id=a;https://x.test\u001b\\link\u001b]8;;\u001b\\\r\n"),
        new("\u001b]0;TITLE\u0007\u001b]1;ICON\u0007\u001b]22;\u0007\u001b]0;SECOND\u0007\u001b(0lqk\u001b(B\u001b)0\ttab\r\n"),
        CaseStep.Mark("titles"),
        new("\u001b[3;6r\u001b[?6h\u001b[2;1Hin origin\u001b[?6l\u001b[?69h\u001b[5;30s\u001b[?69l\u001b[r"),
        new("\u001b7\u001b[5;5Hsaved\u001b8\u001b[?1049halt \u001b7x\u001b[?1049lback\r\n"),
        new("漢字 é 👩‍💻 a long wrapping line that goes past the margin and on and on and on\r\n"),
        CaseStep.Resize(40, 10),
        CaseStep.Resize(30, 8),
        new(string.Concat(Enumerable.Range(1, 20).Select(i => $"row {i} \u001b[{i % 7 + 30}mcolour\u001b[m\r\n"))),
        CaseStep.Mark("history"),
        CaseStep.Resize(45, 12),
        new("\u001b[3gA\u001bHB\tC\u001b[4hins\u001b[4lX\u001b[2b\u001b[?25l\u001b]133;A\u0007$ ls\r\n\u001b]133;D;0\u0007\u001b]9;4;1;40\u0007\u001b]7;file://host/tmp\u0007"),
        new("\u001b[?1h\u001b=\u001b[?2004h\u001b[?1004h\u001b[?1002h\u001b[?1006h\u001b[20h\u001b[?45h"),
        new("\u001b[1\u001b"),
        new([.. "\u001b[m"u8, 0xe6, 0xbc]),
        CaseStep.Mark("pending"),
        new([0xbc, (byte)'!']),
    ];

    [TestMethod]
    public async Task Reapply_SameBuildAgreement()
    {
        using var root = new CaseRoot();
        foreach (var (name, presentation) in new (string, Func<HeadlessPresentationAdapter>)[]
        {
            ("default", () => new HeadlessPresentationAdapter(40, 10)),
            ("kitty-modern", () => new HeadlessPresentationAdapter(40, 10, TerminalCapabilities.Modern).WithReflow(KittyReflowStrategy.Instance)),
        })
        {
            var path = await RecordCaseAsync(root, ReapplicationCorpus, presentation());
            foreach (var label in new[] { "titles", "history", "pending", "stop" })
                AssertMatched(Reapply(path, label: label), $"{name} {label}");
        }

        // A Hex1b application: its output reaches the live model pre-tokenized; its recorded bytes are re-applied raw.
        await using var app = await TrackedApp.StartAsync(new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        var appPath = app.Diagnostics.GetCaseStatus().Path!;
        await app.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("typed 漢字 text"));
        await Settle(app.Terminal);
        Assert.AreEqual(DiagnosticOutcome.Captured, app.Diagnostics.MarkCase("typed").Outcome);
        app.Terminal.Resize(30, 6);
        await Settle(app.Terminal);
        Assert.AreEqual(DiagnosticOutcome.Captured, app.Diagnostics.MarkCase("resized").Outcome);
        await app.Diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.IsGreaterThan(3, Artifact.Read(appPath).ModelEvents().Count, "fixture: the application wrote little");
        foreach (var label in new[] { "typed", "resized", "stop" })
            AssertMatched(Reapply(appPath, label: label), $"app {label}");
    }

    [TestMethod]
    public async Task Reapply_ConfigurationReproduced()
    {
        using var root = new CaseRoot();
        // Content whose reconstruction depends on each setting: wrapped text across a resize (reflow and width),
        // three command marks (capacity 2), scrolled rows (history), and a variation selector (capabilities).
        CaseStep[] steps =
        [
            new(string.Concat(Enumerable.Range(1, 12).Select(i => $"\u001b]133;A\u0007line {i} that is long enough to wrap at the width\r\n"))),
            new("❤️ heart\r\n"),
            CaseStep.Resize(25, 6),
            CaseStep.Resize(52, 9),
        ];
        foreach (var (name, presentation, configure) in new (string, Func<HeadlessPresentationAdapter>, Action<Hex1bTerminalOptions>)[]
        {
            ("kitty", () => new HeadlessPresentationAdapter(41, 8).WithReflow(KittyReflowStrategy.Instance), _ => { }),
            ("modern", () => new HeadlessPresentationAdapter(41, 8, TerminalCapabilities.Modern), _ => { }),
            ("marks-2", () => new HeadlessPresentationAdapter(41, 8), o => o.CommandMarkHistoryCapacity = 2),
            ("no-scrollback", () => new HeadlessPresentationAdapter(41, 8), o => o.ScrollbackCapacity = null),
        })
        {
            var path = await RecordCaseAsync(root, steps, presentation(), configure);
            AssertMatched(Reapply(path, label: "stop"), name);
        }
    }

    [TestMethod]
    public async Task Reapply_Format1Incompatible()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("format one")], new HeadlessPresentationAdapter(20, 4));
        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")))!;
        manifest["formatVersion"] = 1;
        var configuration = manifest["checkpoint"]!["configuration"]!.AsObject();
        configuration.Remove("reflowStrategy");
        configuration["capabilities"] = "TerminalCapabilities { }";
        configuration["graphics"] = "SixelCompatibilityPolicy { }";
        RewriteOwnerOnly(Path.Combine(path, "manifest.json"), manifest.ToJsonString());

        var result = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code));
        StringAssert.StartsWith(result.Problem!.Message, "formatVersion");
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "an incompatible case was written to");
        // Ticket 14 (R2): the refusal keeps the shape it had (the checkpoint and the coverage), names the recording build and carries the record.
        AssertRecord(result.Compatibility, "i n n n n n n", "format 1");
        Assert.AreEqual(TerminalDiagnostics.Hex1bBuild, result.Producer?.Hex1bBuild, "the format-1 refusal names the recording build");
        Assert.IsNotNull(result.Checkpoint, "the format-1 refusal lost its checkpoint");
        Assert.IsNotNull(result.Coverage, "the format-1 refusal lost its coverage");
        Assert.AreEqual(DiagnosticOutcome.Captured, DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Outcome,
            "a format 1 case is no longer inspectable");
    }

    [TestMethod]
    public async Task Reapply_UnknownConfigurationIncompatible()
    {
        using var root = new CaseRoot();
        async Task<string> Edited(Action<JsonObject> edit)
        {
            var path = await RecordCaseAsync(root, [new("configured")], new HeadlessPresentationAdapter(20, 4));
            var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(path, "manifest.json")))!.AsObject();
            edit(manifest["checkpoint"]!["configuration"]!.AsObject());
            RewriteOwnerOnly(Path.Combine(path, "manifest.json"), manifest.ToJsonString());
            return path;
        }

        var strategy = Reapply(await Edited(c => c["reflowStrategy"] = "nope"), label: "stop");
        Assert.AreEqual("incompatible", strategy.Problem?.Code);
        StringAssert.StartsWith(strategy.Problem!.Message, "reflowStrategy");
        var capability = Reapply(await Edited(c => c["capabilities"]!["supportsHolograms"] = true), label: "stop");
        Assert.AreEqual("incompatible", capability.Problem?.Code);
        StringAssert.StartsWith(capability.Problem!.Message, "capabilities.supportsHolograms");
        var graphics = Reapply(await Edited(c => c["graphics"]!["maximumHolograms"] = 3), label: "stop");
        Assert.AreEqual("incompatible", graphics.Problem?.Code);
        StringAssert.StartsWith(graphics.Problem!.Message, "graphics.maximumHolograms");

        // A checkpoint's projection profile this build does not know.
        var profiled = await RecordCaseAsync(root, [new("profiled")], new HeadlessPresentationAdapter(20, 4));
        EditEventLine(profiled, e => e["kind"]?.GetValue<string>() == "checkpoint", e => e["checkpoint"]!["profile"] = "text-state/9");
        var profile = Reapply(profiled, label: "stop");
        Assert.AreEqual("incompatible", profile.Problem?.Code);
        StringAssert.Contains(profile.Problem!.Message, "text-state/9");
    }

    [TestMethod]
    public async Task Reapply_RefusesBeforeApplying()
    {
        using var root = new CaseRoot();
        var counter = new StrongBox<int>();
        CaseReapplier.AppliedEventsForTesting.Value = counter;
        try
        {
            // Graphics: coverage ends at the Sixel's application.
            var graphics = await RecordCaseAsync(root, [new("before "), new("\u001bP0;0;0q#0;2;100;0;0#0~~~~~~-\u001b\\"), new("after")],
                new HeadlessPresentationAdapter(40, 10));
            // Interrupted: a case whose writer never finished (no completion record).
            var interrupted = await RecordCaseAsync(root, [new("one"), new("two"), new("three")], new HeadlessPresentationAdapter(20, 4));
            File.Delete(Path.Combine(interrupted, "completion.json"));
            // Truncated: a torn last line.
            var truncated = await RecordCaseAsync(root, [new("one"), new("two"), new("three")], new HeadlessPresentationAdapter(20, 4));
            TearLastModelEvent(truncated);

            foreach (var path in new[] { graphics, interrupted, truncated })
            {
                var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Intervals.Single();
                counter.Value = 0;
                var result = Reapply(path, modelSequence: interval.ToModelSequence!.Value + 1);
                Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
                Assert.AreEqual(interval.ToModelSequence, result.LastValidModelSequence);
                Assert.AreEqual(interval.EndReason, result.IntervalEndReason);
                Assert.AreEqual(0, counter.Value, "events were applied before the target was refused");
                Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "a refused re-application wrote a run");
            }

            // No valid interval at all: a case without reapplication-data. (A case started after output re-applies from
            // its start since ticket 09: DiagnosticCaseTests.Reapply_LiveStartMatchesAtEveryCheckpoint.)
            var unauthorized = await RecordCaseAsync(root, [new("plain")], new HeadlessPresentationAdapter(20, 4), authorized: false);
            Assert.AreEqual("no-valid-interval", Reapply(unauthorized, modelSequence: 1).Problem?.Code);
            Assert.AreEqual(0, counter.Value);
        }
        finally
        {
            CaseReapplier.AppliedEventsForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Reapply_TargetNaming()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("one "), CaseStep.Mark("twin"), new("two "), CaseStep.Mark("twin"), new("three "), CaseStep.Mark("once")],
            new HeadlessPresentationAdapter(30, 4));
        var lines = Checkpoints(Artifact.Read(path));
        var once = lines.Single(l => l.GetProperty("checkpoint").GetProperty("label").GetString() == "once");
        var onceSequence = once.GetProperty("modelSequence").GetInt64();

        var byLabel = Reapply(path, label: "once");
        var byCase = Reapply(path, caseSequence: once.GetProperty("caseSequence").GetInt64());
        var byModel = Reapply(path, modelSequence: onceSequence);
        foreach (var result in new[] { byLabel, byCase, byModel })
        {
            AssertMatched(result, "once");
            Assert.AreEqual(onceSequence, result.Target!.ModelSequence);
            Assert.AreEqual("once", result.Target.Label);
        }

        var twins = lines.Where(l => l.GetProperty("checkpoint").GetProperty("label").GetString() == "twin").Select(l => l.GetProperty("caseSequence").GetInt64()).ToList();
        var ambiguous = Reapply(path, label: "twin");
        Assert.AreEqual((DiagnosticOutcome.InvalidRequest, "ambiguous-label"), (ambiguous.Outcome, ambiguous.Problem?.Code));
        foreach (var twin in twins)
            StringAssert.Contains(ambiguous.Problem!.Message, twin.ToString());
        Assert.AreEqual("unknown-label", Reapply(path, label: "nope").Problem?.Code);
        Assert.AreEqual("unknown-case-sequence", Reapply(path, caseSequence: 9_999).Problem?.Code);
        Assert.AreEqual("invalid-target", Reapply(new DiagnosticCaseReapplyRequest { Path = path }).Problem?.Code);
        Assert.AreEqual("invalid-target", Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "once", ToModelSequence = 1 }).Problem?.Code);

        // The fresh model (sequence 0) and a model event's case sequence are boundaries without a checkpoint.
        var fresh = Reapply(path, modelSequence: 0);
        Assert.AreEqual((DiagnosticOutcome.Captured, "unavailable", 0L), (fresh.Outcome, fresh.Comparison, fresh.AppliedThrough));
        var modelEvent = Artifact.Read(path).ModelEvents()[0];
        var byEvent = Reapply(path, caseSequence: modelEvent.GetProperty("caseSequence").GetInt64());
        Assert.AreEqual(modelEvent.GetProperty("modelSequence").GetInt64(), byEvent.Target!.ModelSequence);

        // A case record is not a boundary.
        var graphics = await RecordCaseAsync(root, [new("x"), new("\u001bP0;0;0q#0;2;100;0;0#0~~~~~~-\u001b\\")], new HeadlessPresentationAdapter(20, 4));
        var end = Artifact.Read(graphics).Events.First(e => e.GetProperty("kind").GetString() == "interval-end");
        Assert.AreEqual("not-a-boundary", Reapply(graphics, caseSequence: end.GetProperty("caseSequence").GetInt64()).Problem?.Code);
    }

    [TestMethod]
    public async Task Reapply_SequenceParity()
    {
        using var root = new CaseRoot();
        var steps = Enumerable.Range(0, 200).Select(i => i % 50 == 49 ? CaseStep.Resize(30 + i % 7, 5 + i % 3) : new CaseStep($"event {i}\r\n")).ToArray();
        var path = await RecordCaseAsync(root, steps, new HeadlessPresentationAdapter(30, 5));
        var recorded = Artifact.Read(path).ModelEvents().Select(e => e.GetProperty("modelSequence").GetInt64()).ToList();
        Assert.HasCount(200, recorded);

        var reached = new List<long>();
        CaseReapplier.AfterEventForTesting.Value = reached.Add;
        try
        {
            AssertMatched(Reapply(path, label: "stop"), "200 events");
        }
        finally
        {
            CaseReapplier.AfterEventForTesting.Value = null;
        }
        CollectionAssert.AreEqual(recorded, reached, "the reconstructed model's sequence diverged");

        // A payload that is not applied is caught at its own event.
        // Sequence 50 is a resize (step 49); 51 is an application.
        CaseReapplier.SkipApplicationForTesting.Value = 51;
        try
        {
            var skipped = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Captured, "different", 50L), (skipped.Outcome, skipped.Comparison, skipped.AppliedThrough));
            StringAssert.Contains(skipped.ComparisonReason, "After model sequence 51");
        }
        finally
        {
            CaseReapplier.SkipApplicationForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Reapply_VirtualClock()
    {
        using var root = new CaseRoot();
        var clock = new FakeTimeProvider();
        var workload = new ScriptedWorkload();
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(30, 5),
            WorkloadAdapter = workload,
            Width = 30,
            Height = 5,
            TimeProvider = clock,
        };
        var (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options, new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            Authorizations = [DiagnosticAuthorization.ReapplicationData],
        });
        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            await workload.WriteAndWaitAsync(terminal, "\u001b[?2026hinside the update");
            diagnostics.MarkCase("updating");
            var before = terminal.CurrentModelSequence;
            clock.Advance(TimeSpan.FromSeconds(1));
            await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
            await workload.WriteAndWaitAsync(terminal, " after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var timeout = Artifact.Read(started.Path!).ModelEvents().Single(e => e.GetProperty("kind").GetString() == "synchronized-update-timeout");

        // Slower than the timeout in real time (first, so nothing else masks it): no timeout fires on its own.
        CaseReapplier.AfterEventForTesting.Value = sequence =>
        {
            if (sequence == 1)
                Thread.Sleep(1200);
        };
        try
        {
            AssertMatched(Reapply(started.Path!, label: "updating"), "a slow replay");
        }
        finally
        {
            CaseReapplier.AfterEventForTesting.Value = null;
        }

        AssertMatched(Reapply(started.Path!, label: "updating"), "inside the update");
        var sequences = new List<long>();
        CaseReapplier.AfterEventForTesting.Value = sequences.Add;
        try
        {
            AssertMatched(Reapply(started.Path!, label: "stop"), "after the timeout");
        }
        finally
        {
            CaseReapplier.AfterEventForTesting.Value = null;
        }
        CollectionAssert.Contains(sequences, timeout.GetProperty("modelSequence").GetInt64());

    }

    [TestMethod]
    public async Task Compare_Unavailable()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("one "), new("two "), new("a\u001bP0;0;0q#0;2;1"), CaseStep.Mark("mid-dcs")],
            new HeadlessPresentationAdapter(30, 4));

        var none = Reapply(path, modelSequence: 1);
        Assert.AreEqual((DiagnosticOutcome.Captured, "unavailable"), (none.Outcome, none.Comparison));
        StringAssert.StartsWith(none.ComparisonReason, "no-checkpoint");
        Assert.IsTrue(File.Exists(Path.Combine(none.RunPath!, "reapplied.json")), "the reconstructed state was not returned");

        var unsupported = Reapply(path, label: "mid-dcs");
        Assert.AreEqual("unavailable", unsupported.Comparison);
        StringAssert.Contains(unsupported.ComparisonReason, "dcs-continuation");

        // A checkpoint recorded without its state (too large for the case).
        var large = await RecordCaseAsync(root, [new(string.Concat(Enumerable.Range(0, 30_000).Select(i => $"{i}\r\n"))), CaseStep.Mark("large")],
            new HeadlessPresentationAdapter(20, 5), o => o.ScrollbackCapacity = 30_000, maxBytes: 1024 * 1024);
        var missing = Reapply(large, label: "large");
        Assert.AreEqual("unavailable", missing.Comparison, missing.Problem?.Message);
        StringAssert.StartsWith(missing.ComparisonReason, "checkpoint missing");
        Assert.IsTrue(File.Exists(Path.Combine(missing.RunPath!, "reapplied.json")));
    }

    [TestMethod]
    public void Reader_StreamsEvents()
    {
        using var root = new CaseRoot();
        var events = Path.Combine(root.Path, "events.jsonl");
        string Line(long sequence)
        {
            var json = JsonSerializer.Serialize(new DiagnosticCaseEvent { CaseSequence = sequence, Stream = "model", Ordinal = sequence, Kind = "resize", ModelSequence = sequence },
                DiagnosticsJsonContext.Default.DiagnosticCaseEvent);
            return $"{CaseCrc32.Compute(Encoding.UTF8.GetBytes(json)):x8}\t{json}\n";
        }

        File.WriteAllText(events, Line(1));
        var seen = new List<long>();
        foreach (var item in CaseArtifactReader.ReadEvents(events))
        {
            seen.Add(item.CaseSequence);
            // Lines appended after enumeration began are read: the file is streamed, not read up front.
            if (item.CaseSequence < 3)
                File.AppendAllText(events, Line(item.CaseSequence + 1));
        }
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, seen);
    }

    [TestMethod]
    public async Task Reapply_DamagedArtifacts()
    {
        using var root = new CaseRoot();
        CaseStep[] steps = [new("one "), CaseStep.Mark("early"), new("two "), new("three "), CaseStep.Mark("late")];

        var interrupted = await RecordCaseAsync(root, steps, new HeadlessPresentationAdapter(30, 4));
        File.Delete(Path.Combine(interrupted, "completion.json"));
        AssertMatched(Reapply(interrupted, label: "early"), "interrupted, inside");

        // A corrupt line in the middle ends the verified prefix there.
        var corrupt = await RecordCaseAsync(root, steps, new HeadlessPresentationAdapter(30, 4));
        var third = Artifact.Read(corrupt).ModelEvents()[2].GetProperty("caseSequence").GetInt64();
        EditEventLine(corrupt, e => e["caseSequence"]!.GetValue<long>() == third, e => e["ordinal"] = 999_999, recomputeChecksum: false);
        AssertMatched(Reapply(corrupt, label: "early"), "corrupt, inside");
        var beyond = Reapply(corrupt, modelSequence: 3);
        Assert.AreEqual(("beyond-interval", "truncated"), (beyond.Problem?.Code, beyond.IntervalEndReason));

        var torn = await RecordCaseAsync(root, steps, new HeadlessPresentationAdapter(30, 4));
        TearLastModelEvent(torn);
        AssertMatched(Reapply(torn, label: "early"), "torn, inside");
        Assert.AreEqual("truncated", Reapply(torn, modelSequence: 3).IntervalEndReason);
    }

    [TestMethod]
    public async Task Reapply_NoSideEffects()
    {
        using var root = new CaseRoot();
        // Recorded protocol queries (DA1, DSR) must not make the replica write input anywhere.
        var path = await RecordCaseAsync(root, [.. ReapplicationCorpus, new("\u001b[c\u001b[6nqueried")], new HeadlessPresentationAdapter(40, 10));
        var before = HashCaseFiles(path);
        Hex1bTerminal? replica = null;
        CaseReapplier.ReplicaForTesting.Value = t => replica = t;
        try
        {
            AssertMatched(Reapply(path, label: "stop"), "stop");
        }
        finally
        {
            CaseReapplier.ReplicaForTesting.Value = null;
        }
        CollectionAssert.AreEquivalent(before, HashCaseFiles(path), "the case's files changed");
        Assert.IsNotNull(replica);
        Assert.IsNull(typeof(Hex1bTerminal).GetField("_outputProcessingTask", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(replica),
            "the detached model's output pump ran");
        Assert.AreEqual(0, ((CaseReapplier.DetachedWorkload)replica!.Workload).Calls, "the detached model read or wrote its workload");
        Assert.AreEqual(0, replica.OutputBytesRead);
        Assert.IsFalse(replica.IsDisposed, "the replica was disposed, which writes terminal-control sequences to its presentation");

        // A failure mid-interval is a failed result naming the last applied model sequence.
        CaseReapplier.AfterEventForTesting.Value = sequence =>
        {
            if (sequence == 4)
                throw new InvalidOperationException("injected failure");
        };
        try
        {
            var failed = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Failed, "reapplication-failed", 4L), (failed.Outcome, failed.Problem?.Code, failed.AppliedThrough));
            Assert.IsTrue(File.Exists(Path.Combine(failed.RunPath!, "result.json")));
        }
        finally
        {
            CaseReapplier.AfterEventForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Reapply_OutputDirectories()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, ReapplicationCorpus, new HeadlessPresentationAdapter(40, 10));
        var runs = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Reapply(path, label: "stop"))));
        foreach (var run in runs)
            AssertMatched(run, "parallel");
        Assert.HasCount(4, runs.Select(r => r.RunPath).Distinct().ToList());
        if (!OperatingSystem.IsWindows())
        {
            foreach (var run in runs)
            {
                Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(run.RunPath!));
                foreach (var file in Directory.GetFiles(run.RunPath!))
                    Assert.AreEqual(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(file), file);
                CollectionAssert.AreEquivalent(new[] { "reapplied.json", "recorded.json", "result.json" }, Directory.GetFiles(run.RunPath!).Select(Path.GetFileName).ToArray());
                CollectionAssert.AreEqual(new[] { "reapplied.json", "recorded.json", "result.json" }, run.Files.ToArray(), "the result lists its files wrongly");
            }

            var loose = await RecordCaseAsync(root, [new("loose")], new HeadlessPresentationAdapter(20, 4));
            File.SetUnixFileMode(loose, File.GetUnixFileMode(loose) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            var refused = Reapply(loose, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Failed, "storage-refused"), (refused.Outcome, refused.Problem?.Code));
            Assert.IsFalse(Directory.Exists(Path.Combine(loose, "reapplications")), "a refused case was written to");
        }

        // The run's files hold the complete states: every history row of both.
        var result = runs[0];
        var reapplied = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.RunPath!, "reapplied.json"))).RootElement;
        var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.RunPath!, "recorded.json"))).RootElement;
        Assert.AreEqual(recorded.GetProperty("history").GetProperty("rows").GetArrayLength(), reapplied.GetProperty("history").GetProperty("rows").GetArrayLength());
        Assert.IsGreaterThan(10, reapplied.GetProperty("history").GetProperty("rows").GetArrayLength(), "fixture: little history");
    }

    [TestMethod]
    public async Task Reapply_FaultLabelled()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, ReapplicationCorpus, new HeadlessPresentationAdapter(40, 10));
        var clean = Reapply(path, label: "history");
        var faulted = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "history", Faults = ["cell-text", "mode"] });

        AssertMatched(clean, "without a fault");
        Assert.AreNotEqual(clean.RunPath, faulted.RunPath, "a faulted run shared a directory");
        Assert.AreEqual((DiagnosticOutcome.Captured, "different", true), (faulted.Outcome, faulted.Comparison, faulted.FaultInjected));
        CollectionAssert.AreEqual(new[] { "cell-text=screen[0][0].text", "mode=modes.wraparound" }, faulted.Faults.Select(f => $"{f.Kind}={f.Path}").ToArray());
        // reapplied.json is the reconstruction either way; faulted.json is what the faulted run compared.
        Assert.AreEqual(File.ReadAllText(Path.Combine(clean.RunPath!, "reapplied.json")), File.ReadAllText(Path.Combine(faulted.RunPath!, "reapplied.json")));
        var faultedState = JsonDocument.Parse(File.ReadAllText(Path.Combine(faulted.RunPath!, "faulted.json"))).RootElement;
        Assert.AreNotEqual(File.ReadAllText(Path.Combine(clean.RunPath!, "reapplied.json")), faultedState.GetRawText());
        CollectionAssert.DoesNotContain(clean.Files.ToArray(), "faulted.json");
        CollectionAssert.IsSubsetOf(new[] { "screen[0][0].text", "modes.wraparound" }, faulted.Differences!.Differences.Select(d => d.Path).ToArray());

        // Each run's own result file says the same.
        var cleanFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(clean.RunPath!, "result.json"))).RootElement;
        var faultedFile = JsonDocument.Parse(File.ReadAllText(Path.Combine(faulted.RunPath!, "result.json"))).RootElement;
        Assert.IsFalse(cleanFile.GetProperty("faultInjected").GetBoolean());
        Assert.AreEqual("matched", cleanFile.GetProperty("comparison").GetString());
        Assert.IsTrue(faultedFile.GetProperty("faultInjected").GetBoolean());
        Assert.AreEqual("different", faultedFile.GetProperty("comparison").GetString());

        Assert.AreEqual("invalid-fault", Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["nope"] }).Problem?.Code);
        var bare = await RecordCaseAsync(root, [new("x")], new HeadlessPresentationAdapter(20, 4), o => o.ScrollbackCapacity = null);
        var inapplicable = Reapply(new DiagnosticCaseReapplyRequest { Path = bare, ToLabel = "stop", Faults = ["history-rows"] });
        Assert.AreEqual((DiagnosticOutcome.Captured, "unavailable"), (inapplicable.Outcome, inapplicable.Comparison));
        StringAssert.StartsWith(inapplicable.ComparisonReason, "fault-not-applicable");
        Assert.AreEqual("invalid-fault", Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["cell-text:1,2"] }).Problem?.Code);
        Assert.AreEqual("invalid-fault", Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["title:x"] }).Problem?.Code);
        var targeted = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["cell-text:2/3", "mode:insert", "history-row:1"] });
        CollectionAssert.AreEqual(new[] { "screen[2][3].text", "modes.insert", "history.rows[1][0].text" }, targeted.Faults.Select(f => f.Path).ToArray());
        CollectionAssert.IsSubsetOf(new[] { "screen[2][3].text", "modes.insert", "history.rows[1][0].text" },
            targeted.Differences!.Differences.Select(d => d.Path).ToArray());
        var outside = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Faults = ["cell-text:99/0"] });
        StringAssert.StartsWith(outside.ComparisonReason, "fault-not-applicable");
    }

    [TestMethod]
    public async Task Reapply_Previews()
    {
        using var root = new CaseRoot();
        // U+E000 is a filler the text exporter renders as a space; the recorded preview must follow it.
        var path = await RecordCaseAsync(root, [.. ReapplicationCorpus, new("pua\uE000end")], new HeadlessPresentationAdapter(40, 10));
        var expected = new Dictionary<string, string>();
        CaseReapplier.ReconstructedForTesting.Value = replica =>
        {
            using var snapshot = replica.CreateSnapshot();
            expected["reapplied.txt"] = snapshot.GetScreenText();
            expected["reapplied.ansi"] = snapshot.ToAnsi();
            expected["reapplied.svg"] = snapshot.ToSvg();
            expected["reapplied.html"] = snapshot.ToHtml();
        };
        DiagnosticCaseReapplyResult result;
        try
        {
            result = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Previews = ["text", "ansi", "svg", "html"] });
        }
        finally
        {
            CaseReapplier.ReconstructedForTesting.Value = null;
        }
        AssertMatched(result, "previews");
        foreach (var (name, content) in expected)
            Assert.AreEqual(content, File.ReadAllText(Path.Combine(result.RunPath!, name)), name);
        CollectionAssert.IsSubsetOf(expected.Keys.Append("recorded.txt").ToArray(), result.Files.ToArray());

        // The recorded checkpoint's preview is its projection's text, with the text exporter's cell rules.
        var recorded = JsonDocument.Parse(File.ReadAllText(Path.Combine(result.RunPath!, "recorded.json"))).RootElement;
        Assert.IsTrue(recorded.GetProperty("screen").EnumerateArray().Select(RowText).Any(row => row.Contains('\uE000')), "fixture: no U+E000 cell");
        Assert.IsFalse(File.ReadAllText(Path.Combine(result.RunPath!, "recorded.txt")).Contains('\uE000'), "the filler was copied as text");
        StringAssert.Contains(File.ReadAllText(Path.Combine(result.RunPath!, "recorded.txt")), "colour");
        // For a matched target the two text previews are the same text (wide glyphs, erased cells).
        Assert.AreEqual(File.ReadAllText(Path.Combine(result.RunPath!, "reapplied.txt")), File.ReadAllText(Path.Combine(result.RunPath!, "recorded.txt")));

        // Without a checkpoint at the target, only the reconstructed model is previewed.
        var none = Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToModelSequence = 1, Previews = ["text"] });
        CollectionAssert.Contains(none.Files.ToArray(), "reapplied.txt");
        CollectionAssert.DoesNotContain(none.Files.ToArray(), "recorded.txt");
        Assert.AreEqual("invalid-preview", Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = "stop", Previews = ["pdf"] }).Problem?.Code);
    }

    // --- fixtures ---

    private sealed record CaseStep(byte[]? Bytes, string? MarkLabel = null, int Width = 0, int Height = 0)
    {
        public CaseStep(string text) : this(Encoding.UTF8.GetBytes(text))
        {
        }

        public static CaseStep Mark(string label) => new(null, label);

        public static CaseStep Resize(int width, int height) => new(null, null, width, height);
    }

    // Records a construction-started case with reapplication-data through the steps, then stops it.
    private static async Task<string> RecordCaseAsync(CaseRoot root, IReadOnlyList<CaseStep> steps, HeadlessPresentationAdapter presentation,
        Action<Hex1bTerminalOptions>? configure = null, bool authorized = true, long? maxBytes = null)
    {
        var workload = new ScriptedWorkload();
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = workload,
            Width = presentation.Width,
            Height = presentation.Height,
            ScrollbackCapacity = 100,
        };
        configure?.Invoke(options);
        var (terminal, started) = Hex1bTerminal.CreateWithDiagnosticCase(options, new DiagnosticCaseStartRequest
        {
            Directory = root.Path,
            MaxBytes = maxBytes,
            Authorizations = authorized ? [DiagnosticAuthorization.ReapplicationData] : [],
        });
        Assert.AreEqual(DiagnosticOutcome.Captured, started.Outcome, started.Problem?.Message);
        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            foreach (var step in steps)
            {
                if (step.Bytes is { } bytes)
                    await workload.WriteAndWaitAsync(terminal, bytes);
                else if (step.MarkLabel is { } label)
                {
                    // Written before the next step, so each checkpoint's place in the file is fixed.
                    var mark = diagnostics.MarkCase(label);
                    Assert.AreEqual(DiagnosticOutcome.Captured, mark.Outcome);
                    await WaitAsync(() => WrittenCheckpointLines(started.Path!) >= mark.CheckpointOrdinal);
                }
                else
                    terminal.Resize(step.Width, step.Height);
            }
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }
        return started.Path!;
    }

    private static DiagnosticCaseReapplyResult Reapply(string path, string? label = null, long? modelSequence = null, long? caseSequence = null) =>
        Reapply(new DiagnosticCaseReapplyRequest { Path = path, ToLabel = label, ToModelSequence = modelSequence, ToCaseSequence = caseSequence });

    private static DiagnosticCaseReapplyResult Reapply(DiagnosticCaseReapplyRequest request) => DiagnosticCaseReapplier.Reapply(request);

    private static void AssertMatched(DiagnosticCaseReapplyResult result, string what)
    {
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, $"{what}: {result.Problem?.Code} {result.Problem?.Message}");
        Assert.AreEqual("matched", result.Comparison,
            $"{what}: {result.ComparisonReason} {string.Join("; ", result.Differences?.Differences.Take(8).Select(d => $"{d.Path} {d.Recorded} vs {d.Reapplied}") ?? [])}");
        Assert.IsFalse(result.FaultInjected);
    }

    // Case files are owner-only and created exclusively; the test replaces one in place.
    private static void RewriteOwnerOnly(string file, string content) => File.WriteAllText(file, content);

    // Rewrites the first event line matching `select`, with a fresh checksum unless told not to (a corrupt line).
    private static void EditEventLine(string path, Func<JsonNode, bool> select, Action<JsonNode> edit, bool recomputeChecksum = true)
    {
        var file = Path.Combine(path, "events.jsonl");
        var lines = File.ReadAllLines(file);
        for (var i = 0; i < lines.Length; i++)
        {
            var node = JsonNode.Parse(lines[i][9..])!;
            if (!select(node))
                continue;
            edit(node);
            var json = node.ToJsonString();
            var checksum = recomputeChecksum ? CaseCrc32.Compute(Encoding.UTF8.GetBytes(json)).ToString("x8") : lines[i][..8];
            lines[i] = $"{checksum}\t{json}";
            File.WriteAllText(file, string.Join("\n", lines) + "\n");
            return;
        }
        Assert.Fail("fixture: no event line matched");
    }

    // Cuts the file in the middle of its last model event, as a writer killed mid-line leaves it.
    private static void TearLastModelEvent(string path)
    {
        var file = Path.Combine(path, "events.jsonl");
        var text = File.ReadAllText(file);
        var start = text.LastIndexOf("\"stream\":\"model\"", StringComparison.Ordinal);
        var lineStart = text.LastIndexOf('\n', start) + 1;
        File.WriteAllText(file, text[..(lineStart + 20)]);
        File.Delete(Path.Combine(path, "completion.json"));
    }

    private static List<string> HashCaseFiles(string path) =>
        Directory.GetFiles(path).Select(f => $"{Path.GetFileName(f)}:{Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f)))}").ToList();
}
