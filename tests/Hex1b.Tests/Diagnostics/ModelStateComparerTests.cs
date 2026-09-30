using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

/// <summary>
/// The typed model-state comparison finds every difference, cell by cell with styles resolved, never
/// concludes from counts, and counts every difference while listing at most its cap.
/// </summary>
[TestClass]
public class ModelStateComparerTests
{
    [TestMethod]
    public async Task Compare_Discriminates()
    {
        // The corpus ends inside a CSI and a scalar (an escape prefix, pending UTF-8 bytes and a framer count); a
        // second one ends in a bare ESC, the one pending holder that excludes the others (ticket 12).
        var corpus = "\u001b[31mred\u001b[m plain\r\n" + string.Concat(Enumerable.Range(1, 12).Select(i => $"line {i}\r\n")) + "\u001b[?1h\u001b]0;T\u0007\u001b(0q\u001b(B"
            + "\u001b]22;\u0007\u001b]133;A\u0007$ \u001b[1;";
        var state = await ProjectAsync(corpus, tail: [0xe6]);
        var same = await ProjectAsync(corpus, tail: [0xe6]);
        var escState = await ProjectAsync(corpus[..^4] + "\u001b");
        var escSame = await ProjectAsync(corpus[..^4] + "\u001b");
        Assert.IsTrue(state.PendingInput.Utf8.Length > 0 && state.PendingInput.EscapePrefix.Length > 0 && escState.PendingInput.GroundEscape, "fixture: pending input");
        var identical = ModelStateComparer.Compare(state, same, ModelStateComparer.DefaultMaxDifferences);
        Assert.AreEqual(0, identical.Total, string.Join("; ", identical.Differences.Select(d => d.Path)));
        Assert.IsGreaterThan(0, state.History!.Rows.Count, "fixture: no history");

        // Every declared fault differs at the path it declares.
        foreach (var kind in ModelStateFault.Kinds)
        {
            var (reference, subject) = kind == "pending-ground-escape" ? (escState, escSame) : (state, same);
            var faulted = ModelStateFault.Apply(subject, kind, out var path, out var problem);
            Assert.IsNotNull(faulted, $"{kind}: {problem}");
            var comparison = ModelStateComparer.Compare(reference, faulted, ModelStateComparer.DefaultMaxDifferences);
            Assert.IsGreaterThan(0, comparison.Total, $"{kind}: no difference");
            CollectionAssert.Contains(comparison.Differences.Select(d => d.Path).ToList(), path, $"{kind}: {string.Join("; ", comparison.Differences.Select(d => d.Path))}");
        }
        Assert.IsNull(ModelStateFault.Apply(same, "nope", out _, out var unknown));
        StringAssert.StartsWith(unknown, "Unknown fault 'nope'");
        var noHistory = await ProjectAsync("x", scrollback: null);
        Assert.IsNull(ModelStateFault.Apply(noHistory, "history-rows", out _, out var needsHistory));
        StringAssert.Contains(needsHistory, "has no retained history");
        Assert.IsNull(ModelStateFault.Apply(noHistory, "title-stack", out _, out var needsStack));
        StringAssert.Contains(needsStack, "has no saved title");
        Assert.IsNull(ModelStateFault.Apply(noHistory, "command-mark", out _, out var needsMark));
        StringAssert.Contains(needsMark, "has no command mark with a position");

        // Equal counts, different content: one cell's text, one cell's color, one history row's text.
        AssertOnly(await ProjectAsync("abc"), await ProjectAsync("abd"), "screen[0][2].text", "lastPrinted.cell.text");
        AssertOnly(await ProjectAsync("\u001b[31mab"), await ProjectAsync("\u001b[32mab"), "screen[0][0].style.foreground", "screen[0][1].style.foreground",
            "rendition.foreground", "lastPrinted.cell.style.foreground");
        var history = await ProjectAsync(string.Concat(Enumerable.Range(1, 12).Select(i => $"row {i}\r\n")));
        var changed = await ProjectAsync(string.Concat(Enumerable.Range(1, 12).Select(i => i == 2 ? "rOw 2\r\n" : $"row {i}\r\n")));
        Assert.AreEqual(history.History!.Rows.Count, changed.History!.Rows.Count, "fixture: row counts differ");
        AssertOnly(history, changed, "history.rows[1][1].text");
    }

    [TestMethod]
    public async Task Compare_StylesFieldByField()
    {
        // A separator inside a hyperlink, or null against empty, must not make two styles equal.
        var state = await ProjectAsync("x");
        DiagnosticModelState Styled(DiagnosticModelStyle style) => state with
        {
            Styles = [style],
            Screen = [state.Screen[0] with { Cells = [state.Screen[0].Cells[0] with { Style = 0 }] }],
        };
        foreach (var (a, b) in new (DiagnosticModelStyle, DiagnosticModelStyle)[]
        {
            (new() { HyperlinkUri = "a|b", HyperlinkParameters = "c" }, new() { HyperlinkUri = "a", HyperlinkParameters = "b|c" }),
            (new() { Foreground = null }, new() { Foreground = "" }),
            (new() { Attributes = ["bold", "dim"] }, new() { Attributes = ["bold,dim"] }),
        })
        {
            var comparison = ModelStateComparer.Compare(Styled(a), Styled(b), ModelStateComparer.DefaultMaxDifferences);
            Assert.IsGreaterThan(0, comparison.Total, $"{a} and {b} compared equal");
        }
    }

    [TestMethod]
    public async Task Compare_LargeDifferences()
    {
        var state = await ProjectAsync(string.Concat(Enumerable.Range(0, 10_010).Select(i => $"{i}\r\n")), width: 10, height: 4, scrollback: 10_000);
        Assert.AreEqual(10_000, state.History!.Rows.Count, "fixture: history is not full");
        var faulted = ModelStateFault.Apply(state, "history-rows", out _, out _)!;
        faulted = ModelStateFault.Apply(faulted, "mode", out _, out _)!;

        var comparison = ModelStateComparer.Compare(state, faulted, ModelStateComparer.DefaultMaxDifferences);
        Assert.AreEqual(10_001, comparison.Total);
        Assert.IsTrue(comparison.Truncated);
        Assert.HasCount(ModelStateComparer.DefaultMaxDifferences, comparison.Differences);
        Assert.AreEqual(10_000, comparison.BySurface["history"]);
        Assert.AreEqual(1, comparison.BySurface["modes"]);
        Assert.AreEqual("history.rows[0].originalWidth", comparison.Differences[0].Path, "differences are not in the projection's order");

        // An independent diff of the two serialized states agrees on the total.
        var independent = JsonDifferences(JsonNode.Parse(Json(state)), JsonNode.Parse(Json(faulted)), "$");
        Assert.AreEqual(comparison.Total, independent);

        var one = ModelStateComparer.Compare(state, faulted, 1);
        Assert.AreEqual((10_001L, 1, true), (one.Total, one.Differences.Count, one.Truncated));
    }

    [TestMethod]
    public async Task CommandMarkPositionsAndStackCompared()
    {
        // Equal counts and equal text: a mark one column later is reported at its column alone (ticket 11).
        var mark = await ProjectAsync("ab\u001b]133;A\u0007cd");
        var moved = await ProjectAsync("abc\u001b]133;A\u0007d");
        Assert.AreEqual((1, 1), (mark.CommandMarks.Count, moved.CommandMarks.Count), "fixture: mark counts");
        AssertOnly(mark, moved, "commandMarks[0].column");
        AssertOnly(mark, mark with { CommandMarks = [mark.CommandMarks[0] with { Row = 1 }] }, "commandMarks[0].row");
        AssertOnly(mark, mark with { CommandMarks = [mark.CommandMarks[0] with { Buffer = "alternate" }] }, "commandMarks[0].buffer");

        // Which empty cell continues its left neighbour is compared too (review RR#1).
        var wide = await ProjectAsync("ab\u6f22");
        var row = wide.Screen[0];
        Assert.IsTrue(row.Cells[3].Continues, "fixture: the wide glyph's continuation");
        AssertOnly(wide, wide with { Screen = [row with { Cells = [.. row.Cells.Take(3), row.Cells[3] with { Continues = false }, .. row.Cells.Skip(4)] }, .. wide.Screen.Skip(1)] },
            "screen[0][3].continues");
        Assert.IsNotNull(wide.Screen[1].Unwritten, "fixture: a never-written row");
        AssertOnly(wide, wide with { Screen = [wide.Screen[0], wide.Screen[1] with { Unwritten = null }, .. wide.Screen.Skip(2)] }, "screen[1].unwritten");

        // Equal current titles and stack depth: a different saved entry is reported at that entry.
        var stack = await ProjectAsync("\u001b]0;x\u0007\u001b]22;\u0007");
        var other = await ProjectAsync("\u001b]0;y\u0007\u001b]22;\u0007\u001b]0;x\u0007");
        Assert.AreEqual((stack.Titles.Window, stack.Titles.Stack.Count), (other.Titles.Window, other.Titles.Stack.Count), "fixture: titles");
        AssertOnly(stack, other, "titles.stack[0].window", "titles.stack[0].icon");
    }

    private static void AssertOnly(DiagnosticModelState recorded, DiagnosticModelState reapplied, params string[] paths)
    {
        var comparison = ModelStateComparer.Compare(recorded, reapplied, ModelStateComparer.DefaultMaxDifferences);
        CollectionAssert.AreEquivalent(paths, comparison.Differences.Select(d => d.Path).ToList(),
            string.Join("; ", comparison.Differences.Select(d => $"{d.Path} {d.Recorded} vs {d.Reapplied}")));
        Assert.AreEqual(paths.Length, comparison.Total);
    }

    private static string Json(DiagnosticModelState state) =>
        JsonSerializer.Serialize(state, DiagnosticsJsonContext.Default.DiagnosticModelState);

    private static long JsonDifferences(JsonNode? a, JsonNode? b, string path)
    {
        if (a is JsonObject oa && b is JsonObject ob)
            return oa.Select(p => p.Key).Union(ob.Select(p => p.Key)).Sum(key => JsonDifferences(oa[key], ob[key], $"{path}.{key}"));
        if (a is JsonArray aa && b is JsonArray ab)
            return (aa.Count == ab.Count ? 0 : 1) + Enumerable.Range(0, Math.Min(aa.Count, ab.Count)).Sum(i => JsonDifferences(aa[i], ab[i], $"{path}[{i}]"));
        return a?.ToJsonString() == b?.ToJsonString() ? 0 : 1;
    }

    private static async Task<DiagnosticModelState> ProjectAsync(string text, int width = 40, int height = 8, int? scrollback = 50, byte[]? tail = null)
    {
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            PresentationAdapter = new HeadlessPresentationAdapter(width, height),
            WorkloadAdapter = workload,
            Width = width,
            Height = height,
            ScrollbackCapacity = scrollback,
        });
        var bytes = tail is null ? Encoding.UTF8.GetBytes(text) : [.. Encoding.UTF8.GetBytes(text), .. tail];
        workload.Enqueue(bytes);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while ((terminal.OutputBytesRead < bytes.Length || terminal.CurrentModelSequence == 0) && DateTime.UtcNow < deadline)
            await Task.Delay(5, TestContext.Current.CancellationToken);
        Assert.AreEqual(bytes.Length, terminal.OutputBytesRead, "fixture: the text was never read");
        return terminal.CaptureModelState();
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
