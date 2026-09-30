using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    // The output continuation a start owns (ticket 12): the unfinished escape sequence, the bytes of an unfinished
    // scalar, an ESC the framer holds, and the continuation bytes it expects. A replica restored between two chunks
    // handles the rest as the original does. The streams are the evidence probe's; every split point of each is run,
    // with the rest split again in two, and the screen text is read through GetScreenText beside the projection.
    [TestMethod]
    [DataRow("2-byte scalar", "abæcd", "")]
    [DataRow("3-byte scalar", "ab漢cd", "")]
    [DataRow("4-byte scalar", "ab\U0001F600cd", "")]
    [DataRow("csi sgr", "ab\u001b[1;31mcd\u001b[m", "")]
    [DataRow("csi cup", "ab\u001b[2;3Hcd", "")]
    [DataRow("osc title", "ab\u001b]0;title\u0007cd", "")]
    [DataRow("osc 8 link", "ab\u001b]8;;http://x/\u0007link\u001b]8;;\u0007cd", "")]
    [DataRow("apc", "ab\u001b_Gx=1\u001b\\cd", "")]
    [DataRow("two-byte esc", "ab\u001b(Bcd", "")]
    [DataRow("esc 7 8", "ab\u001b7cd\u001b8e", "")]
    [DataRow("bare esc then text", "ab", "1b:Mcd")]
    [DataRow("invalid E0 80 then 90", "ab", "e0 80 90:cd")]
    [DataRow("lone continuation (control: nothing pending)", "ab", "bc:cd")]
    [DataRow("overlong C0 80 (control: nothing pending)", "ab", "c0 80:cd")]
    [DataRow("truncated then ascii", "ab", "e6:cd")]
    [DataRow("c1 dcs after scalar", "ab漢", "90:1$r\u001b\\cd")]
    [DataRow("dcs decrqss", "ab\u001bP$qm\u001b\\cd", "")]
    [DataRow("esc then P after the start", "ab", "1b:P$qm\u001b\\cd")]
    [DataRow("mixed", "x\u001b[1mæ漢", "1b:[0m\U0001F600\u001b]0;t\u0007y")]
    [DataRow("scalar then csi", "漢\u001b[1;1Hæ", "")]
    [DataRow("wrap at edge", "aaaaaaaaaaaaaaaaaaa漢æz", "")]
    [DataRow("osc title with a wide glyph", "ab\u001b]0;t漢t\u0007cd", "")]
    public void ModelRestore_PendingInputContinues(string shape, string text, string rawTail)
    {
        // rawTail: "<hex bytes>:<text>" appended after text, for bytes no string can carry.
        var bytes = Encoding.UTF8.GetBytes(text).ToList();
        if (rawTail.Length > 0)
        {
            var parts = rawTail.Split(':', 2);
            bytes.AddRange(parts[0].Split(' ').Select(h => Convert.ToByte(h, 16)));
            bytes.AddRange(Encoding.UTF8.GetBytes(parts[1]));
        }
        var stream = bytes.ToArray();
        var pendingStarts = 0;
        for (var split = 1; split < stream.Length; split++)
        {
            var rest = stream[split..];
            var chunks = rest.Length > 1 ? new[] { stream[..split], rest[..(rest.Length / 2)], rest[(rest.Length / 2)..] } : new[] { stream[..split], rest };
            foreach (var resizeAt in new[] { -1, 1, 2 })
            {
                var original = Detached(new FakeTimeProvider());
                original.ApplyRecordedOutput(chunks[0]);
                var state = original.CaptureModelState();
                if (state.Unsupported.Contains("dcs-continuation"))
                    continue;
                var p = state.PendingInput;
                if (p.EscapePrefix.Length > 0 || p.Utf8.Length > 0 || p.GroundEscape || p.FramerUtf8Remaining != 0)
                    pendingStarts++;
                var replica = Detached(new FakeTimeProvider());
                replica.RestoreModelState(state);
                for (var i = 1; i < chunks.Length; i++)
                {
                    if (resizeAt == i)
                    {
                        original.Resize(12, 6);
                        replica.Resize(12, 6);
                    }
                    original.ApplyRecordedOutput(chunks[i]);
                    replica.ApplyRecordedOutput(chunks[i]);
                    var differences = JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState()));
                    Assert.IsEmpty(differences, $"{shape} split={split} resize={resizeAt} chunk {i}: " + string.Join("; ", differences.Take(3)));
                    Assert.AreEqual(original.GetScreenText(), replica.GetScreenText(), $"{shape} split={split} resize={resizeAt} chunk {i}: screen text (oracle)");
                }
            }
        }
        // Invalid bytes the decoder rejects at once hold nothing; those rows are controls for the restore's empty case.
        Assert.AreEqual(!shape.Contains("control"), pendingStarts > 0, $"fixture: {shape} held pending input at {pendingStarts} splits");
    }

    [TestMethod]
    [DataRow("prefix", "ab\u001b[1;")]
    [DataRow("utf8 two bytes", "ab\U0001F600")]
    [DataRow("prefix and utf8", "ab\u001b[1;漢")]
    [DataRow("held esc", "ab\u001b")]
    [DataRow("utf8 then held esc", "ab漢\u001b")]
    public void ModelRestore_PendingInputRoundTrips(string shape, string text)
    {
        // The projection's pendingInput, restored and projected again before any chunk, is equal; and the replica's
        // committed copies are what a start would hold.
        var bytes = Encoding.UTF8.GetBytes(text);
        var cut = shape switch { "utf8 two bytes" => 2, "prefix and utf8" => 1, "utf8 then held esc" => 0, _ => 0 };
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(shape == "utf8 then held esc" ? [.. bytes[..^2], 0x1b] : bytes[..^cut]);
        var state = original.CaptureModelState();
        var p = state.PendingInput;
        Assert.IsTrue(p.EscapePrefix.Length > 0 || p.Utf8.Length > 0 || p.GroundEscape, $"fixture: {shape} holds nothing");
        var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        var differences = JsonDifferences(Json(state), Json(replica.CaptureModelState()));
        Assert.IsEmpty(differences, $"{shape}: " + string.Join("; ", differences.Take(3)));
        Assert.AreEqual(state.ModelSequence, replica.CurrentModelSequence, $"{shape}: the restore applied something");
    }

    [TestMethod]
    [DataRow("utf8 not base64", "utf8", "**")]
    [DataRow("utf8 a complete byte", "utf8", "QQ==")]
    [DataRow("utf8 a complete scalar", "utf8", "5ryi")]
    [DataRow("utf8 four bytes", "utf8", "8J+YgA==")]
    [DataRow("utf8 an invalid lead", "utf8", "gA==")]
    [DataRow("framer count 4", "framer", "4")]
    [DataRow("framer count negative", "framer", "-1")]
    [DataRow("esc held with a framer count", "esc+framer", "1")]
    [DataRow("prefix that is text", "prefix", "abc")]
    [DataRow("prefix with a complete sequence before it", "prefix", "\u001b[m\u001b[")]
    [DataRow("prefix that is a complete sequence", "prefix", "\u001b[m")]
    [DataRow("prefix null", "prefix", null)]
    [DataRow("pending input null", "null", "")]
    public void ModelRestore_RefusesMalformedPendingInput(string shape, string field, string? value)
    {
        var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes("ok"));
        var state = original.CaptureModelState();
        var pending = state.PendingInput;
        var forged = state with
        {
            PendingInput = field switch
            {
                "utf8" => pending with { Utf8 = value! },
                "framer" => pending with { FramerUtf8Remaining = int.Parse(value!) },
                "esc+framer" => pending with { GroundEscape = true, FramerUtf8Remaining = int.Parse(value!) },
                "prefix" => pending with { EscapePrefix = value! },
                _ => null!,
            },
        };
        var replica = Detached(new FakeTimeProvider());
        var error = Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged), shape);
        StringAssert.Contains(error.Message, "pending input", shape);
        Assert.AreEqual(0L, replica.CurrentModelSequence, $"{shape}: a refused restore changed the model");
        var untouched = replica.CaptureModelState().PendingInput;
        Assert.AreEqual(("", "", false, 0), (untouched.EscapePrefix, untouched.Utf8, untouched.GroundEscape, untouched.FramerUtf8Remaining), $"{shape}: the replica holds pending input");
    }

    [TestMethod]
    public void Estimates_CountThePendingPrefix()
    {
        // The start's floor and the budget estimate grow with an unfinished escape sequence by at least its UTF-8 bytes
        // (an OSC title has no length cap in the model), and the floor never exceeds the projection's exact size.
        var bare = Detached(new FakeTimeProvider());
        var full = Detached(new FakeTimeProvider());
        var prefix = "\u001b]0;" + string.Concat(Enumerable.Repeat("t漢", 200_000));
        full.ApplyRecordedOutput(Encoding.UTF8.GetBytes(prefix));
        Assert.AreEqual(prefix, full.CaptureModelState().PendingInput.EscapePrefix, "fixture: the prefix is pending");
        var oracle = Encoding.UTF8.GetByteCount(prefix);
        Assert.IsGreaterThanOrEqualTo(oracle, full.MinimumModelStateJsonBytesUnsafe() - bare.MinimumModelStateJsonBytesUnsafe(), "the floor does not count the prefix");
        Assert.IsGreaterThanOrEqualTo(oracle, full.EstimateModelStateBytesUnsafe() - bare.EstimateModelStateBytesUnsafe(), "the budget estimate does not count the prefix");
        var exact = JsonSerializer.SerializeToUtf8Bytes(full.CaptureModelState(), DiagnosticsJsonContext.Default.DiagnosticModelState).LongLength;
        Assert.IsLessThanOrEqualTo(exact, full.MinimumModelStateJsonBytesUnsafe(), "the floor exceeds the projection's exact size");
    }
}
