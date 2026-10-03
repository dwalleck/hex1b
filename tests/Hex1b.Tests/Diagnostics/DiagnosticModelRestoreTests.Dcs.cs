using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Microsoft.Extensions.Time.Testing;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticModelRestoreTests
{
    [TestMethod]
    [DataRow("empty introducer", "1b50", "introducer", null, "", "24716d1b5c6166746572")]
    [DataRow("partial introducer", "1b50313b", "introducer", null, "1;", "327a7061796c6f61641b5c6166746572")]
    [DataRow("DECRQSS payload", "1b5024716d", "payload", null, "$qm", "1b5c6166746572")]
    [DataRow("ignored payload", "1b50313b327a706179", "payload", null, "1;2zpay", "6c6f61649c6166746572")]
    [DataRow("malformed introducer", "1b50313a", "malformed-introducer", null, "1:", "7169676e6f7265641b5c6166746572")]
    [DataRow("held ESC in introducer", "1b50311b", "escape", "introducer", "1", "5c6166746572")]
    [DataRow("held ESC in payload", "1b507a7061791b", "escape", "payload", "zpay", "5c6166746572")]
    [DataRow("held ESC in malformed introducer", "1b50313a1b", "escape", "malformed-introducer", "1:", "585c1b5c6166746572")]
    [DataRow("C1 DCS and CAN", "90313b327a706179", "payload", null, "1;2zpay", "186166746572")]
    [DataRow("SUB cancellation", "1b507a706179", "payload", null, "zpay", "1a6166746572")]
    public void ModelRestore_DcsContinuationPreservesFramingAndContent(string shape, string prefixHex, string expectedState,
        string? stateBeforeEscape, string retained, string suffixHex)
    {
        using var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput("before "u8.ToArray());
        original.ApplyRecordedOutput(Convert.FromHexString(prefixHex));
        var state = original.CaptureModelState();
        Assert.IsEmpty(state.Unsupported, shape);
        var dcs = state.PendingInput.Dcs;
        Assert.IsNotNull(dcs, shape);
        Assert.AreEqual((expectedState, stateBeforeEscape, (long)Encoding.UTF8.GetByteCount(retained), false),
            (dcs.State, dcs.StateBeforeEscape, dcs.ByteCount, dcs.RetentionLimitExceeded), shape);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(retained), Convert.FromBase64String(dcs.RetainedBytes), shape);
        using var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        Assert.IsEmpty(JsonDifferences(Json(state), Json(replica.CaptureModelState())), shape + ": state before continuation");
        Assert.AreEqual(state.ModelSequence, replica.CurrentModelSequence, shape + ": restore is not an application");

        var suffix = Convert.FromHexString(suffixHex);
        foreach (var chunk in new[] { suffix[..1], suffix[1..] })
        {
            original.ApplyRecordedOutput(chunk);
            replica.ApplyRecordedOutput(chunk);
            Assert.IsEmpty(JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState())), shape + ": continued parser state");
        }
        Assert.IsNull(replica.CaptureModelState().PendingInput.Dcs, shape + ": DCS was not terminated");
        Assert.AreEqual(original.GetScreenText(), replica.GetScreenText(), shape);
        StringAssert.Contains(replica.GetScreenText(), "before after", shape);
    }

    [TestMethod]
    public void ModelRestore_DcsRetainsBinaryContentWithoutDecodingIt()
    {
        using var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput([.. "before \u001bPz"u8, 0xff, 0x00, 0x80, 0x1b]);
        var state = original.CaptureModelState();
        CollectionAssert.AreEqual(new byte[] { (byte)'z', 0xff, 0x00, 0x80 }, Convert.FromBase64String(state.PendingInput.Dcs!.RetainedBytes));
        Assert.AreEqual(("escape", "payload", 4L), (state.PendingInput.Dcs.State, state.PendingInput.Dcs.StateBeforeEscape, state.PendingInput.Dcs.ByteCount));
        using var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        Assert.IsEmpty(JsonDifferences(Json(state), Json(replica.CaptureModelState())));
        foreach (var chunk in new byte[][] { [.. "\\after "u8, 0xe6], [0xbc, 0xa2] })
        {
            original.ApplyRecordedOutput(chunk);
            replica.ApplyRecordedOutput(chunk);
            Assert.IsEmpty(JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState())));
        }
        StringAssert.Contains(replica.GetScreenText(), "before after 漢");
        Assert.IsNull(replica.CaptureModelState().PendingInput.Dcs);
    }

    [TestMethod]
    public void ModelRestore_DcsHeldEscapeChangesSuffixInterpretation()
    {
        using var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput("before \u001bPzignored\u001b"u8.ToArray());
        var state = original.CaptureModelState();
        using var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(state);
        using var omittedEscape = Detached(new FakeTimeProvider());
        omittedEscape.RestoreModelState(state with
        {
            PendingInput = state.PendingInput with { Dcs = state.PendingInput.Dcs! with { State = "payload", StateBeforeEscape = null } },
        });
        var prior = ModelStateComparer.Compare(state, omittedEscape.CaptureModelState(), ModelStateComparer.DefaultMaxDifferences);
        CollectionAssert.AreEquivalent(new[] { "pendingInput.dcs.state", "pendingInput.dcs.stateBeforeEscape" }, prior.Differences.Select(d => d.Path).ToArray());
        foreach (var model in new[] { original, replica, omittedEscape })
            model.ApplyRecordedOutput("\\VISIBLE"u8.ToArray());
        Assert.IsEmpty(JsonDifferences(Json(original.CaptureModelState()), Json(replica.CaptureModelState())));
        Assert.IsNull(replica.CaptureModelState().PendingInput.Dcs);
        Assert.AreEqual("payload", omittedEscape.CaptureModelState().PendingInput.Dcs!.State);
        StringAssert.Contains(replica.GetScreenText(), "VISIBLE");
        Assert.IsFalse(omittedEscape.GetScreenText().Contains("VISIBLE", StringComparison.Ordinal), "dropping held ESC did not affect framing");
    }

    [TestMethod]
    [DataRow("unknown state")]
    [DataRow("null state")]
    [DataRow("escape without prior state")]
    [DataRow("escape with invalid prior state")]
    [DataRow("prior state without escape")]
    [DataRow("derived state mismatch")]
    [DataRow("null bytes")]
    [DataRow("invalid base64")]
    [DataRow("truncated bytes")]
    [DataRow("negative count")]
    [DataRow("count exceeds bytes")]
    [DataRow("count exceeds int")]
    [DataRow("count long maximum")]
    [DataRow("overflow")]
    [DataRow("early ST")]
    [DataRow("early CAN")]
    [DataRow("identified Sixel")]
    [DataRow("ground ESC coexistence")]
    [DataRow("framer UTF8 coexistence")]
    public void ModelRestore_RejectsInvalidDcsBeforeAnyModelMutation(string shape)
    {
        using var original = Detached(new FakeTimeProvider());
        original.Resize(17, 6);
        original.ApplyRecordedOutput(Encoding.UTF8.GetBytes(HistoryLines(20) + "\u001b]2;producer\u0007\u001bPzignored"));
        var state = original.CaptureModelState();
        var dcs = state.PendingInput.Dcs!;
        var malformed = shape switch
        {
            "unknown state" => dcs with { State = "ground" },
            "null state" => dcs with { State = null! },
            "escape without prior state" => dcs with { State = "escape", StateBeforeEscape = null },
            "escape with invalid prior state" => dcs with { State = "escape", StateBeforeEscape = "escape" },
            "prior state without escape" => dcs with { StateBeforeEscape = "payload" },
            "derived state mismatch" => dcs with { State = "introducer" },
            "null bytes" => dcs with { RetainedBytes = null! },
            "invalid base64" => dcs with { RetainedBytes = "**" },
            "truncated bytes" => dcs with { RetainedBytes = "" },
            "negative count" => dcs with { ByteCount = -1 },
            "count exceeds bytes" => dcs with { ByteCount = dcs.ByteCount + 1 },
            "count exceeds int" => dcs with { ByteCount = (long)int.MaxValue + 1 },
            "count long maximum" => dcs with { ByteCount = long.MaxValue },
            "overflow" => dcs with { RetentionLimitExceeded = true },
            "early ST" => dcs with { RetainedBytes = Convert.ToBase64String("z\u001b\\"u8), ByteCount = 3 },
            "early CAN" => dcs with { RetainedBytes = Convert.ToBase64String("z\u0018"u8), ByteCount = 2 },
            "identified Sixel" => dcs with { RetainedBytes = Convert.ToBase64String("q"u8), ByteCount = 1 },
            _ => dcs,
        };
        var forged = state with
        {
            PendingInput = state.PendingInput with
            {
                Dcs = malformed,
                GroundEscape = shape == "ground ESC coexistence",
                FramerUtf8Remaining = shape == "framer UTF8 coexistence" ? 1 : 0,
            },
        };
        using var replica = Detached(new FakeTimeProvider());
        var before = Json(replica.CaptureModelState());
        Assert.ThrowsExactly<InvalidOperationException>(() => replica.RestoreModelState(forged), shape);
        Assert.IsEmpty(JsonDifferences(before, Json(replica.CaptureModelState())), shape + ": geometry, screen, history, metadata or continuation changed");
    }

    [TestMethod]
    [DataRow("dcs")]
    [DataRow("state")]
    [DataRow("retainedBytes")]
    [DataRow("byteCount")]
    [DataRow("retentionLimitExceeded")]
    public void ModelRestore_RequiredDcsJsonMembersCannotBeOmitted(string member)
    {
        using var original = Detached(new FakeTimeProvider());
        original.ApplyRecordedOutput("\u001bPzignored"u8.ToArray());
        var node = JsonSerializer.SerializeToNode(original.CaptureModelState(), DiagnosticsJsonContext.Default.DiagnosticModelState)!;
        var pending = node["pendingInput"]!.AsObject();
        if (member == "dcs")
            pending.Remove(member);
        else
            pending["dcs"]!.AsObject().Remove(member);
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(node.ToJsonString(), DiagnosticsJsonContext.Default.DiagnosticModelState), member);
    }

    [TestMethod]
    public void ModelRestore_GroundDcsNullIsExplicitAndEmptyIntroducerIsDistinct()
    {
        using var ground = Detached(new FakeTimeProvider());
        var groundState = ground.CaptureModelState();
        var groundJson = Json(groundState);
        Assert.AreEqual(JsonValueKind.Null, groundJson.GetProperty("pendingInput").GetProperty("dcs").ValueKind);
        var missing = JsonNode.Parse(groundJson.GetRawText())!;
        missing["pendingInput"]!.AsObject().Remove("dcs");
        Assert.ThrowsExactly<JsonException>(() => JsonSerializer.Deserialize(missing.ToJsonString(), DiagnosticsJsonContext.Default.DiagnosticModelState));
        ground.ApplyRecordedOutput("\u001bP"u8.ToArray());
        var open = ground.CaptureModelState();
        Assert.AreEqual(("introducer", "", 0L), (open.PendingInput.Dcs!.State, open.PendingInput.Dcs.RetainedBytes, open.PendingInput.Dcs.ByteCount));
        CollectionAssert.Contains(ModelStateComparer.Compare(groundState, open, ModelStateComparer.DefaultMaxDifferences).Differences.Select(d => d.Path).ToArray(), "pendingInput.dcs");
        using var replica = Detached(new FakeTimeProvider());
        replica.RestoreModelState(JsonSerializer.Deserialize(Json(open).GetRawText(), DiagnosticsJsonContext.Default.DiagnosticModelState)!);
        replica.ApplyRecordedOutput("zignored\u001b\\visible"u8.ToArray());
        StringAssert.Contains(replica.GetScreenText(), "visible");
        Assert.IsNull(replica.CaptureModelState().PendingInput.Dcs);
    }
}
