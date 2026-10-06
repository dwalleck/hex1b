using System.IO.Pipelines;
using Hex1b.Automation;
using Hex1b.Flow;
using Hex1b.Input;
using Hex1b.Reflow;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Issue 58: under Flow soft-wrap emission every forwarded live frame parks the
/// host cursor at the live region's top-left, because that park is the anchor
/// commit admission and resize repaint observe. The focused node's cursor is
/// therefore drawn into the frame as a styled cell, and the hardware cursor
/// stays hidden at the park.
/// </summary>
[TestClass]
public class FlowFocusedCursorTests
{
    private static readonly Hex1bColor CaretForeground = Hex1bColor.FromRgb(11, 22, 33);
    private static readonly Hex1bColor CaretBackground = Hex1bColor.FromRgb(201, 202, 203);

    private static Hex1bTheme CaretTheme() => Hex1bThemes.Default.Clone("issue58")
        .Set(TextBoxTheme.CursorForegroundColor, CaretForeground)
        .Set(TextBoxTheme.CursorBackgroundColor, CaretBackground);

    private static bool IsCaretCell(TerminalCell cell) =>
        cell.Background is { } bg && bg.R == CaretBackground.R && bg.G == CaretBackground.G && bg.B == CaretBackground.B
        && cell.Foreground is { } fg && fg.R == CaretForeground.R && fg.G == CaretForeground.G && fg.B == CaretForeground.B;

    private static List<(int X, int Y)> CaretCells(Hex1bTerminalSnapshot snapshot)
    {
        var cells = new List<(int, int)>();
        for (var y = 0; y < snapshot.Height; y++)
            for (var x = 0; x < snapshot.Width; x++)
                if (IsCaretCell(snapshot.GetCell(x, y))) cells.Add((x, y));
        return cells;
    }

    private static async Task<Hex1bTerminalSnapshot> WaitForAsync(
        Hex1bTerminal terminal, Func<Hex1bTerminalSnapshot, bool> predicate, string what, CancellationToken ct)
    {
        var deadline = Environment.TickCount64 + 10000;
        while (true)
        {
            var snapshot = terminal.CreateSnapshot();
            if (predicate(snapshot)) return snapshot;
            if (Environment.TickCount64 > deadline)
                Assert.Fail($"timed out waiting for {what}; screen:\n{snapshot.GetScreenText()}\ncaret cells: {string.Join(",", CaretCells(snapshot))}; reverse cells: {string.Join(",", ReverseCells(snapshot))}");
            await Task.Delay(10, ct);
        }
    }

    [TestMethod]
    public async Task FlowSoftWrap_FocusedTextBox_DrawsCaretCellAtEditingPositionAndKeepsHiddenCursorParked()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 8).Build();
        var state = new TextBoxState("abcdef") { CursorPosition = 3 };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => [v.Text("HEADER-58"), v.TextBox().State(state)]),
                o => { o.MinHeight = 3; o.MaxHeight = 3; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 2, Theme = CaretTheme() }, workload);
        var running = runner.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            var shown = await WaitForAsync(terminal,
                s => s.GetScreenText().Contains("abcdef", StringComparison.Ordinal) && CaretCells(s).Count > 0,
                "a drawn caret", stop.Token);

            // The editing position: row 3 (live origin 2 + header row), column 3, the 'd'.
            CollectionAssert.AreEqual(new List<(int, int)> { (3, 3) }, CaretCells(shown),
                "exactly one caret cell, at the editing position");
            Assert.AreEqual("d", shown.GetCell(3, 3).Character, "the caret cell keeps the character under it");
            Assert.IsFalse(shown.CursorVisible, "the hardware cursor stays hidden under Flow soft-wrap");
            Assert.AreEqual((0, 2), (shown.CursorX, shown.CursorY), "the hidden cursor stays parked at the live origin");
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task FlowSoftWrap_CaretCellFollowsMovementAndLeavesWithFocus()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 8).Build();
        var state = new TextBoxState("abcdef") { CursorPosition = 3 };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => [v.Text("HEADER-58"), v.TextBox().State(state), v.Button("OK-58")]),
                o => { o.MinHeight = 3; o.MaxHeight = 3; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 2, Theme = CaretTheme() }, workload);
        var running = runner.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s) is [(3, 3)], "the caret at column 3", stop.Token);

            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.RightArrow).Build().ApplyAsync(terminal, stop.Token);
            var moved = await WaitForAsync(terminal, s => CaretCells(s) is [(4, 3)], "the caret repainted at column 4", stop.Token);
            Assert.AreEqual("e", moved.GetCell(4, 3).Character);
            Assert.IsFalse(IsCaretCell(moved.GetCell(3, 3)), "the old caret cell is repainted plainly");

            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.End).Build().ApplyAsync(terminal, stop.Token);
            var atEnd = await WaitForAsync(terminal, s => CaretCells(s) is [(6, 3)], "the caret past the last character", stop.Token);
            Assert.AreEqual(" ", atEnd.GetCell(6, 3).Character, "a caret past the text draws a blank cell");

            await new Hex1bTerminalInputSequenceBuilder().Tab().Build().ApplyAsync(terminal, stop.Token);
            var away = await WaitForAsync(terminal, s => CaretCells(s).Count == 0, "no caret once focus leaves the editor", stop.Token);
            Assert.IsFalse(away.CursorVisible, "focus elsewhere does not show the hardware cursor");
            Assert.AreEqual((0, 2), (away.CursorX, away.CursorY));

            await new Hex1bTerminalInputSequenceBuilder().Shift().Tab().Build().ApplyAsync(terminal, stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s) is [(6, 3)], "the caret returns with focus", stop.Token);
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static List<(int X, int Y)> ReverseCells(Hex1bTerminalSnapshot snapshot)
    {
        var cells = new List<(int, int)>();
        for (var y = 0; y < snapshot.Height; y++)
            for (var x = 0; x < snapshot.Width; x++)
                if ((snapshot.GetCell(x, y).Attributes & CellAttributes.Reverse) != 0) cells.Add((x, y));
        return cells;
    }

    [TestMethod]
    public async Task FlowSoftWrap_FocusedEmbeddedTerminal_DrawsChildCursorAsReverseCellWhileVisible()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var childOutput = new Pipe();
        var childInput = new Pipe();
        await using var childWorkload = new StreamWorkloadAdapter(childOutput.Reader.AsStream(), childInput.Writer.AsStream());
        using var childTerminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(childWorkload).WithHeadless().WithDimensions(30, 4)
            .WithTerminalWidget(out var handle).Build();
        var childRun = childTerminal.RunAsync(stop.Token);
        async Task ChildWrites(string text)
        {
            await childOutput.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), stop.Token);
            await childOutput.Writer.FlushAsync(stop.Token);
        }

        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 8).Build();
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => [v.Text("EDITOR-58"), v.Terminal(handle).Fill()]),
                o => { o.MinHeight = 5; o.MaxHeight = 5; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 2 }, workload);
        var running = runner.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            // Child row 1, column 3 (the 'e' of "line two") is live row 3 + 1 = screen row 4.
            var marker = "VIM";
            await ChildWrites($"\x1b[2J\x1b[1;1H{marker}-58 line one\r\nline two\x1b[2;4H\x1b[?25h");
            var shown = await WaitForAsync(terminal,
                s => s.GetScreenText().Contains("line two", StringComparison.Ordinal) && ReverseCells(s).Count > 0,
                "the child's cursor drawn", stop.Token);
            CollectionAssert.AreEqual(new List<(int, int)> { (3, 4) }, ReverseCells(shown),
                "exactly one reverse cell, at the child's cursor");
            Assert.AreEqual("e", shown.GetCell(3, 4).Character);
            Assert.IsFalse(shown.CursorVisible, "the hardware cursor stays hidden");
            Assert.AreEqual((0, 2), (shown.CursorX, shown.CursorY), "and parked at the live origin");

            await ChildWrites("\x1b[1;2H");
            await WaitForAsync(terminal, s => ReverseCells(s) is [(1, 3)], "the drawn cursor follows a cursor-only move", stop.Token);

            await ChildWrites("\x1b[?25l");
            await WaitForAsync(terminal, s => ReverseCells(s).Count == 0, "no drawn cursor while the child hides it", stop.Token);

            await ChildWrites("\x1b[?25h\x1b[2;10H");
            var blank = await WaitForAsync(terminal, s => ReverseCells(s) is [(9, 4)], "the drawn cursor past the text", stop.Token);
            Assert.AreEqual(" ", blank.GetCell(9, 4).Character);

            // A child cursor on the second column of a wide glyph marks the glyph,
            // never splits it.
            await ChildWrites("\x1b[3;1Hx界y\x1b[3;3H");
            var wide = await WaitForAsync(terminal, s => ReverseCells(s) is [(1, 5), (2, 5)], "the drawn cursor on both columns of the wide glyph", stop.Token);
            Assert.AreEqual("界", wide.GetCell(1, 5).Character);
            Assert.AreEqual("y", wide.GetCell(3, 5).Character, "the row is not shifted");

            // On a cell the child already shows in reverse the drawn cursor inverts it back.
            await ChildWrites("\x1b[4;1H\x1b[7mREV\x1b[0m\x1b[4;2H");
            var inverted = await WaitForAsync(terminal, s => ReverseCells(s) is [(0, 6), (2, 6)], "the cursor un-reversing a reverse cell", stop.Token);
            Assert.AreEqual("E", inverted.GetCell(1, 6).Character);

            // Copy mode draws its own cursor and selection; the child's cursor is not drawn over them.
            handle.EnterCopyMode();
            // The copy cursor starts at the child's cursor: it alone reverses the 'E' (a drawn child cursor would cancel it).
            await WaitForAsync(terminal, s => ReverseCells(s) is [(0, 6), (1, 6), (2, 6)], "only the copy-mode cursor", stop.Token);
            handle.ExitCopyMode();
            await WaitForAsync(terminal, s => ReverseCells(s) is [(0, 6), (2, 6)], "the child cursor after copy mode", stop.Token);
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            try { childOutput.Writer.Complete(); } catch (InvalidOperationException) { }
            try { await childRun.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)] // soft-wrap emission outside Flow: nothing parks the cursor, so the native caret stays
    public async Task NonFlowApp_FocusedTextBox_KeepsNativeCaretAndDrawsNoCell(bool softWrap)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 6).Build();
        var state = new TextBoxState("abcdef") { CursorPosition = 3 };
        using var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(
                ctx.VStack(v => [v.Text("HEADER-58"), v.TextBox().State(state)])),
            new Hex1bAppOptions { WorkloadAdapter = workload, Theme = CaretTheme(), UseSoftWrapEmission = softWrap });
        var running = app.RunAsync(stop.Token);
        try
        {
            var shown = await WaitForAsync(terminal,
                s => s.GetScreenText().Contains("abcdef", StringComparison.Ordinal) && s.CursorVisible,
                "the native caret", stop.Token);
            Assert.AreEqual((3, 1), (shown.CursorX, shown.CursorY), "outside Flow the hardware caret marks the editing position");
            Assert.AreEqual(0, CaretCells(shown).Count, "outside Flow no caret cell is drawn");
        }
        finally
        {
            app.RequestStop();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private sealed class RowsSource(int rows) : FlowCommitSource
    {
        public override int UnitCount => rows;

        public override Task<FlowCommitUnit> UnitAsync(int index, int width, CancellationToken cancellationToken)
        {
            var surface = new Hex1b.Surfaces.Surface(Math.Max(1, width), 1);
            surface.WriteText(0, 0, $"HISTORY-58-{index:00}");
            return Task.FromResult(new FlowCommitUnit($"h{index:00}", surface));
        }
    }

    /// <summary>Records the host-bound output, so a failure can show the bytes that produced the model.</summary>
    private sealed class OutputLog : IHex1bTerminalOutputObserver
    {
        private readonly System.Text.StringBuilder _text = new();
        public string Tail(int length)
        {
            lock (_text)
            {
                var all = _text.ToString();
                return (all.Length > length ? all[^length..] : all).Replace("\x1b", "^[", StringComparison.Ordinal);
            }
        }
        public ValueTask<IReadOnlyList<Hex1b.Tokens.AnsiToken>> OnOutputAsync(IReadOnlyList<Hex1b.Tokens.AppliedToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            var output = tokens.Select(t => t.Token).ToList();
            lock (_text) _text.Append(Hex1b.Tokens.AnsiTokenSerializer.Serialize(output));
            return ValueTask.FromResult<IReadOnlyList<Hex1b.Tokens.AnsiToken>>(output);
        }
        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
        {
            lock (_text) _text.Append($"<<RESIZE {width}x{height}>>");
            return ValueTask.CompletedTask;
        }
        public ValueTask OnInputAsync(IReadOnlyList<Hex1b.Tokens.AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private static Hex1bTerminal CreateFlowTerminal(Func<Hex1bFlowContext, Task> flow, int width, int height, OutputLog? log = null)
    {
        var builder = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(flow, options =>
            {
                options.UseSoftWrapTombstones = true;
                options.ResizeSettleDelay = TimeSpan.FromMilliseconds(80);
                options.Theme = CaretTheme();
            })
            .WithDimensions(width, height)
            .WithHeadless()
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(500);
        if (log is not null) builder = builder.AddPresentationFilter(log);
        return builder.Build();
    }

    private static int CaretCellsInScrollback(Hex1bTerminal terminal) =>
        terminal.GetScrollbackRows(terminal.ScrollbackCount).Sum(row => row.Cells.Count(IsCaretCell));

    private static Task<Hex1bWidget> PromptLive(FlowStepContext ctx, TextBoxState state) =>
        Task.FromResult<Hex1bWidget>(ctx.VStack(v => [v.Text("LIVE-58"), v.TextBox().State(state)]));

    [TestMethod]
    public async Task FlowSoftWrap_CommitAndCompletion_NeverCarryTheDrawnCaretIntoHistory()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state = new TextBoxState("draft") { CursorPosition = 2 };
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var commit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        FlowCommitResult? result = null;
        using var terminal = CreateFlowTerminal(async flow =>
        {
            var step = flow.Step(ctx => PromptLive(ctx, state), o => { o.MinHeight = 2; o.MaxHeight = 2; });
            await step.WaitForReadyAsync();
            ready.TrySetResult(step);
            await commit.Task;
            result = await step.CommitAsync(new RowsSource(14), ctx => PromptLive(ctx, state));
            finish.TrySetResult();
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, width: 30, height: 8);
        var running = terminal.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await ready.Task.WaitAsync(stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s).Count == 1, "the live caret before commit", stop.Token);
            // Whatever showed the host cursor before (a shell, an earlier owner), the commit's repaint of the live
            // image hides it again: the image carries the drawn caret.
            terminal.ApplyTokens(Hex1b.Tokens.AnsiTokenizer.Tokenize("\x1b[?25h"));
            Assert.IsTrue(terminal.CreateSnapshot().CursorVisible);
            commit.TrySetResult();
            await finish.Task.WaitAsync(stop.Token);
            Assert.AreEqual(FlowCommitStatus.Emitted, result!.Status);

            var after = await WaitForAsync(terminal,
                s => s.GetScreenText().Contains("HISTORY-58-13", StringComparison.Ordinal) && CaretCells(s).Count == 1,
                "the live caret after commit", stop.Token);
            var live = after.CursorY; // the hidden cursor is parked at the live origin
            Assert.IsFalse(after.CursorVisible, "the commit's repaint keeps the parked cursor hidden");
            Assert.AreEqual("LIVE-58", after.GetScreenText().Split('\n')[live].TrimEnd(), "the park still marks the live origin");
            CollectionAssert.AreEqual(new List<(int, int)> { (2, live + 1) }, CaretCells(after),
                "only the live prompt carries the drawn caret");
            Assert.AreEqual(0, CaretCellsInScrollback(terminal), "no drawn caret reached native scrollback");

            step.Complete();
            await running.WaitAsync(stop.Token);
            // Completion clears the live region (no completed builder): its text and the drawn caret go together.
            await WaitForAsync(terminal, s => !s.GetScreenText().Contains("LIVE-58", StringComparison.Ordinal),
                "the cleared live region", stop.Token);
            var completed = terminal.CreateSnapshot();
            Assert.AreEqual(0, CaretCells(completed).Count,
                $"completion leaves no drawn caret on screen: {string.Join(",", CaretCells(completed))}\n{completed.GetScreenText()}");
            Assert.AreEqual(0, CaretCellsInScrollback(terminal), "completion leaves no drawn caret in scrollback");
            var history = string.Join("\n", terminal.GetScrollbackRows(terminal.ScrollbackCount)
                .Select(r => string.Concat(r.Cells.Select(c => string.IsNullOrEmpty(c.Character) ? " " : c.Character)).TrimEnd()))
                + "\n" + terminal.GetScreenText();
            for (var i = 0; i < 14; i++)
                Assert.AreEqual(1, System.Text.RegularExpressions.Regex.Matches(history, $"HISTORY-58-{i:00}").Count, $"unit {i} committed once");
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    [DataRow(24, 8)] // narrower and shorter
    [DataRow(40, 12)] // wider and taller
    public async Task FlowSoftWrap_Resize_RedrawsCaretAtEditingPositionBelowParkedOrigin(int width, int height)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var state = new TextBoxState("draft") { CursorPosition = 2 };
        var ready = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var committed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var log = new OutputLog();
        using var terminal = CreateFlowTerminal(async flow =>
        {
            var step = flow.Step(ctx => PromptLive(ctx, state), o => { o.MinHeight = 2; o.MaxHeight = 2; });
            await step.WaitForReadyAsync();
            await step.CommitAsync(new RowsSource(6), ctx => PromptLive(ctx, state));
            committed.TrySetResult();
            ready.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, width: 30, height: 10, log);
        var running = terminal.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await ready.Task.WaitAsync(stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s).Count == 1, "the caret before resize", stop.Token);
            await terminal.ResizeWithWorkloadAsync(width, height);
            var resized = await WaitForAsync(terminal,
                s => s.Width == width && s.Height == height && CaretCells(s).Count == 1
                    && s.CursorY < height && s.GetScreenText().Split('\n')[s.CursorY].TrimEnd() == "LIVE-58",
                "the caret redrawn under the parked origin", stop.Token);
            Assert.IsFalse(resized.CursorVisible, $"the hardware cursor stays hidden after resize; output tail:\n{log.Tail(1500)}");
            Assert.AreEqual(0, resized.CursorX, "the park stays at column 0");
            CollectionAssert.AreEqual(new List<(int, int)> { (2, resized.CursorY + 1) }, CaretCells(resized),
                "the caret is redrawn at the editing position of the re-anchored prompt");
            Assert.AreEqual("a", resized.GetCell(2, resized.CursorY + 1).Character);
            Assert.AreEqual(0, CaretCellsInScrollback(terminal), "no drawn caret reached native scrollback");
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task FlowSoftWrap_CaretOnWideGrapheme_StylesTheGlyphCell()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 6).Build();
        var state = new TextBoxState("a界b") { CursorPosition = 1 };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.TextBox().State(state), o => { o.MinHeight = 1; o.MaxHeight = 1; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 1, Theme = CaretTheme() }, workload);
        var running = runner.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            var onWide = await WaitForAsync(terminal, s => CaretCells(s).Count > 0, "the caret on the wide glyph", stop.Token);
            Assert.AreEqual((1, 1), CaretCells(onWide)[0], "the caret starts at the wide glyph's first column");
            Assert.AreEqual("界", onWide.GetCell(1, 1).Character, "the wide glyph is kept under the caret");
            Assert.AreEqual("b", onWide.GetCell(3, 1).Character, "the row is not shifted");

            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.RightArrow).Build().ApplyAsync(terminal, stop.Token);
            var after = await WaitForAsync(terminal, s => CaretCells(s) is [(3, 1)], "the caret after the wide glyph", stop.Token);
            Assert.AreEqual("界", after.GetCell(1, 1).Character);
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task FlowSoftWrap_ScopedThemeMultilineSelectionAndSecondTextBox_DrawOneCaretWithMouseEnabled()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 9).Build();
        var panelForeground = Hex1bColor.FromRgb(1, 2, 3);
        var panelBackground = Hex1bColor.FromRgb(77, 88, 99);
        bool IsPanelCaret(TerminalCell cell) => cell.Background is { } bg && bg.R == 77 && bg.G == 88 && bg.B == 99
            && cell.Foreground is { } fg && fg.R == 1 && fg.G == 2 && fg.B == 3;
        var notes = new TextBoxState("ab\ncd") { CursorPosition = 4 };
        var name = new TextBoxState("xyz") { CursorPosition = 1 };
        var fixedBox = new TextBoxState("abcdef") { CursorPosition = 0 };
        var created = new TaskCompletionSource<FlowStep>(TaskCreationOptions.RunContinuationsAsynchronously);
        var runner = new Hex1bFlowRunner(async flow =>
        {
            var step = flow.Step(ctx => ctx.VStack(v => [
                    v.TextBox().State(notes).Multiline(),
                    v.ThemePanel(t => t.Set(TextBoxTheme.CursorForegroundColor, panelForeground)
                        .Set(TextBoxTheme.CursorBackgroundColor, panelBackground), v.TextBox().State(name)),
                    v.HStack(h => [h.TextBox().State(fixedBox).FixedWidth(6), h.Text("NEXT")]),
                ]),
                o => { o.MinHeight = 5; o.MaxHeight = 5; o.EnableMouse = true; });
            created.TrySetResult(step);
            await step.WaitForCompletionAsync(flow.CancellationToken);
        }, new Hex1bFlowOptions { UseSoftWrapTombstones = true, InitialCursorRow = 2, Theme = CaretTheme() }, workload);
        var running = runner.RunAsync(stop.Token);
        FlowStep? step = null;
        try
        {
            step = await created.Task.WaitAsync(stop.Token);
            // The multiline box's second line "cd" is row 3; offset 4 is its 'd'.
            var multiline = await WaitForAsync(terminal, s => CaretCells(s) is [(1, 3)], "the multiline caret", stop.Token);
            Assert.AreEqual("d", multiline.GetCell(1, 3).Character);
            Assert.IsFalse(multiline.CursorVisible, "a mouse-enabled step keeps the hardware cursor hidden");

            await new Hex1bTerminalInputSequenceBuilder().Shift().Key(Hex1bKey.LeftArrow).Build().ApplyAsync(terminal, stop.Token);
            var selecting = await WaitForAsync(terminal, s => CaretCells(s).Count == 0, "no caret while selecting", stop.Token);
            Assert.IsFalse(selecting.CursorVisible);

            await new Hex1bTerminalInputSequenceBuilder().Tab().Build().ApplyAsync(terminal, stop.Token);
            var scoped = await WaitForAsync(terminal, s => IsPanelCaret(s.GetCell(1, 4)), "the panel-themed caret", stop.Token);
            Assert.AreEqual(0, CaretCells(scoped).Count, "the scoped theme's caret colours, not the app's");
            Assert.AreEqual("y", scoped.GetCell(1, 4).Character);

            // A caret just past a full six-column box would land on its neighbour: it is not drawn there.
            await new Hex1bTerminalInputSequenceBuilder().Tab().Build().ApplyAsync(terminal, stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s) is [(0, 5)], "the caret in the fixed-width box", stop.Token);
            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.End).Build().ApplyAsync(terminal, stop.Token);
            var full = await WaitForAsync(terminal, s => fixedBox.CursorPosition == 6 && CaretCells(s).Count == 0,
                "the caret past the full box not drawn", stop.Token);
            Assert.AreEqual("N", full.GetCell(6, 5).Character);
            Assert.IsFalse(IsCaretCell(full.GetCell(6, 5)), "the neighbour's 'N' is never recoloured");
            await new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.LeftArrow).Build().ApplyAsync(terminal, stop.Token);
            await WaitForAsync(terminal, s => CaretCells(s) is [(5, 5)], "the caret back inside the box", stop.Token);
        }
        finally
        {
            step?.Complete();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    [TestMethod]
    public async Task NonFlowApp_EmbeddedTerminal_NativeCursorFollowsCursorOnlyMoves()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var childOutput = new Pipe();
        var childInput = new Pipe();
        await using var childWorkload = new StreamWorkloadAdapter(childOutput.Reader.AsStream(), childInput.Writer.AsStream());
        using var childTerminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(childWorkload).WithHeadless().WithDimensions(30, 4)
            .WithTerminalWidget(out var handle).Build();
        var childRun = childTerminal.RunAsync(stop.Token);
        async Task ChildWrites(string text)
        {
            await childOutput.Writer.WriteAsync(System.Text.Encoding.UTF8.GetBytes(text), stop.Token);
            await childOutput.Writer.FlushAsync(stop.Token);
        }

        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless()
            .WithDimensions(30, 6).Build();
        using var app = new Hex1bApp(ctx => Task.FromResult<Hex1bWidget>(
                ctx.VStack(v => [v.Text("EDITOR-58"), v.Terminal(handle).Fill()])),
            new Hex1bAppOptions { WorkloadAdapter = workload });
        var running = app.RunAsync(stop.Token);
        try
        {
            await ChildWrites("\x1b[2J\x1b[1;1Hline one\r\nline two\x1b[2;4H\x1b[?25h");
            await WaitForAsync(terminal, s => s.CursorVisible && (s.CursorX, s.CursorY) == (3, 2), "the native child cursor", stop.Token);

            // A cursor-only move re-renders without any cell change.
            await ChildWrites("\x1b[1;6H");
            await WaitForAsync(terminal, s => (s.CursorX, s.CursorY) == (5, 1), "the native cursor after a cursor-only move", stop.Token);
            var moved = terminal.CreateSnapshot();
            Assert.AreEqual(0, ReverseCells(moved).Count, "outside Flow the child cursor is not drawn as a cell");
        }
        finally
        {
            app.RequestStop();
            await stop.CancelAsync();
            try { await running.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
            try { childOutput.Writer.Complete(); } catch (InvalidOperationException) { }
            try { await childRun.WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (Exception) when (stop.IsCancellationRequested) { }
        }
    }
}
