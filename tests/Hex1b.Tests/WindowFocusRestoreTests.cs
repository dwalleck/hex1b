using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b.Tests;

/// <summary>
/// Issue 64: when a window closes, the window that becomes active gets focus back on the content control it last
/// had focused, not on its first content focusable.
/// </summary>
/// <remarks>
/// By default each window "X" holds two buttons, "X1" and "X2"; pressing either opens the next window over it (A, then
/// B, then C). Root bindings stand in for an app's own commands: F1 opens a modal "Keys" window (like Janet's key help),
/// F10 closes it, F11 narrows A and closes it in one input, F2 opens a non-modal window N, F3 closes A, F5 requests
/// focus on a named control, F6 opens a modal window L holding [TextBox, list], F7 adds a list to A and opens the help.
/// F9 reports the focused node from the app's own input thread, so the tests read focus without racing the render loop.
/// </remarks>
[TestClass]
public sealed class WindowFocusRestoreTests
{
    [TestMethod]
    [DataRow(true, DisplayName = "Modal window over A")]
    [DataRow(false, DisplayName = "Non-modal window over A")]
    public async Task ClosingWindowOverAnother_RestoresFocusToTheControlItHad(bool modal)
    {
        await using var scene = await WindowScene.StartAsync(modal);

        await scene.OpenWindowAAsync();
        Assert.AreEqual("A1", await scene.FocusedLabelAsync(), "A opens with focus on its first control.");

        await scene.PressAsync(Hex1bKey.Tab);
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "Precondition: focus is on A's second control.");

        await scene.ActivateAndWaitForWindowAsync("Window B");
        Assert.AreEqual("B1", await scene.FocusedLabelAsync(), "B opens with focus on its first control.");

        await scene.CloseTopWindowAsync("Window B");
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "Closing B returns focus to A's second control.");
        await scene.StopAsync();
    }

    [TestMethod]
    public async Task ClosingAStackOfWindows_EachWindowGetsBackItsOwnControl()
    {
        await using var scene = await WindowScene.StartAsync(modal: true);

        await scene.OpenWindowAAsync();
        await scene.PressAsync(Hex1bKey.Tab);
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "Precondition: focus is on A's second control.");
        await scene.ActivateAndWaitForWindowAsync("Window B");
        await scene.PressAsync(Hex1bKey.Tab);
        Assert.AreEqual("B2", await scene.FocusedLabelAsync(), "Precondition: focus is on B's second control.");
        await scene.ActivateAndWaitForWindowAsync("Window C");
        Assert.AreEqual("C1", await scene.FocusedLabelAsync(), "C opens with focus on its first control.");

        await scene.CloseTopWindowAsync("Window C");
        Assert.AreEqual("B2", await scene.FocusedLabelAsync(), "Closing C returns focus to B's second control.");

        await scene.CloseTopWindowAsync("Window B");
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "Closing B returns focus to A's second control.");
        await scene.StopAsync();
    }

    [TestMethod]
    public async Task ClosingAWindowAgain_RestoresTheControlFocusedMostRecently()
    {
        await using var scene = await WindowScene.StartAsync(modal: true);

        await scene.OpenWindowAAsync();
        await scene.PressAsync(Hex1bKey.Tab);
        await scene.ActivateAndWaitForWindowAsync("Window B");
        await scene.CloseTopWindowAsync("Window B");
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "First close returns focus to A's second control.");

        await scene.PressAsync(Hex1bKey.Tab, Hex1bModifiers.Shift);
        Assert.AreEqual("A1", await scene.FocusedLabelAsync(), "Precondition: focus moved back to A's first control.");
        await scene.ActivateAndWaitForWindowAsync("Window B");
        await scene.CloseTopWindowAsync("Window B");
        Assert.AreEqual("A1", await scene.FocusedLabelAsync(), "Second close returns focus to the control focused last.");
        await scene.StopAsync();
    }

    /// <summary>
    /// Guard for the fallback: A2 removes itself from A when pressed, so it leaves A in the frame B opens; when B closes
    /// the gone control is not restored and A's first content focusable gets focus, as before issue 64.
    /// </summary>
    [TestMethod]
    public async Task ClosingWindow_WhenTheRememberedControlIsGone_FocusesTheFirstControl()
    {
        await using var scene = await WindowScene.StartAsync(modal: true, removedWhenActivated: "A2");

        await scene.OpenWindowAAsync();
        await scene.PressAsync(Hex1bKey.Tab);
        Assert.AreEqual("A2", await scene.FocusedLabelAsync(), "Precondition: focus is on A's second control.");
        await scene.ActivateAndWaitForWindowAsync("Window B");

        await scene.CloseTopWindowAsync("Window B");
        Assert.AreEqual("A1", await scene.FocusedLabelAsync(), "A2 left A while B was open, so A's first control gets focus.");
        await scene.StopAsync();
    }

    /// <summary>Janet's shape: a modal help window over a modal window whose focused control is not a button.</summary>
    [TestMethod]
    [DataRow("A-TB", DisplayName = "Focused TextBox")]
    [DataRow("A-SP", DisplayName = "Focused ScrollPanel")]
    public async Task ClosingModalHelpOverAModalWindow_RestoresItsFocusedControl(string target)
    {
        await using var scene = await WindowScene.StartAsync(modal: true, contentOfA: ContentOfA.EditorAndPanel, modalA: true);

        await scene.OpenWindowAAsync();
        Assert.AreEqual(target, await scene.FocusAsync(target), "Precondition: focus is on A's control.");
        await scene.KeyAndWaitAsync(Hex1bKey.F1, "Keys", shown: true);
        Assert.AreEqual("help", await scene.FocusedLabelAsync(), "The help window has focus.");

        await scene.KeyAndWaitAsync(Hex1bKey.F10, "Keys", shown: false);
        Assert.AreEqual(target, await scene.FocusedLabelAsync(), "Closing the help returns focus to A's control.");
        await scene.StopAsync();
    }

    /// <summary>
    /// Review r1 F1: the remembered control is present when the panel reconciles but leaves the window during that
    /// frame's layout (a responsive branch flips because A is narrowed in the same input that closes the help). Focus
    /// must fall back to A's first content control, not leave the window.
    /// </summary>
    [TestMethod]
    public async Task ClosingWindow_WhenLayoutDropsTheRememberedControl_FocusesTheFirstControl()
    {
        await using var scene = await WindowScene.StartAsync(modal: true, contentOfA: ContentOfA.Responsive);

        await scene.OpenWindowAAsync();
        Assert.AreEqual("Wide", await scene.FocusAsync("Wide"), "Precondition: focus is on A's wide-layout control.");
        await scene.KeyAndWaitAsync(Hex1bKey.F1, "Keys", shown: true);

        await scene.KeyAndWaitAsync(Hex1bKey.F11, "Keys", shown: false);
        Assert.AreEqual("A1", await scene.FocusedLabelAsync(), "A narrowed as the help closed, so A's first control gets focus.");
        await scene.StopAsync();
    }

    /// <summary>Review r1 F2 (declared): opening a window under an active modal leaves the modal's focus alone.</summary>
    [TestMethod]
    public async Task OpeningAWindowUnderAModal_LeavesTheModalsFocusWhereItWas()
    {
        await using var scene = await WindowScene.StartAsync(modal: true, contentOfA: ContentOfA.EditorAndPanel, modalA: true);

        await scene.OpenWindowAAsync();
        Assert.AreEqual("A-TB", await scene.FocusAsync("A-TB"), "Precondition: focus is on A's TextBox.");

        await scene.KeyAndWaitAsync(Hex1bKey.F2, "Window N", shown: true);
        Assert.AreEqual("A-TB", await scene.FocusedLabelAsync(), "Modal A stays active and keeps its focused control.");
        await scene.StopAsync();
    }

    /// <summary>Review r1 F2 (declared): closing a window that is not the active one leaves the active one's focus alone.</summary>
    [TestMethod]
    public async Task ClosingALowerWindow_LeavesTheTopWindowsFocusWhereItWas()
    {
        await using var scene = await WindowScene.StartAsync(modal: false);

        await scene.OpenWindowAAsync();
        await scene.KeyAndWaitAsync(Hex1bKey.F2, "Window N", shown: true);
        Assert.AreEqual("N2", await scene.FocusAsync("N2"), "Precondition: focus is on the top window's second control.");

        await scene.KeyAndWaitAsync(Hex1bKey.F3, "Window A", shown: false);
        Assert.AreEqual("N2", await scene.FocusedLabelAsync(), "Closing the lower window A leaves N's focus where it was.");
        await scene.StopAsync();
    }

    /// <summary>
    /// Consumer check finding (Janet's history dialog): a list marks its own new node focused while it is reconciled.
    /// A new window holding [TextBox, list] must still open on its first control, as before issue 64.
    /// </summary>
    [TestMethod]
    public async Task OpeningAWindowWhoseListFocusesItself_FocusesItsFirstControl()
    {
        await using var scene = await WindowScene.StartAsync(modal: true);

        await scene.KeyAndWaitAsync(Hex1bKey.F6, "Window L", shown: true);
        Assert.AreEqual("L-TB", await scene.FocusedLabelAsync(), "The new window opens on its first control, the TextBox.");
        await scene.StopAsync();
    }

    /// <summary>
    /// Consumer check finding: a list that appears in a window covered by a modal marks itself focused, though only
    /// the modal's controls can have focus. Closing the modal must restore the covered window's control, not the list.
    /// </summary>
    [TestMethod]
    public async Task ClosingHelp_WhenTheCoveredWindowGainedASelfFocusingList_RestoresItsControl()
    {
        await using var scene = await WindowScene.StartAsync(modal: true, contentOfA: ContentOfA.EditorAndPanel, modalA: true);

        await scene.OpenWindowAAsync();
        Assert.AreEqual("A-TB", await scene.FocusAsync("A-TB"), "Precondition: focus is on A's TextBox.");
        await scene.KeyAndWaitAsync(Hex1bKey.F7, "Keys", shown: true);
        Assert.AreEqual("help", await scene.FocusedLabelAsync(), "The help window has focus.");

        await scene.KeyAndWaitAsync(Hex1bKey.F10, "Keys", shown: false);
        Assert.AreEqual("A-TB", await scene.FocusedLabelAsync(), "Closing the help returns focus to A's TextBox.");
        await scene.StopAsync();
    }

    /// <summary>What window A holds.</summary>
    public enum ContentOfA
    {
        /// <summary>Buttons "A1" and "A2"; either opens window B.</summary>
        TwoButtons,
        /// <summary>
        /// Button "A1", a TextBox "A-TB" and a ScrollPanel "A-SP" (metric names), like a Janet surface; after F7, also a
        /// list "A-list".
        /// </summary>
        EditorAndPanel,
        /// <summary>Button "A1", then "Wide" while A's content is at least 30 cells wide, else "Narrow".</summary>
        Responsive,
    }

    /// <summary>
    /// The scene: a background button that opens window A, a window panel, and an F9 focus probe on the root.
    /// </summary>
    private sealed class WindowScene : IAsyncDisposable
    {
        private readonly Hex1bAppWorkloadAdapter _workload;
        private readonly Hex1bTerminal _terminal;
        private readonly Hex1bApp _app;
        private readonly Task _run;
        private readonly bool _modal;
        private readonly string? _removedWhenActivated;
        private readonly ContentOfA _contentOfA;
        private readonly bool _modalA;
        private TaskCompletionSource<Hex1bNode?>? _probe;
        private string? _focusTarget;
        private bool _listInA;
        private bool _stopping;

        // Handles of A and of the help window. Read and changed only on the app's thread.
        private WindowHandle? _windowA;
        private WindowHandle? _help;

        // The windows' controls, by window name. Read and changed only on the app's thread (builds and click handlers).
        private readonly Dictionary<string, string[]> _controls = new()
        {
            ["A"] = ["A1", "A2"],
            ["B"] = ["B1", "B2"],
            ["C"] = ["C1", "C2"],
        };

        private WindowScene(bool modal, string? removedWhenActivated, ContentOfA contentOfA, bool modalA)
        {
            _modal = modal;
            _removedWhenActivated = removedWhenActivated;
            _contentOfA = contentOfA;
            _modalA = modalA;
            _workload = new Hex1bAppWorkloadAdapter();
            _terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(_workload)
                .WithHeadless()
                .WithDimensions(80, 24)
                .Build();
            _app = new Hex1bApp(
                ctx => Task.FromResult<Hex1bWidget>(
                    ctx.VStack(outer => [
                        outer.Button("Open A").OnClick(e => Open(e.Windows, "A")),
                        outer.WindowPanel().Height(SizeHint.Fill)
                    ]).InputBindings(bindings =>
                    {
                        bindings.Key(Hex1bKey.F9).Action(input => _probe?.TrySetResult(input.FocusedNode), "Focus probe");
                        bindings.Key(Hex1bKey.F5).Action(_ =>
                        {
                            var target = _focusTarget;
                            _app.RequestFocus(n => Label(n) == target);
                            _app.Invalidate();
                        }, "Focus a named control");
                        bindings.Key(Hex1bKey.F1).Action(input => OpenHelp(input.Windows), "Open help");
                        bindings.Key(Hex1bKey.F7).Action(input =>
                        {
                            _listInA = true;
                            OpenHelp(input.Windows);
                        }, "Add a list to A and open help");
                        bindings.Key(Hex1bKey.F6).Action(input => input.Windows.Open(
                            input.Windows.Window(w => w.VStack(v => [
                                    new TextBoxWidget("query") { MetricName = "L-TB" },
                                    new ListWidget<string>(["one", "two"]) { MetricName = "L-list" }]))
                                .Title("Window L").Modal().Size(30, 8)), "Open L");
                        bindings.Key(Hex1bKey.F10).Action(input => CloseHelp(input.Windows), "Close help");
                        bindings.Key(Hex1bKey.F11).Action(input =>
                        {
                            if (_windowA?.Entry is { } a) a.Manager.UpdateSize(a, 20, a.Height);
                            CloseHelp(input.Windows);
                        }, "Narrow A and close help");
                        bindings.Key(Hex1bKey.F2).Action(input => input.Windows.Open(
                            input.Windows.Window(w => w.VStack(v => [v.Button("N1"), v.Button("N2")]))
                                .Title("Window N").Size(30, 8).Position(44, 10)), "Open N");
                        bindings.Key(Hex1bKey.F3).Action(input =>
                        {
                            if (_windowA is not null) input.Windows.Close(_windowA);
                        }, "Close A");
                    })),
                new Hex1bAppOptions { WorkloadAdapter = _workload });
            _run = _app.RunAsync(TestContext.Current.CancellationToken);
        }

        /// <param name="modal">Whether the windows opened over A (B and C) are modal.</param>
        /// <param name="removedWhenActivated">A control that leaves its window when pressed, as the window over it opens.</param>
        /// <param name="contentOfA">What window A holds.</param>
        /// <param name="modalA">Whether window A is modal.</param>
        public static async Task<WindowScene> StartAsync(bool modal, string? removedWhenActivated = null,
            ContentOfA contentOfA = ContentOfA.TwoButtons, bool modalA = false)
        {
            var scene = new WindowScene(modal, removedWhenActivated, contentOfA, modalA);
            try
            {
                await scene.ApplyAsync(new Hex1bTerminalInputSequenceBuilder()
                    .WaitUntil(s => s.ContainsText("Open A"), TimeSpan.FromSeconds(5)));
                return scene;
            }
            catch
            {
                await scene.DisposeAsync();
                throw;
            }
        }

        public Task OpenWindowAAsync() => ActivateAndWaitForWindowAsync("Window A");

        /// <summary>Presses Enter on the focused button and waits until the window it opens is drawn.</summary>
        public Task ActivateAndWaitForWindowAsync(string title) =>
            ApplyAsync(new Hex1bTerminalInputSequenceBuilder()
                .Key(Hex1bKey.Enter)
                .WaitUntil(s => s.ContainsText(title), TimeSpan.FromSeconds(5)));

        /// <summary>Closes the active window with Escape and waits until its title is gone.</summary>
        public Task CloseTopWindowAsync(string title) =>
            ApplyAsync(new Hex1bTerminalInputSequenceBuilder()
                .Key(Hex1bKey.Escape)
                .WaitUntil(s => !s.ContainsText(title), TimeSpan.FromSeconds(5)));

        /// <summary>Presses a key and waits until the text is shown or gone.</summary>
        public Task KeyAndWaitAsync(Hex1bKey key, string text, bool shown) =>
            ApplyAsync(new Hex1bTerminalInputSequenceBuilder()
                .Key(key)
                .WaitUntil(s => s.ContainsText(text) == shown, TimeSpan.FromSeconds(5)));

        /// <summary>Requests focus on the named control (F5) and returns the label then focused.</summary>
        public async Task<string?> FocusAsync(string label)
        {
            _focusTarget = label;
            await PressAsync(Hex1bKey.F5);
            // The request is applied in the frame rendered before the next input, so the probe sees its result.
            return await FocusedLabelAsync();
        }

        public Task PressAsync(Hex1bKey key, Hex1bModifiers modifiers = Hex1bModifiers.None)
        {
            var builder = new Hex1bTerminalInputSequenceBuilder();
            if (modifiers.HasFlag(Hex1bModifiers.Shift)) builder.Shift();
            return ApplyAsync(builder.Key(key));
        }

        /// <summary>The label of the focused control, read by the F9 binding on the app's input thread.</summary>
        public async Task<string?> FocusedLabelAsync()
        {
            var probe = new TaskCompletionSource<Hex1bNode?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _probe = probe;
            await ApplyAsync(new Hex1bTerminalInputSequenceBuilder().Key(Hex1bKey.F9));
            var focused = await probe.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            return Label(focused);
        }

        /// <summary>A button's label, a window frame as "Window", else the control's metric name or type.</summary>
        private static string? Label(Hex1bNode? node) => node switch
        {
            null => null,
            ButtonNode button => button.Label,
            WindowNode => "Window",
            _ => node.MetricName ?? node.GetType().Name,
        };

        private void OpenHelp(WindowManager windows)
        {
            _help = windows.Window(w => new ScrollPanelWidget(w.Text("help rows")) { MetricName = "help" })
                .Title("Keys").Modal().Size(40, 8);
            windows.Open(_help);
            // As Janet does: the app asks for its dialog's panel.
            _app.RequestFocus(n => n.MetricName == "help");
        }

        private void CloseHelp(WindowManager windows)
        {
            if (_help is not null) windows.Close(_help);
            _help = null;
        }

        private void Open(WindowManager windows, string name)
        {
            var next = name switch { "A" => "B", "B" => "C", _ => null };
            var handle = name == "A" && _contentOfA != ContentOfA.TwoButtons
                ? windows.Window(w => w.VStack(v => _contentOfA == ContentOfA.EditorAndPanel
                        ? [v.Button("A1"),
                           new TextBoxWidget("text") { MetricName = "A-TB" },
                           new ScrollPanelWidget(v.Text("body")) { MetricName = "A-SP" },
                           .. (_listInA ? [new ListWidget<string>(["x", "y"]) { MetricName = "A-list" }] : Array.Empty<Hex1bWidget>())]
                        : [v.Button("A1"),
                           v.Responsive(r => [r.WhenMinWidth(30, x => x.Button("Wide")), r.Otherwise(x => x.Button("Narrow"))])]))
                    .Title("Window A")
                    .Size(40, 12)
                : windows.Window(w => w.VStack(v =>
                        [.. _controls[name].Select(label => v.Button(label).OnClick(e =>
                        {
                            if (label == _removedWhenActivated) _controls[name] = [.. _controls[name].Where(l => l != label)];
                            if (next is not null) Open(e.Windows, next);
                        }))]))
                    .Title($"Window {name}")
                    .Size(30, 8);
            if (name == "A") _windowA = handle;
            windows.Open((name == "A" ? _modalA : _modal) ? handle.Modal() : handle);
        }

        private async Task ApplyAsync(Hex1bTerminalInputSequenceBuilder builder)
        {
            using var _ = await builder.Build().ApplyAsync(_terminal, TestContext.Current.CancellationToken);
        }

        /// <summary>Stops the app with Ctrl+C and waits for its run to end cleanly; the last step of a passing test.</summary>
        public async Task StopAsync()
        {
            _stopping = true;
            await ApplyAsync(new Hex1bTerminalInputSequenceBuilder().Ctrl().Key(Hex1bKey.C));
            await _run.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        }

        /// <summary>
        /// Releases the app, terminal and workload. When the test failed before <see cref="StopAsync"/>, it also tries to
        /// stop the app, without throwing: an exception from disposal would replace the test's own failure.
        /// </summary>
        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_stopping) await StopAsync();
            }
            catch (Exception)
            {
                // Failure path only: the test's own exception is already propagating and is the one to report.
            }
            finally
            {
                _app.Dispose();
                _terminal.Dispose();
                _workload.Dispose();
            }
        }
    }
}
