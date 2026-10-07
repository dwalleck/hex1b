using Hex1b.Diagnostics;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    // Issue 60, decision D2: line renditions are an unsupported surface on both ends of a re-application. A case whose
    // recorded end state holds a double-width row compares unavailable (the reapplied end state names it too), never
    // matched by a projection that cannot see renditions; a start holding one is unsupported. A control case without
    // renditions still matches.
    [TestMethod]
    public async Task Reapply_LineRenditionsAtTheTargetAreUnavailable()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "before ");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "\r\n\u001b#6wide\r\nafter");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var result = Reapply(path, label: "stop");
        Assert.AreEqual(DiagnosticOutcome.Captured, result.Outcome, $"{result.Problem?.Code} {result.Problem?.Message}");
        Assert.AreEqual("unavailable", result.Comparison, result.ComparisonReason);
        StringAssert.Contains(result.ComparisonReason, "line-renditions");
    }

    [TestMethod]
    public async Task Start_HoldingLineRenditionsIsUnsupported()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "\u001b#6wide\r\n");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "after");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);

        var checkpoint = Artifact.Read(path).Manifest.GetProperty("checkpoint");
        Assert.AreEqual("unsupported", checkpoint.GetProperty("status").GetString(), checkpoint.ToString());
        StringAssert.Contains(checkpoint.GetProperty("reason").GetString(), "line-renditions");
    }

    [TestMethod]
    public async Task Reapply_WithoutLineRenditionsStillMatches()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = HistoryTerminal(workload, strategy: null, capacity: 100);
        await workload.WriteAndWaitAsync(terminal, "before ");
        var path = StartLive(terminal, root);
        await workload.WriteAndWaitAsync(terminal, "\r\n\u001b#6wide\u001b#5\r\nafter");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        AssertMatched(Reapply(path, label: "stop"), "renditions reset before the stop");
    }
}
