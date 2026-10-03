using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hex1b.Diagnostics;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// The model-state projection (<c>text-state/2</c>) covers every model field, is deterministic,
/// names what it cannot represent, and is read coherently under the model lock.
/// </summary>
[TestClass]
public class DiagnosticModelStateTests
{
    // Every surface non-default: rendition with curly underline and a parameterized hyperlink,
    // titles and the title stack, DEC graphics, tabs, margins and origin, DECLRMM, saved cursors
    // on both screens, wide, combining and joined graphemes, wrapping, retained history, a resize,
    // cleared tabs, insert mode, REP, cursor style, command marks, a synchronized update, and chunks
    // ending mid-CSI and mid-UTF-8 (evidence P2).
    private static readonly Step[] Corpus =
    [
        new("\u001b[1;31mred bold\u001b[0m \u001b[4:3m\u001b[58;2;9;8;7mcurly\u001b[24;59m \u001b]8;id=a;https://x.test\u001b\\link\u001b]8;;\u001b\\\r\n"),
        new("\u001b]0;TITLE\u0007\u001b]1;ICON\u0007\u001b]22;\u0007\u001b]0;SECOND\u0007\u001b(0lqk\u001b(B\u001b)0\ttab\r\n"),
        new("\u001b[3;6r\u001b[?6h\u001b[2;1Hin origin\u001b[?6l\u001b[?69h\u001b[5;30s\u001b[?69l\u001b[r"),
        new("\u001b7\u001b[5;5Hsaved\u001b8\u001b[?1049halt \u001b7x\u001b[?1049lback\r\n"),
        new("漢字 é 👩‍💻 a long wrapping line that goes past the margin and on and on and on\r\n"),
        new(null, 30, 8),
        new(string.Concat(Enumerable.Range(1, 20).Select(i => $"row {i} \u001b[{i % 7 + 30}mcolour\u001b[m\r\n"))),
        new(null, 45, 10),
        new("\u001b[3gA\u001bHB\tC\u001b[4hins\u001b[4lX\u001b[2b\u001b[?25l\u001b[5 q\u001b]133;A\u0007$ ls\r\n\u001b]133;D;0\u0007\u001b]9;4;1;40\u0007\u001b]7;file://host/tmp\u0007"),
        new("\u001b[?1h\u001b=\u001b[?2004h\u001b[?1004h\u001b[?1002h\u001b[?1006h\u001b[20h\u001b[?45h\u001b[?2026h"),
        new("\u001b[1\u001b"),
        new([.. "\u001b[m"u8, 0xe6, 0xbc]),
    ];

    [TestMethod]
    public void Projection_CoversEveryModelField()
    {
        var coverage = Hex1bTerminal.ModelStateFieldCoverage;
        var fields = FieldNames(typeof(Hex1bTerminal), qualified: false)
            .Concat(FieldNames(typeof(TerminalCell), qualified: true))
            .Concat(FieldNames(typeof(ScrollbackRow), qualified: true))
            .Concat(FieldNames(typeof(ScrollbackBuffer), qualified: true))
            .Concat(FieldNames(typeof(TerminalCommandMark), qualified: true))
            .Concat(FieldNames(typeof(TerminalActivityState), qualified: true))
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = fields.Where(f => !coverage.ContainsKey(f)).Order().ToList();
        var stale = coverage.Keys.Where(k => !fields.Contains(k)).Order().ToList();
        Assert.IsEmpty(unclassified, "fields the projection does not classify: " + string.Join(", ", unclassified));
        Assert.IsEmpty(stale, "classified fields that no longer exist: " + string.Join(", ", stale));
        foreach (var (field, classification) in coverage)
        {
            Assert.IsTrue(classification == "projected" || classification.StartsWith("unsupported:", StringComparison.Ordinal)
                || classification.Contains(": ", StringComparison.Ordinal), $"{field}: an exclusion must give its reason");
        }
    }

    [TestMethod]
    public async Task Projection_ReflectsEveryProjectedScalarField()
    {
        // A field classified as projected must change the projection when it changes. Scalars are
        // perturbed directly; buffers, history, marks and titles are exercised by the corpus tests.
        await using var fed = await FeedAsync(Corpus);
        var terminal = fed.Terminal;
        var silent = new List<string>();
        foreach (var field in typeof(Hex1bTerminal).GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public))
        {
            if (Hex1bTerminal.ModelStateFieldCoverage[field.Name] != "projected")
                continue;
            var original = field.GetValue(terminal);
            var perturbed = Perturb(field, original);
            if (perturbed is null)
                continue;
            var before = Json(terminal.CaptureModelState());
            field.SetValue(terminal, perturbed);
            var after = Json(terminal.CaptureModelState());
            field.SetValue(terminal, original);
            if (before == after)
                silent.Add(field.Name);
        }
        Assert.IsEmpty(silent, "projected fields that do not reach the projection: " + string.Join(", ", silent));
    }

    [TestMethod]
    public async Task Projection_Deterministic()
    {
        await using var a = await FeedAsync(Corpus);
        await using var b = await FeedAsync(Corpus, clockOffset: TimeSpan.FromSeconds(1.1));
        var stateA = a.Terminal.CaptureModelState();
        var stateB = b.Terminal.CaptureModelState();
        Assert.AreEqual(Json(stateA), Json(stateB), "identically fed models projected differently");

        // The corpus reached every surface (so the equality is not vacuous).
        Assert.AreEqual("main", stateA.ActiveBuffer);
        Assert.IsNotNull(stateA.SavedCursor, "fixture: no DECSC");
        Assert.IsNotEmpty(stateA.Titles.Stack, "fixture: no pushed title");
        Assert.IsGreaterThan(0, stateA.History!.Rows.Count, "fixture: no history");
        Assert.IsTrue(stateA.Styles.Any(s => s.HyperlinkParameters == "id=a"), "fixture: no parameterized hyperlink");
        Assert.IsTrue(stateA.Styles.Any(s => s.UnderlineStyle == "curly" && s.UnderlineColor == "rgb:#090807"), "fixture: no curly colored underline");
        Assert.IsNotEmpty(stateA.CommandMarks, "fixture: no command mark");
        Assert.AreEqual("B", stateA.Charsets.G0);
        Assert.AreEqual("0", stateA.Charsets.G1, "fixture: no G1 designation");
        Assert.IsTrue(stateA.Modes["application-cursor-keys"] && stateA.Modes["bracketed-paste"] && stateA.Modes["newline"], "fixture: modes not set");
        Assert.IsTrue(stateA.SynchronizedUpdate.Active, "fixture: no synchronized update");
        Assert.IsFalse(stateA.Cursor.Visible, "fixture: cursor still visible");
        Assert.AreEqual(Convert.ToBase64String([0xe6, 0xbc]), stateA.PendingInput.Utf8, "fixture: no pending UTF-8");
        Assert.AreEqual("40", stateA.Activity.ProgressPercentage?.ToString(), "fixture: no progress");

        // One differing byte differs at exactly that cell's text.
        var changed = Corpus.ToArray();
        changed[6] = new(Corpus[6].Text!.Replace("row 7 ", "row 7!", StringComparison.Ordinal));
        await using var c = await FeedAsync(changed);
        var differences = JsonDifferences(JsonNode.Parse(Json(stateA)), JsonNode.Parse(Json(c.Terminal.CaptureModelState())), "$");
        Assert.HasCount(1, differences, "differences: " + string.Join("; ", differences));
        StringAssert.EndsWith(differences[0].Split(' ')[0], ".t");
        StringAssert.Contains(differences[0], "\" \" vs \"!\"");
    }

    [TestMethod]
    public async Task Projection_GraphicsUnsupported()
    {
        await using var fresh = await FeedAsync([new("text")]);
        Assert.IsEmpty(fresh.Terminal.CaptureModelState().Unsupported, "a text-only model named an unsupported surface");

        await using var image = await FeedAsync([new("\u001bP0;0;0q#0;2;100;0;0#0~~~~~~-\u001b\\")]);
        Assert.IsTrue(image.Terminal.TrackedSixelCount > 0, "fixture: the Sixel left no image");
        CollectionAssert.AreEqual(new[] { "graphics" }, image.Terminal.CaptureModelState().Unsupported.ToArray());

        // An erased image leaves nothing resident, but its register definitions change later graphics.
        await using var erased = await FeedAsync([new("\u001bP0;0;0q#5;2;0;100;0#5~~\u001b\\"), new("\u001b[H\u001b[2J\u001b[3J")]);
        Assert.AreEqual(0, erased.Terminal.TrackedSixelCount, "fixture: the image is still resident");
        Assert.IsFalse(HasResidentGraphics(erased.Terminal), "fixture: graphics state is still resident");
        Assert.IsTrue(RegistersModified(erased.Terminal), "fixture: erasing reset the registers");
        CollectionAssert.AreEqual(new[] { "graphics" }, erased.Terminal.CaptureModelState().Unsupported.ToArray());

        // Each non-resident indicator alone: a modified register, and a used placement counter.
        await using var register = await FeedAsync([new("text")]);
        Field<Hex1b.Sixel.SixelColorRegisters>(register.Terminal, "_sixelColorRegisters").Define(5, new Hex1b.Surfaces.Rgba32(1, 2, 3, 255));
        CollectionAssert.AreEqual(new[] { "graphics" }, register.Terminal.CaptureModelState().Unsupported.ToArray());
        await using var counter = await FeedAsync([new("text")]);
        typeof(Hex1bTerminal).GetField("_sixelPlacementSequence", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(counter.Terminal, 1L);
        CollectionAssert.AreEqual(new[] { "graphics" }, counter.Terminal.CaptureModelState().Unsupported.ToArray());
    }

    [TestMethod]
    public async Task Projection_PendingInput()
    {
        await using var utf8 = await FeedAsync([new([(byte)'a', 0xe6, 0xbc])]);
        var utf8State = utf8.Terminal.CaptureModelState();
        CollectionAssert.AreEqual(new byte[] { 0xe6, 0xbc }, Convert.FromBase64String(utf8State.PendingInput.Utf8));
        Assert.IsEmpty(utf8State.Unsupported);

        await using var csi = await FeedAsync([new("a\u001b[1;3")]);
        Assert.AreEqual("\u001b[1;3", csi.Terminal.CaptureModelState().PendingInput.EscapePrefix);

        await using var escape = await FeedAsync([new("a\u001b")]);
        var escapeState = escape.Terminal.CaptureModelState();
        Assert.IsTrue(escapeState.PendingInput.GroundEscape || escapeState.PendingInput.EscapePrefix == "\u001b",
            "a trailing ESC was not projected");

        await using var dcs = await FeedAsync([new("a\u001bP0;0;0q#0;2;1")]);
        CollectionAssert.Contains(dcs.Terminal.CaptureModelState().Unsupported.ToArray(), "dcs-continuation");
    }

    [TestMethod]
    public async Task Projection_CoherentUnderResize()
    {
        await using var fed = await FeedAsync([new(string.Concat(Enumerable.Range(1, 40).Select(i => $"line {i} with some text\r\n")))]);
        var terminal = fed.Terminal;
        using var stop = new CancellationTokenSource();
        var resizer = Task.Run(() =>
        {
            var width = 20;
            while (!stop.IsCancellationRequested)
            {
                width = width >= 80 ? 20 : width + 7;
                terminal.Resize(width, 6 + width % 5);
            }
        });
        var writer = Task.Run(async () =>
        {
            var i = 0;
            while (!stop.IsCancellationRequested)
            {
                fed.Workload.Enqueue(Encoding.UTF8.GetBytes($"more {i++} text\r\n"));
                await Task.Delay(1);
            }
        });

        var torn = new List<string>();
        var widths = new HashSet<int>();
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        for (var i = 0; i < 1000 || (widths.Count < 6 && DateTime.UtcNow < deadline); i++)
        {
            if (i % 10 == 0)
                Thread.Sleep(1);
            var state = terminal.CaptureModelState();
            widths.Add(state.Width);
            if (state.Screen.Count != state.Height)
                torn.Add($"#{i}: {state.Screen.Count} rows for height {state.Height}");
            else if (state.Screen.Any(r => r.Cells.Count != state.Width))
                torn.Add($"#{i}: a row not {state.Width} wide");
            else if (state.Cursor.X < 0 || state.Cursor.X > state.Width || state.Cursor.Y < 0 || state.Cursor.Y >= state.Height)
                torn.Add($"#{i}: cursor {state.Cursor.X},{state.Cursor.Y} outside {state.Width}x{state.Height}");
            else if (state.Margins.Bottom >= state.Height || state.Margins.Right >= state.Width)
                torn.Add($"#{i}: margins outside {state.Width}x{state.Height}");
        }
        stop.Cancel();
        await Task.WhenAll(resizer, writer);
        Assert.IsEmpty(torn, string.Join("; ", torn.Take(5)));
        Assert.IsGreaterThanOrEqualTo(6, widths.Count, "fixture: the resizes did not interleave with the projections");
    }

    private static T Field<T>(Hex1bTerminal terminal, string name) =>
        (T)typeof(Hex1bTerminal).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(terminal)!;

    private static bool HasResidentGraphics(Hex1bTerminal terminal) =>
        Field<SixelGraphicsState>(terminal, "_sixelGraphicsState").HasResidentState
        || Field<KgpTerminalGraphicsState>(terminal, "_kgpGraphicsState").HasResidentState;

    private static bool RegistersModified(Hex1bTerminal terminal)
    {
        var registers = Field<Hex1b.Sixel.SixelColorRegisters>(terminal, "_sixelColorRegisters");
        var defaults = new Hex1b.Sixel.SixelColorRegisters(registers.Policy);
        return Enumerable.Range(0, registers.Count).Any(r => registers.Get(r) != defaults.Get(r));
    }

    private static IEnumerable<string> FieldNames(Type type, bool qualified) =>
        type.GetFields(BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.DeclaredOnly)
            .Select(f => qualified ? $"{type.Name}.{f.Name}" : f.Name);

    private static object? Perturb(FieldInfo field, object? value) => value switch
    {
        bool b => !b,
        int i => i + 1,
        long l => l + 1,
        char c => (char)(c + 1),
        string s => s + "x",
        Enum e => Enum.GetValues(e.GetType()).Cast<object>().First(v => !v.Equals(e)),
        _ => null,
    };

    private static string Json(DiagnosticModelState state) =>
        JsonSerializer.Serialize(state, DiagnosticsJsonContext.Default.DiagnosticModelState);

    // An independent structural diff of two JSON documents (the comparer is not used).
    private static List<string> JsonDifferences(JsonNode? a, JsonNode? b, string path)
    {
        var differences = new List<string>();
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
                differences.AddRange(JsonDifferences(oa[key], ob[key], $"{path}.{key}"));
        }
        else if (a is JsonArray aa && b is JsonArray ab)
        {
            if (aa.Count != ab.Count)
                differences.Add($"{path} count {aa.Count} vs {ab.Count}");
            for (var i = 0; i < Math.Min(aa.Count, ab.Count); i++)
                differences.AddRange(JsonDifferences(aa[i], ab[i], $"{path}[{i}]"));
        }
        else if (a?.ToJsonString() != b?.ToJsonString())
        {
            differences.Add($"{path} {a?.ToJsonString()} vs {b?.ToJsonString()}");
        }
        return differences;
    }

    private static async Task<Fed> FeedAsync(IReadOnlyList<Step> steps, TimeSpan clockOffset = default)
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 29, 0, 0, 0, TimeSpan.Zero) + clockOffset);
        var workload = new ScriptedWorkload();
        var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(40, 8),
            WorkloadAdapter = workload,
            Width = 40,
            Height = 8,
            ScrollbackCapacity = 50,
            TimeProvider = clock,
        });
        var fed = new Fed(terminal, workload);
        long expected = 0;
        foreach (var step in steps)
        {
            if (step.Bytes is { } bytes)
            {
                var sequence = terminal.CurrentModelSequence;
                expected += bytes.Length;
                workload.Enqueue(bytes);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while ((terminal.OutputBytesRead < expected || terminal.CurrentModelSequence == sequence) && DateTime.UtcNow < deadline)
                    await Task.Delay(5, TestContext.Current.CancellationToken);
                Assert.IsGreaterThanOrEqualTo(expected, terminal.OutputBytesRead, "fixture: a chunk was never read");
            }
            else
            {
                terminal.Resize(step.Width, step.Height);
            }
            clock.Advance(TimeSpan.FromMilliseconds(10));
        }
        return fed;
    }

    private sealed record Step(byte[]? Bytes, int Width = 0, int Height = 0)
    {
        public Step(string text) : this(Encoding.UTF8.GetBytes(text)) => Text = text;

        public string? Text { get; }
    }

    private sealed class Fed(Hex1bTerminal terminal, ScriptedWorkload workload) : IAsyncDisposable
    {
        public Hex1bTerminal Terminal { get; } = terminal;
        public ScriptedWorkload Workload { get; } = workload;
        public ValueTask DisposeAsync() => Terminal.DisposeAsync();
    }

    private sealed class ScriptedWorkload : IHex1bTerminalWorkloadAdapter
    {
        private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
        public void Enqueue(byte[] chunk) => _chunks.Writer.TryWrite(chunk);
        public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default) => await _chunks.Reader.ReadAsync(ct);
        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;
        public event Action? Disconnected { add { } remove { } }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
