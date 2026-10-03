using System.Text;
using System.Text.Json;
using Hex1b.Diagnostics;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    [TestMethod]
    [DataRow("identified Sixel", "\u001bPq@\u001b", "q@", 2L, false, "sixel-continuation")]
    [DataRow("discarded non-Sixel content", "\u001bPz12345678\u001b", "z1234567", 9L, true, "dcs-retention-limit")]
    public async Task Start_HeldEscDcsRefusesStartAndRecoveryWithoutStateAndPreservesOrdinaryIngress(
        string shape, string prefix, string retained, long byteCount, bool retentionExceeded, string reason)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(40, 10),
            Width = 40,
            Height = 10,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 8 },
        });
        using var running = new Running(terminal);
        var appliedSequence = terminal.CurrentModelSequence + 1;
        await workload.WriteAndWaitAsync(terminal, "before " + prefix);
        await WaitAsync(() =>
        {
            var applied = terminal.CaptureModelState();
            return applied.ModelSequence == appliedSequence && applied.PendingInput.Dcs is { State: "escape" };
        });
        var pending = terminal.CaptureModelState();
        Assert.AreEqual(appliedSequence, pending.ModelSequence, shape + ": prefix was not applied");
        Assert.IsNotNull(pending.PendingInput.Dcs, shape + ": no held-ESC DCS continuation");
        var dcs = pending.PendingInput.Dcs;
        Assert.AreEqual(("escape", "payload", byteCount, retentionExceeded),
            (dcs.State, dcs.StateBeforeEscape, dcs.ByteCount, dcs.RetentionLimitExceeded), shape);
        CollectionAssert.AreEqual(Encoding.UTF8.GetBytes(retained), Convert.FromBase64String(dcs.RetainedBytes),
            shape + ": held ESC must not enter retained content or its byte count");
        CollectionAssert.AreEqual(new[] { reason }, pending.Unsupported.ToArray(), shape);

        var path = StartLive(terminal, root);
        var diagnostics = new TerminalDiagnostics(terminal);
        var recovery = diagnostics.RecoverCase("held-escape");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "unsupported", appliedSequence),
            (recovery.Outcome, recovery.Status, recovery.ModelSequence), shape + ": " + recovery.Problem?.Message);
        CollectionAssert.AreEqual(new[] { reason }, recovery.UnsupportedSurfaces!.ToArray(), shape);
        StringAssert.Contains(recovery.Reason, reason, shape);

        const string cancellation = "\u0018 ordinary recording";
        const string ordinary = " continues";
        await workload.WriteAndWaitAsync(terminal, cancellation);
        await workload.WriteAndWaitAsync(terminal, ordinary);
        await WaitAsync(() => terminal.CaptureModelState() is { ModelSequence: var sequence, PendingInput.Dcs: null }
            && sequence == appliedSequence + 2);
        Assert.IsEmpty(terminal.CaptureModelState().Unsupported, shape + ": CAN did not restore supported ordinary output");
        StringAssert.Contains(terminal.GetScreenText(), "before  ordinary recording continues", shape);
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);

        var artifact = Artifact.Read(path);
        var start = artifact.Manifest.GetProperty("checkpoint");
        Assert.AreEqual(("text-state/3", "unsupported", appliedSequence),
            (start.GetProperty("profile").GetString(), start.GetProperty("status").GetString(), start.GetProperty("modelSequence").GetInt64()), shape);
        CollectionAssert.AreEqual(new[] { reason }, start.GetProperty("unsupportedSurfaces").EnumerateArray().Select(e => e.GetString()).ToArray(), shape);
        StringAssert.Contains(start.GetProperty("reason").GetString(), reason, shape);
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "checkpoint"
            && e.GetProperty("checkpoint").GetProperty("trigger").GetString() == "start"),
            shape + ": refused start recorded a usable continuation");
        var recoveryEvent = Recoveries(artifact).Single();
        var checkpoint = recoveryEvent.GetProperty("checkpoint");
        Assert.AreEqual(("unsupported", recovery.CheckpointOrdinal, appliedSequence),
            (checkpoint.GetProperty("status").GetString(), checkpoint.GetProperty("ordinal").GetInt64(), recoveryEvent.GetProperty("modelSequence").GetInt64()), shape);
        StringAssert.Contains(checkpoint.GetProperty("reason").GetString(), reason, shape);
        Assert.IsFalse(checkpoint.TryGetProperty("state", out var refusedState) && refusedState.ValueKind == JsonValueKind.Object,
            shape + ": refused recovery recorded a usable or truncated continuation");
        var applications = artifact.ModelEvents().Where(e => e.GetProperty("kind").GetString() == "application").ToList();
        CollectionAssert.AreEqual(new[] { appliedSequence + 1, appliedSequence + 2 }, applications.Select(e => e.GetProperty("modelSequence").GetInt64()).ToArray(), shape);
        CollectionAssert.AreEqual(new[] { cancellation, ordinary }, applications
            .Select(e => Encoding.UTF8.GetString(Convert.FromBase64String(e.GetProperty("data").GetString()!))).ToArray(),
            shape + ": refusal changed, dropped, reordered or synthesized original ingress");
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "no-valid-interval"), (refused.Outcome, refused.Problem?.Code),
            shape + ": a refused origin became usable: " + refused.Problem?.Message);
    }
}
