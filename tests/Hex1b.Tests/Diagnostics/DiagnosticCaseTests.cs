using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hex1b.Diagnostics;
using Hex1b.Widgets;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// A diagnostics-enabled terminal records a bounded diagnostic case: explicitly started and stopped,
/// stored owner-only, with a checkpoint that is complete only for a fresh model.
/// </summary>
[TestClass]
public class DiagnosticCaseTests
{
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    [TestMethod]
    public async Task ConstructionStart_RecordsFromTheFirstModelEventWithACompleteFreshCheckpoint()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        using var run = new Running(terminal);

        foreach (var chunk in new[] { "one", "two", "three" })
            await workload.WriteAndWaitAsync(terminal, chunk);
        var stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(stopped.Path!);

        Assert.AreEqual(DiagnosticOutcome.Captured, stopped.Outcome, stopped.Problem?.Message);
        Assert.AreEqual("construction", artifact.Manifest.GetProperty("startPath").GetString());
        Assert.IsTrue(artifact.Manifest.GetProperty("fresh").GetBoolean(), "a terminal built with a case is fresh");
        var checkpoint = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("fresh-model/1", "complete"), (checkpoint.GetProperty("profile").GetString(), checkpoint.GetProperty("status").GetString()));
        Assert.AreEqual((40, 10), (checkpoint.GetProperty("configuration").GetProperty("width").GetInt32(), checkpoint.GetProperty("configuration").GetProperty("height").GetInt32()));
        var model = artifact.Events.Where(e => e.GetProperty("stream").GetString() == "model").ToList();
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, model.Select(e => e.GetProperty("modelSequence").GetInt64()).ToArray(),
            "the case missed a model event after construction");
        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, model.Select(e => e.GetProperty("ordinal").GetInt64()).ToArray());
        Assert.IsTrue(model.All(e => e.GetProperty("kind").GetString() == "application"));
        Assert.AreEqual("requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        if (!OperatingSystem.IsWindows())
        {
            Assert.AreEqual(OwnerDirectory, File.GetUnixFileMode(stopped.Path!));
            foreach (var file in Directory.GetFiles(stopped.Path!))
                Assert.AreEqual(OwnerFile, File.GetUnixFileMode(file), file);
        }
    }

    [TestMethod]
    public async Task LiveStart_FreshnessVerdicts()
    {
        using var root = new CaseRoot();
        async Task<(DiagnosticCaseResult Started, Artifact Artifact)> RecordAsync(Func<Hex1bTerminal, ScriptedWorkload, Task> before)
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
            using var run = new Running(terminal);
            await before(terminal, workload);
            var diagnostics = new TerminalDiagnostics(terminal);
            var sequenceAtStart = terminal.CurrentModelSequence;
            var started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
            await workload.WriteAndWaitAsync(terminal, "after");
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            var artifact = Artifact.Read(started.Path!);
            var first = artifact.Events.First(e => e.GetProperty("stream").GetString() == "model");
            Assert.AreEqual(sequenceAtStart + 1, first.GetProperty("modelSequence").GetInt64(), "the next model event after the start was not recorded");
            return (started, artifact);
        }

        var untouched = await RecordAsync((_, _) => Task.CompletedTask);
        var applied = await RecordAsync((t, w) => w.WriteAndWaitAsync(t, "before"));
        var resized = await RecordAsync((t, _) => { t.Resize(40, 10); return Task.CompletedTask; });

        Assert.AreEqual("complete", untouched.Artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
        Assert.AreEqual("live", untouched.Artifact.Manifest.GetProperty("startPath").GetString());
        foreach (var (name, result) in new[] { ("applied batch", applied), ("same-size resize", resized) })
        {
            var checkpoint = result.Artifact.Manifest.GetProperty("checkpoint");
            Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), name);
            StringAssert.StartsWith(checkpoint.GetProperty("reason").GetString(), "not-fresh", name);
            Assert.IsFalse(result.Artifact.Manifest.GetProperty("fresh").GetBoolean(), name);
        }
    }

    [TestMethod]
    public async Task RefusedStarts_CreateAndRecordNothing()
    {
        using var root = new CaseRoot();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(20, 5).Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var before = root.Listing();
        var cases = new (DiagnosticCaseStartRequest Request, DiagnosticOutcome Outcome, string Code)[]
        {
            (new() { Directory = root.Path, MaxBytes = 1024 * 1024 - 1 }, DiagnosticOutcome.InvalidRequest, "invalid-bounds"),
            (new() { Directory = root.Path, MaxBytes = 1024L * 1024 * 1024 + 1 }, DiagnosticOutcome.InvalidRequest, "invalid-bounds"),
            (new() { Directory = root.Path, MaxSeconds = 0 }, DiagnosticOutcome.InvalidRequest, "invalid-bounds"),
            (new() { Directory = root.Path, MaxSeconds = 86_401 }, DiagnosticOutcome.InvalidRequest, "invalid-bounds"),
            (new() { Directory = root.Path, Authorizations = [(DiagnosticAuthorization)99] }, DiagnosticOutcome.InvalidRequest, "unsupported-authorization"),
            (new() { Directory = "" }, DiagnosticOutcome.InvalidRequest, "invalid-directory"),
        };
        foreach (var (request, outcome, code) in cases)
        {
            var result = diagnostics.StartCase(request);
            Assert.AreEqual((outcome, code), (result.Outcome, result.Problem?.Code), code);
        }

        if (!OperatingSystem.IsWindows())
        {
            var loose = Directory.CreateDirectory(Path.Combine(root.Path, "loose"), OwnerDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute).FullName;
            var target = Directory.CreateDirectory(Path.Combine(root.Path, "target"), OwnerDirectory).FullName;
            var link = Path.Combine(root.Path, "link");
            Directory.CreateSymbolicLink(link, target);
            var file = Path.Combine(root.Path, "file");
            File.WriteAllText(file, "x");
            before = root.Listing();
            foreach (var refusedRoot in new[] { loose, link, file })
            {
                var result = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = refusedRoot });
                Assert.AreEqual((DiagnosticOutcome.Failed, "storage-refused"), (result.Outcome, result.Problem?.Code), refusedRoot);
            }
        }

        CollectionAssert.AreEquivalent(before, root.Listing(), "a refused start created something");
        Assert.AreEqual("no-active-case", diagnostics.GetCaseStatus().Problem?.Code, "a refused start left a case active");

        // Positive control: exact bounds are accepted, and a second start while one records is refused.
        var accepted = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = 1024 * 1024, MaxSeconds = 1 });
        Assert.AreEqual(DiagnosticOutcome.Captured, accepted.Outcome, accepted.Problem?.Message);
        var second = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = 1024L * 1024 * 1024, MaxSeconds = 86_400 });
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "case-active", accepted.CaseId), (second.Outcome, second.Problem?.Code, second.CaseId));
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
    }

    [TestMethod]
    public async Task Lifecycle_ConcurrentStartsStopsAndStatus()
    {
        using var root = new CaseRoot();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(20, 5).Build();
        var diagnostics = new TerminalDiagnostics(terminal);

        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-active-case"), (diagnostics.GetCaseStatus().Outcome, diagnostics.GetCaseStatus().Problem?.Code));
        var stopWithout = await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-active-case"), (stopWithout.Outcome, stopWithout.Problem?.Code));

        for (var round = 0; round < 10; round++)
        {
            var starts = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
                diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path }))));
            var winners = starts.Where(s => s.Outcome == DiagnosticOutcome.Captured).ToList();
            Assert.HasCount(1, winners, $"round {round}: {winners.Count} cases armed at once");
            Assert.IsTrue(starts.Where(s => s.Outcome != DiagnosticOutcome.Captured).All(s => s.Problem?.Code == "case-active"), $"round {round}");
            Assert.AreEqual(DiagnosticCaseState.Recording, diagnostics.GetCaseStatus().State);

            var stops = await Task.WhenAll(diagnostics.StopCaseAsync(TestContext.Current.CancellationToken), diagnostics.StopCaseAsync(TestContext.Current.CancellationToken));
            Assert.IsTrue(stops.Where(s => s.Outcome == DiagnosticOutcome.Captured).All(s => s.StopReason == DiagnosticCaseStopReason.Requested && s.CaseId == winners[0].CaseId),
                $"round {round}: a stop reported another case or reason");
            Assert.IsTrue(stops.Any(s => s.Outcome == DiagnosticOutcome.Captured), $"round {round}: no stop took effect");
            Assert.AreEqual("no-active-case", diagnostics.GetCaseStatus().Problem?.Code, $"round {round}: the case stayed active after its stop");
        }

        // Losing starts removed their directories: one per round remains.
        Assert.HasCount(10, Directory.GetDirectories(root.Path));
    }

    [TestMethod]
    public async Task FreshCheckpoint_ConfigurationDeterminesState()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var recorded = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).WithScrollback(50)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        using (new Running(recorded))
        {
            foreach (var chunk in Corpus.Chunks)
                await workload.WriteAndWaitAsync(recorded, chunk);
            var stopped = await new TerminalDiagnostics(recorded).StopCaseAsync(TestContext.Current.CancellationToken);
            var configuration = Artifact.Read(stopped.Path!).Manifest.GetProperty("checkpoint").GetProperty("configuration");

            // A terminal rebuilt from the recorded configuration behaves identically on the same bytes.
            var rebuilt = await Corpus.FeedAsync(configuration.GetProperty("width").GetInt32(), configuration.GetProperty("height").GetInt32(),
                configuration.TryGetProperty("scrollbackCapacity", out var scrollback) ? scrollback.GetInt32() : null);
            Assert.AreEqual(Corpus.Digest(recorded), rebuilt, "the recorded configuration does not determine the model's state");

            // Positive control: dropping the scrollback capacity changes the state.
            var withoutScrollback = await Corpus.FeedAsync(40, 10, null);
            Assert.AreNotEqual(rebuilt, withoutScrollback, "fixture: the corpus does not exercise retained history");
        }
    }

    [TestMethod]
    [DoNotParallelize]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Storage_OwnerOnlyUnderAnyUmaskAndUnicodeRoots()
    {
        if (!OperatingSystem.IsLinux())
            Assert.Inconclusive("Linux umask and modes.");
        using var root = new CaseRoot();
        var unicodeRoot = Path.Combine(root.Path, "cases dir", "ñ");
        var previous = Umask(0);
        DiagnosticCaseResult started;
        try
        {
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(20, 5)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = unicodeRoot }).Build();
            started = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Umask(previous);
        }

        Assert.AreEqual(DiagnosticOutcome.Captured, started.Outcome, started.Problem?.Message);
        Assert.AreEqual(OwnerDirectory, File.GetUnixFileMode(unicodeRoot), "root under umask 0");
        Assert.AreEqual(OwnerDirectory, File.GetUnixFileMode(started.Path!), "case directory under umask 0");
        foreach (var file in Directory.GetFiles(started.Path!))
            Assert.AreEqual(OwnerFile, File.GetUnixFileMode(file), file);
    }

    [TestMethod]
    public async Task Authorizations_IngressOnlyWithReapplicationData()
    {
        using var root = new CaseRoot();
        var sentinel = Encoding.ASCII.GetBytes("ZQX-INGRESS-SECRET");
        foreach (var authorized in new[] { false, true })
        {
            var copies = Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.IngressCopiesForTesting.Value = new System.Runtime.CompilerServices.StrongBox<int>();
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = authorized ? [DiagnosticAuthorization.ReapplicationData] : [] })
                .Build();
            DiagnosticCaseResult stopped;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, sentinel);
                stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            }

            // Oracle: the raw files, not a reader. The chunk is exactly the sentinel, so its base64 appears whole.
            var raw = string.Concat(Directory.GetFiles(stopped.Path!).Select(File.ReadAllText));
            var artifact = Artifact.Read(stopped.Path!);
            var model = artifact.Manifest.GetProperty("streams").EnumerateArray().Single(e => e.GetProperty("stream").GetString() == "model");
            Assert.AreEqual(authorized, raw.Contains(Convert.ToBase64String(sentinel), StringComparison.Ordinal), $"authorized={authorized}: sentinel presence");
            Assert.AreEqual(authorized ? "included" : "excluded", model.GetProperty("payloads").GetString());
            Assert.AreEqual(authorized ? "complete" : "excluded", artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
            Assert.AreEqual(sentinel.Length, artifact.ModelEvents().Single().GetProperty("length").GetInt32(), "the length is recorded either way");
            if (authorized)
                Assert.IsGreaterThan(0, copies.Value, "fixture: an authorized case copied nothing");
            else
                Assert.AreEqual(0, copies.Value, "an unauthorized case copied ingress bytes");
        }
    }

    [TestMethod]
    public async Task Ingress_ByteAndChunkFidelity()
    {
        using var root = new CaseRoot();

        // Raw-byte workload: chunks split UTF-8 scalars, a CSI and an OSC 8, plus one 64 KiB chunk.
        var chunks = new List<byte[]>
        {
            "caf"u8.ToArray(), new byte[] { 0xC3 }, new byte[] { 0xA9, (byte)' ' }, "\u001b"u8.ToArray(), "[31mred\u001b[0m "u8.ToArray(),
            "\u001b]8;;https://ex"u8.ToArray(), "ample.test\u001b\\link\u001b]8;;\u001b\\\r\n"u8.ToArray(),
            Enumerable.Repeat((byte)'x', 64 * 1024).Concat("\r\n"u8.ToArray()).ToArray(),
        };
        var random = new Random(7);
        var tail = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, 40).Select(i => $"é{i}漢\u001b[{i % 7 + 30}m👩‍💻 ")));
        for (var offset = 0; offset < tail.Length && chunks.Count < 37;)
        {
            var size = Math.Min(random.Next(1, 23), tail.Length - offset);
            chunks.Add(tail[offset..(offset + size)]);
            offset += size;
        }

        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            foreach (var chunk in chunks)
                await workload.WriteAndWaitAsync(terminal, chunk);
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var recorded = Artifact.Read(stopped.Path!).ModelEvents().Select(e => Convert.FromBase64String(e.GetProperty("data").GetString()!)).ToList();
        var returned = workload.Returned();
        Assert.HasCount(returned.Count, recorded, "chunk count");
        for (var i = 0; i < returned.Count; i++)
            CollectionAssert.AreEqual(returned[i], recorded[i], $"chunk {i} differs");
    }

    [TestMethod]
    public async Task ApplicationIngress_EqualsTheItemsTheModelReads()
    {
        using var root = new CaseRoot();
        var appAdapter = new Hex1bAppWorkloadAdapter();
        var tap = new TapWorkload(appAdapter);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(tap).WithHeadless().WithDimensions(60, 20)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        using var app = new Hex1bApp(ctx => Task.FromResult(Widgets.Build(ctx, 0)), new Hex1bAppOptions { WorkloadAdapter = appAdapter });
        DiagnosticCaseResult stopped;
        List<byte[]> consumed;
        using (new Running(terminal))
        {
            using var appCts = new CancellationTokenSource();
            var appRun = app.RunAsync(appCts.Token);
            await WaitForTextAsync(terminal, "Panel");
            foreach (var (w, h) in new[] { (40, 15), (72, 24), (60, 20) })
            {
                await terminal.ResizeForAutomationAsync(w, h);
                await Settle(terminal);
            }
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            // What the model had read when the case stopped (the app's exit output comes later).
            consumed = tap.Items();
            await appCts.CancelAsync();
            try { await appRun; } catch (OperationCanceledException) { }
        }

        var recorded = Artifact.Read(stopped.Path!).ModelEvents().Where(e => e.GetProperty("kind").GetString() == "application")
            .Select(e => Convert.FromBase64String(e.GetProperty("data").GetString()!)).ToList();
        var kinds = string.Join(",", Artifact.Read(stopped.Path!).ModelEvents().Select(e => e.GetProperty("kind").GetString()));
        Assert.IsGreaterThan(3, consumed.Count, "fixture: the app wrote too little");
        Assert.HasCount(consumed.Count, recorded, $"item count; recorded kinds: {kinds}");
        for (var i = 0; i < consumed.Count; i++)
            CollectionAssert.AreEqual(consumed[i], recorded[i], $"item {i} differs");
    }

    [TestMethod]
    public async Task ModelStream_EveryTickOnceInOrder()
    {
        using var root = new CaseRoot();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        var workload = new ScriptedWorkload();
        var hold = new HoldingFilter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithTimeProvider(clock).AddWorkloadFilter(hold)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var expected = new List<(long Sequence, string Kind)>();
        void Expect(string kind) => expected.Add((terminal.CurrentModelSequence, kind));
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "a"u8.ToArray()); Expect("application");
            terminal.Resize(40, 10); Expect("resize");
            await workload.WriteAndWaitAsync(terminal, "\u001b[?2026h"u8.ToArray()); Expect("application");
            var beforeTimeout = terminal.CurrentModelSequence;
            clock.Advance(TimeSpan.FromSeconds(2));
            await WaitAsync(() => terminal.CurrentModelSequence > beforeTimeout);
            Expect("synchronized-update-timeout");
            await workload.WriteAndWaitAsync(terminal, [0xC3]); Expect("application");
            await workload.WriteAndWaitAsync(terminal, [0xA9]); Expect("application");

            // A chunk held after its read while another thread resizes the model.
            hold.Arm();
            workload.Enqueue("ABCDEFGHIJKLMNOPQRSTUVWXYZ0123"u8.ToArray());
            Assert.IsTrue(hold.Held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "fixture: the chunk was never held");
            await Task.Run(() => terminal.Resize(20, 10)); Expect("resize");
            var beforeRelease = terminal.CurrentModelSequence;
            hold.Release.Set();
            await WaitAsync(() => terminal.CurrentModelSequence > beforeRelease);
            Expect("application");
            await Settle(terminal);
            using (var snapshot = terminal.CreateSnapshot())
                // "aé" occupies columns 0-1, so the 30-column chunk wraps after 18 columns at width 20;
                // applied at the old width of 40 it would fit on row 0.
                Assert.AreEqual("STUVWXYZ0123", snapshot.GetScreenText().Split('\n')[1].TrimEnd(), "oracle: the held chunk wrapped at the new width");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var model = Artifact.Read(stopped.Path!).ModelEvents();
        var actual = model.Select(e => (e.GetProperty("modelSequence").GetInt64(), e.GetProperty("kind").GetString()!)).ToList();
        CollectionAssert.AreEqual(Enumerable.Range(1, actual.Count).Select(i => (long)i).ToList(), actual.Select(a => a.Item1).ToList(),
            "model sequences are not 1..N: a tick was missed or recorded twice");
        CollectionAssert.AreEqual(expected, actual, string.Join(", ", actual));
        CollectionAssert.AreEqual(new byte[] { 0xC3 }, Convert.FromBase64String(model[4].GetProperty("data").GetString()!), "the continuation-only chunk");
    }

    [TestMethod]
    public async Task LiveStart_InFlightChunkOnce()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var hold = new HoldingFilter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .AddWorkloadFilter(hold).Build();
        DiagnosticCaseResult started;
        using (new Running(terminal))
        {
            hold.Arm();
            workload.Enqueue("INFLIGHT"u8.ToArray());
            Assert.IsTrue(hold.Held.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "fixture: the chunk was never held");
            var diagnostics = new TerminalDiagnostics(terminal);
            started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
            hold.Release.Set();
            await workload.WriteAndWaitAsync(terminal, "NEXT"u8.ToArray());
            await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(started.Path!);
        var payloads = artifact.ModelEvents().Select(e => Encoding.ASCII.GetString(Convert.FromBase64String(e.GetProperty("data").GetString()!))).ToList();
        CollectionAssert.AreEqual(new[] { "INFLIGHT", "NEXT" }, payloads, "the in-flight chunk was lost or recorded twice");
        Assert.IsFalse(artifact.Manifest.GetProperty("fresh").GetBoolean(), "a model that had read bytes is not fresh");
        Assert.AreEqual("unsupported", artifact.Manifest.GetProperty("checkpoint").GetProperty("status").GetString());
    }

    [TestMethod]
    public async Task ApplicationCase_BytesReproduceModel()
    {
        using var root = new CaseRoot();
        await using var live = Hex1bTerminal.CreateBuilder().WithHex1bApp(ctx => Widgets.Build(ctx, ctx is null ? 0 : 1)).WithHeadless().WithDimensions(60, 20)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        string liveDigest;
        DiagnosticCaseResult stopped;
        using (new Running(live))
        {
            await WaitForTextAsync(live, "Panel");
            foreach (var (w, h) in new[] { (40, 15), (72, 24), (33, 12), (60, 20) })
            {
                await live.ResizeForAutomationAsync(w, h);
                await Settle(live);
            }
            await Settle(live);
            liveDigest = Corpus.Digest(live);
            stopped = await new TerminalDiagnostics(live).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        // Replay only the recorded events into a fresh terminal of the recorded configuration.
        var artifact = Artifact.Read(stopped.Path!);
        var configuration = artifact.Manifest.GetProperty("checkpoint").GetProperty("configuration");
        var workload = new ScriptedWorkload();
        await using var replay = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(configuration.GetProperty("width").GetInt32(), configuration.GetProperty("height").GetInt32()).Build();
        using (new Running(replay))
        {
            foreach (var e in artifact.ModelEvents())
            {
                switch (e.GetProperty("kind").GetString())
                {
                    case "application":
                        await workload.WriteAndWaitAsync(replay, Convert.FromBase64String(e.GetProperty("data").GetString()!));
                        break;
                    case "resize":
                        replay.Resize(e.GetProperty("width").GetInt32(), e.GetProperty("height").GetInt32());
                        break;
                    default:
                        Assert.Fail($"unexpected model event {e}");
                        break;
                }
            }
            await Settle(replay);
            Assert.AreEqual(liveDigest, Corpus.Digest(replay), "the recorded application bytes and resizes do not reproduce the model");
        }
    }

    [TestMethod]
    public async Task Semantics_Unchanged()
    {
        using var root = new CaseRoot();
        var chunks = new[] { "caf"u8.ToArray(), new byte[] { 0xC3 }, new byte[] { 0xA9, 0x0D, 0x0A }, "\u001b[1;32mgreen\u001b[0m"u8.ToArray(), "\u001b]0;T\u0007done"u8.ToArray() };
        async Task<(string Presentation, string Model)> RunAsync(bool armed)
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
            var presentation = new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25));
            var workload = new ScriptedWorkload();
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 10);
            if (armed)
                builder.WithDiagnosticCase(new DiagnosticCaseStartRequest
                {
                    Directory = root.Path,
                    Authorizations = [DiagnosticAuthorization.ReapplicationData, DiagnosticAuthorization.RawInput, DiagnosticAuthorization.EditorText, DiagnosticAuthorization.NativeOutput],
                });
            await using var terminal = builder.Build();
            using (new Running(terminal))
            {
                foreach (var chunk in chunks)
                    await workload.WriteAndWaitAsync(terminal, chunk);
                await Settle(terminal);
                return (driver.WrittenText, Corpus.Digest(terminal));
            }
        }

        var unarmed = await RunAsync(armed: false);
        var armed = await RunAsync(armed: true);
        Assert.AreEqual(unarmed.Model, armed.Model, "recording changed the model");
        Assert.AreEqual(unarmed.Presentation, armed.Presentation, "recording changed the presentation output");
        StringAssert.Contains(armed.Model, "é", "fixture: the split scalar did not decode");
    }

    // Measured with PumpAllocation.Measure on the base revision (5a4b0a6c, no case support) in this
    // configuration; the same routine runs in the base probe (…/scratchpad/t07s2/basealloc).
#if DEBUG
    private const long BasePumpBytesPer1000Chunks = 19_106_300;
#else
    private const long BasePumpBytesPer1000Chunks = 19_104_800;
#endif

    [TestMethod]
    [DoNotParallelize]
    public async Task Unarmed_PumpAllocatesAsBase()
    {
        var bytes = await PumpAllocation.MeasureAsync();
        // One allocation per chunk adds 24 KB or more.
        Assert.IsLessThanOrEqualTo(BasePumpBytesPer1000Chunks + 16_000, bytes,
            $"the unarmed pump allocates {bytes} bytes per 1,000 chunks; the base allocated {BasePumpBytesPer1000Chunks}");
    }

    /// <summary>
    /// Bytes an unarmed raw terminal's output pump allocates for 1,000 prepared 64-byte chunks. The pump
    /// runs on the calling thread (every read completes synchronously) so the thread's own allocation
    /// counter measures it alone. Kept textually identical to the base probe's copy.
    /// </summary>
    internal static class PumpAllocation
    {
        public static async Task<long> MeasureAsync()
        {
            var chunks = Enumerable.Range(0, 1200).Select(i => Encoding.ASCII.GetBytes($"line {i:D5} " + new string('x', 53))).ToArray();
            var workload = new MeasuredWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(80, 24).Build();
            var pump = typeof(Hex1bTerminal).GetMethod("PumpWorkloadOutputAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .CreateDelegate<Func<CancellationToken, Task>>(terminal);

            RunPump(pump, workload, chunks[..200]);
            var start = GC.GetAllocatedBytesForCurrentThread();
            RunPump(pump, workload, chunks[200..]);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - start;
            if (terminal.OutputBytesRead != 1200L * 64)
                throw new InvalidOperationException("fixture: the pump did not read every chunk");
            return allocated;
        }

        private static void RunPump(Func<CancellationToken, Task> pump, MeasuredWorkload workload, byte[][] chunks)
        {
            using var cts = new CancellationTokenSource();
            workload.Load(chunks, cts);
            var run = pump(cts.Token);
            if (!run.IsCompleted)
                throw new InvalidOperationException("fixture: the pump did not run synchronously");
            run.GetAwaiter().GetResult();
        }

        // Returns each prepared chunk synchronously; once they are exhausted it cancels the pump.
        private sealed class MeasuredWorkload : IHex1bTerminalWorkloadAdapter
        {
            private byte[][] _chunks = [];
            private int _next;
            private CancellationTokenSource? _cts;

            public void Load(byte[][] chunks, CancellationTokenSource cts) => (_chunks, _next, _cts) = (chunks, 0, cts);

            public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
            {
                if (_next < _chunks.Length)
                    return new ValueTask<ReadOnlyMemory<byte>>(_chunks[_next++]);
                _cts!.Cancel();
                return ValueTask.FromCanceled<ReadOnlyMemory<byte>>(ct);
            }

            public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
            public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
            public event Action? Disconnected { add { } remove { } }
            public ValueTask DisposeAsync() => ValueTask.CompletedTask;
        }
    }

    private static async Task WaitAsync(Func<bool> condition)
    {
        for (var i = 0; i < 500 && !condition(); i++)
            await Task.Delay(10, TestContext.Current.CancellationToken);
        Assert.IsTrue(condition(), "fixture: condition never held");
    }

    private static Task WaitForTextAsync(Hex1bTerminal terminal, string text) => WaitAsync(() =>
    {
        using var snapshot = terminal.CreateSnapshot();
        return snapshot.GetScreenText().Contains(text, StringComparison.Ordinal);
    });

    // Waits until the model sequence has been stable for 60 ms.
    private static async Task Settle(Hex1bTerminal terminal)
    {
        var last = terminal.CurrentModelSequence;
        for (var i = 0; i < 100; i++)
        {
            await Task.Delay(60, TestContext.Current.CancellationToken);
            var now = terminal.CurrentModelSequence;
            if (now == last)
                return;
            last = now;
        }
    }

    /// <summary>A workload filter that holds the pump after a chunk was read and tokenized, before the model applies it.</summary>
    private sealed class HoldingFilter : IHex1bTerminalWorkloadFilter
    {
        private volatile bool _armed;
        public ManualResetEventSlim Held { get; } = new();
        public ManualResetEventSlim Release { get; } = new();
        public void Arm() { Held.Reset(); Release.Reset(); _armed = true; }
        public ValueTask OnOutputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            if (_armed)
            {
                _armed = false;
                Held.Set();
                Release.Wait(TimeSpan.FromSeconds(10));
            }
            return ValueTask.CompletedTask;
        }
        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    /// <summary>Wraps the real application workload and logs every item's bytes as the terminal consumes them.</summary>
    private sealed class TapWorkload(Hex1bAppWorkloadAdapter inner) : IHex1bTerminalTokenWorkloadAdapter
    {
        private readonly List<byte[]> _items = [];

        public List<byte[]> Items() { lock (_items) return [.. _items]; }

        public async ValueTask<WorkloadOutputItem> ReadOutputItemAsync(CancellationToken ct = default)
        {
            var item = await inner.ReadOutputItemAsync(ct);
            if (!item.Bytes.IsEmpty)
                lock (_items) _items.Add(item.Bytes.ToArray());
            return item;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => inner.ReadOutputAsync(ct);
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => inner.WriteInputAsync(data, ct);
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => inner.ResizeAsync(width, height, ct);
        public event Action? Disconnected { add => inner.Disconnected += value; remove => inner.Disconnected -= value; }
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    /// <summary>An application exercising styles, Unicode, borders, a hyperlink and tabs (evidence P1).</summary>
    private static class Widgets
    {
        public static Hex1bWidget Build(RootContext ctx, int tick) => ctx.VStack(v =>
        [
            v.Text($"Tick {tick} — plain ASCII, then 漢字かな, emoji 👩‍💻🎉, combining é e\u0301, box ╔═╗"),
            v.Border(v.Text($"Bordered {tick} with a long line that has to wrap across the available width of the panel")),
            v.Text("Panel"),
            v.Hyperlink($"Link {tick}", "https://example.com/path?q=1"),
            v.Button($"Button {tick}"),
            v.Progress(tick % 100, 0, 100),
            v.Text(new string('x', tick % 7) + "\ttab\tstops"),
        ]);
    }

    [DllImport("libc", EntryPoint = "umask")]
    private static extern uint Umask(uint mask);

    /// <summary>A test-owned, owner-only case root, removed afterwards.</summary>
    private sealed class CaseRoot : IDisposable
    {
        public CaseRoot()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "hex1b-case-tests-" + Guid.NewGuid().ToString("N"));
            if (OperatingSystem.IsWindows())
                Directory.CreateDirectory(Path);
            else
                Directory.CreateDirectory(Path, OwnerDirectory);
        }

        public string Path { get; }

        public List<string> Listing() => Directory.EnumerateFileSystemEntries(Path, "*", SearchOption.AllDirectories).ToList();

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>The artifact read independently of the production reader: plain JSON over the raw files.</summary>
    private sealed record Artifact(JsonElement Manifest, List<JsonElement> Events, JsonElement? Completion)
    {
        public static Artifact Read(string path)
        {
            var manifest = JsonDocument.Parse(File.ReadAllText(System.IO.Path.Combine(path, "manifest.json"))).RootElement;
            var events = new List<JsonElement>();
            foreach (var line in File.ReadAllLines(System.IO.Path.Combine(path, "events.jsonl")))
            {
                var tab = line.IndexOf('\t');
                Assert.AreEqual(8, tab, $"not a checksummed line: {line}");
                events.Add(JsonDocument.Parse(line[(tab + 1)..]).RootElement);
            }

            var completionPath = System.IO.Path.Combine(path, "completion.json");
            JsonElement? completion = File.Exists(completionPath) ? JsonDocument.Parse(File.ReadAllText(completionPath)).RootElement : null;
            return new Artifact(manifest, events, completion);
        }

        public List<JsonElement> ModelEvents() => Events.Where(e => e.GetProperty("stream").GetString() == "model").ToList();
    }

    /// <summary>Runs a terminal until disposed.</summary>
    private sealed class Running : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _run;

        public Running(Hex1bTerminal terminal) => _run = terminal.RunAsync(_cts.Token);

        public void Dispose()
        {
            _cts.Cancel();
            try { _run.Wait(TimeSpan.FromSeconds(10)); } catch (AggregateException) { }
            _cts.Dispose();
        }
    }

    /// <summary>A raw-byte workload whose chunks the test controls.</summary>
    private sealed class ScriptedWorkload : IHex1bTerminalWorkloadAdapter
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();

        public void Enqueue(byte[] chunk) => _chunks.Writer.TryWrite(chunk);

        public Task WriteAndWaitAsync(Hex1bTerminal terminal, string text) => WriteAndWaitAsync(terminal, Encoding.UTF8.GetBytes(text));

        public async Task WriteAndWaitAsync(Hex1bTerminal terminal, byte[] bytes)
        {
            var target = terminal.OutputBytesRead + bytes.Length;
            var sequence = terminal.CurrentModelSequence;
            Enqueue(bytes);
            for (var i = 0; i < 500 && (terminal.OutputBytesRead < target || terminal.CurrentModelSequence == sequence); i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.IsGreaterThanOrEqualTo(target, terminal.OutputBytesRead, "fixture: the chunk was never read");
        }

        private readonly List<byte[]> _returned = [];

        public List<byte[]> Returned() { lock (_returned) return [.. _returned]; }

        public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        {
            var chunk = await _chunks.Reader.ReadAsync(ct);
            lock (_returned) _returned.Add(chunk);
            return chunk;
        }

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public event Action? Disconnected { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    /// <summary>A byte corpus that exercises rendition, modes, wrapping and retained history (evidence P5).</summary>
    private static class Corpus
    {
        public static readonly string[] Chunks =
        [
            "\u001b[1;31mred bold\u001b[0m plain \u001b[4:3munder\u001b[24m \u001b[38;2;1;2;3mrgb\u001b[m\r\n",
            "\u001b]0;TITLE\u0007\u001b]8;;https://x.test\u001b\\link\u001b]8;;\u001b\\ tab\tstop\r\n",
            "\u001b[3;8r\u001b[5;1Hin region\u001b[r\u001b[10;1H" + string.Concat(Enumerable.Range(1, 30).Select(i => $"scrolled line {i}\r\n")),
            "漢字 é é 👩‍💻 wide and combining, then a long line that wraps and wraps and wraps\r\n",
        ];

        public static async Task<string> FeedAsync(int width, int height, int? scrollback)
        {
            var workload = new ScriptedWorkload();
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(width, height);
            if (scrollback is { } capacity)
                builder.WithScrollback(capacity);
            await using var terminal = builder.Build();
            using (new Running(terminal))
            {
                foreach (var chunk in Chunks)
                    await workload.WriteAndWaitAsync(terminal, chunk);
                return Digest(terminal);
            }
        }

        // Geometry, cursor, modes, title, retained history and every cell's content fields.
        public static string Digest(Hex1bTerminal terminal)
        {
            using var s = terminal.CreateSnapshot();
            var b = new StringBuilder($"{s.Width}x{s.Height} {s.CursorX},{s.CursorY} {s.CursorVisible} {s.InAlternateScreen} {s.WindowTitle} history={terminal.ScrollbackCount}\n");
            for (var y = 0; y < s.Height; y++)
            {
                for (var x = 0; x < s.Width; x++)
                {
                    var c = s.GetCell(x, y);
                    b.Append(c.Character).Append('/').Append(Color(c.Foreground)).Append('/').Append(Color(c.Background))
                        .Append('/').Append(c.Attributes).Append('/').Append(c.TrackedHyperlink?.Data.Uri).Append('|');
                }
                b.Append('\n');
            }
            return b.ToString();
        }

        private static string Color(Hex1b.Theming.Hex1bColor? color) =>
            color is not { } c ? "-" : c.IsDefault ? "default" : $"{c.Kind}:{c.R},{c.G},{c.B}:{c.AnsiIndex}";
    }
}
