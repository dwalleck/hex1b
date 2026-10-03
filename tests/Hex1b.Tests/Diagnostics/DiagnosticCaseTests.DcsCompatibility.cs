using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Hex1b.Sixel;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    [TestMethod]
    [DataRow("fresh", "missing object")]
    [DataRow("fresh", "null object")]
    [DataRow("fresh", "missing header limit")]
    [DataRow("fresh", "missing numeric limit")]
    [DataRow("fresh", "header zero")]
    [DataRow("fresh", "numeric negative")]
    [DataRow("fresh", "numeric not integer")]
    [DataRow("fresh", "unknown field")]
    [DataRow("continuation", "missing object")]
    [DataRow("continuation", "null object")]
    [DataRow("continuation", "missing header limit")]
    [DataRow("continuation", "missing numeric limit")]
    [DataRow("continuation", "header zero")]
    [DataRow("continuation", "numeric negative")]
    [DataRow("continuation", "numeric not integer")]
    [DataRow("continuation", "unknown field")]
    public async Task Compatibility_DcsFramingPolicyIsRequiredBeforeReplicaConstruction(string origin, string shape)
    {
        using var root = new CaseRoot();
        var path = origin == "fresh"
            ? await RecordCaseAsync(root, [new("\u001bP1;2zignored\u001b\\ visible")], new HeadlessPresentationAdapter(20, 4))
            : await RecordDcsContinuationCaseAsync(root, "\u001bP1;", "2zignored\u001b\\ visible");
        EditManifest(path, manifest =>
        {
            var configuration = manifest["checkpoint"]!["configuration"]!.AsObject();
            var framing = configuration["dcsFraming"]!.AsObject();
            switch (shape)
            {
                case "missing object": configuration.Remove("dcsFraming"); break;
                case "null object": configuration["dcsFraming"] = null; break;
                case "missing header limit": framing.Remove("maximumHeaderParameters"); break;
                case "missing numeric limit": framing.Remove("maximumNumericValue"); break;
                case "header zero": framing["maximumHeaderParameters"] = 0; break;
                case "numeric negative": framing["maximumNumericValue"] = -1; break;
                case "numeric not integer": framing["maximumNumericValue"] = "3"; break;
                case "unknown field": framing["unknownLimit"] = 1; break;
            }
        });
        var built = 0;
        CaseReapplier.BuildReplicaForTesting.Value = () => built++;
        try
        {
            var result = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), origin + ": " + shape + ": " + result.Problem?.Message);
            Assert.AreEqual("configuration", result.Compatibility.Checks.Single(c => c.Verdict == "incompatible").Check, shape);
            Assert.AreEqual(0, built, "invalid policy built a replica");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), "invalid policy wrote a run");
        }
        finally
        {
            CaseReapplier.BuildReplicaForTesting.Value = null;
        }
    }

    [TestMethod]
    [DataRow("fresh", "numeric")]
    [DataRow("fresh", "parameters")]
    [DataRow("continuation", "numeric")]
    [DataRow("continuation", "parameters")]
    public async Task Reapply_NondefaultDcsPolicyRebuildsFreshAndContinuationInterpretation(string origin, string limit)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var policy = SixelCompatibilityPolicy.Default with { MaximumDcsHeaderParameters = 2, MaximumNumericValue = 3 };
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(20, 4),
            Width = 20,
            Height = 4,
            SixelPolicy = policy,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 32 },
        });
        using var running = new Running(terminal);
        var prefix = limit == "numeric" ? "\u001bP3" : "\u001bP1;2";
        string path;
        if (origin == "continuation")
        {
            await workload.WriteAndWaitAsync(terminal, prefix);
            path = StartLive(terminal, root);
        }
        else
        {
            path = StartLive(terminal, root);
            await workload.WriteAndWaitAsync(terminal, prefix);
        }
        var suffix = limit == "numeric" ? "4zignored" : ";3zignored";
        await workload.WriteAndWaitAsync(terminal, suffix);
        var diagnostics = new TerminalDiagnostics(terminal);
        Assert.AreEqual("malformed-introducer", terminal.CaptureModelState().PendingInput.Dcs!.State, "fixture did not exceed nondefault policy");
        diagnostics.MarkCase("policy-boundary");
        await workload.WriteAndWaitAsync(terminal, "\u001b\\ visible");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        AssertMatched(Reapply(path, label: "policy-boundary"), origin + ": " + limit + " reconstructed policy-sensitive pending state");
        AssertMatched(Reapply(path, label: "stop"), origin + ": " + limit + " subsequent text");
        var defaultPolicy = CopyCase(root, path, "default-" + limit);
        EditManifest(defaultPolicy, manifest =>
        {
            manifest["checkpoint"]!["configuration"]!["dcsFraming"]!["maximumHeaderParameters"] = SixelCompatibilityPolicy.Default.MaximumDcsHeaderParameters;
            manifest["checkpoint"]!["configuration"]!["dcsFraming"]!["maximumNumericValue"] = SixelCompatibilityPolicy.Default.MaximumNumericValue;
        });
        var changed = Reapply(defaultPolicy, label: "policy-boundary");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (changed.Outcome, changed.Problem?.Code), changed.Problem?.Message);
        Assert.AreEqual("checkpoint.profile", changed.Compatibility.Checks.Single(check => check.Verdict == "incompatible").Check,
            "changing producer policy did not invalidate the derived continuation state");
        Assert.IsFalse(Directory.Exists(Path.Combine(defaultPolicy, "reapplications")), "invalid policy/state pairing wrote a run");
    }

    [TestMethod]
    [DataRow("origin")]
    [DataRow("target")]
    public async Task Compatibility_TextStateThreeRejectsTextStateTwoWithoutReplay(string boundary)
    {
        using var root = new CaseRoot();
        var path = await RecordDcsContinuationCaseAsync(root, "\u001bPzignored", "\u001b\\ visible");
        if (boundary == "origin")
            EditManifest(path, manifest => manifest["checkpoint"]!["profile"] = "text-state/2");
        EditEventLine(path, node => node["checkpoint"]?["trigger"]?.GetValue<string>() == (boundary == "origin" ? "start" : "stop"), node =>
        {
            node["checkpoint"]!["profile"] = "text-state/2";
            node["checkpoint"]!["state"]!["profile"] = "text-state/2";
        });
        var built = 0;
        CaseReapplier.BuildReplicaForTesting.Value = () => built++;
        try
        {
            var refused = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (refused.Outcome, refused.Problem?.Code), boundary + ": " + refused.Problem?.Message);
            Assert.AreEqual("checkpoint.profile", refused.Compatibility.Checks.Single(c => c.Verdict == "incompatible").Check, boundary);
            Assert.AreEqual(0, built, boundary + ": incompatible profile built a replica");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), boundary + ": incompatible profile wrote a run");
        }
        finally
        {
            CaseReapplier.BuildReplicaForTesting.Value = null;
        }
    }

    [TestMethod]
    [DataRow("start", "dcs")]
    [DataRow("start", "state")]
    [DataRow("start", "retainedBytes")]
    [DataRow("start", "byteCount")]
    [DataRow("start", "retentionLimitExceeded")]
    [DataRow("start", "stateBeforeEscape")]
    [DataRow("stop", "dcs")]
    [DataRow("stop", "state")]
    [DataRow("stop", "retainedBytes")]
    [DataRow("stop", "byteCount")]
    [DataRow("stop", "retentionLimitExceeded")]
    [DataRow("stop", "stateBeforeEscape")]
    public async Task Reapply_MissingRequiredDcsCheckpointStateIsIncompatible(string boundary, string member)
    {
        using var root = new CaseRoot();
        var path = await RecordDcsContinuationCaseAsync(root, "\u001bPzignored\u001b", "X\u001b");
        EditEventLine(path, node => node["checkpoint"]?["trigger"]?.GetValue<string>() == boundary, node =>
        {
            var pending = node["checkpoint"]!["state"]!["pendingInput"]!.AsObject();
            if (member == "dcs")
                pending.Remove(member);
            else
                pending["dcs"]!.AsObject().Remove(member);
        });
        var applied = new System.Runtime.CompilerServices.StrongBox<int>();
        CaseReapplier.AppliedEventsForTesting.Value = applied;
        try
        {
            var refused = Reapply(path, label: boundary);
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (refused.Outcome, refused.Problem?.Code), boundary + ": " + member + ": " + refused.Problem?.Message);
            Assert.AreEqual(0, applied.Value, boundary + ": malformed checkpoint applied events");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), boundary + ": malformed checkpoint wrote a run");
        }
        finally
        {
            CaseReapplier.AppliedEventsForTesting.Value = null;
        }
    }

    [TestMethod]
    [DataRow("old profile")]
    [DataRow("old configuration")]
    public async Task Compatibility_OldConsumerCannotSilentlyIgnoreRequiredDcsSupport(string declaration)
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, [new("visible")], new HeadlessPresentationAdapter(20, 4));
        var build = CaseCompatibility.Consumer.Build;
        CaseCompatibility.Consumer.CurrentForTesting.Value = declaration == "old profile"
            ? build with { Profiles = ["fresh-model/1", "text-state/2"] }
            : build with { RequiredFields = build.RequiredFields.Where(field => field != "dcsFraming").ToArray() };
        var built = 0;
        CaseReapplier.BuildReplicaForTesting.Value = () => built++;
        try
        {
            var result = declaration == "old profile" ? Reapply(path, label: "stop") : Reapply(path, modelSequence: 1);
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
            Assert.AreEqual(declaration == "old profile" ? "checkpoint.profile" : "configuration",
                result.Compatibility.Checks.Single(check => check.Verdict == "incompatible").Check);
            Assert.AreEqual(0, built, "old consumer built a replica without required support");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")));
        }
        finally
        {
            CaseReapplier.BuildReplicaForTesting.Value = null;
            CaseCompatibility.Consumer.CurrentForTesting.Value = null;
        }
        AssertMatched(Reapply(path, label: "stop"), "current consumer of the unchanged case");
    }

    [TestMethod]
    [DataRow("derived state mismatch")]
    [DataRow("count exceeds producer retention")]
    [DataRow("completed prefix")]
    [DataRow("identified Sixel")]
    public async Task Reapply_InvalidDcsTargetCannotReplayBeforeRefusal(string shape)
    {
        using var root = new CaseRoot();
        var original = await RecordDcsContinuationCaseAsync(root, "\u001bPzignored", "suffix");
        var path = CopyCase(root, original, shape.Replace(' ', '-'));
        if (shape == "count exceeds producer retention")
            EditManifest(path, manifest => manifest["checkpoint"]!["configuration"]!["graphics"]!["maximumRetainedInputBytesPerImage"] = 8);
        EditEventLine(path, node => node["checkpoint"]?["trigger"]?.GetValue<string>() == "stop", node =>
        {
            var dcs = node["checkpoint"]!["state"]!["pendingInput"]!["dcs"]!;
            var bytes = shape switch
            {
                "derived state mismatch" => "$q"u8.ToArray(),
                "completed prefix" => "z\u001b\\"u8.ToArray(),
                "identified Sixel" => "q"u8.ToArray(),
                _ => "z12345678"u8.ToArray(),
            };
            dcs["state"] = shape == "derived state mismatch" ? "introducer" : "payload";
            dcs["retainedBytes"] = Convert.ToBase64String(bytes);
            dcs["byteCount"] = bytes.LongLength;
        });
        var applied = new System.Runtime.CompilerServices.StrongBox<int>();
        CaseReapplier.AppliedEventsForTesting.Value = applied;
        try
        {
            var refused = Reapply(path, label: "stop");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (refused.Outcome, refused.Problem?.Code), shape + ": " + refused.Problem?.Message);
            Assert.AreEqual(0, applied.Value, shape + ": invalid target replayed an application");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), shape + ": invalid target wrote a run");
        }
        finally
        {
            CaseReapplier.AppliedEventsForTesting.Value = null;
        }
        AssertMatched(Reapply(original, label: "stop"), "the unmodified continuation target");
    }

    private static async Task<string> RecordDcsContinuationCaseAsync(CaseRoot root, string prefix, string suffix)
    {
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, null, 100);
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before " + prefix);
        var path = StartLive(terminal, root);
        if (suffix.Length > 0)
            await workload.WriteAndWaitAsync(terminal, suffix);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        return path;
    }
}
