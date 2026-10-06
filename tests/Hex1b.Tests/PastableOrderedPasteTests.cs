using System.Text;
using System.Threading.Channels;
using Hex1b.Automation;
using Hex1b.Documents;
using Hex1b.Events;
using Hex1b.Input;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class PastableOrderedPasteTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("literal café 界 🚀")]
    public async Task OrderedPaste_ReadOnlyDescendant_AncestorReceivesLiteralDataAndTerminal(string payload)
    {
        var calls = new List<string>();
        var text = new StringBuilder();
        var state = new EditorState(new Hex1bDocument("unchanged")) { IsReadOnly = true };
        await RunAsync(app => new PastableWidget(new EditorWidget(state))
            .OnPaste(_ => calls.Add("legacy"))
            .OnOrderedPaste(_ =>
            {
                calls.Add("begin");
                return update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk) text.Append(update.Text);
                    else calls.Add(update.Phase.ToString());
                };
            }), "\u001b[200~" + payload + "\u001b[201~");
        TestSeq.AreEqual(new[] { "begin", "Completed" }, calls);
        Assert.AreEqual(payload, text.ToString());
        Assert.AreEqual("unchanged", state.Document.GetText());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_LegacyLimitWithExplicitFactory_RefusesBeforeFactory(bool timeout)
    {
        var starts = 0;
        var error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => RunAsync(_ =>
        {
            var widget = new PastableWidget(new EditorWidget(new EditorState(new Hex1bDocument("retained")))).OnOrderedPaste(_ =>
            { starts++; return _ => { }; });
            return timeout ? widget.Timeout(TimeSpan.FromSeconds(1)) : widget.MaxSize(100);
        }, "\u001b[200~small\u001b[201~"));
        Assert.AreEqual(0, starts);
        StringAssert.Contains(error.Message, "legacy");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_DescendantReceiverOrNull_RespectsBubbleOrder(bool decline)
    {
        var calls = new List<string>();
        var text = new StringBuilder();
        await RunAsync(_ => new PastableWidget(new TextBoxWidget().OnOrderedPaste(_ =>
        {
            calls.Add("child");
            if (decline) return null;
            return update => { if (update.Phase == OrderedPastePhase.Chunk) text.Append(update.Text); else calls.Add("child:" + update.Phase); };
        })).OnOrderedPaste(_ =>
        {
            calls.Add("parent");
            return update => { if (update.Phase == OrderedPastePhase.Chunk) text.Append(update.Text); else calls.Add("parent:" + update.Phase); };
        }), "\u001b[200~payload\u001b[201~");
        TestSeq.AreEqual(decline ? new[] { "child", "parent", "parent:Completed" } : new[] { "child", "child:Completed" }, calls);
        Assert.AreEqual("payload", text.ToString());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task OrderedPaste_ReconcileDuringPaste_PreservesCapturedReceiverOrCancelsDetach(bool detach)
    {
        var changed = false;
        var calls = new List<string>();
        var text = new StringBuilder();
        var state = new EditorState(new Hex1bDocument("readonly")) { IsReadOnly = true };
        await RunAsync(app =>
        {
            if (changed && detach) return new VStackWidget([new TextBlockWidget("changed-ready"), new ButtonWidget("replacement")]);
            var capturedChanged = changed;
            return new PastableWidget(new VStackWidget([
                new TextBlockWidget(changed ? "changed-ready" : "original-ready"), new EditorWidget(state)
            ])).OnOrderedPaste(_ =>
            {
                calls.Add(capturedChanged ? "new-begin" : "old-begin");
                changed = true;
                app.Invalidate();
                return update =>
                {
                    if (update.Phase == OrderedPastePhase.Chunk) text.Append(update.Text);
                    else calls.Add((capturedChanged ? "new:" : "old:") + update.Phase);
                };
            });
        }, async (terminal, presentation) =>
        {
            presentation.Send("\u001b[200~");
            using var changedFrame = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("changed-ready"), TimeSpan.FromSeconds(5), "reconcile after paste begin")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            presentation.Send("late\u001b[201~");
        });
        TestSeq.AreEqual(new[] { "old-begin", detach ? "old:Cancelled" : "old:Completed" }, calls);
        Assert.AreEqual(detach ? "" : "late", text.ToString());
        Assert.AreEqual("readonly", state.Document.GetText());
    }

    [TestMethod]
    public async Task OrderedPaste_ContainerDeclines_AncestorReceivesExactlyOneOperation()
    {
        var calls = new List<string>();
        var text = new StringBuilder();
        await RunAsync(_ => new PastableWidget(
            new PastableWidget(new EditorWidget(new EditorState(new Hex1bDocument("reader"))))
                .OnOrderedPaste(_ => { calls.Add("inner"); return null; }))
            .OnOrderedPaste(_ =>
            {
                calls.Add("outer");
                return update => { if (update.Phase == OrderedPastePhase.Chunk) text.Append(update.Text); else calls.Add(update.Phase.ToString()); };
            }), "\u001b[200~once\u001b[201~");
        TestSeq.AreEqual(new[] { "inner", "outer", "Completed" }, calls);
        Assert.AreEqual("once", text.ToString());
    }

    [TestMethod]
    public async Task OrderedPaste_ShutdownWithoutEnd_ReportsShutdownOnce()
    {
        var calls = new List<string>();
        var state = new EditorState(new Hex1bDocument("reader")) { IsReadOnly = true };
        await RunAsync(app => new PastableWidget(new EditorWidget(state)).OnOrderedPaste(_ =>
        {
            calls.Add("begin");
            app.RequestStop();
            return update => { if (update.Phase != OrderedPastePhase.Chunk) calls.Add(update.Phase.ToString()); };
        }), "\u001b[200~");
        TestSeq.AreEqual(new[] { "begin", "Shutdown" }, calls);
        Assert.AreEqual("reader", state.Document.GetText());
    }

    private static Task RunAsync(Func<Hex1bApp, Hex1bWidget> build, string input)
        => RunAsync(build, (_, presentation) => { presentation.Send(input); return Task.CompletedTask; });

    private static async Task RunAsync(Func<Hex1bApp, Hex1bWidget> build, Func<Hex1bTerminal, InputPresentation, Task> drive)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var presentation = new InputPresentation();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithOrderedPasteInput(2)
            .WithHex1bApp(_ => { }, app => _ => new VStackWidget([
                new TextBlockWidget("paste-container-ready"), build(app)
            ]).InputBindings(bindings => bindings.Key(Hex1bKey.F5).Action(_ => app.RequestStop())))
            .WithPresentation(presentation).WithDimensions(50, 8).Build();
        var running = Task.Run(() => terminal.RunAsync(cancellation.Token));
        try
        {
            using var ready = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(snapshot => snapshot.ContainsText("paste-container-ready"), TimeSpan.FromSeconds(5), "initial render")
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
            await drive(terminal, presentation);
            presentation.Send("\u001b[15~");
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            cancellation.Cancel();
            await running.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private sealed class InputPresentation : IHex1bTerminalPresentationAdapter
    {
        private readonly Channel<ReadOnlyMemory<byte>> input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
        public int Width => 50;
        public int Height => 8;
        public TerminalCapabilities Capabilities { get; } = new();
        public event Action<int, int>? Resized { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }
        public void Send(string text) => input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default) => await input.Reader.ReadAsync(ct);
        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public ValueTask DisposeAsync() { input.Writer.TryComplete(); return ValueTask.CompletedTask; }
    }
}
