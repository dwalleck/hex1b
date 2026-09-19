using Hex1b.Tokens;

namespace Hex1b.Tests;

/// <summary>
/// The geometry-gated delivery's contract with presentation filters.
/// </summary>
/// <remarks>
/// <para>
/// A gated batch reaches the device before the presentation filters see it, so a filter
/// that rewrote the output would leave the screen describing something the model never
/// produced. The terminal therefore states the rule twice: a filter that has not claimed
/// to be an observer is rejected before anything is written, and an observer that changes
/// what it was shown fails the delivery after a write it can no longer take back.
/// </para>
/// <para>
/// The two failures reach the producer differently, and deliberately. The pre-write
/// rejection is a fault on the delivery, so nothing is retried. The post-write divergence
/// is reported to the producer as <see cref="NativeDeliveryOutcome.Applied"/> — the bytes
/// really are on the device, and replaying them would duplicate output that may already be
/// visible — and it is the terminal run that fails.
/// </para>
/// </remarks>
[TestClass]
public class GeometryGatedObserverContractTests
{
    private const int Columns = 80;
    private const int Rows = 24;
    private const string Payload = "PAYLOAD\r\n";

    private static readonly TimeSpan FastProbeTimeout = TimeSpan.FromMilliseconds(25);
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(10);

    [TestMethod]
    public async Task GatedDelivery_RejectsAFilterThatIsNotAnObserverBeforeWriting()
    {
        await using var terminal = GatedTerminal.Start(new NonObserverFilter());

        var refusal = await Assert.ThrowsExactlyAsync<NotSupportedException>(
            async () => await terminal.Workload.WriteRequiredIfGeometry(Payload, Columns, Rows));

        StringAssert.Contains(refusal.Message, nameof(NonObserverFilter),
            "The refusal must name the filter that cannot be honoured.");
        Assert.IsFalse(terminal.Driver.WrittenText.Contains("PAYLOAD", StringComparison.Ordinal),
            "The rejection must land before any byte reaches the device, because a write " +
            "cannot be taken back.");
    }

    [TestMethod]
    public async Task GatedDelivery_FailsTheRunWhenAnObserverChangesTheOutputItWasShown()
    {
        await using var terminal = GatedTerminal.Start(new TransformingObserver());

        var outcome = await terminal.Workload.WriteRequiredIfGeometry(Payload, Columns, Rows);

        Assert.AreEqual(NativeDeliveryOutcome.Applied, outcome,
            "The batch was written before the observers ran, so it reports applied — the " +
            "producer must never replay bytes the device may already be showing.");
        Assert.Contains("PAYLOAD", terminal.Driver.WrittenText);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await terminal.Run.WaitAsync(WaitTimeout));

        StringAssert.Contains(failure.Message, "workload output pump");
        Assert.IsInstanceOfType<InvalidOperationException>(failure.InnerException);
        StringAssert.Contains(failure.InnerException!.Message, "changed the output it was shown",
            "The divergence must be raised loudly rather than left to surface as corrupted output.");
    }

    [TestMethod]
    public async Task GatedDelivery_AppliesAndShowsTheOutputToAPassThroughObserver()
    {
        var observer = new PassThroughObserver();
        await using var terminal = GatedTerminal.Start(observer);

        var outcome = await terminal.Workload.WriteRequiredIfGeometry(Payload, Columns, Rows);

        Assert.AreEqual(NativeDeliveryOutcome.Applied, outcome);
        Assert.Contains("PAYLOAD", terminal.Driver.WrittenText);
        Assert.HasCount(1, observer.Outputs, "The observer must see the batch exactly once.");
        StringAssert.Contains(AnsiTokenSerializer.Serialize(observer.Outputs[0]), "PAYLOAD",
            "The observer must be shown what was written, not a substitute.");
        Assert.IsFalse(terminal.Run.IsCompleted,
            "An observer that passes output through must not fail the terminal run. Without " +
            "this, the faulting test above could pass for an unrelated reason.");
    }

    /// <summary>
    /// A terminal wired so a gated batch travels the real path: application workload
    /// adapter, geometry-gated presentation, and one installed presentation filter.
    /// </summary>
    private sealed class GatedTerminal : IAsyncDisposable
    {
        private readonly CancellationTokenSource _lifetime = new();
        private readonly Hex1bTerminal _terminal;
        private readonly ConsolePresentationAdapter _presentation;

        private GatedTerminal(
            FakeConsoleDriver driver,
            ConsolePresentationAdapter presentation,
            Hex1bAppWorkloadAdapter workload,
            Hex1bTerminal terminal,
            Task<int> run,
            CancellationTokenSource lifetime)
        {
            Driver = driver;
            _presentation = presentation;
            Workload = workload;
            _terminal = terminal;
            Run = run;
            _lifetime = lifetime;
        }

        internal FakeConsoleDriver Driver { get; }

        internal Hex1bAppWorkloadAdapter Workload { get; }

        internal Task<int> Run { get; }

        internal static GatedTerminal Start(IHex1bTerminalPresentationFilter filter)
        {
            var driver = new FakeConsoleDriver { TerminalSize = (Columns, Rows) };
            var presentation = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
            var workload = new Hex1bAppWorkloadAdapter();

            var terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(workload)
                .WithPresentation(presentation)
                .AddPresentationFilter(filter)
                .WithDimensions(Columns, Rows)
                .Build();

            var lifetime = new CancellationTokenSource();
            var run = terminal.RunAsync(lifetime.Token);
            return new GatedTerminal(driver, presentation, workload, terminal, run, lifetime);
        }

        public async ValueTask DisposeAsync()
        {
            await _lifetime.CancelAsync();
            try
            {
                await Run.WaitAsync(WaitTimeout);
            }
            catch (OperationCanceledException)
            {
                // Normal shutdown.
            }
            catch (InvalidOperationException)
            {
                // A pump failure the test has already asserted on.
            }

            await _terminal.DisposeAsync();
            await _presentation.DisposeAsync();
            _lifetime.Dispose();
        }
    }

    /// <summary>A presentation filter that never claimed to be an observer.</summary>
    private sealed class NonObserverFilter : IHex1bTerminalPresentationFilter
    {
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.FromResult<IReadOnlyList<AnsiToken>>(
                appliedTokens.Select(token => token.Token).ToArray());

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>An observer that rewrites the stream it was shown.</summary>
    private sealed class TransformingObserver : IHex1bTerminalOutputObserver
    {
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            var observed = appliedTokens.Select(token => token.Token).ToList();

            // Duplicating a token is enough to make the returned stream differ from the
            // one that reached the device; the verifier compares what came back.
            if (observed.Count > 0)
            {
                observed.Add(observed[0]);
            }

            return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(observed);
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    /// <summary>An observer that records the output and passes it through untouched.</summary>
    private sealed class PassThroughObserver : IHex1bTerminalOutputObserver
    {
        internal List<IReadOnlyList<AnsiToken>> Outputs { get; } = [];

        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            var tokens = appliedTokens.Select(token => token.Token).ToArray();
            Outputs.Add(tokens);
            return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(tokens);
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }
}
