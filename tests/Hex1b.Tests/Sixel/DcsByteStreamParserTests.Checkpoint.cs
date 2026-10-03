using System.Security.Cryptography;
using System.Text;
using Hex1b.Tokens;
using StateKind = Hex1b.Tokens.DcsParserCheckpoint.StateKind;

namespace Hex1b.Tests.Sixel;

public partial class DcsByteStreamParserTests
{
    [TestMethod]
    public void Constructor_LargeDeclaredHeaderLimit_DoesNotAllocateUnusedParameterSlots()
    {
        using (var warmOrdinary = new DcsByteStreamParser(maximumParameterCount: 16))
        using (var warmLarge = new DcsByteStreamParser(maximumParameterCount: 65_536))
        {
            // Warm both policy paths and release hash resources outside either measurement.
        }

        var beforeOrdinary = GC.GetAllocatedBytesForCurrentThread();
        var ordinary = new DcsByteStreamParser(maximumParameterCount: 16);
        var ordinaryAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeOrdinary;
        ordinary.Dispose();

        var beforeLarge = GC.GetAllocatedBytesForCurrentThread();
        var large = new DcsByteStreamParser(maximumParameterCount: 65_536);
        var largeAllocated = GC.GetAllocatedBytesForCurrentThread() - beforeLarge;
        large.Dispose();

        Assert.IsLessThanOrEqualTo(4096L, largeAllocated - ordinaryAllocated,
            "A declared header limit must not allocate storage for parameter slots that no input has consumed.");
    }

    [TestMethod]
    [DataRow(32)]
    [DataRow(33)]
    [DataRow(34)]
    public void CreateRestored_GrownHeader_EverySplitPreservesExactParametersAndLogicalBoundary(int parameterCount)
    {
        const int logicalLimit = 33;
        var header = string.Join(';', Enumerable.Repeat("1", parameterCount));
        var bytes = Encoding.ASCII.GetBytes("\x1bP" + header + "pABC\x1b\\X");
        using var reference = new DcsByteStreamParser(maximumParameterCount: logicalLimit);
        var expected = new ParserObservation();
        expected.Add(reference.Process(bytes));
        expected.Add(reference.Complete());
        var expectedFrame = TestSeq.Single(expected.Frames);
        Assert.AreEqual(parameterCount <= logicalLimit ? DcsSequenceStatus.Complete : DcsSequenceStatus.Malformed,
            expectedFrame.Status);
        TestSeq.AreEqual(Enumerable.Repeat<int?>(1, Math.Min(parameterCount, logicalLimit)),
            expectedFrame.Introducer.Parameters);
        Assert.AreEqual("X", Encoding.ASCII.GetString(expected.Text.ToArray()));

        for (var split = 2; split < bytes.Length; split++)
        {
            using var original = new DcsByteStreamParser(maximumParameterCount: logicalLimit);
            _ = original.Process(bytes.AsSpan(0, split));
            if (!original.IsInDcs)
                continue;
            using var target = new DcsByteStreamParser(maximumParameterCount: logicalLimit);
            using var restored = target.CreateRestored(original.CaptureCheckpoint());
            var actual = new ParserObservation();
            actual.Add(restored.Process(bytes.AsSpan(split)));
            actual.Add(restored.Complete());
            AssertObservationEqual(expected, actual, $"parameters={parameterCount}, split={split}");

            var fresh = restored.Process("\x1bP;pNEXT\x1b\\Y"u8);
            TestSeq.AreEqual(new int?[] { null, null }, TestSeq.Single(fresh.Frames).Frame.Introducer.Parameters);
            Assert.AreEqual(DcsSequenceStatus.Complete, TestSeq.Single(fresh.Frames).Frame.Status);
            Assert.AreEqual("Y", Encoding.ASCII.GetString(fresh.TextBytes.Span));
        }
    }

    [TestMethod]
    [DataRow("$qm", false)]
    [DataRow("$qm", true)]
    [DataRow("?1qABC", false)]
    [DataRow("?1qABC", true)]
    [DataRow("1+r544e", false)]
    [DataRow("1+r544e", true)]
    [DataRow("1;2pABC", false)]
    [DataRow("1;2pABC", true)]
    [DataRow("1:2qABC", false)]
    [DataRow("1:2qABC", true)]
    [DataRow("1 2qABC", false)]
    [DataRow("1 2qABC", true)]
    [DataRow("$qA\x1bZB", false)]
    [DataRow("$qA\x1bZB", true)]
    [DataRow("1\x1b;2pABC", false)]
    [DataRow("1\x1b;2pABC", true)]
    [DataRow("1:2\x1bZABC", false)]
    [DataRow("1:2\x1bZABC", true)]
    public void CreateRestored_EverySupportedSplit_PreservesExactFrameAndFollowingText(
        string content,
        bool useC1)
    {
        var bytes = Encoding.Latin1.GetBytes(useC1
            ? $"\u0090{content}\u009cX"
            : $"\x1bP{content}\x1b\\X");
        var expected = Parse(bytes);

        for (var split = 1; split < bytes.Length; split++)
        {
            using var original = new DcsByteStreamParser();
            _ = original.Process(bytes.AsSpan(0, split));
            if (!original.IsInDcs)
                continue;

            using var target = new DcsByteStreamParser();
            using var restored = target.CreateRestored(original.CaptureCheckpoint());
            var actual = new ParserObservation();
            actual.Add(restored.Process(bytes.AsSpan(split)));
            actual.Add(restored.Complete());

            AssertObservationEqual(expected, actual, $"{content}, C1={useC1}, split {split}");
        }
    }

    [TestMethod]
    [DataRow("\x1bP", 0x18)]
    [DataRow("\x1bP", 0x1a)]
    [DataRow("\x1bP", -1)]
    [DataRow("\x1bP1;", 0x18)]
    [DataRow("\x1bP1;", 0x1a)]
    [DataRow("\x1bP1;", -1)]
    [DataRow("\x1bP$qm", 0x18)]
    [DataRow("\x1bP$qm", 0x1a)]
    [DataRow("\x1bP$qm", -1)]
    [DataRow("\x1bP1:2", 0x18)]
    [DataRow("\x1bP1:2", 0x1a)]
    [DataRow("\x1bP1:2", -1)]
    [DataRow("\x1bP1;\x1b", 0x18)]
    [DataRow("\x1bP1;\x1b", 0x1a)]
    [DataRow("\x1bP1;\x1b", -1)]
    [DataRow("\x1bP$qm\x1b", 0x18)]
    [DataRow("\x1bP$qm\x1b", 0x1a)]
    [DataRow("\x1bP$qm\x1b", -1)]
    [DataRow("\x1bP1:2\x1b", 0x18)]
    [DataRow("\x1bP1:2\x1b", 0x1a)]
    [DataRow("\x1bP1:2\x1b", -1)]
    [DataRow("\u0090$qm\x1b", 0x18)]
    [DataRow("\u0090$qm\x1b", 0x1a)]
    [DataRow("\u0090$qm\x1b", -1)]
    public void CreateRestored_CancellationAndEof_PreserveFrameContentHashCountAndStatus(
        string prefix,
        int ending)
    {
        using var original = new DcsByteStreamParser();
        _ = original.Process(Encoding.Latin1.GetBytes(prefix));
        using var target = new DcsByteStreamParser();
        using var restored = target.CreateRestored(original.CaptureCheckpoint());
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        if (ending >= 0)
        {
            var suffix = new byte[] { (byte)ending, (byte)'X' };
            expected.Add(original.Process(suffix));
            actual.Add(restored.Process(suffix));
        }
        expected.Add(original.Complete());
        actual.Add(restored.Complete());
        expected.Add(original.Process("Y"u8));
        actual.Add(restored.Process("Y"u8));

        AssertObservationEqual(expected, actual, $"{prefix}, ending {ending}");
        Assert.AreEqual(ending < 0 ? DcsSequenceStatus.Unterminated : DcsSequenceStatus.Cancelled,
            TestSeq.Single(actual.Frames).Status);
        Assert.AreEqual(ending < 0 ? "Y" : "XY", Encoding.ASCII.GetString(actual.Text.ToArray()));
    }

    [TestMethod]
    public void CaptureCheckpoint_StablePrefix_SurvivesAppendGrowthCompletionAndReplacement()
    {
        var prefix = "\x1bP$q" + new string('A', 61);
        var retainedPrefix = Encoding.ASCII.GetBytes(prefix[2..]);
        var growingSuffix = "B" + new string('C', 120);
        using var live = new DcsByteStreamParser();
        _ = live.Process(Encoding.ASCII.GetBytes(prefix));
        var checkpoint = live.CaptureCheckpoint();

        _ = live.Process("B"u8); // Fits the initial array, beyond the captured prefix.
        TestSeq.AreEqual(retainedPrefix, checkpoint.RetainedContent.ToArray());
        _ = live.Process(Encoding.ASCII.GetBytes(new string('C', 120))); // Grows the retained array.
        TestSeq.AreEqual(retainedPrefix, checkpoint.RetainedContent.ToArray());
        _ = live.Process("\x1b\\X\x1bPpNEXT"u8); // Completes and replaces the live prefix.
        TestSeq.AreEqual(retainedPrefix, checkpoint.RetainedContent.ToArray());
        var ahead = live.CaptureCheckpoint();

        using var restored = live.CreateRestored(checkpoint);
        var actual = new ParserObservation();
        actual.Add(restored.Process(Encoding.ASCII.GetBytes(growingSuffix + "\x1b\\X")));
        actual.Add(restored.Complete());
        var expected = Parse(Encoding.ASCII.GetBytes(prefix + growingSuffix + "\x1b\\X"));
        AssertObservationEqual(expected, actual, "captured boundary while live parser is ahead");

        var liveFrame = TestSeq.Single(live.Process("\x1b\\Y"u8).Frames).Frame;
        Assert.AreEqual("pNEXT", Encoding.ASCII.GetString(liveFrame.RetainedContent.Span));
        TestSeq.AreEqual("pNEXT"u8.ToArray(), ahead.RetainedContent.ToArray());
        TestSeq.AreEqual(retainedPrefix, checkpoint.RetainedContent.ToArray());
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CreateRestored_LargePrefix_RebuildsFlushedAndBufferedHash(bool transferPrivateArray)
    {
        var prefix = Encoding.ASCII.GetBytes("\x1bP1+r" + new string('A', 9000));
        using var original = new DcsByteStreamParser();
        _ = original.Process(prefix);
        var checkpoint = original.CaptureCheckpoint();
        byte[]? privateBytes = transferPrivateArray ? checkpoint.RetainedContent.ToArray() : null;
        if (privateBytes is not null)
            checkpoint = checkpoint with { RetainedContent = privateBytes };
        using var target = new DcsByteStreamParser();
        using var restored = target.CreateRestored(checkpoint, privateBytes);
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        expected.Add(original.Process("B\x1b\\X"u8));
        actual.Add(restored.Process("B\x1b\\X"u8));
        expected.Add(original.Complete());
        actual.Add(restored.Complete());

        AssertObservationEqual(expected, actual, $"private array ownership={transferPrivateArray}");
        Assert.AreEqual(9004L, TestSeq.Single(actual.Frames).ByteCount);
    }

    [TestMethod]
    [DataRow("100pABC")]
    [DataRow("1;2;3pABC")]
    [DataRow("1;2;")]
    [DataRow("100pABC\x1b")]
    public void CreateRestored_CustomHeaderPolicy_PreservesMalformedContinuation(string content)
    {
        using var original = new DcsByteStreamParser(128, 2, 99);
        _ = original.Process(Encoding.ASCII.GetBytes("\x1bP" + content));
        using var target = new DcsByteStreamParser(128, 2, 99);
        using var restored = target.CreateRestored(original.CaptureCheckpoint());
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        expected.Add(original.Process("\x1b\\X"u8));
        actual.Add(restored.Process("\x1b\\X"u8));

        AssertObservationEqual(expected, actual, content);
        Assert.AreEqual(DcsSequenceStatus.Malformed, TestSeq.Single(actual.Frames).Status);
        Assert.IsFalse(TestSeq.Single(actual.Frames).Introducer.IsValid);
    }

    [TestMethod]
    [DataRow("wrong-count")]
    [DataRow("negative-count")]
    [DataRow("retained-truncated")]
    [DataRow("unsupported-state")]
    [DataRow("ground-state")]
    [DataRow("wrong-state")]
    [DataRow("false-malformed")]
    [DataRow("unexpected-before-escape")]
    [DataRow("wrong-held-state")]
    [DataRow("missing-held-state")]
    [DataRow("overflow")]
    [DataRow("declared-sixel")]
    [DataRow("hidden-sixel")]
    [DataRow("terminator")]
    [DataRow("c1-terminator")]
    [DataRow("cancellation")]
    [DataRow("unaccounted-held-escape")]
    [DataRow("header-numeric-policy")]
    [DataRow("header-count-policy")]
    [DataRow("malformed-hidden-state")]
    [DataRow("retention-policy")]
    public void CreateRestored_RejectsInvalidContinuationWithoutChangingTarget(string alteration)
    {
        using var target = new DcsByteStreamParser(64, 2, 99);
        using var original = new DcsByteStreamParser(64, 2, 99);
        _ = target.Process("\x1bP$qm"u8);
        _ = original.Process("\x1bP$qm"u8);
        var checkpoint = target.CaptureCheckpoint();
        var invalid = alteration switch
        {
            "wrong-count" => checkpoint with { ByteCount = checkpoint.ByteCount + 1 },
            "negative-count" => checkpoint with { ByteCount = -1 },
            "retained-truncated" => checkpoint with { RetainedContent = checkpoint.RetainedContent[..^1] },
            "unsupported-state" => checkpoint with { State = (StateKind)123 },
            "ground-state" => checkpoint with { State = StateKind.Ground },
            "wrong-state" => checkpoint with { State = StateKind.Introducer },
            "false-malformed" => checkpoint with { State = StateKind.MalformedIntroducer },
            "unexpected-before-escape" => checkpoint with { StateBeforeEscape = StateKind.Payload },
            "wrong-held-state" => checkpoint with { State = StateKind.DcsEscape, StateBeforeEscape = StateKind.Introducer },
            "missing-held-state" => checkpoint with { State = StateKind.DcsEscape },
            "overflow" => checkpoint with { RetentionLimitExceeded = true },
            "declared-sixel" => checkpoint with { IsSixel = true },
            "hidden-sixel" => PayloadCheckpoint("qABC"),
            "terminator" => PayloadCheckpoint("$qm\x1b\\"),
            "c1-terminator" => PayloadCheckpoint("$qm\u009c"),
            "cancellation" => PayloadCheckpoint("$qm\x18X"),
            "unaccounted-held-escape" => PayloadCheckpoint("$qm\x1b"),
            "header-numeric-policy" => PayloadCheckpoint("100p"),
            "header-count-policy" => PayloadCheckpoint("1;2;3p"),
            "malformed-hidden-state" => PayloadCheckpoint("1:2p"),
            "retention-policy" => PayloadCheckpoint("$q" + new string('A', 63)),
            _ => throw new InvalidOperationException(alteration),
        };

        Assert.ThrowsExactly<ArgumentException>(() => target.CreateRestored(invalid));
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        expected.Add(original.Process("MORE\x1b\\X"u8));
        actual.Add(target.Process("MORE\x1b\\X"u8));
        AssertObservationEqual(expected, actual, alteration);
        TestSeq.AreEqual("$qm"u8.ToArray(), checkpoint.RetainedContent.ToArray());
    }

    [TestMethod]
    [DataRow(31, false)]
    [DataRow(32, false)]
    [DataRow(33, false)]
    [DataRow(31, true)]
    [DataRow(32, true)]
    [DataRow(33, true)]
    public void CreateRestored_RetentionBoundary_IsIntactOnlyThroughExactLimit(int contentLength, bool heldEscape)
    {
        var prefix = Encoding.ASCII.GetBytes("\x1bPp" + new string('A', contentLength - 1) +
            (heldEscape ? "\x1b" : ""));
        ReadOnlySpan<byte> suffix = heldEscape ? "\\X"u8 : "\x1b\\X"u8;
        using var original = new DcsByteStreamParser(32);
        _ = original.Process(prefix);
        var checkpoint = original.CaptureCheckpoint();
        Assert.AreEqual(heldEscape ? StateKind.DcsEscape : StateKind.Payload, checkpoint.State);
        Assert.IsFalse(checkpoint.IsSixel);
        using var target = new DcsByteStreamParser(32);
        if (contentLength > 32)
        {
            Assert.IsTrue(checkpoint.RetentionLimitExceeded);
            Assert.AreEqual(32, checkpoint.RetainedContent.Length);
            Assert.AreEqual(33L, checkpoint.ByteCount);
            Assert.ThrowsExactly<ArgumentException>(() => target.CreateRestored(checkpoint));
            var batch = original.Process(suffix);
            Assert.AreEqual("X", Encoding.ASCII.GetString(batch.TextBytes.Span));
            var frame = TestSeq.Single(batch.Frames).Frame;
            Assert.AreEqual(DcsSequenceStatus.Complete, frame.Status);
            Assert.IsTrue(frame.RetentionLimitExceeded);
            Assert.AreEqual(33L, frame.ByteCount);
            TestSeq.AreEqual(prefix.AsSpan(2, 32).ToArray(), frame.RetainedContent.ToArray());
            TestSeq.AreEqual(SHA256.HashData(prefix.AsSpan(2, contentLength)), frame.ContentHash);
            return;
        }

        Assert.IsFalse(checkpoint.RetentionLimitExceeded);
        using var restored = target.CreateRestored(checkpoint);
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        expected.Add(original.Process(suffix));
        actual.Add(restored.Process(suffix));
        AssertObservationEqual(expected, actual, $"retained content length {contentLength}");
        Assert.AreEqual((long)contentLength, TestSeq.Single(actual.Frames).ByteCount);
    }

    [TestMethod]
    [DataRow("\x1bPq@")]
    [DataRow("\x1bP1;2q@\x1b")]
    [DataRow("\u0090q@")]
    public void CreateRestored_IdentifiedSixelAndHeldEscape_AreRejected(string prefix)
    {
        using var original = new DcsByteStreamParser();
        var batch = original.Process(Encoding.Latin1.GetBytes(prefix));
        Assert.IsTrue(batch.SixelIdentified);
        Assert.IsTrue(original.IsSixel);
        var checkpoint = original.CaptureCheckpoint();
        Assert.IsTrue(checkpoint.IsSixel);
        Assert.IsFalse(checkpoint.RetentionLimitExceeded);
        if (prefix.EndsWith('\x1b'))
        {
            Assert.AreEqual(StateKind.DcsEscape, checkpoint.State);
            Assert.AreEqual(StateKind.Payload, checkpoint.StateBeforeEscape);
        }
        using var target = new DcsByteStreamParser();
        Assert.ThrowsExactly<ArgumentException>(() => target.CreateRestored(checkpoint));

        var frame = TestSeq.Single(original.Complete().Frames).Frame;
        Assert.AreEqual(DcsSequenceStatus.Unterminated, frame.Status);
        Assert.IsTrue(frame.Introducer.IsSixel);
    }

    [TestMethod]
    [DataRow("\x1b\\X", false)]
    [DataRow("\u009cX", false)]
    [DataRow("\x18X", true)]
    [DataRow("\x1aX", true)]
    [DataRow("\x1b\\X\x1bP$qr", false)]
    [DataRow("\x18X\x1bP$qr", true)]
    public void Process_SixelIdentification_SurvivesCompletionCancellationAndReplacement(
        string ending,
        bool cancelled)
    {
        using var original = new DcsByteStreamParser();
        _ = original.Process("\x1bP1;"u8);
        using var target = new DcsByteStreamParser();
        using var restored = target.CreateRestored(original.CaptureCheckpoint());
        var suffix = Encoding.Latin1.GetBytes("2q@" + ending);
        var batch = restored.Process(suffix);
        var expected = new ParserObservation();
        var actual = new ParserObservation();
        expected.Add(original.Process(suffix));
        actual.Add(batch);

        AssertObservationEqual(expected, actual, ending);
        Assert.IsTrue(batch.SixelIdentified);
        Assert.IsFalse(restored.IsSixel);
        Assert.AreEqual(cancelled ? DcsSequenceStatus.Cancelled : DcsSequenceStatus.Complete,
            TestSeq.Single(batch.Frames).Frame.Status);
        Assert.IsTrue(TestSeq.Single(batch.Frames).Frame.Introducer.IsSixel);
        Assert.AreEqual("X", Encoding.ASCII.GetString(batch.TextBytes.Span));
    }

    [TestMethod]
    [DataRow("$qm")]
    [DataRow("?1qABC")]
    [DataRow("1 qABC")]
    [DataRow("1:2qABC")]
    public void Process_NonSixelFinalQ_DoesNotIdentifySixel(string content)
    {
        using var parser = new DcsByteStreamParser();
        var prefix = parser.Process(Encoding.ASCII.GetBytes("\x1bP" + content));
        Assert.IsFalse(prefix.SixelIdentified);
        Assert.IsFalse(parser.IsSixel);
        var completed = parser.Process("\x1b\\X"u8);
        Assert.IsFalse(completed.SixelIdentified);
        Assert.IsFalse(TestSeq.Single(completed.Frames).Frame.Introducer.IsSixel);
        Assert.AreEqual("X", Encoding.ASCII.GetString(completed.TextBytes.Span));
    }

    private static DcsParserCheckpoint PayloadCheckpoint(string content)
    {
        var retained = Encoding.Latin1.GetBytes(content);
        return new DcsParserCheckpoint(StateKind.Payload, StateKind.Ground, retained, retained.Length, false, false);
    }
}
