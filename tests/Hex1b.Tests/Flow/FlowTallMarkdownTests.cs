using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hex1b.Flow;
using Hex1b.Markdown;
using Hex1b.Surfaces;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
[DoNotParallelize]
public class FlowTallMarkdownTests
{
    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    [DataRow(true, 80)]
    [DataRow(false, 20)]
    public async Task PublicMaterialization_TallMarkdown_PreservesEveryUnicodePhrase(bool code, int width)
    {
        const int count = 10_020;
        var phrases = Enumerable.Range(1, count).Select(i => $"TALL-{i:00000} café 界").ToArray();
        var source = code ? "```text\n" + string.Join('\n', phrases) + "\n```" : string.Join(" ", phrases);
        var clock = Stopwatch.StartNew();
        var allocated = GC.GetTotalAllocatedBytes(true);
        var widget = new MarkdownWidget(MarkdownParser.Parse(source));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.CancellationToken);
        stop.CancelAfter(TimeSpan.FromSeconds(45));
        using var terminal = Hex1bTerminal.CreateBuilder().WithHex1bFlow(async flow =>
        {
            var step = flow.Step(ctx => ctx.Text("tall materialization remains live"));
            Surface? surface = null;
            try
            {
                await step.WaitForReadyAsync(stop.Token);
                // Both public entry points independently exercise their admission path.
                var measured = code ? default : await step.MeasureWidgetAsync(widget, width, 50_000, stop.Token);
                surface = await step.RenderWidgetAsync(widget, width, 50_000, stop.Token);
                if (code) measured = await step.MeasureWidgetAsync(widget, width, 50_000, stop.Token);
                Assert.IsTrue(surface.Height > 10_000, "This must exercise the removed height refusal.");
                Assert.AreEqual(measured.Height, surface.Height);
                Assert.AreEqual(width, surface.Width);
                var output = new StringBuilder();
                for (var y = 0; y < surface.Height; y++)
                {
                    for (var x = 0; x < surface.Width; x++)
                    {
                        var cell = surface.GetCell(x, y);
                        if (!cell.IsContinuation) output.Append(cell.Character);
                    }
                    output.Append('\n');
                }
                var actual = Regex.Matches(output.ToString(), @"TALL-\d{5} café 界").Select(match => match.Value).ToArray();
                CollectionAssert.AreEqual(phrases, actual, "Every full phrase, including the last, must occur exactly once in order.");
                TestContext.WriteLine(JsonSerializer.Serialize(new
                {
                    scope = "Single-run resource observation; no upper bound or native-host claim",
                    code, width, count, surface.Height, sourceChars = source.Length,
                    elapsedMs = clock.Elapsed.TotalMilliseconds,
                    allocatedBytes = GC.GetTotalAllocatedBytes(true) - allocated,
                    peakWorkingSetBytes = Process.GetCurrentProcess().PeakWorkingSet64
                }));
            }
            finally
            {
                surface?.ClearAndReleaseTrackedObjects();
                await step.CompleteAsync();
            }
        }).WithHeadless().WithDimensions(80, 24).Build();
        await terminal.RunAsync(stop.Token);
    }
}
