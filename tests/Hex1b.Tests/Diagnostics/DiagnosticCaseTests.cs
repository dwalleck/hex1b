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
    public async Task ConstructionStart_ArmsBeforeThePumpsRead()
    {
        // Output is ready before Build, and the pump is given every chance to apply it before the case
        // arms: a construction start holds the pumps until the case is armed, so it stays fresh.
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        workload.Enqueue("ready before build"u8.ToArray());
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeArmForTesting.Value = () =>
        {
            SpinWait.SpinUntil(() => workload.Returned().Count > 0, TimeSpan.FromMilliseconds(500));
            Thread.Sleep(200);
        };
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeArmForTesting.Value = null;
        }

        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            Assert.AreEqual(DiagnosticCaseCheckpointStatus.Complete, diagnostics.GetCaseStatus().Checkpoint!.Status);
            await WaitAsync(() => terminal.CurrentModelSequence >= 1);
            var stopped = await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            Assert.AreEqual(1L, Artifact.Read(stopped.Path!).ModelEvents()[0].GetProperty("modelSequence").GetInt64(),
                "the output ready before Build is not the case's first model event");
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

    [TestMethod]
    public async Task Unsupported_EndsIntervalBeforeLaterEvents()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var sixel = "\u001bP0;0;0q#0;2;100;0;0#0~~~~~~-\u001b\\"u8.ToArray();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "text ");
            await workload.WriteAndWaitAsync(terminal, sixel);
            Assert.IsTrue(terminal.TrackedSixelCount > 0 || terminal.KgpVirtualPlacementCount > 0, "fixture: the Sixel left no graphics state");
            await workload.WriteAndWaitAsync(terminal, "after one ");
            await workload.WriteAndWaitAsync(terminal, "after two");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var artifact = Artifact.Read(stopped.Path!);
        var graphicsEvent = artifact.ModelEvents().Single(e => e.TryGetProperty("data", out var d) && Convert.FromBase64String(d.GetString()!).SequenceEqual(sixel));
        var graphicsSequence = graphicsEvent.GetProperty("modelSequence").GetInt64();
        var endIndex = artifact.Events.FindIndex(e => e.GetProperty("stream").GetString() == "case" && e.GetProperty("kind").GetString() == "interval-end");
        Assert.IsGreaterThanOrEqualTo(0, endIndex, "no interval end was recorded");
        Assert.AreEqual((graphicsSequence, "graphics"), (artifact.Events[endIndex].GetProperty("modelSequence").GetInt64(),
            artifact.Events[endIndex].GetProperty("record").GetProperty("reason").GetString()));
        var laterModel = artifact.Events.Select((e, i) => (e, i)).Where(p => p.e.GetProperty("stream").GetString() == "model"
            && p.e.GetProperty("modelSequence").GetInt64() > graphicsSequence).ToList();
        Assert.HasCount(2, laterModel, "collection must continue after the interval ends");
        Assert.IsTrue(laterModel.All(p => p.i > endIndex), "a later model event was written before the interval end");

        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        Assert.AreEqual((true, 0L, graphicsSequence - 1, "graphics"),
            (inspection.Intervals.Single().Valid, inspection.Intervals.Single().FromModelSequence, inspection.Intervals.Single().ToModelSequence, inspection.Intervals.Single().EndReason));

        // A model application the case holds no input for (the terminal's own API) also ends the interval.
        var plain = new ScriptedWorkload();
        await using var withoutIngress = Hex1bTerminal.CreateBuilder().WithWorkload(plain).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        using (new Running(withoutIngress))
        {
            await plain.WriteAndWaitAsync(withoutIngress, "before");
            withoutIngress.EnterAlternateScreen();
            await plain.WriteAndWaitAsync(withoutIngress, "after");
            stopped = await new TerminalDiagnostics(withoutIngress).StopCaseAsync(TestContext.Current.CancellationToken);
        }
        var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! }).Intervals.Single();
        Assert.AreEqual((1L, "application-without-ingress"), (interval.ToModelSequence, interval.EndReason));

        // A remote (HMP1) workload's model is driven by state the case cannot hold: no valid interval at all.
        await using var remote = Hex1bTerminal.CreateBuilder().WithWorkload(new FakeHmp1Workload()).WithHeadless().WithDimensions(40, 10).Build();
        var remoteCase = new TerminalDiagnostics(remote).StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
        await new TerminalDiagnostics(remote).StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual(DiagnosticCaseCheckpointStatus.Unsupported, remoteCase.Checkpoint!.Status);
        StringAssert.StartsWith(remoteCase.Checkpoint.Reason, "hmp1-workload");
        Assert.IsFalse(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = remoteCase.Path! }).Intervals.Single().Valid);
    }

    [TestMethod]
    public async Task InputStream_RecordsAcceptsAndProcessingWithIds()
    {
        using var root = new CaseRoot();
        foreach (var rawInput in new[] { false, true })
        {
            await using var app = await TrackedApp.StartAsync();
            var started = app.Diagnostics.StartCase(new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                Authorizations = rawInput ? [DiagnosticAuthorization.RawInput] : [],
            });
            var accepted = await app.Diagnostics.TrackSendAsync(() => app.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("xyz")), "text");
            Assert.IsNotNull(accepted, "fixture: the send was not tracked");
            await WaitAsync(() => app.Terminal.InputMilestones!.ProcessedInput >= accepted.LastId);
            await app.Diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

            var input = Artifact.Read(started.Path!).Events.Where(e => e.GetProperty("stream").GetString() == "input").ToList();
            var acceptedIds = input.Where(e => e.GetProperty("kind").GetString() == "accepted").Select(e => e.GetProperty("input").GetProperty("id").GetInt64()).ToList();
            var processedIds = input.Where(e => e.GetProperty("kind").GetString() == "processed").Select(e => e.GetProperty("input").GetProperty("id").GetInt64()).ToList();
            var range = Enumerable.Range((int)accepted.FirstId, (int)(accepted.LastId - accepted.FirstId + 1)).Select(i => (long)i).ToList();
            CollectionAssert.IsSubsetOf(range, acceptedIds, $"rawInput={rawInput}: accepted ids missing");
            CollectionAssert.IsSubsetOf(range, processedIds, $"rawInput={rawInput}: processed ids missing");
            var payloads = input.Where(e => e.GetProperty("input").TryGetProperty("payload", out _)).ToList();
            Assert.AreEqual(rawInput, payloads.Count > 0, $"rawInput={rawInput}: key payloads");
        }
    }

    [TestMethod]
    public async Task FrameStream_RecordsEachPublishedFrame()
    {
        using var root = new CaseRoot();
        foreach (var editorText in new[] { false, true })
        {
            await using var app = await TrackedApp.StartAsync();
            var started = app.Diagnostics.StartCase(new DiagnosticCaseStartRequest
            {
                Directory = root.Path,
                Authorizations = editorText ? [DiagnosticAuthorization.EditorText] : [],
            });
            var captured = new List<long>();
            var before = app.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame!.FrameId;
            for (var i = 0; i < 5; i++)
            {
                app.App.Invalidate();
                await WaitAsync(() => app.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame?.FrameId > before);
                before = app.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame!.FrameId;
                captured.Add(before);
            }
            await app.Diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

            var frames = Artifact.Read(started.Path!).Events.Where(e => e.GetProperty("stream").GetString() == "frames").ToList();
            var ids = frames.Select(e => e.GetProperty("frame").GetProperty("frameId").GetInt64()).ToList();
            CollectionAssert.IsSubsetOf(captured, ids, $"editorText={editorText}: a published frame was not recorded");
            CollectionAssert.AreEqual(ids.OrderBy(i => i).ToList(), ids, "frames out of order");
            var texts = frames.Select(e => e.GetProperty("frame").GetProperty("projection").TryGetProperty("focusedEditor", out var fe)
                && fe.TryGetProperty("text", out var t) ? t.GetString() : null).ToList();
            Assert.AreEqual(editorText, texts.Any(t => t == TrackedApp.EditorSentinel), $"editorText={editorText}: focused editor text");
        }

        // A raw-byte workload publishes no frames: the stream is declared unavailable.
        await using var raw = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(20, 5).Build();
        var rawCase = new TerminalDiagnostics(raw).StartCase(new DiagnosticCaseStartRequest { Directory = root.Path });
        await new TerminalDiagnostics(raw).StopCaseAsync(TestContext.Current.CancellationToken);
        var declaration = Artifact.Read(rawCase.Path!).Manifest.GetProperty("streams").EnumerateArray().Single(e => e.GetProperty("stream").GetString() == "frames");
        Assert.AreEqual("unavailable", declaration.GetProperty("events").GetString());
    }

    [TestMethod]
    public async Task DeliveryStream_RecordsNativeWritesAndEvictions()
    {
        using var root = new CaseRoot();
        foreach (var nativeOutput in new[] { false, true })
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10).Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            DiagnosticCaseResult started;
            using (new Running(terminal))
            {
                var baseline = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0;
                started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = nativeOutput ? [DiagnosticAuthorization.NativeOutput] : [] });
                for (var i = 0; i < 10; i++)
                    await workload.WriteAndWaitAsync(terminal, $"DLV-{i} ");
                await Task.Delay(100, TestContext.Current.CancellationToken);
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                var expected = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest { Since = baseline }).Records.Select(r => r.Sequence).ToList();
                var delivery = Artifact.Read(started.Path!).Events.Where(e => e.GetProperty("stream").GetString() == "delivery").ToList();
                var recorded = delivery.Select(e => e.GetProperty("ordinal").GetInt64()).ToList();
                CollectionAssert.IsSubsetOf(recorded, expected, "a recorded delivery is not one the terminal made");
                Assert.IsGreaterThanOrEqualTo(10, recorded.Count, "fixture: too few deliveries");
                Assert.AreEqual(nativeOutput, delivery.Any(e => e.GetProperty("delivery").TryGetProperty("content", out _)), $"nativeOutput={nativeOutput}: bytes");
            }
        }

        // Records evicted from the delivery ring while the writer is held become a missing range.
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10).Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                var started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path });
                var before = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0;
                for (var i = 0; i < 5000; i++)
                    workload.Enqueue(Encoding.ASCII.GetBytes($"E{i} "));
                await WaitAsync(() => (diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0) >= before + 5000);
                gate.Set();
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                var totalAfter = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence!.Value;
                var artifact = Artifact.Read(started.Path!);
                var missing = artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing"
                    && e.GetProperty("record").GetProperty("stream").GetString() == "delivery").ToList();
                Assert.HasCount(1, missing, "the eviction was not recorded once");
                var record = missing[0].GetProperty("record");
                var evicted = record.GetProperty("toOrdinal").GetInt64() - record.GetProperty("fromOrdinal").GetInt64() + 1;
                var kept = artifact.Events.Count(e => e.GetProperty("stream").GetString() == "delivery");
                Assert.AreEqual(totalAfter - before, evicted + kept, "missing plus recorded deliveries do not account for every write");
                Assert.AreEqual("incomplete", DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = started.Path! })
                    .Streams.Single(s => s.Stream == "delivery").State);
            }
        }
        finally
        {
            gate.Set();
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Inspect_MatchesAnIndependentReadingOfTheFiles()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            for (var i = 0; i < 12; i++)
                await workload.WriteAndWaitAsync(terminal, $"line {i}\r\n");
            terminal.Resize(30, 8);
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        // Oracle: the test's own reading of the raw files.
        var artifact = Artifact.Read(stopped.Path!);
        var model = artifact.ModelEvents();
        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path!, Since = 0, Limit = DiagnosticCaseInspector.MaxEvents });
        Assert.AreEqual(DiagnosticOutcome.Captured, inspection.Outcome, inspection.Problem?.Message);
        Assert.AreEqual(DiagnosticCaseCompletionState.Complete, inspection.CompletionState);
        Assert.AreEqual(artifact.Events[^1].GetProperty("caseSequence").GetInt64(), inspection.LastCaseSequence);
        var modelCoverage = inspection.Streams.Single(s => s.Stream == "model");
        Assert.AreEqual(((long)model.Count, 1L, (long)model.Count, "complete"), (modelCoverage.Events, modelCoverage.FirstOrdinal, modelCoverage.LastOrdinal, modelCoverage.State));
        var interval = inspection.Intervals.Single();
        Assert.AreEqual((true, 0L, model[^1].GetProperty("modelSequence").GetInt64(), "case-stopped: requested"),
            (interval.Valid, interval.FromModelSequence, interval.ToModelSequence, interval.EndReason));
        CollectionAssert.AreEqual(artifact.Events.Select(e => e.GetProperty("caseSequence").GetInt64()).ToList(), inspection.Events.Select(e => e.CaseSequence).ToList());
        CollectionAssert.AreEqual(model.Select(e => e.TryGetProperty("data", out var d) ? d.GetString() : null).ToList(),
            inspection.Events.Where(e => e.Stream == "model").Select(e => e.Data).ToList(), "payloads differ from the file");

        // Paging.
        Assert.AreEqual(1L, DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path!, Limit = 1 }).Events.Single().CaseSequence);
        Assert.IsEmpty(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path!, Since = inspection.LastCaseSequence, Limit = 10 }).Events);
        Assert.IsEmpty(DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! }).Events, "no limit returns no events");
        foreach (var (request, code) in new (DiagnosticCaseInspectRequest, string)[]
        {
            (new() { Path = stopped.Path!, Limit = 0 }, "invalid-limit"),
            (new() { Path = stopped.Path!, Limit = 4097 }, "invalid-limit"),
            (new() { Path = stopped.Path!, Since = -1 }, "invalid-since"),
            (new() { Path = "" }, "invalid-path"),
        })
            Assert.AreEqual((DiagnosticOutcome.InvalidRequest, code), (DiagnosticCaseInspector.Inspect(request).Outcome, DiagnosticCaseInspector.Inspect(request).Problem?.Code), code);
        Assert.AreEqual("case-not-found", DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = Path.Combine(root.Path, "absent") }).Problem?.Code);

        // An artifact whose completion record is missing was interrupted; its verified prefix remains valid.
        var copy = Path.Combine(root.Path, "interrupted");
        Directory.CreateDirectory(copy);
        foreach (var file in new[] { "manifest.json", "events.jsonl" })
            File.Copy(Path.Combine(stopped.Path!, file), Path.Combine(copy, file));
        var interrupted = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = copy });
        Assert.AreEqual((DiagnosticCaseCompletionState.Interrupted, "interrupted"), (interrupted.CompletionState, interrupted.Intervals.Single().EndReason));

        // A case that did not start fresh has no valid interval.
        var late = new ScriptedWorkload();
        await using var nonFresh = Hex1bTerminal.CreateBuilder().WithWorkload(late).WithHeadless().WithDimensions(40, 10).Build();
        using (new Running(nonFresh))
        {
            await late.WriteAndWaitAsync(nonFresh, "before");
            var liveCase = new TerminalDiagnostics(nonFresh).StartCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
            await late.WriteAndWaitAsync(nonFresh, "after");
            await new TerminalDiagnostics(nonFresh).StopCaseAsync(TestContext.Current.CancellationToken);
            var notFresh = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = liveCase.Path! }).Intervals.Single();
            Assert.IsFalse(notFresh.Valid, "a case that did not start fresh reported a re-applicable interval");
            StringAssert.StartsWith(notFresh.EndReason, "checkpoint unsupported");
        }

        // Unreadable and unknown-format manifests.
        var broken = Path.Combine(root.Path, "broken");
        Directory.CreateDirectory(broken);
        Assert.AreEqual("invalid-artifact", DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = broken }).Problem?.Code);
        File.WriteAllText(Path.Combine(broken, "manifest.json"), File.ReadAllText(Path.Combine(stopped.Path!, "manifest.json")).Replace("\"formatVersion\":1", "\"formatVersion\":2"));
        Assert.AreEqual("unsupported-format", DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = broken }).Problem?.Code);
    }

    /// <summary>A diagnostics-enabled Hex1b application on a headless terminal, with a focused editor.</summary>
    private sealed class TrackedApp : IAsyncDisposable
    {
        public const string EditorSentinel = "ZQX-EDITOR-SENTINEL";
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _terminalRun;
        private readonly Task _appRun;

        private TrackedApp(Hex1bTerminal terminal, Hex1bApp app)
        {
            Terminal = terminal;
            App = app;
            Diagnostics = new TerminalDiagnostics(terminal);
            _terminalRun = terminal.RunAsync(_cts.Token);
            _appRun = app.RunAsync(_cts.Token);
        }

        public Hex1bTerminal Terminal { get; }
        public Hex1bApp App { get; }
        public TerminalDiagnostics Diagnostics { get; }

        public static async Task<TrackedApp> StartAsync(DiagnosticCaseStartRequest? recordFromConstruction = null)
        {
            var workload = new Hex1bAppWorkloadAdapter { DiagnosticTimingEnabled = true };
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 8);
            var terminal = (recordFromConstruction is null ? builder : builder.WithDiagnosticCase(recordFromConstruction)).Build();
            var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(ctx.VStack(v => [v.Text("Editor:"), v.TextBox(EditorSentinel)])),
                new Hex1bAppOptions { WorkloadAdapter = workload });
            var tracked = new TrackedApp(terminal, app);
            await WaitAsync(() => tracked.Diagnostics.CaptureApplicationFrame(new DiagnosticApplicationFrameRequest()).Frame is not null);
            return tracked;
        }

        public async ValueTask DisposeAsync()
        {
            await _cts.CancelAsync();
            try { await _appRun; } catch (OperationCanceledException) { }
            try { await _terminalRun; } catch (OperationCanceledException) { }
            App.Dispose();
            await Terminal.DisposeAsync();
            _cts.Dispose();
        }
    }

    /// <summary>A remote (HMP1-shaped) workload that never produces output; only its kind matters.</summary>
    private sealed class FakeHmp1Workload : IHex1bTerminalWorkloadAdapter, IHmp1TerminalOutputSource
    {
        public Hmp1WorkloadAdapter? Hmp1Workload => null;
        public async ValueTask<Hmp1WorkloadOutput> ReadTerminalOutputAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            throw new OperationCanceledException();
        }
        public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        {
            await Task.Delay(Timeout.Infinite, ct);
            return ReadOnlyMemory<byte>.Empty;
        }
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public event Action? Disconnected { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [TestMethod]
    public async Task Overload_PumpNeverWaits()
    {
        using var root = new CaseRoot();
        var chunks = Enumerable.Range(0, 20_000).Select(i => Encoding.ASCII.GetBytes($"{i % 97} ")).ToArray();
        async Task<(string Presentation, string Model)> RunAsync(bool armed, ManualResetEventSlim? gate)
        {
            var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
            var workload = new ScriptedWorkload();
            var builder = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10);
            if (armed)
                builder.WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
            await using var terminal = builder.Build();
            using (new Running(terminal))
            {
                try
                {
                    var total = chunks.Sum(c => (long)c.Length);
                    foreach (var chunk in chunks)
                        workload.Enqueue(chunk);
                    // The writer is held for the whole run: the pump must still read and apply everything.
                    await WaitAsync(() => terminal.OutputBytesRead == total);
                    await Settle(terminal);
                    var result = (driver.WrittenText, Corpus.Digest(terminal));
                    if (armed)
                    {
                        var status = new TerminalDiagnostics(terminal).GetCaseStatus();
                        Assert.AreEqual(0L, status.Streams.Single(s => s.Stream == "model").Written, "fixture: the writer was not held");
                    }
                    return result;
                }
                finally
                {
                    // Released before disposal, so a pump that did wait on the writer can finish and report.
                    gate?.Set();
                }
            }
        }

        var unarmed = await RunAsync(armed: false, gate: null);
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var armed = await RunAsync(armed: true, gate);
            Assert.AreEqual(unarmed.Model, armed.Model, "overload changed the model");
            Assert.AreEqual(unarmed.Presentation, armed.Presentation, "overload changed the presentation output");
        }
        finally
        {
            gate.Set();
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Overload_DropNewestAccounted()
    {
        using var root = new CaseRoot();
        var artifact = await RecordHeldAsync(root, Enumerable.Range(0, 5000).Select(i => Encoding.ASCII.GetBytes($"c{i} ")).ToArray(), payloads: false);
        var ordinals = artifact.ModelEvents().Select(e => e.GetProperty("ordinal").GetInt64()).ToList();
        var missing = MissingOrdinals(artifact, "model");

        CollectionAssert.AreEqual(Enumerable.Range(1, CaseEventQueueMax).Select(i => (long)i).ToList(), ordinals.Take(CaseEventQueueMax).ToList(),
            "the queue did not keep the oldest events (drop-newest)");
        var dropped = Enumerable.Range(CaseEventQueueMax + 1, 5000 - CaseEventQueueMax).Select(i => (long)i).ToList();
        CollectionAssert.AreEqual(dropped, missing, "the recorded missing ranges are not exactly the dropped events");
        CollectionAssert.AreEquivalent(ordinals.Concat(missing).ToList(), Enumerable.Range(1, 5000).Select(i => (long)i).ToList(), "an event is neither recorded nor missing");

    }

    [TestMethod]
    public void Queue_RefusesPastTheByteBound()
    {
        // The model cannot apply 8 MiB of input quickly enough for an end-to-end test, so the byte
        // bound is exercised on the queue itself; the event bound is exercised end to end above.
        static Hex1b.Diagnostics.Cases.CaseEvent Event(long ordinal, int payload) =>
            new(Hex1b.Diagnostics.Cases.CaseStream.Model, ordinal, 0, "application", ordinal, 40, 10, payload, new byte[payload]);

        var queue = new Hex1b.Diagnostics.Cases.CaseEventQueue();
        Assert.IsFalse(queue.TryEnqueue(Event(1, 9 * 1024 * 1024)), "an event over the byte bound was queued into an empty queue");
        var accepted = 0;
        for (var i = 0; i < 10; i++)
            accepted += queue.TryEnqueue(Event(i + 2, 1024 * 1024)) ? 1 : 0;
        Assert.AreEqual(7, accepted, "eight 1 MiB events plus their overhead exceed 8 MiB; seven fit");
        Assert.IsLessThanOrEqualTo(Hex1b.Diagnostics.Cases.CaseEventQueue.MaxBytes, queue.Bytes);
        Assert.IsTrue(queue.TryDequeue(out var oldest));
        Assert.AreEqual(2L, oldest.Ordinal, "the oldest event was not kept");
        Assert.IsTrue(queue.TryEnqueue(Event(99, 1024 * 1024)), "space freed by the writer was not reusable");
    }

    [TestMethod]
    public async Task Loss_PersistsOutsideQueue()
    {
        using var root = new CaseRoot();
        // The queue is full from the first dropped event on, so no queued notice could have carried the loss.
        var artifact = await RecordHeldAsync(root, Enumerable.Range(0, CaseEventQueueMax + 700).Select(i => Encoding.ASCII.GetBytes($"q{i} ")).ToArray(), payloads: true);
        var records = artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing").ToList();
        Assert.IsNotEmpty(records, "the loss was not persisted");
        Assert.IsTrue(records.All(e => e.GetProperty("record").GetProperty("reason").GetString() == "overload"));
        Assert.HasCount(700, MissingOrdinals(artifact, "model"));

        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = artifact.Path });
        Assert.AreEqual("incomplete", inspection.Streams.Single(s => s.Stream == "model").State);
        Assert.AreEqual(((long)CaseEventQueueMax, "overload"), (inspection.Intervals.Single().ToModelSequence!.Value, inspection.Intervals.Single().EndReason),
            "re-application crossed the lost events");
    }

    [TestMethod]
    public async Task Reader_DetectsOrdinalGap()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            for (var i = 0; i < 6; i++)
                await workload.WriteAndWaitAsync(terminal, $"g{i} ");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        // Remove the third model event's line, keeping every remaining line intact and verified.
        var events = Path.Combine(stopped.Path!, "events.jsonl");
        var lines = File.ReadAllLines(events).ToList();
        var third = lines.FindIndex(l => l.Contains("\"stream\":\"model\"", StringComparison.Ordinal) && l.Contains("\"ordinal\":3,", StringComparison.Ordinal));
        Assert.IsGreaterThanOrEqualTo(0, third, "fixture: no third model event");
        lines.RemoveAt(third);
        File.WriteAllLines(events, lines);

        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        var model = inspection.Streams.Single(s => s.Stream == "model");
        Assert.AreEqual("incomplete", model.State, "a missing line went unnoticed");
        Assert.AreEqual((3L, 3L, "unknown"), (model.Missing.Single().FromOrdinal!.Value, model.Missing.Single().ToOrdinal!.Value, model.Missing.Single().Reason));
        Assert.AreEqual((2L, "model-events-missing"), (inspection.Intervals.Single().ToModelSequence!.Value, inspection.Intervals.Single().EndReason));
    }

    [TestMethod]
    public void Loss_CapBecomesUnknownExtent()
    {
        var ledger = new Hex1b.Diagnostics.Cases.CaseLossLedger();
        const int Ranges = Hex1b.Diagnostics.Cases.CaseLossLedger.MaxRangesPerStream + 76;
        // Every other ordinal lost: each loss is its own range.
        for (var i = 0; i < Ranges; i++)
            ledger.Record(Hex1b.Diagnostics.Cases.CaseStream.Model, 2L * i + 1);

        var records = ledger.Take(final: true);
        var closed = records.Where(r => r.ToOrdinal is not null).ToList();
        var unknown = records.Where(r => r.ToOrdinal is null).ToList();
        Assert.HasCount(Hex1b.Diagnostics.Cases.CaseLossLedger.MaxRangesPerStream, closed, "the ledger grew past its cap");
        Assert.AreEqual((2L * Hex1b.Diagnostics.Cases.CaseLossLedger.MaxRangesPerStream + 1, "overload-unknown-extent"),
            (unknown.Single().FromOrdinal!.Value, unknown.Single().Reason), "loss past the cap was not reported as unknown extent");
        Assert.IsEmpty(ledger.Take(final: true), "a range was reported twice");
    }

    [TestMethod]
    public async Task Status_ReportsProgressAndLoss()
    {
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                const int Chunks = 4500;
                var total = 0L;
                for (var i = 0; i < Chunks; i++)
                {
                    var chunk = Encoding.ASCII.GetBytes($"s{i} ");
                    total += chunk.Length;
                    workload.Enqueue(chunk);
                }
                await WaitAsync(() => terminal.OutputBytesRead == total);
                await Settle(terminal);

                var held = diagnostics.GetCaseStatus();
                var model = held.Streams.Single(s => s.Stream == "model");
                Assert.AreEqual((DiagnosticCaseState.Recording, (long)Chunks, 0L, (long)(Chunks - CaseEventQueueMax)),
                    (held.State!.Value, model.Offered, model.Written, model.Dropped), "status while the writer is held");
                Assert.AreEqual(new FileInfo(Path.Combine(held.Path!, "manifest.json")).Length, held.BytesWritten,
                    "only the manifest may be written while the writer is held");
                Assert.IsGreaterThanOrEqualTo(0.0, held.ElapsedSeconds!.Value);

                gate.Set();
                await WaitAsync(() => diagnostics.GetCaseStatus().Streams.Single(s => s.Stream == "model").Written == CaseEventQueueMax);
                Assert.IsGreaterThan(0L, diagnostics.GetCaseStatus().BytesWritten!.Value);
                await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
        }
        finally
        {
            gate.Set();
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Limits_Size()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(200, 50)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = 1024 * 1024, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
        using (new Running(terminal))
        {
            var chunk = Enumerable.Repeat((byte)'s', 16 * 1024).ToArray();
            for (var i = 0; i < 128 && new TerminalDiagnostics(terminal).GetCaseStatus().Outcome == DiagnosticOutcome.Captured; i++)
                await workload.WriteAndWaitAsync(terminal, chunk);
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
        }

        var artifact = Artifact.Read(path);
        var bytes = Directory.GetFiles(path).Sum(f => new FileInfo(f).Length);
        Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        Assert.IsLessThanOrEqualTo(1024L * 1024, bytes, $"the artifact holds {bytes} bytes against a 1 MiB bound");
        Assert.AreEqual(artifact.Events[^1].GetProperty("caseSequence").GetInt64(), artifact.Completion.Value.GetProperty("lastCaseSequence").GetInt64(),
            "the completion does not name the last retained event");
        Assert.IsGreaterThan(20, artifact.ModelEvents().Count, "fixture: too little was recorded before the bound");
    }

    [TestMethod]
    public async Task Limits_SizeCrossedInsideADeliveryPull()
    {
        using var root = new CaseRoot();

        // With the writer held, 3,000 writes queue their model events and fill the delivery ring. On release the
        // writer drains the model events, then pulls every delivery record in one snapshot.
        async Task<(string Path, long First, long Last)> RecordAsync(long? maxBytes)
        {
            using var gate = new ManualResetEventSlim(false);
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
            try
            {
                var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
                var workload = new ScriptedWorkload();
                await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                    .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10).Build();
                var diagnostics = new TerminalDiagnostics(terminal);
                using (new Running(terminal))
                {
                    var started = diagnostics.StartCase(new DiagnosticCaseStartRequest
                    {
                        Directory = root.Path,
                        MaxBytes = maxBytes,
                        Authorizations = [DiagnosticAuthorization.ReapplicationData, DiagnosticAuthorization.NativeOutput],
                    });
                    var before = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0;
                    for (var i = 0; i < 3000; i++)
                        workload.Enqueue(Encoding.ASCII.GetBytes($"P{i:D5} ".PadRight(100, 'x')));
                    await WaitAsync(() => (diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0) >= before + 3000);
                    var last = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence!.Value;
                    gate.Set();
                    // A bounded case ends itself; a requested stop would win the stop reason.
                    if (maxBytes is null)
                        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                    await WaitAsync(() => File.Exists(Path.Combine(started.Path!, "completion.json")));
                    return (started.Path!, before + 1, last);
                }
            }
            finally
            {
                gate.Set();
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            }
        }

        long LineBytes(string path, string stream) => File.ReadAllLines(Path.Combine(path, "events.jsonl"))
            .Where(l => l.Contains($"\"stream\":\"{stream}\"", StringComparison.Ordinal)).Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1);

        var measured = await RecordAsync(null);
        var manifest = new FileInfo(Path.Combine(measured.Path, "manifest.json")).Length;
        var bound = manifest + LineBytes(measured.Path, "model") + LineBytes(measured.Path, "delivery") / 2 + 16 * 1024;
        Assert.IsGreaterThanOrEqualTo(Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.MinMaxBytes, bound, "fixture: too few bytes for a legal bound");

        var (path, first, last) = await RecordAsync(bound);
        var artifact = Artifact.Read(path);
        Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var kept = artifact.Events.Where(e => e.GetProperty("stream").GetString() == "delivery").Select(e => e.GetProperty("ordinal").GetInt64()).ToList();
        Assert.IsGreaterThan(0, kept.Count, "fixture: the bound was crossed before any delivery was written");
        Assert.IsLessThan(last - first + 1, (long)kept.Count, "fixture: the bound was not crossed inside the delivery pull");
        var missing = MissingOrdinals(artifact, "delivery");
        CollectionAssert.AreEqual(Enumerable.Range(0, (int)(last - first + 1)).Select(i => first + i).ToList(), kept.Concat(missing).Order().ToList(),
            "every delivery the pull took is either written or declared missing");
    }

    [TestMethod]
    public async Task Limits_Time()
    {
        using var root = new CaseRoot();
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(20, 5)
            .WithTimeProvider(clock).WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxSeconds = 1 }).Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;

        clock.Advance(TimeSpan.FromMilliseconds(900));
        await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.AreEqual(DiagnosticCaseState.Recording, diagnostics.GetCaseStatus().State, "the case stopped before its time bound");
        clock.Advance(TimeSpan.FromMilliseconds(100));
        await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));

        Assert.AreEqual("time-limit", Artifact.Read(path).Completion!.Value.GetProperty("stopReason").GetString());
        Assert.AreEqual("no-active-case", diagnostics.GetCaseStatus().Problem?.Code);
    }

    [TestMethod]
    public async Task Stop_DrainBounded()
    {
        Assert.AreEqual(TimeSpan.FromSeconds(10), Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeout, "the drain bound is 10 s (spec Q11)");
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = TimeSpan.FromSeconds(1);
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            using (new Running(terminal))
            {
                for (var i = 0; i < 100; i++)
                    await workload.WriteAndWaitAsync(terminal, $"d{i} ");

                var watch = System.Diagnostics.Stopwatch.StartNew();
                var stop = diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                var finished = await Task.WhenAny(stop, Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken)) == stop;
                gate.Set();
                Assert.IsTrue(finished, "stop waited on a stalled writer past its bound");
                Assert.IsLessThan(TimeSpan.FromSeconds(5), watch.Elapsed);
                var result = await stop;
                Assert.AreEqual(DiagnosticCaseStopReason.Requested, result.StopReason);

                await WaitAsync(() => File.Exists(Path.Combine(result.Path!, "completion.json")));
                var artifact = Artifact.Read(result.Path!);
                Assert.IsEmpty(artifact.ModelEvents(), "events were written after the drain was abandoned");
                CollectionAssert.AreEqual(Enumerable.Range(1, 100).Select(i => (long)i).ToList(), MissingOrdinals(artifact, "model"),
                    "the unwritten tail is not exactly the missing range");
                Assert.IsTrue(artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing")
                    .All(e => e.GetProperty("record").GetProperty("reason").GetString() == "drain-timeout"));
                Assert.AreEqual(artifact.Events[^1].GetProperty("caseSequence").GetInt64(), artifact.Completion!.Value.GetProperty("lastCaseSequence").GetInt64());
            }
        }
        finally
        {
            gate.Set();
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = null;
        }
    }

    [TestMethod]
    public async Task Failures_WriterAndDisposal()
    {
        using var root = new CaseRoot();
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = new IOException("injected storage failure");
        string failedPath;
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
            var diagnostics = new TerminalDiagnostics(terminal);
            failedPath = diagnostics.GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "fails");
                await WaitAsync(() => diagnostics.GetCaseStatus().Problem?.Code == "no-active-case");
                // A stop after the failure returns at once.
                Assert.AreEqual("no-active-case", (await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken)).Problem?.Code);
            }
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
        }
        Assert.AreEqual("collector-failed", Artifact.Read(failedPath).Completion!.Value.GetProperty("stopReason").GetString());

        var disposedWorkload = new ScriptedWorkload();
        var disposed = Hex1bTerminal.CreateBuilder().WithWorkload(disposedWorkload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        var disposedPath = new TerminalDiagnostics(disposed).GetCaseStatus().Path!;
        using (new Running(disposed))
            await disposedWorkload.WriteAndWaitAsync(disposed, "before disposal");
        await disposed.DisposeAsync();
        await WaitAsync(() => File.Exists(Path.Combine(disposedPath, "completion.json")));
        Assert.AreEqual("target-disposed", Artifact.Read(disposedPath).Completion!.Value.GetProperty("stopReason").GetString());
    }

    [TestMethod]
    public async Task StreamFailure_Isolated()
    {
        using var root = new CaseRoot();
        foreach (var faulted in new[] { "frames", "model" })
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.StreamFaultForTesting.Value = faulted;
            DiagnosticCaseResult started;
            try
            {
                // Started at construction, so the model interval is valid until the failure ends it.
                await using var app = await TrackedApp.StartAsync(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] });
                started = app.Diagnostics.GetCaseStatus();
                app.App.Invalidate();
                await Task.Delay(200, TestContext.Current.CancellationToken);
                var accepted = await app.Diagnostics.TrackSendAsync(() => app.Terminal.SendInputAsync(Encoding.UTF8.GetBytes("ok")), "text");
                await WaitAsync(() => app.Terminal.InputMilestones!.ProcessedInput >= accepted!.LastId);
                await app.Diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            }
            finally
            {
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.StreamFaultForTesting.Value = null;
            }

            var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = started.Path! });
            Assert.AreEqual("failed", inspection.Streams.Single(s => s.Stream == faulted).State, $"{faulted}: the failed stream");
            Assert.AreEqual(DiagnosticCaseCompletionState.Complete, inspection.CompletionState, $"{faulted}: one stream's failure ended the case");
            var input = inspection.Streams.Single(s => s.Stream == "input");
            Assert.AreEqual("complete", input.State, $"{faulted}: input stopped with the other stream");
            Assert.IsGreaterThan(0L, input.Events, $"{faulted}: input was not recorded after the failure");
            if (faulted == "model")
            {
                Assert.AreEqual("stream-failed", inspection.Intervals.Single().EndReason);
                // The app registered after the case started at construction; its frames still reach the case.
                var frames = inspection.Streams.Single(s => s.Stream == "frames");
                Assert.AreEqual(("complete", true), (frames.State, frames.Events > 0), "frames were not recorded after the model stream failed");
            }
            else
                Assert.StartsWith("case-stopped", inspection.Intervals.Single().EndReason, "a frames failure ended the model interval");
        }
    }

    [TestMethod]
    public async Task Reader_Truncation()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            for (var i = 0; i < 8; i++)
                await workload.WriteAndWaitAsync(terminal, $"truncate-me-{i} ");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        string Copy(string name)
        {
            var copy = Path.Combine(root.Path, name);
            Directory.CreateDirectory(copy);
            foreach (var file in Directory.GetFiles(stopped.Path!))
                File.Copy(file, Path.Combine(copy, Path.GetFileName(file)));
            return copy;
        }

        var lines = File.ReadAllLines(Path.Combine(stopped.Path!, "events.jsonl"));

        // A torn final line: everything before it is the verified prefix.
        var torn = Copy("torn");
        var tornEvents = Path.Combine(torn, "events.jsonl");
        File.WriteAllBytes(tornEvents, File.ReadAllBytes(tornEvents)[..^6]);
        var tornInspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = torn, Limit = 100 });
        Assert.AreEqual((DiagnosticCaseCompletionState.Truncated, (long?)lines.Length), (tornInspection.CompletionState, tornInspection.TruncatedAtLine));
        Assert.HasCount(lines.Length - 1, tornInspection.Events, "the verified prefix is every complete line");

        // One flipped character inside a middle line's base64 payload: still valid JSON, but the checksum fails.
        var flipped = Copy("flipped");
        var middle = lines.Length / 2;
        var edited = lines.ToArray();
        var at = edited[middle].IndexOf("\"data\":\"", StringComparison.Ordinal) + 8;
        Assert.IsGreaterThan(8, at, "fixture: the middle line has no payload");
        edited[middle] = edited[middle][..at] + (edited[middle][at] == 'A' ? 'B' : 'A') + edited[middle][(at + 1)..];
        File.WriteAllLines(Path.Combine(flipped, "events.jsonl"), edited);
        var flippedInspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = flipped, Limit = 100 });
        Assert.AreEqual((DiagnosticCaseCompletionState.Truncated, (long?)(middle + 1)), (flippedInspection.CompletionState, flippedInspection.TruncatedAtLine),
            "a corrupt line was accepted");
        Assert.HasCount(middle, flippedInspection.Events);
    }

    // === Review round 1 fences ===

    [TestMethod]
    public async Task Failures_WriterDisposeFaultStillForgetsTheCase()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10).Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterDisposeFaultForTesting.Value = new IOException("injected flush failure on dispose");
        try
        {
            Assert.AreEqual(DiagnosticOutcome.Captured, diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Outcome);
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterDisposeFaultForTesting.Value = null;
        }

        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        Assert.AreEqual("no-active-case", diagnostics.GetCaseStatus().Problem?.Code, "a failed disposal left the case armed");
        var again = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path });
        Assert.AreEqual(DiagnosticOutcome.Captured, again.Outcome, $"a new case could not start: {again.Problem?.Code}");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
    }

    [TestMethod]
    public async Task Dispose_WaitsForTheCaseArtifact()
    {
        using var root = new CaseRoot();
        foreach (var asynchronous in new[] { true, false })
        {
            using var gate = new ManualResetEventSlim(false);
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
            Hex1bTerminal terminal;
            ScriptedWorkload workload = new();
            try
            {
                terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                    .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
            }
            finally
            {
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            }

            var path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
            using (new Running(terminal))
                await workload.WriteAndWaitAsync(terminal, "before disposal");

            var dispose = asynchronous ? terminal.DisposeAsync().AsTask() : Task.Run(terminal.Dispose);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            Assert.IsFalse(dispose.IsCompleted, $"async={asynchronous}: disposal returned before the case's artifact was finished");
            gate.Set();
            await dispose.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
            // No polling: once disposal returns, the artifact is complete.
            Assert.IsTrue(File.Exists(Path.Combine(path, "completion.json")), $"async={asynchronous}: no completion after disposal");
            Assert.AreEqual("target-disposed", Artifact.Read(path).Completion!.Value.GetProperty("stopReason").GetString());
        }
    }

    [TestMethod]
    public async Task Failures_WriterFaultDeclaresTheUnwrittenMissing()
    {
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = new InvalidOperationException("injected serializer failure");
        Hex1bTerminal terminal;
        var workload = new ScriptedWorkload();
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
        }

        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            var path = diagnostics.GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                for (var i = 0; i < 50; i++)
                    await workload.WriteAndWaitAsync(terminal, $"q{i} ");
                gate.Set();
                await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
            }

            var artifact = Artifact.Read(path);
            Assert.AreEqual("collector-failed", artifact.Completion!.Value.GetProperty("stopReason").GetString());
            CollectionAssert.AreEqual(Enumerable.Range(1, 50).Select(i => (long)i).ToList(), MissingOrdinals(artifact, "model"),
                "the events the failed writer never wrote are not exactly the missing ranges");
            Assert.IsTrue(artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing")
                .All(e => e.GetProperty("record").GetProperty("reason").GetString() == "collector-failed"));
            var model = artifact.Completion.Value.GetProperty("streams").EnumerateArray().Single(e => e.GetProperty("stream").GetString() == "model");
            Assert.AreEqual(model.GetProperty("offered").GetInt64(), model.GetProperty("written").GetInt64() + model.GetProperty("dropped").GetInt64());
        }
    }

    [TestMethod]
    public async Task Reader_CompletionCountsBeyondTheFileAreAnUnknownTail()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "one ");
            await workload.WriteAndWaitAsync(terminal, "two");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        // The completion says five more model events were offered than the file holds or declares lost.
        var completionPath = Path.Combine(stopped.Path!, "completion.json");
        var completion = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(completionPath))!;
        var model = completion["streams"]!.AsArray().Single(s => s!["stream"]!.GetValue<string>() == "model")!;
        model["offered"] = model["offered"]!.GetValue<long>() + 5;
        File.WriteAllText(completionPath, completion.ToJsonString());

        var coverage = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! }).Streams.Single(s => s.Stream == "model");
        Assert.AreEqual("incomplete", coverage.State);
        var tail = coverage.Missing.Single();
        Assert.AreEqual((coverage.LastOrdinal + 1, (long?)null, "unaccounted"), (tail.FromOrdinal, tail.ToOrdinal, tail.Reason));
    }

    [TestMethod]
    public async Task StreamFailure_AModelEventFailureStaysInTheCase()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.StreamFaultForTesting.Value = "model";
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.StreamFaultForTesting.Value = null;
        }

        await using (terminal)
        {
            // The first model event is a resize: its failure must not reach Resize, which has advanced the sequence.
            terminal.Resize(50, 12);
            Assert.AreEqual((50, 12), (terminal.Width, terminal.Height), "the resize was not applied");
            DiagnosticCaseResult stopped;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "after the failure");
                terminal.Resize(60, 12);
                stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            }

            var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path!, Limit = 100 });
            Assert.AreEqual("failed", inspection.Streams.Single(s => s.Stream == "model").State);
            Assert.IsEmpty(inspection.Events.Where(e => e.Stream == "model").ToList(), "a failed model stream kept recording");
            Assert.AreEqual((0L, "stream-failed"), (inspection.Intervals.Single().ToModelSequence, inspection.Intervals.Single().EndReason));
        }
    }

    [TestMethod]
    public async Task Limits_MissingRangesStayWithinTheBound()
    {
        const long bound = 1024 * 1024;
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(200, 50)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, MaxBytes = bound, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        var recorder = terminal.DiagnosticCase!;
        var path = recorder.Path;
        using (new Running(terminal))
        {
            // Fill the artifact to within ~150 KB of its bound with ordinary events.
            var chunk = Enumerable.Repeat((byte)'f', 8 * 1024).ToArray();
            while ((diagnostics.GetCaseStatus().BytesWritten ?? 0) < bound - 150 * 1024)
                await workload.WriteAndWaitAsync(terminal, chunk);

            // Then more loss than fits: 1,024 separate model ranges (about 170 KB of records), past any real ordinal.
            for (var i = 0; i < Hex1b.Diagnostics.Cases.CaseLossLedger.MaxRangesPerStream; i++)
                recorder.LossForTesting.Record(Hex1b.Diagnostics.Cases.CaseStream.Model, 1_000_000 + 2L * i);
            await workload.WriteAndWaitAsync(terminal, "wake the writer");
            await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")), TimeSpan.FromSeconds(30));
        }

        var bytes = Directory.GetFiles(path).Sum(f => new FileInfo(f).Length);
        Assert.IsLessThanOrEqualTo(bound, bytes, $"the artifact holds {bytes} bytes against its {bound}-byte bound");
        var artifact = Artifact.Read(path);
        Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var summary = artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing"
            && e.GetProperty("record").GetProperty("reason").GetString() == "size-limit-unknown-extent").ToList();
        Assert.HasCount(1, summary, "the loss that did not fit is not summarized once");
        Assert.AreEqual(JsonValueKind.Null, summary[0].GetProperty("record").TryGetProperty("toOrdinal", out var to) ? to.ValueKind : JsonValueKind.Null);
        Assert.AreEqual("incomplete", DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Streams.Single(s => s.Stream == "model").State);
    }

    [TestMethod]
    public void Queue_ChargesAFrameItsRetainedSize()
    {
        var editor = new string('x', 1_000_000);
        var frame = new DiagnosticApplicationFrame
        {
            FocusedEditor = new DiagnosticEditorState { Kind = "editor", Text = editor, Length = editor.Length },
            Root = new DiagnosticFrameNode { Type = "Root", Children = [new DiagnosticFrameNode { Type = "Text", Text = new string('y', 10_000) }] },
        };
        var estimate = Hex1b.Diagnostics.Cases.CaseFrameSize.Estimate(frame);
        Assert.IsGreaterThanOrEqualTo(2 * 1_000_000 + 2 * 10_000, estimate, "a frame's editor text and node text are not charged");

        var queue = new Hex1b.Diagnostics.Cases.CaseEventQueue();
        var accepted = 0;
        for (var i = 0; i < 8; i++)
        {
            if (queue.TryEnqueue(new Hex1b.Diagnostics.Cases.CaseEvent(Hex1b.Diagnostics.Cases.CaseStream.Frames, i + 1, 0, "published",
                    null, null, null, null, null, new DiagnosticCaseFrameEvent(), estimate)))
                accepted++;
        }

        Assert.AreEqual(4, accepted, "the 8 MiB bound did not hold for 2 MB frames");
    }

    [TestMethod]
    public async Task Graphics_IntervalEndIsSetBeforeItsApplicationIsOffered()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        Hex1bTerminal? terminal = null;
        var offers = new List<(long Sequence, long IntervalEnd)>();
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = () =>
        {
            if (terminal?.DiagnosticCase is { } recorder)
                lock (offers)
                    offers.Add((terminal.CurrentModelSequence, recorder.IntervalEnd));
        };
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
                .Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = null;
        }

        await using (terminal)
        {
            long graphicsSequence;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "text ");
                await workload.WriteAndWaitAsync(terminal, "\u001bP0;0;0q#0;2;100;0;0#0~~~~~~-\u001b\\"u8.ToArray());
                graphicsSequence = terminal.CurrentModelSequence;
                await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
            }

            // The interval end is known when the graphics application is offered, so no reader (live, or of a
            // case killed right after) can see that application inside the interval.
            lock (offers)
                Assert.AreEqual(graphicsSequence, offers.First(o => o.Sequence == graphicsSequence).IntervalEnd);
        }
    }

    [TestMethod]
    public async Task Stop_WaitsForAnOfferInFlight()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holding = 0;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = () =>
        {
            if (Volatile.Read(ref holding) == 1 && Interlocked.Exchange(ref holding, 2) == 1)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = null;
        }

        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            Volatile.Write(ref holding, 1);
            // A resize passes the recording check, then is held before its enqueue while the case stops.
            var resize = Task.Run(() => terminal.Resize(50, 12));
            Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10)), "fixture: the resize never reached the enqueue");
            var stop = diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
            await Task.Delay(300, TestContext.Current.CancellationToken);
            release.Set();
            var stopped = await stop;
            await resize;

            var artifact = Artifact.Read(stopped.Path!);
            Assert.IsTrue(artifact.ModelEvents().Any(e => e.GetProperty("kind").GetString() == "resize"),
                "an event offered while the case was recording was never written");
            var model = artifact.Completion!.Value.GetProperty("streams").EnumerateArray().Single(e => e.GetProperty("stream").GetString() == "model");
            Assert.AreEqual(model.GetProperty("offered").GetInt64(), model.GetProperty("written").GetInt64() + model.GetProperty("dropped").GetInt64());
        }
    }

    [TestMethod]
    public async Task Stop_BeforeTheWriterStartsStillWaits()
    {
        using var root = new CaseRoot();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(40, 10).Build();
        var diagnostics = new TerminalDiagnostics(terminal);
        Task<DiagnosticCaseResult>? stop = null;
        var completedBeforeStart = false;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.AfterArmForTesting.Value = () =>
        {
            // Another client's stop arrives between the arming and the writer's start.
            stop = Task.Run(() => diagnostics.StopCaseAsync());
            Thread.Sleep(300);
            completedBeforeStart = stop.IsCompleted;
        };
        DiagnosticCaseResult started;
        try
        {
            started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path });
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.AfterArmForTesting.Value = null;
        }

        Assert.IsFalse(completedBeforeStart, "the stop returned before the case's writer had even started");
        var stopped = await stop!.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        Assert.AreEqual((DiagnosticCaseState.Stopped, true), (stopped.State, File.Exists(Path.Combine(started.Path!, "completion.json"))));
    }

    [TestMethod]
    public async Task Ingress_OnlyThePumpTakesItsBytes()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var filter = new HoldingFilter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .AddWorkloadFilter(filter)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            filter.Arm();
            workload.Enqueue("PUMP-ITEM-BYTES"u8.ToArray());
            Assert.IsTrue(filter.Held.Wait(TimeSpan.FromSeconds(10)), "fixture: the pump never held the item");
            // Another thread applies to the model while the pump holds its item's bytes.
            terminal.EnterAlternateScreen();
            filter.Release.Set();
            await WaitAsync(() => terminal.CurrentModelSequence >= 2);
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var model = Artifact.Read(stopped.Path!).ModelEvents();
        Assert.AreEqual("application-without-ingress", model[0].GetProperty("kind").GetString(), "the other thread's application took the pump's bytes");
        Assert.AreEqual("PUMP-ITEM-BYTES", Encoding.UTF8.GetString(Convert.FromBase64String(model[1].GetProperty("data").GetString()!)));
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public void Storage_RefusesARootOwnedByAnotherUser()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            Assert.Inconclusive("Needs a non-root Linux user.");
        // /proc/1/fd is init's: owner-only (0500) and owned by root.
        const string foreign = "/proc/1/fd";
        Assert.AreEqual(UnixFileMode.None, File.GetUnixFileMode(foreign) & ~(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute),
            "fixture: the foreign root is not owner-only, so the mode check would refuse it first");
        try
        {
            File.SetUnixFileMode(foreign, File.GetUnixFileMode(foreign));
            Assert.Inconclusive("fixture: /proc/1/fd is this user's (a container whose init runs as the test user).");
        }
        catch (UnauthorizedAccessException)
        {
        }

        StringAssert.Contains(Hex1b.Diagnostics.Cases.CaseStorage.CheckRoot(foreign), "not owned by the current user");
    }

    [TestMethod]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Reader_UnreadableOrIncompleteManifestIsAStructuredFailure()
    {
        if (!OperatingSystem.IsLinux() || Environment.UserName == "root")
            Assert.Inconclusive("Needs Unix permissions enforced for this user.");
        using var root = new CaseRoot();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        var stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var manifest = Path.Combine(stopped.Path!, "manifest.json");

        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifest))!;
        node["checkpoint"] = null;
        File.WriteAllText(manifest, node.ToJsonString());
        var incomplete = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        Assert.AreEqual((DiagnosticOutcome.Failed, "invalid-artifact"), (incomplete.Outcome, incomplete.Problem?.Code));

        File.SetUnixFileMode(manifest, UnixFileMode.None);
        var unreadable = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        File.SetUnixFileMode(manifest, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        Assert.AreEqual((DiagnosticOutcome.Failed, "unreadable-artifact"), (unreadable.Outcome, unreadable.Problem?.Code));
    }

    // === Review round 2 fences ===

    [TestMethod]
    public async Task Dispose_ReenteredUnderTheModelLockDoesNotStall()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        Hex1bTerminal? terminal = null;
        TimeSpan? disposal = null;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = TimeSpan.FromSeconds(3);
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(10, 2)
                .WithScrollback(10, _ =>
                {
                    // A scrollback callback runs under the model lock; disposing from it must not wait for the writer.
                    if (disposal is not null)
                        return;
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    terminal!.Dispose();
                    disposal = watch.Elapsed;
                })
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = null;
        }

        var path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
        using (new Running(terminal))
        {
            workload.Enqueue("one\r\ntwo\r\nthree\r\nfour\r\n"u8.ToArray());
            await WaitAsync(() => disposal is not null);
        }

        Assert.IsLessThan(TimeSpan.FromSeconds(1.5), disposal!.Value, "a disposal under the model lock waited for the case writer");
        await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
        Assert.AreEqual("target-disposed", Artifact.Read(path).Completion!.Value.GetProperty("stopReason").GetString());
    }

    [TestMethod]
    public async Task Reentrant_ModelEventDuringAnApplicationKeepsSequenceOrder()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        // A title handler that resizes runs in the middle of the application that set the title.
        terminal.WindowTitleChanged += _ => terminal.Resize(50, 12);
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before ");
            await workload.WriteAndWaitAsync(terminal, "\u001b]0;renamed\u0007after");
            await workload.WriteAndWaitAsync(terminal, " later");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var model = Artifact.Read(stopped.Path!).ModelEvents();
        var sequences = model.Select(e => e.GetProperty("modelSequence").GetInt64()).ToList();
        CollectionAssert.AreEqual(sequences.Order().ToList(), sequences, "model events are not in model-sequence order");
        CollectionAssert.AreEqual(Enumerable.Range(1, model.Count).Select(i => (long)i).ToList(), sequences);
        var titled = model.Single(e => e.TryGetProperty("data", out var d) && Encoding.UTF8.GetString(Convert.FromBase64String(d.GetString()!)).Contains("renamed", StringComparison.Ordinal));
        var interval = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! }).Intervals.Single();
        Assert.AreEqual((titled.GetProperty("modelSequence").GetInt64() - 1, "reentrant-model-event"), (interval.ToModelSequence, interval.EndReason));
    }

    [TestMethod]
    public async Task Reader_DamagedCompletionOrStreamDeclarationIsStructured()
    {
        using var root = new CaseRoot();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        var stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var completionPath = Path.Combine(stopped.Path!, "completion.json");
        var completion = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(completionPath))!;
        completion["streams"] = null;
        File.WriteAllText(completionPath, completion.ToJsonString());
        var withoutCounts = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        Assert.AreEqual((DiagnosticOutcome.Captured, DiagnosticCaseCompletionState.Interrupted), (withoutCounts.Outcome, withoutCounts.CompletionState),
            "a completion without its counts was trusted");

        var manifestPath = Path.Combine(stopped.Path!, "manifest.json");
        var manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!;
        manifest["streams"]![0]!["stream"] = null;
        File.WriteAllText(manifestPath, manifest.ToJsonString());
        var nameless = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        Assert.AreEqual((DiagnosticOutcome.Failed, "invalid-artifact"), (nameless.Outcome, nameless.Problem?.Code));
    }

    [TestMethod]
    public async Task Failures_AnOfferInFlightAtAWriterFaultIsDeclared()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        using var gate = new ManualResetEventSlim(false);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holding = 0;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = new InvalidOperationException("injected");
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = () =>
        {
            if (Volatile.Read(ref holding) == 1 && Interlocked.Exchange(ref holding, 2) == 1)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = null;
        }

        await using (terminal)
        {
            var path = new TerminalDiagnostics(terminal).GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                await workload.WriteAndWaitAsync(terminal, "queued");
                Volatile.Write(ref holding, 1);
                // A resize passes its check, then is held before its enqueue while the writer fails.
                var resize = Task.Run(() => terminal.Resize(50, 12));
                Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10)), "fixture: the resize never reached the enqueue");
                gate.Set();
                await Task.Delay(300, TestContext.Current.CancellationToken);
                release.Set();
                await resize;
                await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
            }

            var artifact = Artifact.Read(path);
            Assert.AreEqual("collector-failed", artifact.Completion!.Value.GetProperty("stopReason").GetString());
            CollectionAssert.AreEqual(new List<long> { 1, 2 }, MissingOrdinals(artifact, "model"), "the in-flight resize was not declared with the queue");
            var missing = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Streams.Single(s => s.Stream == "model").Missing;
            Assert.IsTrue(missing.All(m => m.Reason == "collector-failed"), "the in-flight event surfaced as an unaccounted tail instead of a declared range");
        }
    }

    [TestMethod]
    public void Limits_ClosingRecordsFitTheirReserves()
    {
        // The worst case each closing tier must hold, serialized as the writer serializes it (CRC, tab, newline).
        static long Line(DiagnosticCaseEvent item) =>
            10 + JsonSerializer.SerializeToUtf8Bytes(item, DiagnosticsJsonContext.Default.DiagnosticCaseEvent).Length;
        DiagnosticCaseEvent Case(string kind, DiagnosticCaseRecord record) => new()
        {
            CaseSequence = long.MaxValue, Stream = "case", Ordinal = long.MaxValue, Timestamp = long.MaxValue, Kind = kind,
            ModelSequence = long.MaxValue, Record = record,
        };

        string[] streams = ["model", "input", "frames", "delivery"];
        var message = Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.Bounded("stream-failed: " + new string('\u00e9', 1000));
        Assert.IsTrue(message.All(c => c is >= ' ' and <= '~') && message.Length <= Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.MaxFailureMessage,
            "a failure message is not bounded printable ASCII");
        var closing = Line(Case("interval-end", new DiagnosticCaseRecord { Stream = "model", Reason = "reentrant-model-event" }))
            + streams.Sum(s => Line(Case("stream-failed", new DiagnosticCaseRecord { Stream = s, FromOrdinal = long.MaxValue, Reason = message })))
            + streams.Sum(s => Line(Case("missing", new DiagnosticCaseRecord { Stream = s, FromOrdinal = long.MaxValue, Reason = "size-limit-unknown-extent" })));
        Assert.IsLessThanOrEqualTo(Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.RangeReserve - Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.ClosingReserve,
            closing, "the closing records can overflow their tier");

        var completion = JsonSerializer.SerializeToUtf8Bytes(new DiagnosticCaseCompletion
        {
            CaseId = new string('f', 32),
            StopReason = DiagnosticCaseStopReason.CollectorFailed,
            StoppedAt = DateTimeOffset.MaxValue,
            LastCaseSequence = long.MaxValue,
            BytesWritten = long.MaxValue,
            Streams = streams.Select(s => new DiagnosticCaseStreamStatus { Stream = s, Offered = long.MaxValue, Written = long.MaxValue, Dropped = long.MaxValue }).ToList(),
        }, DiagnosticsJsonContext.Default.DiagnosticCaseCompletion).Length;
        Assert.IsLessThanOrEqualTo(Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.ClosingReserve, (long)completion, "the completion can overflow its reserve");
    }

    [TestMethod]
    public async Task Limits_SizeCrossedInTheModelDrainDeclaresTheDelivery()
    {
        using var root = new CaseRoot();
        async Task<(string Path, long First, long Last)> RecordAsync(long? maxBytes)
        {
            using var gate = new ManualResetEventSlim(false);
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
            try
            {
                var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
                var workload = new ScriptedWorkload();
                await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                    .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10).Build();
                var diagnostics = new TerminalDiagnostics(terminal);
                using (new Running(terminal))
                {
                    var started = diagnostics.StartCase(new DiagnosticCaseStartRequest
                    {
                        Directory = root.Path,
                        MaxBytes = maxBytes,
                        Authorizations = [DiagnosticAuthorization.ReapplicationData, DiagnosticAuthorization.NativeOutput],
                    });
                    var before = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0;
                    for (var i = 0; i < 4000; i++)
                        workload.Enqueue(Encoding.ASCII.GetBytes($"M{i:D5} ".PadRight(600, 'x')));
                    await WaitAsync(() => (diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0) >= before + 4000);
                    var last = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence!.Value;
                    gate.Set();
                    if (maxBytes is null)
                        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                    await WaitAsync(() => File.Exists(Path.Combine(started.Path!, "completion.json")));
                    return (started.Path!, before + 1, last);
                }
            }
            finally
            {
                gate.Set();
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            }
        }

        long LineBytes(string path, string stream) => File.ReadAllLines(Path.Combine(path, "events.jsonl"))
            .Where(l => l.Contains($"\"stream\":\"{stream}\"", StringComparison.Ordinal)).Sum(l => (long)Encoding.UTF8.GetByteCount(l) + 1);

        // The bound falls inside the model drain, before any delivery record is pulled.
        var measured = await RecordAsync(null);
        var bound = new FileInfo(Path.Combine(measured.Path, "manifest.json")).Length + LineBytes(measured.Path, "model") / 2 + 16 * 1024;
        Assert.IsGreaterThanOrEqualTo(Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.MinMaxBytes, bound, "fixture: too few bytes for a legal bound");

        var (path, first, last) = await RecordAsync(bound);
        var artifact = Artifact.Read(path);
        Assert.AreEqual("size-limit", artifact.Completion!.Value.GetProperty("stopReason").GetString());
        var kept = artifact.Events.Where(e => e.GetProperty("stream").GetString() == "delivery").Select(e => e.GetProperty("ordinal").GetInt64()).ToList();
        Assert.IsEmpty(kept, "fixture: a delivery record was written before the bound");
        var missing = MissingOrdinals(artifact, "delivery");
        CollectionAssert.IsSubsetOf(Enumerable.Range(0, (int)(last - first + 1)).Select(i => first + i).ToList(), missing,
            "delivery records made before the stop were neither written nor declared missing");
    }

    [TestMethod]
    public async Task Failures_LossRangesTakenBeforeAFaultAreStillDeclared()
    {
        using var root = new CaseRoot();
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.LossFaultForTesting.Value = new InvalidOperationException("injected loss write failure");
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(new ScriptedWorkload()).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.LossFaultForTesting.Value = null;
        }

        await using (terminal)
        {
            var recorder = terminal.DiagnosticCase!;
            // Three separate losses: the first two are closed ranges the writer takes before it fails.
            foreach (var ordinal in new long[] { 100, 102, 104 })
                recorder.LossForTesting.Record(Hex1b.Diagnostics.Cases.CaseStream.Model, ordinal);
            gate.Set();
            await WaitAsync(() => File.Exists(Path.Combine(recorder.Path, "completion.json")));

            var artifact = Artifact.Read(recorder.Path);
            Assert.AreEqual("collector-failed", artifact.Completion!.Value.GetProperty("stopReason").GetString());
            CollectionAssert.IsSubsetOf(new List<long> { 100, 102, 104 }, MissingOrdinals(artifact, "model"),
                "loss ranges taken before the failure were never declared");
        }
    }

    // === Review round 3 fences ===

    [TestMethod]
    public async Task Stop_UnpulledDeliveryIsDeclaredOnDrainTimeoutAndCollectorFailure()
    {
        using var root = new CaseRoot();
        foreach (var failure in new[] { false, true })
        {
            using var gate = new ManualResetEventSlim(false);
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = TimeSpan.FromMilliseconds(300);
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = failure ? new InvalidOperationException("injected") : null;
            try
            {
                var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
                var workload = new ScriptedWorkload();
                await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                    .WithPresentation(new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))).WithDimensions(40, 10).Build();
                var diagnostics = new TerminalDiagnostics(terminal);
                using (new Running(terminal))
                {
                    var started = diagnostics.StartCase(new DiagnosticCaseStartRequest { Directory = root.Path });
                    var before = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0;
                    for (var i = 0; i < 100; i++)
                        workload.Enqueue(Encoding.ASCII.GetBytes($"U{i} "));
                    await WaitAsync(() => (diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence ?? 0) >= before + 100);
                    var last = diagnostics.CaptureDelivery(new DiagnosticDeliveryRequest()).Totals!.LastSequence!.Value;
                    if (!failure)
                        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                    gate.Set();
                    await WaitAsync(() => File.Exists(Path.Combine(started.Path!, "completion.json")));

                    var reason = failure ? "collector-failed" : "drain-timeout";
                    var artifact = Artifact.Read(started.Path!);
                    CollectionAssert.IsSubsetOf(Enumerable.Range(0, (int)(last - before)).Select(i => before + 1 + i).ToList(), MissingOrdinals(artifact, "delivery"),
                        $"{reason}: delivery records made before the stop were neither written nor declared");
                    var delivery = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = started.Path! }).Streams.Single(s => s.Stream == "delivery");
                    Assert.AreEqual("incomplete", delivery.State, reason);
                    Assert.IsTrue(delivery.Missing.All(m => m.Reason == reason), $"{reason}: {string.Join(", ", delivery.Missing.Select(m => m.Reason))}");
                }
            }
            finally
            {
                gate.Set();
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = null;
                Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterFaultForTesting.Value = null;
            }
        }
    }

    [TestMethod]
    public async Task Reentrant_NestedApplicationKeepsEveryEvent()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] })
            .Build();
        // A title handler that applies output runs an application inside the one that set the title.
        terminal.WindowTitleChanged += _ => terminal.ApplyTokens(Hex1b.Tokens.AnsiTokenizer.Tokenize("nested"));
        DiagnosticCaseResult stopped;
        using (new Running(terminal))
        {
            await workload.WriteAndWaitAsync(terminal, "before ");
            await workload.WriteAndWaitAsync(terminal, "\u001b]0;renamed\u0007after");
            await workload.WriteAndWaitAsync(terminal, " later");
            stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        }

        var model = Artifact.Read(stopped.Path!).ModelEvents();
        CollectionAssert.AreEqual(Enumerable.Range(1, (int)terminal.CurrentModelSequence).Select(i => (long)i).ToList(),
            model.Select(e => e.GetProperty("modelSequence").GetInt64()).ToList(), "a model event was lost or reordered by the nested application");
        var titled = model.Single(e => e.TryGetProperty("data", out var d) && Encoding.UTF8.GetString(Convert.FromBase64String(d.GetString()!)).Contains("renamed", StringComparison.Ordinal));
        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = stopped.Path! });
        Assert.AreEqual("complete", inspection.Streams.Single(s => s.Stream == "model").State);
        Assert.AreEqual((titled.GetProperty("modelSequence").GetInt64() - 1, "reentrant-application"),
            (inspection.Intervals.Single().ToModelSequence, inspection.Intervals.Single().EndReason));
    }

    [TestMethod]
    public async Task Stop_DrainTimeoutDeclaresAnOfferInFlight()
    {
        await AssertOfferInFlightDeclaredAsync(sizeLimit: false);
    }

    [TestMethod]
    public async Task Limits_SizeStopDeclaresAnOfferInFlight()
    {
        await AssertOfferInFlightDeclaredAsync(sizeLimit: true);
    }

    // A resize passes its recording check and is held before its enqueue while the case discards its queue (a
    // drain timeout, or the size bound); it must be declared with the queue, not left unaccounted.
    private static async Task AssertOfferInFlightDeclaredAsync(bool sizeLimit)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        using var gate = new ManualResetEventSlim(false);
        using var held = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        var holding = 0;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = TimeSpan.FromMilliseconds(300);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = () =>
        {
            if (Volatile.Read(ref holding) == 1 && Interlocked.Exchange(ref holding, 2) == 1)
            {
                held.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };
        Hex1bTerminal terminal;
        try
        {
            terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(200, 50)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest
                {
                    Directory = root.Path,
                    MaxBytes = 1024 * 1024,
                    Authorizations = [DiagnosticAuthorization.ReapplicationData],
                }).Build();
        }
        finally
        {
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.DrainTimeoutForTesting.Value = null;
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.BeforeEnqueueForTesting.Value = null;
        }

        await using (terminal)
        {
            var diagnostics = new TerminalDiagnostics(terminal);
            var path = diagnostics.GetCaseStatus().Path!;
            using (new Running(terminal))
            {
                // Enough queued output to cross a 1 MiB bound when the writer is released, or a little to time out on.
                var chunk = Enumerable.Repeat((byte)'s', sizeLimit ? 16 * 1024 : 16).ToArray();
                for (var i = 0; i < 80; i++)
                    await workload.WriteAndWaitAsync(terminal, chunk);
                Volatile.Write(ref holding, 1);
                var resize = Task.Run(() => terminal.Resize(150, 40));
                Assert.IsTrue(held.Wait(TimeSpan.FromSeconds(10)), "fixture: the resize never reached the enqueue");
                if (!sizeLimit)
                    await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
                gate.Set();
                await Task.Delay(300, TestContext.Current.CancellationToken);
                release.Set();
                await resize;
                await WaitAsync(() => File.Exists(Path.Combine(path, "completion.json")));
            }

            var reason = sizeLimit ? "size-limit" : "drain-timeout";
            var artifact = Artifact.Read(path);
            Assert.AreEqual(sizeLimit ? "size-limit" : "requested", artifact.Completion!.Value.GetProperty("stopReason").GetString());
            var model = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path }).Streams.Single(s => s.Stream == "model");
            Assert.IsTrue(model.Missing.All(m => m.Reason == reason),
                $"{reason}: the held resize surfaced as {string.Join(", ", model.Missing.Select(m => m.Reason))} instead of a declared range");
            Assert.IsTrue(MissingOrdinals(artifact, "model").Contains(81), $"{reason}: the held resize (ordinal 81) was not declared");
        }
    }

    private const int CaseEventQueueMax = Hex1b.Diagnostics.Cases.CaseEventQueue.MaxEvents;

    // Records the chunks with the writer held until the pump has applied them all, then stops.
    private static async Task<Artifact> RecordHeldAsync(CaseRoot root, byte[][] chunks, bool payloads)
    {
        using var gate = new ManualResetEventSlim(false);
        Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = gate;
        try
        {
            var workload = new ScriptedWorkload();
            await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
                .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = payloads ? [DiagnosticAuthorization.ReapplicationData] : [] })
                .Build();
            using (new Running(terminal))
            {
                var total = chunks.Sum(c => (long)c.Length);
                foreach (var chunk in chunks)
                    workload.Enqueue(chunk);
                await WaitAsync(() => terminal.OutputBytesRead == total, TimeSpan.FromSeconds(60));
                await Settle(terminal);
                gate.Set();
                var stopped = await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
                return Artifact.Read(stopped.Path!);
            }
        }
        finally
        {
            gate.Set();
            Hex1b.Diagnostics.Cases.DiagnosticCaseRecorder.WriterGateForTesting.Value = null;
        }
    }

    private static List<long> MissingOrdinals(Artifact artifact, string stream) =>
        artifact.Events.Where(e => e.GetProperty("kind").GetString() == "missing" && e.GetProperty("record").GetProperty("stream").GetString() == stream)
            .SelectMany(e =>
            {
                var record = e.GetProperty("record");
                var from = record.GetProperty("fromOrdinal").GetInt64();
                var to = record.GetProperty("toOrdinal").GetInt64();
                return Enumerable.Range(0, (int)(to - from + 1)).Select(i => from + i);
            }).OrderBy(o => o).ToList();

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

    private static async Task WaitAsync(Func<bool> condition, TimeSpan? limit = null)
    {
        var deadline = DateTime.UtcNow + (limit ?? TimeSpan.FromSeconds(5));
        while (!condition() && DateTime.UtcNow < deadline)
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
    private sealed record Artifact(JsonElement Manifest, List<JsonElement> Events, JsonElement? Completion, string Path)
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
            return new Artifact(manifest, events, completion, path);
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
