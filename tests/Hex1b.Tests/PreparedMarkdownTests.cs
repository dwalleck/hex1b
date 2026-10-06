using Hex1b.Documents;
using Hex1b.Input;
using Hex1b.Markdown;
using Hex1b.Widgets;

namespace Hex1b.Tests;

[TestClass]
public class PreparedMarkdownTests
{
    [TestMethod]
    public async Task PreparedDocument_RendersAstInsteadOfSource()
    {
        var document = MarkdownParser.Parse("PREPARED café 界🚀");
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(60, 12).Build();
        using var app = new Hex1bApp(_ => new MarkdownWidget(document) { Source = "SOURCE-DECOY" },
            new Hex1bAppOptions { WorkloadAdapter = workload });
        var running = app.RunAsync(stop.Token);
        try
        {
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("PREPARED café 界🚀") && !s.ContainsText("SOURCE-DECOY"),
                    TimeSpan.FromSeconds(5), "prepared AST rendered without parsing Source")
                .Ctrl().Key(Hex1bKey.C).Build().ApplyAsync(terminal, stop.Token);
            await running;
        }
        finally
        {
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
    [TestMethod]
    public void PreparedDocument_NullRejected()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new MarkdownWidget((MarkdownDocument)null!));
    }

    [TestMethod]
    public async Task PreparedDocument_NewInstanceReplacesOldContent()
    {
        await RenderStagesAsync([
            new(MarkdownParser.Parse("AST-FIRST")),
            new(MarkdownParser.Parse("AST-SECOND"))
        ], ["AST-FIRST", "AST-SECOND"], ["AST-SECOND", "AST-FIRST"]);
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("SAME-SOURCE")]
    public async Task PreparedDocument_SourceTransitionsClearPreviousInput(string source)
    {
        // Prepared content intentionally differs from Source, including equal/empty
        // Source across the mode transition. The AST must not escape into source mode.
        var prepared = new MarkdownWidget(MarkdownParser.Parse("AST-CONTENT")) { Source = source };
        await RenderStagesAsync([new(source), prepared, new(source), prepared],
            [source, "AST-CONTENT", source, "AST-CONTENT"],
            ["AST-CONTENT", "SAME-SOURCE", "AST-CONTENT", "SAME-SOURCE"]);
    }

    [TestMethod]
    public async Task PreparedDocument_VersionedTransitionsAndUpdatesUseCurrentInput()
    {
        var document = new Hex1bDocument("DOC-ORIGINAL");
        var prepared = new MarkdownWidget(MarkdownParser.Parse("AST-CONTENT"));
        await RenderStagesAsync([new(document), prepared, new(document), new(document), new("DOC-UPDATED"), new(document)],
            ["DOC-ORIGINAL", "AST-CONTENT", "DOC-ORIGINAL", "DOC-UPDATED", "DOC-UPDATED", "DOC-NEWEST"],
            ["AST-CONTENT", "DOC-ORIGINAL", "AST-CONTENT", "DOC-ORIGINAL", "DOC-NEWEST", "DOC-UPDATED"],
            stage =>
            {
                if (stage is 3 or 5)
                    document.Apply(new ReplaceOperation(new DocumentRange(new DocumentOffset(0),
                        new DocumentOffset(document.Length)), stage == 3 ? "DOC-UPDATED" : "DOC-NEWEST"));
            });
    }

    [TestMethod]
    public async Task PreparedDocument_SameAstUsesCurrentBlockHandler()
    {
        var document = MarkdownParser.Parse("# ORIGINAL");
        var heading = document.Blocks[0];
        MarkdownWidget WithHandler(string text) => new MarkdownWidget(document)
            .OnBlock<HeadingBlock>((_, block) =>
            {
                Assert.AreSame(heading, block, "The renderer must receive the borrowed AST block, not a reparsed copy.");
                return new TextBlockWidget(text);
            });
        await RenderStagesAsync([WithHandler("HANDLER-FIRST"), WithHandler("HANDLER-SECOND")],
            ["HANDLER-FIRST", "HANDLER-SECOND"], ["HANDLER-SECOND", "HANDLER-FIRST"]);
    }

    [TestMethod]
    public async Task PreparedDocument_ReferencesCodeUnicodeAndUpdatedLinkSettingsUseExistingRenderer()
    {
        var document = MarkdownParser.Parse("[Go 界][target]\n\n```text\nCODE café 🚀\n```\n\n[target]: https://example.com/prepared");
        var enabled = false;
        string? activated = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(60, 12).Build();
        using var app = new Hex1bApp(ctx => ctx.VStack(v =>
            [v.Text(activated ?? (enabled ? "LINK-ENABLED" : "LINK-DISABLED")),
             new MarkdownWidget(document).Focusable(children: enabled)
                .OnLinkActivated(enabled ? args => activated = "CURRENT " + args.Url
                    : args => activated = "STALE " + args.Url)])
            .InputBindings(bindings => bindings.Key(Hex1bKey.F4).Action(_ => enabled = true)),
            new Hex1bAppOptions { WorkloadAdapter = workload });
        var running = app.RunAsync(stop.Token);
        try
        {
            await new Hex1bTerminalInputSequenceBuilder()
                .WaitUntil(s => s.ContainsText("LINK-DISABLED") && s.ContainsText("Go 界") &&
                    s.ContainsText("CODE café 🚀"), TimeSpan.FromSeconds(5), "prepared references and code rendered")
                .Key(Hex1bKey.F4)
                .WaitUntil(s => s.ContainsText("LINK-ENABLED"), TimeSpan.FromSeconds(5), "same AST with current settings")
                .Tab().Key(Hex1bKey.Enter)
                .WaitUntil(s => s.ContainsText("CURRENT https://example.com/prepared") && !s.ContainsText("STALE"),
                    TimeSpan.FromSeconds(5), "reference link resolves through current focus and callback")
                .Ctrl().Key(Hex1bKey.C).Build().ApplyAsync(terminal, stop.Token);
            await running;
            Assert.AreEqual("CURRENT https://example.com/prepared", activated);
        }
        finally
        {
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static async Task RenderStagesAsync(MarkdownWidget[] widgets, string[] expected, string[] absent,
        Action<int>? advance = null)
    {
        var stage = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
            .WithHeadless().WithDimensions(60, 12).Build();
        using var app = new Hex1bApp(ctx => ctx.VStack(v =>
            [v.Text($"STAGE-{stage}"), widgets[stage]])
            .InputBindings(bindings => bindings.Key(Hex1bKey.F4).Action(_ =>
            {
                stage++;
                advance?.Invoke(stage);
            })), new Hex1bAppOptions { WorkloadAdapter = workload });
        var running = app.RunAsync(stop.Token);
        try
        {
            for (var i = 0; i < widgets.Length; i++)
            {
                var current = i;
                var sequence = new Hex1bTerminalInputSequenceBuilder();
                if (i > 0) sequence.Key(Hex1bKey.F4);
                await sequence.WaitUntil(s => s.ContainsText($"STAGE-{current}") &&
                    (expected[current].Length == 0 || s.ContainsText(expected[current])) &&
                    !s.ContainsText(absent[current]), TimeSpan.FromSeconds(5),
                    $"stage {current} uses exact current input and settings")
                    .Build().ApplyAsync(terminal, stop.Token);
            }
            await new Hex1bTerminalInputSequenceBuilder().Ctrl().Key(Hex1bKey.C)
                .Build().ApplyAsync(terminal, stop.Token);
            await running;
        }
        finally
        {
            await stop.CancelAsync();
            try { await running; } catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }
}
