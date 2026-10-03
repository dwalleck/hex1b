using Hex1b.Diagnostics;
using Hex1b.Diagnostics.Cases;
using Hex1b.Tests.Sixel;
using Hex1b.Tokens;

namespace Hex1b.Tests.Diagnostics;

public partial class DiagnosticCaseTests
{
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task Reapply_CompletedIgnoredDcsRetentionOverflowStaysTextOnly(bool pretokenized, bool impactAware)
    {
        using var root = new CaseRoot();
        var workload = new Hex1bAppWorkloadAdapter();
        var impacts = new DcsImpactPresentationAdapter();
        IHex1bTerminalPresentationAdapter presentation = impactAware ? impacts : new HeadlessPresentationAdapter(40, 10);
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = presentation,
            Width = 40,
            Height = 10,
            Graphics = new Hex1bTerminalGraphicsOptions { MaximumRetainedInputBytesPerImage = 8 },
        });
        using var running = new Running(terminal);
        var path = StartLive(terminal, root);
        var bytes = "before \u001bPz12345678\u001b\\ after"u8.ToArray();
        var before = terminal.CurrentModelSequence;
        var expectedBytesRead = terminal.OutputBytesRead + bytes.Length;
        if (pretokenized)
            await workload.WriteTokensWithBytesAsync([new TextToken("before "), new DcsToken("z12345678"), new TextToken(" after")],
                bytes, cancellationToken: TestContext.Current.CancellationToken);
        else
            workload.Write(bytes.AsMemory());
        await WaitAsync(() => terminal.OutputBytesRead == expectedBytesRead && terminal.CurrentModelSequence == before + 1
            && (!impactAware || impacts.Batches == 1));
        Assert.IsFalse(terminal.ContainsSixelData(), "ignored non-Sixel retention overflow created resident graphics");
        Assert.AreEqual(0, terminal.TrackedSixelCount);
        var state = terminal.CaptureModelState();
        Assert.IsNull(state.PendingInput.Dcs, "completed ignored DCS left a continuation");
        Assert.IsEmpty(state.Unsupported, "completed ignored DCS poisoned text-state support");
        StringAssert.Contains(terminal.GetScreenText(), "before  after", "ignored payload altered surrounding text");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(artifact.ModelEvents().Single().GetProperty("data").GetString()!),
            "recorder must preserve original ingress even when impact presentation emits a shorter retained payload");
        Assert.IsFalse(artifact.Events.Any(e => e.GetProperty("kind").GetString() == "interval-end"),
            "ignored non-Sixel overflow ended the supported interval");
        AssertMatched(Reapply(path, label: "stop"), "ignored overflow in raw/pretokenized and impact application");
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reapply_TransientSixelRawAndImpactApplicationsEndTheInterval(bool impactAware)
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        var impacts = new DcsImpactPresentationAdapter();
        IHex1bTerminalPresentationAdapter presentation = impactAware ? impacts : new HeadlessPresentationAdapter(40, 10);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 10).Build();
        using var running = new Running(terminal);
        await workload.WriteAndWaitAsync(terminal, "before \u001bP0;");
        var path = StartLive(terminal, root);
        var start = terminal.CurrentModelSequence;
        await workload.WriteAndWaitAsync(terminal, "0q\u0018visible");
        await WaitAsync(() => terminal.CurrentModelSequence > start && (!impactAware || impacts.Batches == 2));
        Assert.IsNull(terminal.CaptureModelState().PendingInput.Dcs);
        Assert.IsFalse(terminal.ContainsSixelData(), "transient Sixel fixture left graphics");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        var end = artifact.Events.Single(e => e.GetProperty("kind").GetString() == "interval-end");
        var application = artifact.ModelEvents().Single(e => e.GetProperty("kind").GetString() == "application");
        Assert.AreEqual((start + 1, "sixel-continuation"), (end.GetProperty("modelSequence").GetInt64(), end.GetProperty("record").GetProperty("reason").GetString()));
        CollectionAssert.AreEqual("0q\u0018visible"u8.ToArray(), Convert.FromBase64String(application.GetProperty("data").GetString()!));
        Assert.IsLessThan(artifact.Events.IndexOf(application), artifact.Events.IndexOf(end), "forbidden application preceded durable interval end");
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", "sixel-continuation"), (refused.Outcome, refused.Problem?.Code, refused.IntervalEndReason), refused.Problem?.Message);
    }

    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task Reapply_AcceptedGeometryGatedSixelCannotBypassTransientRefusal(bool nativeGate)
    {
        using var root = new CaseRoot();
        var workload = new Hex1bAppWorkloadAdapter();
        var driver = new FakeConsoleDriver { TerminalSize = (40, 10) };
        IHex1bTerminalPresentationAdapter presentation = nativeGate
            ? new ConsolePresentationAdapter(driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(25))
            : new HeadlessPresentationAdapter(40, 10);
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithPresentation(presentation).WithDimensions(40, 10).Build();
        using var running = new Running(terminal);
        Assert.AreEqual(NativeDeliveryOutcome.Applied, await workload.WriteRequiredIfGeometry("before \u001bP0;", 40, 10)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "prefix delivery");
        var path = StartLive(terminal, root);
        var before = terminal.CurrentModelSequence;
        Assert.AreEqual(NativeDeliveryOutcome.Applied, await workload.WriteRequiredIfGeometry("0q\u0018visible", 40, 10)
            .WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken), "classification delivery");
        await WaitAsync(() => terminal.CurrentModelSequence == before + 1);
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var interval = Inspect(path).Intervals.Single();
        Assert.AreEqual((before, before, "sixel-continuation"), (interval.FromModelSequence, interval.ToModelSequence, interval.EndReason));
        var model = Artifact.Read(path).ModelEvents().Single(e => e.GetProperty("kind").GetString() == "application");
        CollectionAssert.AreEqual("0q\u0018visible"u8.ToArray(), Convert.FromBase64String(model.GetProperty("data").GetString()!));
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", "sixel-continuation"), (refused.Outcome, refused.Problem?.Code, refused.IntervalEndReason), refused.Problem?.Message);
    }

    [TestMethod]
    public async Task Reapply_GeometryRefusedSixelDoesNotManufactureAppliedClassification()
    {
        using var root = new CaseRoot();
        var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] }).Build();
        using var running = new Running(terminal);
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;
        Assert.AreEqual(NativeDeliveryOutcome.Applied, await workload.WriteRequiredIfGeometry("before ", 40, 10));
        var before = terminal.CurrentModelSequence;
        Assert.AreEqual(NativeDeliveryOutcome.GeometryChanged, await workload.WriteRequiredIfGeometry("\u001bPq\u0018", 41, 10));
        Assert.AreEqual(before, terminal.CurrentModelSequence, "refused classification advanced model");
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var artifact = Artifact.Read(path);
        CollectionAssert.AreEqual(new[] { "unapplied-output" }, artifact.Events.Where(e => e.GetProperty("kind").GetString() == "interval-end")
            .Select(e => e.GetProperty("record").GetProperty("reason").GetString()).ToArray());
        Assert.AreEqual("unapplied-output", Inspect(path).Intervals.Single().EndReason);
    }

    [TestMethod]
    public async Task Reapply_PretokenizedKnownSixelEndsIntervalEvenWithoutResidentGraphics()
    {
        using var root = new CaseRoot();
        var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = new Hex1bTerminal(new Hex1bTerminalOptions
        {
            WorkloadAdapter = workload,
            PresentationAdapter = new HeadlessPresentationAdapter(40, 10),
            Width = 40,
            Height = 10,
            SixelPolicy = Hex1b.Sixel.SixelCompatibilityPolicy.Default with { RejectZeroExtentGraphics = true },
        });
        using var running = new Running(terminal);
        var path = StartLive(terminal, root);
        var before = terminal.CurrentModelSequence;
        var bytes = "\u001bPq\u001b\\"u8.ToArray();
        var expectedBytesRead = terminal.OutputBytesRead + bytes.Length;
        await workload.WriteTokensWithBytesAsync([new DcsToken("q")], bytes, cancellationToken: TestContext.Current.CancellationToken);
        await WaitAsync(() => terminal.OutputBytesRead == expectedBytesRead && terminal.CurrentModelSequence == before + 1);
        Assert.IsFalse(terminal.ContainsSixelData(), "zero-extent refusal policy left resident graphics");
        Assert.IsEmpty(terminal.CaptureModelState().Unsupported, "zero-extent refusal policy left residual graphics effects");
        await new TerminalDiagnostics(terminal).StopCaseAsync(TestContext.Current.CancellationToken);
        var interval = Inspect(path).Intervals.Single();
        CollectionAssert.AreEqual(bytes, Convert.FromBase64String(Artifact.Read(path).ModelEvents().Single().GetProperty("data").GetString()!),
            "pretokenized Sixel classification must retain its original complete ingress");
        Assert.AreEqual((before, "sixel-continuation"), (interval.ToModelSequence, interval.EndReason));
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", "sixel-continuation"), (refused.Outcome, refused.Problem?.Code, refused.IntervalEndReason), refused.Problem?.Message);
    }

    [TestMethod]
    public async Task Reapply_TokenOnlySixelRetainsNoIngressRefusalPriority()
    {
        using var root = new CaseRoot();
        var workload = new ScriptedWorkload();
        await using var terminal = Hex1bTerminal.CreateBuilder().WithWorkload(workload).WithHeadless().WithDimensions(40, 10)
            .WithDiagnosticCase(new DiagnosticCaseStartRequest { Directory = root.Path, Authorizations = [DiagnosticAuthorization.ReapplicationData] }).Build();
        using var running = new Running(terminal);
        var diagnostics = new TerminalDiagnostics(terminal);
        var path = diagnostics.GetCaseStatus().Path!;
        await workload.WriteAndWaitAsync(terminal, "before ");
        var before = terminal.CurrentModelSequence;
        terminal.ApplyTokens([new DcsToken("q")]);
        await diagnostics.StopCaseAsync(TestContext.Current.CancellationToken);
        var interval = Inspect(path).Intervals.Single();
        Assert.AreEqual((before, "application-without-ingress"), (interval.ToModelSequence, interval.EndReason));
        var refused = Reapply(path, label: "stop");
        Assert.AreEqual((DiagnosticOutcome.Unavailable, "beyond-interval", "application-without-ingress"), (refused.Outcome, refused.Problem?.Code, refused.IntervalEndReason), refused.Problem?.Message);
    }

    private sealed class DcsImpactPresentationAdapter : ICellImpactAwarePresentationAdapter
    {
        private readonly HeadlessPresentationAdapter _inner = new(40, 10);
        private int _batches;
        public int Batches => Volatile.Read(ref _batches);
        public int Width => _inner.Width;
        public int Height => _inner.Height;
        public TerminalCapabilities Capabilities => _inner.Capabilities;
        public event Action<int, int>? Resized { add => _inner.Resized += value; remove => _inner.Resized -= value; }
        public event Action? Disconnected { add => _inner.Disconnected += value; remove => _inner.Disconnected -= value; }
        public ValueTask WriteOutputWithImpactsAsync(IReadOnlyList<AppliedToken> tokens, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _batches);
            return ValueTask.CompletedTask;
        }
        public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default) => _inner.WriteOutputAsync(data, ct);
        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default) => _inner.ReadInputAsync(ct);
        public ValueTask FlushAsync(CancellationToken ct = default) => _inner.FlushAsync(ct);
        public ValueTask EnterRawModeAsync(CancellationToken ct = default) => _inner.EnterRawModeAsync(ct);
        public ValueTask ExitRawModeAsync(CancellationToken ct = default) => _inner.ExitRawModeAsync(ct);
        public (int Row, int Column) GetCursorPosition() => _inner.GetCursorPosition();
        public ValueTask DisposeAsync() => _inner.DisposeAsync();
    }
}
