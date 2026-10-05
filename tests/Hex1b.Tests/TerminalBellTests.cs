using System.Reflection;
using System.Text;
using Hex1b.Input;
using Hex1b.Tokens;

namespace Hex1b.Tests;

[TestClass]
public class TerminalBellTests
{
    [TestMethod]
    public void Tokenize_StandaloneBellAndOscTerminator_KeepDistinctSemanticsAndBytes()
    {
        const string input = "A\aB\x1b]2;title\aC";
        var tokens = AnsiTokenizer.Tokenize(input);
        Assert.HasCount(5, tokens);
        Assert.AreEqual("A", TestSeq.IsType<TextToken>(tokens[0]).Text);
        Assert.AreEqual('\a', TestSeq.IsType<ControlCharacterToken>(tokens[1]).Character);
        Assert.AreEqual("B", TestSeq.IsType<TextToken>(tokens[2]).Text);
        var title = TestSeq.IsType<OscToken>(tokens[3]);
        Assert.AreEqual("2", title.Command);
        Assert.AreEqual("title", title.Payload);
        Assert.AreEqual("C", TestSeq.IsType<TextToken>(tokens[4]).Text);
        Assert.AreEqual(input, AnsiTokenSerializer.Serialize(tokens));
        Assert.AreEqual(input, Encoding.UTF8.GetString(AnsiTokenUtf8Serializer.Serialize(tokens).Span));
    }

    [TestMethod]
    [DataRow("AB", 2, 0, "X", 2, 0)]
    [DataRow("12345678", 7, 0, "X", 0, 1)]
    public void Apply_StandaloneBell_PreservesCellsCursorAndPendingWrap(
        string initial, int beforeX, int beforeY, string next, int nextX, int nextY)
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 3).Build();
        terminal.ApplyTokens(AnsiTokenizer.Tokenize(initial));
        using var before = terminal.CreateSnapshot();
        Assert.AreEqual(beforeX, before.CursorX);
        Assert.AreEqual(beforeY, before.CursorY);
        terminal.ApplyTokens(AnsiTokenizer.Tokenize("\a\a"));
        using var after = terminal.CreateSnapshot();
        Assert.AreEqual(before.CursorX, after.CursorX);
        Assert.AreEqual(before.CursorY, after.CursorY);
        for (var y = 0; y < 3; y++)
            for (var x = 0; x < 8; x++) Assert.AreEqual(before.GetCell(x, y), after.GetCell(x, y));
        terminal.ApplyTokens(AnsiTokenizer.Tokenize(next));
        using var written = terminal.CreateSnapshot();
        Assert.AreEqual(next, written.GetCell(nextX, nextY).Character);
        Assert.AreEqual(initial[^1].ToString(), written.GetCell(initial.Length - 1, 0).Character);
    }

    [TestMethod]
    public void Apply_BellBetweenTextInOneBatch_DoesNotAttachItToThePreviousCell()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 3).Build();
        terminal.ApplyTokens(AnsiTokenizer.Tokenize("A\aB"));
        using var snapshot = terminal.CreateSnapshot();
        Assert.AreEqual("A", snapshot.GetCell(0, 0).Character);
        Assert.AreEqual("B", snapshot.GetCell(1, 0).Character);
        Assert.AreEqual(" ", snapshot.GetCell(2, 0).Character);
        Assert.AreEqual(2, snapshot.CursorX);
        Assert.AreEqual(0, snapshot.CursorY);
    }

    [TestMethod]
    public async Task Paste_StandaloneBellBetweenText_RemainsLiteralPasteContent()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 3).Build();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var dispatch = typeof(Hex1bTerminal).GetMethod("DispatchTokensAsEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(terminal, [AnsiTokenizer.Tokenize("\x1b[200~A\aB\x1b[201~"), workload, stop.Token])!;
        var paste = TestSeq.IsType<Hex1bPasteEvent>(await workload.InputEvents.ReadAsync(stop.Token));
        Assert.AreEqual("A\aB", await paste.Paste.ReadToEndAsync(ct: stop.Token));
        Assert.IsTrue(paste.Paste.IsCompleted);
        Assert.AreEqual(3L, paste.Paste.TotalCharactersWritten);
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
    }

    [TestMethod]
    public async Task Input_StandaloneBellBetweenText_PreservesCtrlGAndAdjacentKeys()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(8, 3).Build();
        // Exercise the actual terminal token-dispatch seam used by native input,
        // as existing paste tests do. The adapter's simplified raw-byte helper
        // is not this parser and cannot establish its compatibility.
        var dispatch = typeof(Hex1bTerminal).GetMethod("DispatchTokensAsEventsAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        await (Task)dispatch.Invoke(terminal, [AnsiTokenizer.Tokenize("A\aB"), workload, stop.Token])!;
        var first = TestSeq.IsType<Hex1bKeyEvent>(await workload.InputEvents.ReadAsync(stop.Token));
        var bell = TestSeq.IsType<Hex1bKeyEvent>(await workload.InputEvents.ReadAsync(stop.Token));
        var last = TestSeq.IsType<Hex1bKeyEvent>(await workload.InputEvents.ReadAsync(stop.Token));
        Assert.AreEqual('A', first.Character);
        Assert.AreEqual(Hex1bKey.G, bell.Key);
        Assert.AreEqual(Hex1bModifiers.Control, bell.Modifiers);
        Assert.AreEqual('B', last.Character);
        Assert.IsFalse(workload.InputEvents.TryRead(out _));
    }
}
