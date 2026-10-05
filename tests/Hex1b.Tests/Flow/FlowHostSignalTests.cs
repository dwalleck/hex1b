using System.Reflection;
using Hex1b.Flow;
using Hex1b.Tokens;

namespace Hex1b.Tests.Flow;

[TestClass]
public class FlowHostSignalTests
{
    [TestMethod]
    public async Task SetWindowTitle_LiteralUnicodeAndSemicolons_PreservesTitleAndCellsWithoutRinging()
    {
        await WithFlowAsync((flow, parent) =>
        {
            const string title = ";Janet;東京 😀;<b>literal</b>;";
            var start = parent.SnapshotWrites();
            var available = flow.AvailableHeight;
            flow.SetWindowTitle(title);
            var output = TestSeq.Single(parent.SnapshotWritesSince(start));
            Assert.AreEqual("\x1b]2;" + title + "\x1b\\", output);
            Assert.IsFalse(output.Contains('\a'));
            Assert.AreEqual(available, flow.AvailableHeight);
            using var workload = new Hex1bAppWorkloadAdapter();
            using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload)
                .WithHeadless().WithDimensions(12, 3).Build();
            terminal.ApplyTokens(AnsiTokenizer.Tokenize("AB"));
            using var before = terminal.CreateSnapshot();
            terminal.ApplyTokens(AnsiTokenizer.Tokenize(output));
            using var after = terminal.CreateSnapshot();
            Assert.AreEqual(title, after.WindowTitle);
            Assert.AreEqual(before.CursorX, after.CursorX);
            Assert.AreEqual(before.CursorY, after.CursorY);
            for (var y = 0; y < 3; y++)
                for (var x = 0; x < 12; x++) Assert.AreEqual(before.GetCell(x, y), after.GetCell(x, y));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task SetWindowTitle_ControlsAndMalformedSurrogates_EmitsOnlyNormalizedPlainTitle()
    {
        await WithFlowAsync((flow, parent) =>
        {
            var controls = new string(Enumerable.Range(0, 32).Concat(Enumerable.Range(127, 33))
                .Select(value => (char)value).ToArray());
            var start = parent.SnapshotWrites();
            flow.SetWindowTitle(controls + "safe;東京 😀" + "\ud800x\udc00");
            Assert.AreEqual("\x1b]2;safe;東京 😀\ufffdx\ufffd\x1b\\",
                TestSeq.Single(parent.SnapshotWritesSince(start)));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    [DataRow(4094, true)]
    [DataRow(4095, false)]
    [DataRow(4096, false)]
    [DataRow(4097, false)]
    public async Task SetWindowTitle_LengthBoundary_DoesNotSplitTheSurrogatePair(int prefixLength, bool keepPair)
    {
        await WithFlowAsync((flow, parent) =>
        {
            var start = parent.SnapshotWrites();
            flow.SetWindowTitle(new string('x', prefixLength) + "😀tail");
            var expected = new string('x', Math.Min(prefixLength, 4096)) + (keepPair ? "😀" : "");
            Assert.AreEqual("\x1b]2;" + expected + "\x1b\\", TestSeq.Single(parent.SnapshotWritesSince(start)));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task SetWindowTitle_EmptyAndNull_EmptyClearsAndNullWritesNothing()
    {
        await WithFlowAsync((flow, parent) =>
        {
            var start = parent.SnapshotWrites();
            flow.SetWindowTitle("working");
            flow.SetWindowTitle("");
            TestSeq.AreEqual(new[] { "\x1b]2;working\x1b\\", "\x1b]2;\x1b\\" }, parent.SnapshotWritesSince(start));
            var beforeNull = parent.SnapshotWrites();
            Assert.ThrowsExactly<ArgumentNullException>(() => flow.SetWindowTitle(null!));
            Assert.AreEqual(beforeNull, parent.SnapshotWrites());
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task RequestAttention_ExplicitCalls_EmitOneStandaloneBellPerCall()
    {
        await WithFlowAsync((flow, parent) =>
        {
            var start = parent.SnapshotWrites();
            flow.RequestAttention();
            flow.RequestAttention();
            TestSeq.AreEqual(new[] { "\a", "\a" }, parent.SnapshotWritesSince(start));
            return Task.CompletedTask;
        });
    }

    [TestMethod]
    public async Task HostSignals_InsideExistingAtomicUpdate_JoinOneOrderedBatch()
    {
        var parent = new RecordingParentAdapter(80, 24);
        Hex1bFlowRunner? runner = null;
        runner = new(async flow =>
        {
            var start = parent.SnapshotWrites();
            // Inject the existing atomic scope without widening the public API.
            // Public Flow operations remain the behavior under test.
            var begin = typeof(Hex1bFlowRunner).GetMethod("BeginAtomicTerminalUpdate", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var update = (IAtomicTerminalUpdate)begin.Invoke(runner, [null])!;
            using (update)
            {
                flow.SetWindowTitle("working");
                flow.RequestAttention();
                flow.SetWindowTitle("idle");
                Assert.AreEqual(start, parent.SnapshotWrites(), "Nothing may escape the active batch.");
            }
            Assert.IsTrue(update.FlushCompleted);
            Assert.AreEqual("\x1b[?2026h\x1b]2;working\x1b\\\a\x1b]2;idle\x1b\\\x1b[?2026l",
                TestSeq.Single(parent.SnapshotWritesSince(start)));
            await Task.CompletedTask;
        }, new Hex1bFlowOptions { InitialCursorRow = 0 }, parent);
        await runner.RunAsync(TestContext.Current.CancellationToken);
    }

    [TestMethod]
    public async Task HostSignals_RealTerminalFlowCallback_PresentsIdleTitleWithoutCellCorruption()
    {
        Hex1bTerminal terminal = null!;
        using var lifetime = terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                flow.SetWindowTitle("working");
                flow.RequestAttention();
                flow.SetWindowTitle("idle 東京");
                using var snapshot = await new Hex1bTerminalInputSequenceBuilder()
                    .WaitUntil(s => s.WindowTitle == "idle 東京", TimeSpan.FromSeconds(5), "native-facing flow title processed")
                    .Build().ApplyAsync(terminal, TestContext.Current.CancellationToken);
                Assert.AreEqual("idle 東京", snapshot.WindowTitle);
                for (var y = 0; y < 5; y++)
                    for (var x = 0; x < 20; x++) Assert.AreEqual(" ", snapshot.GetCell(x, y).Character);
            }, options => options.InitialCursorRow = 0)
            .WithHeadless().WithDimensions(20, 5).Build();
        await terminal.RunAsync(TestContext.Current.CancellationToken).WaitAsync(TimeSpan.FromSeconds(15));
    }

    private static async Task WithFlowAsync(Func<Hex1bFlowContext, RecordingParentAdapter, Task> action)
    {
        var parent = new RecordingParentAdapter(80, 24);
        var runner = new Hex1bFlowRunner(flow => action(flow, parent), new Hex1bFlowOptions { InitialCursorRow = 0 }, parent);
        await runner.RunAsync(TestContext.Current.CancellationToken);
    }
}
