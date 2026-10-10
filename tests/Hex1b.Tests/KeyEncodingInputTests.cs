using System.Text;
using System.Threading.Channels;
using Hex1b.Input;
using Hex1b.Tokens;

namespace Hex1b.Tests;

/// <summary>
/// Issue 63: key encodings a terminal sends on input (xterm modifyOtherKeys, CSI u, Alt as an ESC prefix) decode to
/// key events. Every case goes through the terminal's presentation input pump to the app workload's input events.
/// </summary>
[TestClass]
public sealed class KeyEncodingInputTests
{
    private static readonly TimeSpan ReadBound = TimeSpan.FromSeconds(10);

    public TestContext TestContext { get; set; } = null!;

    [TestMethod]
    public async Task ModifyOtherKeys_ShiftEnterFromGhostty_DecodesAsShiftEnter()
    {
        var events = await DecodeAsync("\x1b[27;2;13~");

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(Hex1bKey.Enter, key.Key);
        Assert.AreEqual(Hex1bModifiers.Shift, key.Modifiers);
    }

    [TestMethod]
    public async Task CsiU_CtrlIFromGhostty_DecodesAsCtrlI()
    {
        var events = await DecodeAsync("\x1b[105;5u");

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(Hex1bKey.I, key.Key);
        Assert.AreEqual(Hex1bModifiers.Control, key.Modifiers);
    }

    [TestMethod]
    [DataRow("\x1b\r", Hex1bKey.Enter, "\r", DisplayName = "Alt+Enter (ESC CR)")]
    [DataRow("\x1b\x7f", Hex1bKey.Backspace, "\x7f", DisplayName = "Alt+Backspace (ESC DEL)")]
    public async Task EscPrefix_GhosttyAltEnterAndAltBackspace_DecodeWithAlt(string input, Hex1bKey expected, string expectedText)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expected, key.Key);
        Assert.AreEqual(Hex1bModifiers.Alt, key.Modifiers);
        Assert.AreEqual(expectedText, key.Text);
    }

    // Janet reads input with ordered paste admission; the Ghostty receipts decode the same on that path.
    [TestMethod]
    [DataRow("\x1b[27;2;13~", Hex1bKey.Enter, Hex1bModifiers.Shift, DisplayName = "ordered: Shift+Enter")]
    [DataRow("\x1b\r", Hex1bKey.Enter, Hex1bModifiers.Alt, DisplayName = "ordered: Alt+Enter")]
    [DataRow("\x1b\x7f", Hex1bKey.Backspace, Hex1bModifiers.Alt, DisplayName = "ordered: Alt+Backspace")]
    [DataRow("\x1b[105;5u", Hex1bKey.I, Hex1bModifiers.Control, DisplayName = "ordered: Ctrl+I")]
    public async Task OrderedInput_GhosttyReceipts_DecodeTheSame(string input, Hex1bKey expectedKey, Hex1bModifiers expectedModifiers)
    {
        var events = await DecodeAsync(input, ordered: true);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedKey, key.Key);
        Assert.AreEqual(expectedModifiers, key.Modifiers);
    }

    // ESC before any control byte is Alt on the key that byte decodes to alone (the legacy table's Ctrl keys too).
    [TestMethod]
    [DataRow("\x1b\t", Hex1bKey.Tab, Hex1bModifiers.Alt, "\t", DisplayName = "ESC TAB = Alt+Tab")]
    [DataRow("\x1b\b", Hex1bKey.Backspace, Hex1bModifiers.Alt, "\b", DisplayName = "ESC BS = Alt+Backspace")]
    [DataRow("\x1b\x02", Hex1bKey.B, Hex1bModifiers.Control | Hex1bModifiers.Alt, "\x02", DisplayName = "ESC ^B = Ctrl+Alt+B")]
    [DataRow("\x1b\0", Hex1bKey.Spacebar, Hex1bModifiers.Control | Hex1bModifiers.Alt, "", DisplayName = "ESC NUL = Ctrl+Alt+Space")]
    public async Task EscPrefix_OtherControlBytes_DecodeAsAltOnTheirKey(string input, Hex1bKey expectedKey, Hex1bModifiers expectedModifiers, string expectedText)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedKey, key.Key);
        Assert.AreEqual(expectedModifiers, key.Modifiers);
        Assert.AreEqual(expectedText, key.Text);
    }

    [TestMethod]
    public async Task EscPrefix_EscEsc_StaysUndecoded()
    {
        var events = await DecodeAsync("\x1b\x1b");

        Assert.IsEmpty(events);
    }

    // Decodings that existed before issue 63, unchanged: key, modifiers and text.
    [TestMethod]
    [DataRow("\x1b[A", Hex1bKey.UpArrow, Hex1bModifiers.None, "", DisplayName = "Up (CSI A)")]
    [DataRow("\x1bOB", Hex1bKey.DownArrow, Hex1bModifiers.None, "", DisplayName = "Down (SS3 B)")]
    [DataRow("\x1b[1;5C", Hex1bKey.RightArrow, Hex1bModifiers.Control, "", DisplayName = "Ctrl+Right (CSI 1;5C)")]
    [DataRow("\x1b[1;2D", Hex1bKey.LeftArrow, Hex1bModifiers.Shift, "", DisplayName = "Shift+Left (CSI 1;2D)")]
    [DataRow("\x1bOP", Hex1bKey.F1, Hex1bModifiers.None, "", DisplayName = "F1 (SS3 P)")]
    [DataRow("\x1b[15~", Hex1bKey.F5, Hex1bModifiers.None, "", DisplayName = "F5 (CSI 15~)")]
    [DataRow("\x1b[24;5~", Hex1bKey.F12, Hex1bModifiers.Control, "", DisplayName = "Ctrl+F12 (CSI 24;5~)")]
    [DataRow("\x1b[3~", Hex1bKey.Delete, Hex1bModifiers.None, "", DisplayName = "Delete (CSI 3~)")]
    [DataRow("\x1b[Z", Hex1bKey.Tab, Hex1bModifiers.Shift, "\t", DisplayName = "Shift+Tab (CSI Z)")]
    [DataRow("\u001bf", Hex1bKey.F, Hex1bModifiers.Alt, "f", DisplayName = "Alt+f (ESC f)")]
    [DataRow("\u001bF", Hex1bKey.F, Hex1bModifiers.Alt | Hex1bModifiers.Shift, "F", DisplayName = "Alt+Shift+F (ESC F)")]
    [DataRow("\u001b1", Hex1bKey.D1, Hex1bModifiers.Alt, "1", DisplayName = "Alt+1 (ESC 1)")]
    [DataRow("\r", Hex1bKey.Enter, Hex1bModifiers.None, "\r", DisplayName = "Enter (CR)")]
    [DataRow("\x7f", Hex1bKey.Backspace, Hex1bModifiers.None, "\x7f", DisplayName = "Backspace (DEL)")]
    [DataRow("\t", Hex1bKey.Tab, Hex1bModifiers.None, "\t", DisplayName = "Tab (HT)")]
    public async Task ExistingSequences_DecodeAsBefore(string input, Hex1bKey expectedKey, Hex1bModifiers expectedModifiers, string expectedText)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedKey, key.Key);
        Assert.AreEqual(expectedModifiers, key.Modifiers);
        Assert.AreEqual(expectedText, key.Text);
    }

    // A decoded key re-encoded for a child terminal (TerminalNode, an editor handoff) gives the key's legacy bytes.
    [TestMethod]
    [DataRow("\x1b[27;3;49~", "\u001b1", DisplayName = "Alt+1 → ESC 1")]
    [DataRow("\x1b[44;3u", "\x1b,", DisplayName = "Alt+, → ESC ,")]
    [DataRow("\x1b[59;3u", "\x1b;", DisplayName = "Alt+; → ESC ;")]
    [DataRow("\x1b[97;7u", "\x1b\x01", DisplayName = "Ctrl+Alt+a → ESC ^A")]
    [DataRow("\x1b[105;5u", "\t", DisplayName = "Ctrl+i → HT")]
    [DataRow("\x1b[91;5u", "\x1b", DisplayName = "Ctrl+[ → ESC")]
    [DataRow("\x1b[27;2;13~", "\r", DisplayName = "Shift+Enter → CR")]
    public async Task KeyCodeEncoding_ReencodedForAChildTerminal_GivesTheLegacyBytes(string input, string expectedBytes)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedBytes, TerminalInputEncoder.EncodeKey(key, default));
    }

    // Workload filters (recorders, loggers) see input key encodings as tokens that serialize back to the bytes sent:
    // CSI u is not restore-cursor and modifyOtherKeys keeps its key code.
    [TestMethod]
    public async Task InputFilters_KeyEncodings_SeeTokensThatSerializeToTheSentBytes()
    {
        const string sent = "\x1b[105;5u\x1b[27;2;13~\x1b[u";
        var filter = new InputTokenRecorder();

        await DecodeAsync(sent, filter);

        Assert.AreEqual(sent + "z", AnsiTokenSerializer.Serialize(filter.Tokens));
        Assert.IsFalse(filter.Tokens.Any(t => t is RestoreCursorToken or SpecialKeyToken), "No key encoding may become restore-cursor or a special key.");
    }

    // Literal wire bytes and the key each names. CSI u: CSI code ; mod u. modifyOtherKeys: CSI 27 ; mod ; code ~.
    // mod = 1 + (Shift 1 | Alt 2 | Ctrl 4); code = the key's Unicode codepoint.
    public static IEnumerable<TestDataRow<(string Input, Hex1bKey Key, Hex1bModifiers Modifiers)>> KeyCodeEncodings =>
    [
        new(("\x1b[97;5u", Hex1bKey.A, Hex1bModifiers.Control)) { DisplayName = "CSI u Ctrl+a" },
        new(("\x1b[122;3u", Hex1bKey.Z, Hex1bModifiers.Alt)) { DisplayName = "CSI u Alt+z" },
        new(("\x1b[97;6u", Hex1bKey.A, Hex1bModifiers.Control | Hex1bModifiers.Shift)) { DisplayName = "CSI u Ctrl+Shift+a" },
        new(("\x1b[97;7u", Hex1bKey.A, Hex1bModifiers.Control | Hex1bModifiers.Alt)) { DisplayName = "CSI u Ctrl+Alt+a" },
        new(("\x1b[97;8u", Hex1bKey.A, Hex1bModifiers.Control | Hex1bModifiers.Alt | Hex1bModifiers.Shift)) { DisplayName = "CSI u Ctrl+Alt+Shift+a" },
        new(("\x1b[48;5u", Hex1bKey.D0, Hex1bModifiers.Control)) { DisplayName = "CSI u Ctrl+0" },
        new(("\x1b[57;3u", Hex1bKey.D9, Hex1bModifiers.Alt)) { DisplayName = "CSI u Alt+9" },
        new(("\x1b[9;5u", Hex1bKey.Tab, Hex1bModifiers.Control)) { DisplayName = "CSI u Ctrl+Tab" },
        new(("\x1b[9;2u", Hex1bKey.Tab, Hex1bModifiers.Shift)) { DisplayName = "CSI u Shift+Tab" },
        new(("\x1b[127;5u", Hex1bKey.Backspace, Hex1bModifiers.Control)) { DisplayName = "CSI u Ctrl+Backspace" },
        new(("\x1b[27;2u", Hex1bKey.Escape, Hex1bModifiers.Shift)) { DisplayName = "CSI u Shift+Escape" },
        new(("\x1b[27u", Hex1bKey.Escape, Hex1bModifiers.None)) { DisplayName = "CSI u Escape without a modifier parameter" },
        new(("\x1b[13;3u", Hex1bKey.Enter, Hex1bModifiers.Alt)) { DisplayName = "CSI u Alt+Enter" },
        new(("\x1b[32;5u", Hex1bKey.Spacebar, Hex1bModifiers.Control)) { DisplayName = "CSI u Ctrl+Space" },
        new(("\x1b[27;5;105~", Hex1bKey.I, Hex1bModifiers.Control)) { DisplayName = "modifyOtherKeys Ctrl+i" },
        new(("\x1b[27;6;65~", Hex1bKey.A, Hex1bModifiers.Control | Hex1bModifiers.Shift)) { DisplayName = "modifyOtherKeys Ctrl+Shift+A" },
        new(("\x1b[27;3;49~", Hex1bKey.D1, Hex1bModifiers.Alt)) { DisplayName = "modifyOtherKeys Alt+1" },
        new(("\x1b[27;5;9~", Hex1bKey.Tab, Hex1bModifiers.Control)) { DisplayName = "modifyOtherKeys Ctrl+Tab" },
        new(("\x1b[27;3;127~", Hex1bKey.Backspace, Hex1bModifiers.Alt)) { DisplayName = "modifyOtherKeys Alt+Backspace" },
        new(("\x1b[27;5;27~", Hex1bKey.Escape, Hex1bModifiers.Control)) { DisplayName = "modifyOtherKeys Ctrl+Escape" },
        new(("\x1b[27;5;13~", Hex1bKey.Enter, Hex1bModifiers.Control)) { DisplayName = "modifyOtherKeys Ctrl+Enter" },
        new(("\x1b[27;2;32~", Hex1bKey.Spacebar, Hex1bModifiers.Shift)) { DisplayName = "modifyOtherKeys Shift+Space" },
    ];

    [TestMethod]
    [DynamicData(nameof(KeyCodeEncodings))]
    public async Task KeyCodeEncoding_AnyKeyAndModifiers_DecodesToThatKey(string input, Hex1bKey expectedKey, Hex1bModifiers expectedModifiers)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedKey, key.Key);
        Assert.AreEqual(expectedModifiers, key.Modifiers);
    }

    // A key-code event carries the text the same key's legacy encoding carries: the typed character (Shift on a
    // lowercase letter types the uppercase one; Alt adds only the ESC prefix), or under Ctrl its C0 control.
    [TestMethod]
    [DataRow("\x1b[27;2;32~", " ", DisplayName = "Shift+Space types a space")]
    [DataRow("\x1b[97;2u", "A", DisplayName = "Shift+a (CSI u reports the base key) types A")]
    [DataRow("\x1b[27;2;65~", "A", DisplayName = "Shift+A (modifyOtherKeys reports the shifted key) types A")]
    [DataRow("\x1b[49u", "1", DisplayName = "1 without modifiers types 1")]
    [DataRow("\x1b[27;2;13~", "\r", DisplayName = "Shift+Enter carries CR like Enter")]
    [DataRow("\x1b[13;3u", "\r", DisplayName = "Alt+Enter carries CR like ESC CR")]
    [DataRow("\x1b[122;3u", "z", DisplayName = "Alt+z carries z like ESC z")]
    [DataRow("\x1b[27;3;49~", "1", DisplayName = "Alt+1 carries 1 like ESC 1")]
    [DataRow("\x1b[105;5u", "\t", DisplayName = "Ctrl+i carries its C0 control HT")]
    [DataRow("\x1b[91;5u", "\x1b", DisplayName = "Ctrl+[ carries its C0 control ESC")]
    [DataRow("\x1b[59;5u", "", DisplayName = "Ctrl+; has no C0 control and carries nothing")]
    [DataRow("\x1b[32;6u", "", DisplayName = "Ctrl+Shift+Space carries nothing (NUL)")]
    public async Task KeyCodeEncoding_Text_IsWhatTheLegacyEncodingCarries(string input, string expectedText)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedText, key.Text);
    }

    // Punctuation the fixterm table does not name decodes to the key a bare byte of it decodes to.
    [TestMethod]
    [DataRow("\x1b[44;5u", Hex1bKey.OemComma, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+,")]
    [DataRow("\x1b[47;3u", Hex1bKey.OemQuestion, Hex1bModifiers.Alt, DisplayName = "CSI u Alt+/")]
    [DataRow("\x1b[27;5;45~", Hex1bKey.OemMinus, Hex1bModifiers.Control, DisplayName = "modifyOtherKeys Ctrl+-")]
    [DataRow("\x1b[59;5u", Hex1bKey.Oem1, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+;")]
    [DataRow("\x1b[91;5u", Hex1bKey.Oem4, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+[")]
    [DataRow("\x1b[92;3u", Hex1bKey.Oem5, Hex1bModifiers.Alt, DisplayName = "CSI u Alt+backslash")]
    [DataRow("\x1b[93;5u", Hex1bKey.Oem6, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+]")]
    [DataRow("\x1b[39;5u", Hex1bKey.Oem7, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+'")]
    [DataRow("\x1b[96;5u", Hex1bKey.OemTilde, Hex1bModifiers.Control, DisplayName = "CSI u Ctrl+`")]
    [DataRow("\x1b[27;6;58~", Hex1bKey.Oem1, Hex1bModifiers.Control | Hex1bModifiers.Shift, DisplayName = "modifyOtherKeys Ctrl+: (shifted code)")]
    [DataRow("\x1b[27;6;95~", Hex1bKey.OemMinus, Hex1bModifiers.Control | Hex1bModifiers.Shift, DisplayName = "modifyOtherKeys Ctrl+_ (shifted code)")]
    public async Task KeyCodeEncoding_PunctuationKey_DecodesToTheLegacyKey(string input, Hex1bKey expectedKey, Hex1bModifiers expectedModifiers)
    {
        var events = await DecodeAsync(input);

        var key = Assert.ContainsSingle(events);
        Assert.AreEqual(expectedKey, key.Key);
        Assert.AreEqual(expectedModifiers, key.Modifiers);
    }

    // Sequences that name no key yield no event: no key name and no typed text, a codepoint that is not a Unicode
    // scalar, or a CSI u that is not a key report (bare CSI u, the kitty flags reply CSI ? flags u).
    [TestMethod]
    [DataRow("\x1b[233;5u", DisplayName = "CSI u Ctrl+e-acute (no key name, no C0 control)")]
    [DataRow("\x1b[1u", DisplayName = "CSI u control codepoint without a key")]
    [DataRow("\x1b[27;5;1~", DisplayName = "modifyOtherKeys control codepoint without a key")]
    [DataRow("\x1b[55296u", DisplayName = "CSI u surrogate codepoint")]
    [DataRow("\x1b[1114112;5u", DisplayName = "CSI u codepoint past U+10FFFF")]
    [DataRow("\x1b[u", DisplayName = "bare CSI u")]
    [DataRow("\x1b[?1u", DisplayName = "kitty flags reply")]
    public async Task KeyCodeEncoding_NamingNoKey_DecodesToNothing(string input)
    {
        var events = await DecodeAsync(input);

        Assert.IsEmpty(events);
    }

    /// <summary>
    /// Sends <paramref name="input"/> and then a plain "z" in one read, and returns every key event decoded before
    /// the "z": an input that decodes to nothing returns an empty list instead of hanging.
    /// </summary>
    private async Task<IReadOnlyList<Hex1bKeyEvent>> DecodeAsync(string input, IHex1bTerminalWorkloadFilter? filter = null, bool ordered = false)
    {
        var ct = TestContext.CancellationToken;
        await using var presentation = new InputPresentation();
        using var workload = new Hex1bAppWorkloadAdapter();
        if (ordered)
            workload.OrderedPasteCapacity = 64;
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = workload,
            Width = 80,
            Height = 24
        };
        if (filter is not null)
            options.WorkloadFilters.Add(filter);
        await using var terminal = new Hex1bTerminal(options);

        presentation.Enqueue(input + "z");
        var events = new List<Hex1bKeyEvent>();
        while (true)
        {
            var evt = await workload.InputEvents.ReadAsync(ct).AsTask().WaitAsync(ReadBound, ct);
            var key = Assert.IsInstanceOfType<Hex1bKeyEvent>(evt);
            if (key is { Key: Hex1bKey.Z, Text: "z", Modifiers: Hex1bModifiers.None })
                return events;
            events.Add(key);
        }
    }

    private sealed class InputTokenRecorder : IHex1bTerminalWorkloadFilter
    {
        private readonly List<AnsiToken> _tokens = [];

        public IReadOnlyList<AnsiToken> Tokens
        {
            get
            {
                lock (_tokens)
                    return [.. _tokens];
            }
        }

        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            lock (_tokens)
                _tokens.AddRange(tokens);
            return ValueTask.CompletedTask;
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnOutputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    private sealed class InputPresentation : IHex1bTerminalPresentationAdapter
    {
        private readonly Channel<ReadOnlyMemory<byte>> _input = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();

        public int Width => 80;
        public int Height => 24;
        public TerminalCapabilities Capabilities => new();

        public event Action<int, int>? Resized { add { } remove { } }
        public event Action? Disconnected { add { } remove { } }

        public void Enqueue(string text) => _input.Writer.TryWrite(Encoding.UTF8.GetBytes(text));

        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => ValueTask.CompletedTask;

        public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
        {
            while (await _input.Reader.WaitToReadAsync(ct))
            {
                if (_input.Reader.TryRead(out var data))
                    return data;
            }
            return ReadOnlyMemory<byte>.Empty;
        }

        public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;
        public (int Row, int Column) GetCursorPosition() => (0, 0);

        public ValueTask DisposeAsync()
        {
            _input.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }
}
