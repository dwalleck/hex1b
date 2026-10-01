using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // Compatible candidate builds (ticket 14): every re-application result carries the seven ordered compatibility
    // checks with both sides' values; a declaration that differs in one check refuses by that check, before anything
    // is built or written, from either side (an edited artifact, or a consumer declaring otherwise).

    private static readonly string[] CheckNames =
        ["formatVersion", "contractVersion", "checkpoint.profile", "checkpoint.coveredSurfaces", "configuration", "configuration.capabilities", "origin"];

    private static readonly CaseStep[] CompatibilityCorpus = [new("one\r\n"), CaseStep.Mark("m1"), new("two\r\n")];

    [TestMethod]
    [DataRow("captured")]
    [DataRow("format-3")]
    [DataRow("unknown-strategy")]
    [DataRow("unknown-label")]
    [DataRow("no-manifest")]
    [DataRow("storage-refused")]
    [System.Runtime.Versioning.SupportedOSPlatform("linux")]
    public async Task Compatibility_RecordOnEveryResult(string shape)
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        string? label = "m1";
        switch (shape)
        {
            case "format-3": EditManifest(path, m => m["formatVersion"] = 3); break;
            case "unknown-strategy": EditManifest(path, m => m["checkpoint"]!["configuration"]!["reflowStrategy"] = "nope"); break;
            case "unknown-label": label = "nothing"; break;
            case "no-manifest": File.Delete(Path.Combine(path, "manifest.json")); break;
            case "storage-refused":
                File.SetUnixFileMode(path, File.GetUnixFileMode(path) | UnixFileMode.GroupRead | UnixFileMode.GroupExecute);
                break;
        }

        var result = Reapply(path, label: label);
        var (expectedOutcome, expectedCode, verdicts) = shape switch
        {
            "captured" => (DiagnosticOutcome.Captured, null, "c c c c c c c"),
            "format-3" => (DiagnosticOutcome.Unavailable, "incompatible", "i n n n n n n"),
            "unknown-strategy" => (DiagnosticOutcome.Unavailable, "incompatible", "c c c c i n n"),
            "unknown-label" => (DiagnosticOutcome.InvalidRequest, "unknown-label", "c c c c c c n"),
            "no-manifest" => (DiagnosticOutcome.Failed, "invalid-artifact", "n n n n n n n"),
            _ => (DiagnosticOutcome.Failed, "storage-refused", "n n n n n n n"),
        };
        Assert.AreEqual((expectedOutcome, expectedCode), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        AssertRecord(result.Compatibility, verdicts, shape);
        // The manifest's build id is read with the raw manifest: before the inspection, so even the format refusal compares it.
        Assert.AreEqual(shape is "no-manifest" or "storage-refused" ? null : true, result.Compatibility.SameBuild, shape);
        if (shape != "captured")
            return;

        // The written result carries the same record (read as plain JSON, not through the contract type).
        var written = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.RunPath!, "result.json"))).RootElement.GetProperty("compatibility");
        var checks = written.GetProperty("checks").EnumerateArray().ToList();
        CollectionAssert.AreEqual(CheckNames, checks.Select(c => c.GetProperty("check").GetString()).ToArray());
        Assert.IsTrue(checks.All(c => c.GetProperty("verdict").GetString() == "compatible"), "result.json: a check is not compatible");
        Assert.IsTrue(checks.All(c => c.TryGetProperty("producer", out _) && c.TryGetProperty("consumer", out _)), "result.json: a check lacks a side's value");
        Assert.AreEqual("2", checks[0].GetProperty("producer").GetString());
        Assert.AreEqual("fresh-model/1; target: text-state/1", checks[2].GetProperty("producer").GetString()[7..]);
        Assert.AreEqual("rebuilt", checks[6].GetProperty("consumer").GetString());
    }

    [TestMethod]
    [DataRow("formatVersion", "format-3", "3", "2", "formatVersion: the artifact declares 3; this build reads 2.", "i n n n n n n")]
    [DataRow("contractVersion", "contract-2", "2", "1", "contractVersion: the artifact declares 2; this build speaks 1.", "c i n n n n n")]
    [DataRow("checkpoint.profile", "start-profile", "start: fresh-model/9", "fresh-model/1, text-state/1", "checkpoint.profile: unknown profile 'fresh-model/9'; this build projects fresh-model/1, text-state/1.", "c c i n n n n")]
    [DataRow("checkpoint.coveredSurfaces", "surfaces-fewer", "geometry-and-text-buffers", "graphics-placements-and-resources", "missing: graphics-placements-and-resources.", "c c c i n n n")]
    [DataRow("checkpoint.coveredSurfaces", "surfaces-more", "selection-state", "command-marks", "unknown: selection-state.", "c c c i n n n")]
    [DataRow("checkpoint.coveredSurfaces", "surfaces-renamed", "selection-state", "command-marks", "missing: command-marks; unknown: selection-state.", "c c c i n n n")]
    [DataRow("configuration", "width-missing", "height", "width", "configuration.width: missing", "c c c c i n n")]
    [DataRow("configuration.capabilities", "mouse-missing", "supportsSixel", "supportsMouse", "capabilities.supportsMouse: missing", "c c c c c i n")]
    [DataRow("configuration.capabilities", "mouse-missing-holograms-unknown", "supportsHolograms", "supportsMouse", "capabilities.supportsMouse: missing", "c c c c c i n")]
    [DataRow("checkpoint.profile", "target-profile", "start: fresh-model/1; target: text-state/9", "fresh-model/1, text-state/1", "checkpoint.profile: unknown projection profile 'text-state/9'; this build projects fresh-model/1, text-state/1.", "c c i c c c n")]
    public async Task Compatibility_RefusesByArtifactDeclaration(string check, string edit, string producerHas, string consumerHas, string message, string verdicts)
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        switch (edit)
        {
            case "format-3": EditManifest(path, m => m["formatVersion"] = 3); break;
            case "contract-2": EditManifest(path, m => m["contractVersion"] = 2); break;
            case "start-profile": EditManifest(path, m => m["checkpoint"]!["profile"] = "fresh-model/9"); break;
            case "surfaces-fewer": EditManifest(path, m => m["checkpoint"]!["coveredSurfaces"]!.AsArray().RemoveAt(9)); break;
            case "surfaces-more": EditManifest(path, m => m["checkpoint"]!["coveredSurfaces"]!.AsArray().Add("selection-state")); break;
            case "surfaces-renamed": EditManifest(path, m => m["checkpoint"]!["coveredSurfaces"]!.AsArray()[8] = "selection-state"); break;
            case "width-missing": EditManifest(path, m => m["checkpoint"]!["configuration"]!.AsObject().Remove("width")); break;
            case "mouse-missing": EditManifest(path, m => m["checkpoint"]!["configuration"]!["capabilities"]!.AsObject().Remove("supportsMouse")); break;
            case "mouse-missing-holograms-unknown":
                EditManifest(path, m =>
                {
                    var capabilities = m["checkpoint"]!["configuration"]!["capabilities"]!.AsObject();
                    capabilities.Remove("supportsMouse");
                    capabilities["supportsHolograms"] = true;
                });
                break;
            case "target-profile": EditEventLine(path, e => e["kind"]?.GetValue<string>() == "checkpoint", e => e["checkpoint"]!["profile"] = "text-state/9"); break;
        }
        var before = HashCaseFiles(path);

        var result = Reapply(path, label: "m1");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), $"{edit}: {result.Problem?.Message}");
        StringAssert.Contains(result.Problem!.Message, message, edit);
        AssertRecord(result.Compatibility, verdicts, edit);
        var failed = result.Compatibility.Checks.Single(c => c.Verdict == "incompatible");
        Assert.AreEqual(check, failed.Check, edit);
        StringAssert.Contains(failed.Producer, producerHas, $"{edit}: producer {failed.Producer}");
        StringAssert.Contains(failed.Consumer, consumerHas, $"{edit}: consumer {failed.Consumer}");
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{edit}: an incompatible case was written to");
        CollectionAssert.AreEqual(before, HashCaseFiles(path), $"{edit}: the case's files changed");
    }

    [TestMethod]
    [DataRow("same")]
    [DataRow("edited")]
    [DataRow("empty")]
    [DataRow("removed")]
    [DataRow("version-differs")]
    public async Task Compatibility_IdentifiesBuilds(string shape)
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        var build = typeof(Hex1bTerminal).Assembly.ManifestModule.ModuleVersionId.ToString("N");
        var identity = JsonDocument.Parse(File.ReadAllText(Path.Combine(path, "manifest.json"))).RootElement.GetProperty("identity");
        Assert.AreEqual(build, identity.GetProperty("hex1bBuild").GetString(), "the manifest's build id is this assembly's module version id");
        Assert.AreEqual(TerminalDiagnostics.Hex1bVersion, identity.GetProperty("hex1bVersion").GetString());
        switch (shape)
        {
            case "edited": EditManifest(path, m => m["identity"]!["hex1bBuild"] = "0123456789abcdef0123456789abcdef"); break;
            case "empty": EditManifest(path, m => m["identity"]!["hex1bBuild"] = ""); break;
            case "removed": EditManifest(path, m => m["identity"]!.AsObject().Remove("hex1bBuild")); break;
            case "version-differs": EditManifest(path, m => m["identity"]!["hex1bVersion"] = "9.9.9+cafe"); break;
        }

        var result = Reapply(path, label: "m1");
        AssertMatched(result, shape);
        bool? expected = shape switch { "same" or "version-differs" => true, "edited" => false, _ => null };
        Assert.AreEqual(expected, result.Compatibility.SameBuild, shape);
        Assert.AreEqual((TerminalDiagnostics.Hex1bVersion, build), (result.Consumer!.Hex1bVersion, result.Consumer.Hex1bBuild), shape);
        Assert.AreEqual(result.Consumer.Hex1bVersion, result.ConsumerHex1bVersion, "the existing field stays");
        Assert.AreEqual(shape == "version-differs" ? "9.9.9+cafe" : TerminalDiagnostics.Hex1bVersion, result.Producer!.Hex1bVersion, shape);
        var written = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(result.RunPath!, "result.json"))).RootElement;
        Assert.AreEqual(build, written.GetProperty("consumer").GetProperty("hex1bBuild").GetString(), shape);
        var compatibility = written.GetProperty("compatibility");
        if (expected is { } sameBuild)
            Assert.AreEqual(sameBuild, compatibility.GetProperty("sameBuild").GetBoolean(), shape);
        else
            Assert.IsFalse(compatibility.TryGetProperty("sameBuild", out _), $"{shape}: sameBuild is written although no id was compared");
    }

    [TestMethod]
    public async Task Compatibility_SurfacesOrderIsIrrelevant()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        EditManifest(path, m =>
        {
            var surfaces = m["checkpoint"]!["coveredSurfaces"]!.AsArray();
            var reversed = surfaces.Select(s => s!.GetValue<string>()).Reverse().ToList();
            surfaces.Clear();
            foreach (var surface in reversed)
                surfaces.Add(surface);
        });
        AssertMatched(Reapply(path, label: "m1"), "reversed surfaces");
    }

    [TestMethod]
    [DataRow("formatVersion", "format-3", "2", "3", "formatVersion: the artifact declares 2; this build reads 3.", "i n n n n n n")]
    [DataRow("contractVersion", "contract-2", "1", "2", "contractVersion: the artifact declares 1; this build speaks 2.", "c i n n n n n")]
    [DataRow("checkpoint.profile", "fresh-only", "target: text-state/1", "fresh-model/1", "unknown projection profile 'text-state/1'; this build projects fresh-model/1.", "c c i c c c n")]
    [DataRow("checkpoint.coveredSurfaces", "one-more-surface", "command-marks", "selection-state", "missing: selection-state.", "c c c i n n n")]
    public async Task Compatibility_RefusesByConsumerDeclaration(string check, string declaration, string producerHas, string consumerHas, string message, string verdicts)
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        var build = CaseCompatibility.Consumer.Build;
        CaseCompatibility.Consumer.CurrentForTesting.Value = declaration switch
        {
            "format-3" => build with { FormatVersion = 3 },
            "contract-2" => build with { ContractVersion = 2 },
            "fresh-only" => build with { Profiles = [DiagnosticCaseCheckpointProfiles.FreshModel] },
            _ => build with { Surfaces = [.. build.Surfaces, "selection-state"] },
        };
        try
        {
            var result = Reapply(path, label: "m1");
            Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), $"{declaration}: {result.Problem?.Message}");
            StringAssert.Contains(result.Problem!.Message, message, declaration);
            AssertRecord(result.Compatibility, verdicts, declaration);
            var failed = result.Compatibility.Checks.Single(c => c.Verdict == "incompatible");
            Assert.AreEqual(check, failed.Check, declaration);
            StringAssert.Contains(failed.Producer, producerHas, $"{declaration}: producer {failed.Producer}");
            StringAssert.Contains(failed.Consumer, consumerHas, $"{declaration}: consumer {failed.Consumer}");
            Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")), $"{declaration}: an incompatible case was written to");
        }
        finally
        {
            CaseCompatibility.Consumer.CurrentForTesting.Value = null;
        }
        AssertMatched(Reapply(path, label: "m1"), "this build, the same case");
    }

    [TestMethod]
    public async Task Compatibility_FormatThreeIsIncompatibleNotUnsupported()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        // A newer format's manifest may hold anything after its version: nothing else is read before the refusal.
        EditManifest(path, m => { m["formatVersion"] = 3; m["checkpoint"] = null; m["streams"] = null; });
        var result = Reapply(path, label: "m1");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "incompatible"), (result.Outcome, result.Problem?.Code), result.Problem?.Message);
        StringAssert.StartsWith(result.Problem!.Message, "formatVersion: the artifact declares 3; this build reads 2.");
        Assert.IsFalse(Directory.Exists(Path.Combine(path, "reapplications")));
    }

    [TestMethod]
    public async Task Inspect_FormatThreeStaysUnsupported()
    {
        using var root = new CaseRoot();
        var path = await RecordCaseAsync(root, CompatibilityCorpus, new HeadlessPresentationAdapter(20, 4));
        EditManifest(path, m => m["formatVersion"] = 3);
        var inspection = DiagnosticCaseInspector.Inspect(new DiagnosticCaseInspectRequest { Path = path });
        Assert.AreEqual((DiagnosticOutcome.Failed, "unsupported-format"), (inspection.Outcome, inspection.Problem?.Code), inspection.Problem?.Message);
    }

    private static void EditManifest(string path, Action<JsonObject> edit)
    {
        var file = Path.Combine(path, "manifest.json");
        var manifest = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
        edit(manifest);
        RewriteOwnerOnly(file, manifest.ToJsonString());
    }

    // The record's checks in order, with verdicts given as "c" (compatible), "i" (incompatible) or "n" (not-checked).
    private static void AssertRecord(DiagnosticCaseCompatibility record, string verdicts, string what)
    {
        CollectionAssert.AreEqual(CheckNames, record.Checks.Select(c => c.Check).ToArray(), what);
        var expected = verdicts.Split(' ').Select(v => v switch { "c" => "compatible", "i" => "incompatible", _ => "not-checked" }).ToArray();
        CollectionAssert.AreEqual(expected, record.Checks.Select(c => c.Verdict).ToArray(), $"{what}: {string.Join(" ", record.Checks.Select(c => $"{c.Check}={c.Verdict}"))}");
        foreach (var check in record.Checks)
        {
            if (check.Verdict != "not-checked")
            {
                Assert.IsNotNull(check.Producer, $"{what}: {check.Check} ran without the artifact's value");
                Assert.IsNotNull(check.Consumer, $"{what}: {check.Check} ran without this build's value");
            }
            else if (check.Check != "origin")
            {
                Assert.IsNull(check.Producer, $"{what}: {check.Check} did not run but carries '{check.Producer}'");
                Assert.IsNotNull(check.Consumer, $"{what}: {check.Check} carries no consumer value");
            }
        }
    }
}
