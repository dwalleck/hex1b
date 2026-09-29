using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Hex1b.Diagnostics;

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

        public async Task WriteAndWaitAsync(Hex1bTerminal terminal, string text)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            var target = terminal.OutputBytesRead + bytes.Length;
            var sequence = terminal.CurrentModelSequence;
            Enqueue(bytes);
            for (var i = 0; i < 500 && (terminal.OutputBytesRead < target || terminal.CurrentModelSequence == sequence); i++)
                await Task.Delay(10, TestContext.Current.CancellationToken);
            Assert.IsGreaterThanOrEqualTo(target, terminal.OutputBytesRead, "fixture: the chunk was never read");
        }

        public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => await _chunks.Reader.ReadAsync(ct);
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
