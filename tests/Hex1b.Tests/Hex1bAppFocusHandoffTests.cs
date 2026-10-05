using Hex1b.Input;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class Hex1bAppFocusHandoffTests
{
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(true, true)]
    [DataRow(false, true)]
    public async Task RequestFocus_QueuedTypingAcrossOpenAndClose_ReachesIntendedEditor(bool coalesce, bool invalidate)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var draft = new TextBoxState { Text = "DRAFT", CursorPosition = 4 };
        var query = new TextBoxState();
        var dialog = false;
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Hex1bApp? app = null;
        Hex1bAppWorkloadAdapter? workload = null;
        var observer = new TestWidget().OnRender(args =>
        {
            if (invalidate) app!.Invalidate();
            if (args.RenderCount != 1) return;
            // One queued burst: both handoffs must take effect before subsequent typing.
            workload!.SendKey(Hex1bKey.F12);
            workload.SendKey(Hex1bKey.M, 'm');
            workload.SendKey(Hex1bKey.Escape);
            workload.SendKey(Hex1bKey.N, 'n');
            workload.SendKey(Hex1bKey.F9);
        });
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bApp(options =>
            {
                workload = TestSeq.IsType<Hex1bAppWorkloadAdapter>(options.WorkloadAdapter);
                options.EnableInputCoalescing = coalesce;
                options.EnableRescue = false;
            }, instance =>
            {
                app = instance;
                return _ => new VStackWidget([
                    new TextBlockWidget(dialog ? "Menu" : "Prompt"),
                    new TextBoxWidget().State(dialog ? query : draft), observer
                ]).InputBindings(bindings =>
                {
                    bindings.Key(Hex1bKey.F12).Action(_ =>
                    {
                        dialog = true;
                        app.RequestFocus(n => n is TextBoxNode);
                        app.Invalidate();
                    });
                    bindings.Key(Hex1bKey.Escape).Action(_ =>
                    {
                        dialog = false;
                        app.RequestFocus(n => n is TextBoxNode);
                        app.Invalidate();
                    });
                    bindings.Key(Hex1bKey.F9).Action(_ => finished.TrySetResult());
                });
            })
            .WithHeadless().WithDimensions(40, 6).Build();
        var run = terminal.RunAsync(cancellation.Token);
        try
        {
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            Assert.AreEqual("m", query.Text, "Typing after opening belongs only to the menu.");
            Assert.AreEqual("DRAFnT", draft.Text, "Typing after closing returns to the preserved prompt cursor.");
            Assert.AreEqual(5, draft.CursorPosition);
            using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("Prompt") && s.ContainsText("DRAFnT"), TimeSpan.FromSeconds(5))
                .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
        }
        finally
        {
            cancellation.Cancel();
            await run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }
    }
}
