using System.Text;
using Hex1b.Flow;
using Hex1b.Widgets;
using Hex1b.Surfaces;
using Hex1b.Tokens;
using Hex1b.Reflow;

namespace Hex1b.Tests.Flow;

/// <summary>
/// The enforcement point history commitment relies on: an in-process workload offers a
/// composed batch together with the geometry it was composed for, and the terminal refuses it
/// once its own model no longer has that geometry.
/// </summary>
/// <remarks>
/// <para>
/// This is the delivery-side half of the stale-frame discipline. The geometry read and the
/// write are separate operations, so the gate narrows rather than closes the window; what it
/// guarantees is the direction that destroys retained content — a batch composed for a
/// superseded geometry is never applied to the model a resize has already reflowed.
/// </para>
/// <para>
/// The temporal form of the race — composed while the wide geometry was in force, dequeued
/// after the shrink — belongs to the end-to-end commit scenario, where the resize item and the
/// composed frame share one FIFO. It cannot be expressed by holding the batch: the gate is
/// evaluated when the consumer takes the item, and the receipt for a held batch is exactly
/// what a producer waits on.
/// </para>
/// </remarks>
[DoNotParallelize]
[TestClass]
public class GeometryGatedDeliveryEnforcementTests
{
    private const string Marker = "GATE-ENFORCEMENT-MARKER";

    [TestMethod]
    public async Task BatchOfferedForAGeometryTheModelNoLongerHas_IsRefused()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(68, 21)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000)
            .Build();
        var ct = TestContext.Current.CancellationToken;

        // The model has already left the geometry; nothing about ordering is assumed.
        terminal.Resize(48, 11);

        var delivery = workload.WriteRequiredIfGeometry(ComposedBatch(), expectedWidth: 68, expectedHeight: 21);

        var outcome = await delivery.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.AreEqual(
            NativeDeliveryOutcome.GeometryChanged,
            outcome,
            $"model is {terminal.Width}x{terminal.Height}; an offer for a geometry the model lacks must be refused");
        Assert.IsFalse(
            terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal),
            "a refused batch must leave the model untouched");
    }

    [TestMethod]
    public async Task BatchComposedForTheCurrentGeometry_IsApplied()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(68, 21)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .WithScrollback(5000)
            .Build();
        var ct = TestContext.Current.CancellationToken;

        terminal.Resize(48, 11);

        var delivery = workload.WriteRequiredIfGeometry(ComposedBatch(), expectedWidth: 48, expectedHeight: 11);

        var outcome = await delivery.WaitAsync(TimeSpan.FromSeconds(5), ct);
        Assert.AreEqual(
            NativeDeliveryOutcome.Applied,
            outcome,
            "the gate must not refuse a batch whose geometry the terminal still has");
        Assert.IsTrue(
            terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal),
            "an applied batch must reach the model");
    }

    [TestMethod]
    public async Task WorkloadFilterResizeBeforeApply_IsRefusedUnderModelLock()
    {
        var resizeFilter = new ResizeDuringOutputFilter(48, 11);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(68, 21)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .AddWorkloadFilter(resizeFilter)
            .Build();
        resizeFilter.Terminal = terminal;

        var delivery = workload.WriteRequiredIfGeometry(
            ComposedBatch(),
            expectedWidth: 68,
            expectedHeight: 21);

        var outcome = await delivery.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(NativeDeliveryOutcome.GeometryChanged, outcome);
        Assert.IsTrue(resizeFilter.Resized, "the filter must perform the direct resize before application");
        Assert.AreEqual(48, terminal.Width);
        Assert.AreEqual(11, terminal.Height);
        Assert.IsFalse(
            terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal),
            "a resize during pre-apply filter work must refuse the stale batch");
    }

    [TestMethod]
    public async Task DisposedDeliverySkippedBeforePresentationForwarding_DoesNotReportApplied()
    {
        var presentation = new DelayedDisposePresentation(68, 21);
        var observer = new BlockingObserver();
        using var workload = new Hex1bAppWorkloadAdapter();
        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .AddPresentationFilter(observer)
            .WithDimensions(68, 21)
            .Build();

        var delivery = workload.WriteRequiredIfGeometry(
            ComposedBatch(),
            expectedWidth: 68,
            expectedHeight: 21);
        await observer.Entered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        var disposal = terminal.DisposeAsync().AsTask();
        await presentation.DisposeEntered.Task.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        observer.Release.TrySetResult();

        var failure = await Assert.ThrowsExactlyAsync<ObjectDisposedException>(
            async () => await delivery);
        Assert.IsTrue(
            terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal),
            "the model had applied the batch before disposal skipped its presentation forward");
        Assert.IsNotNull(failure);

        presentation.ReleaseDispose.TrySetResult();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [TestMethod]
    public async Task NativePresentationWithTransformingFilter_UsesOrdinaryFlowForwarding()
    {
        const string transformedMarker = "ORDINARY-FILTER-MARKER";
        var presentation = new RecordingNativePresentation(68, 21);
        FlowCommitResult? result = null;
        Exception? failure = null;

        await using var terminal = Hex1bTerminal.CreateBuilder()
            .WithHex1bFlow(async flow =>
            {
                var step = flow.Step(
                    context => Task.FromResult<Hex1bWidget>(context.Text("LIVE")));
                await step.WaitForReadyAsync();
                try
                {
                    result = await step.CommitAsync(
                        new OneUnitSource(),
                        context => Task.FromResult<Hex1bWidget>(context.Text("LIVE-AFTER")));
                }
                catch (Exception error)
                {
                    failure = error;
                }
                finally
                {
                    step.Complete();
                }
            })
            .WithPresentation(presentation)
            .AddPresentationFilter(new TransformingPresentationFilter(transformedMarker))
            .WithDimensions(68, 21)
            .Build();

        await terminal.RunAsync().WaitAsync(TimeSpan.FromSeconds(30));

        Assert.IsNull(failure, $"ordinary Flow forwarding must not fault: {failure}");
        Assert.IsNotNull(result);
        Assert.AreEqual(1, result!.CompletedUnits);
        Assert.IsFalse(
            presentation.GeometryGateCalled,
            "a native presentation with a transforming filter must not advertise gated delivery");
        Assert.Contains(transformedMarker, presentation.OutputText);
    }

    [TestMethod]
    public async Task AcceptedModelDelivery_WithWorkloadFilter_ForwardsThroughRawManagedPresentation()
    {
        var presentation = new RecordingManagedPresentation(68, 21);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .AddWorkloadFilter(new PassiveWorkloadFilter())
            .WithDimensions(68, 21)
            .Build();

        var delivery = workload.WriteRequiredIfGeometry(
            ComposedBatch(),
            expectedWidth: 68,
            expectedHeight: 21);
        var outcome = await delivery.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);

        Assert.AreEqual(NativeDeliveryOutcome.Applied, outcome);
        Assert.IsTrue(terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal));
        Assert.Contains(
            Marker,
            presentation.OutputText,
            "an accepted model-gated batch must still reach a raw managed presentation");
    }

    [TestMethod]
    public async Task ModelGatedObserverMutation_IsFaultedBeforePresentationForwarding()
    {
        var presentation = new RecordingManagedPresentation(68, 21);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .AddPresentationFilter(new MutatingObserver())
            .WithDimensions(68, 21)
            .Build();

        var delivery = workload.WriteRequiredIfGeometry(
            ComposedBatch(),
            expectedWidth: 68,
            expectedHeight: 21);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await delivery);

        Assert.Contains("observer", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsTrue(terminal.GetScreenText().Contains(Marker, StringComparison.Ordinal));
        Assert.AreEqual(
            string.Empty,
            presentation.OutputText,
            "a model-gated observer that changes output must fault before presentation forwarding");
    }

    [TestMethod]
    public async Task ReentrantResizeDuringModelApplication_IsFaultedWithoutForwardingSuffix()
    {
        const string suffix = "REENTRANT-RESIZE-SUFFIX";
        var presentation = new RecordingManagedPresentation(68, 21);
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithDimensions(68, 21)
            .Build();

        terminal.WindowTitleChanged += _ =>
        {
            terminal.Resize(48, 11);
            terminal.Resize(68, 21);
        };

        var delivery = workload.WriteRequiredIfGeometry(
            $"\x1b]2;reentrant-title\x07{suffix}",
            expectedWidth: 68,
            expectedHeight: 21);

        var failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            async () => await delivery);

        Assert.Contains("geometry", failure.Message, StringComparison.OrdinalIgnoreCase);
        Assert.IsFalse(
            terminal.GetScreenText().Contains(suffix, StringComparison.Ordinal),
            "a re-entrant resize must stop the gated batch before its suffix is applied");
        Assert.IsFalse(
            presentation.OutputText.Contains(suffix, StringComparison.Ordinal),
            "a partially applied batch must never be forwarded as Applied");
    }

    private sealed class PassiveWorkloadFilter : IHex1bTerminalWorkloadFilter
    {
        public ValueTask OnSessionStartAsync(
            int width,
            int height,
            DateTimeOffset timestamp,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(
            int width,
            int height,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnFrameCompleteAsync(
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnOutputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class RecordingManagedPresentation(int width, int height) :
        IHex1bTerminalPresentationAdapter
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();

        internal string OutputText
        {
            get
            {
                lock (_sync)
                    return _output.ToString();
            }
        }

        public int Width => width;
        public int Height => height;
        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;
        public event Action<int, int>? Resized
        {
            add { }
            remove { }
        }
        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public ValueTask WriteOutputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken ct = default)
        {
            lock (_sync)
                _output.Append(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(
            CancellationToken ct = default)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask FlushAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EnterRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ExitRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public (int Row, int Column) GetCursorPosition() => (0, 0);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    }


    private sealed class MutatingObserver : IHex1bTerminalOutputObserver
    {
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
        {
            var output = appliedTokens.Select(item => item.Token).ToList();
            output.Add(new TextToken("OBSERVER-MUTATION"));
            return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(output);
        }

        public ValueTask OnSessionStartAsync(
            int width,
            int height,
            DateTimeOffset timestamp,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(
            int width,
            int height,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class ResizeDuringOutputFilter(int width, int height) :
        IHex1bTerminalWorkloadFilter
    {
        private int _resized;

        internal Hex1bTerminal Terminal { get; set; } = null!;
        internal bool Resized => Volatile.Read(ref _resized) != 0;

        public ValueTask OnSessionStartAsync(
            int width,
            int height,
            DateTimeOffset timestamp,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(
            int width,
            int height,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnOutputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
        {
            if (Interlocked.Exchange(ref _resized, 1) == 0)
                Terminal.Resize(width, height);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class BlockingObserver : IHex1bTerminalOutputObserver
    {
        internal TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct).ConfigureAwait(false);
            return appliedTokens.Select(item => item.Token).ToArray();
        }

        public ValueTask OnSessionStartAsync(
            int width,
            int height,
            DateTimeOffset timestamp,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(
            int width,
            int height,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class DelayedDisposePresentation(int width, int height) :
        IHex1bTerminalPresentationAdapter
    {
        internal TaskCompletionSource DisposeEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource ReleaseDispose { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int Width => width;
        public int Height => height;
        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;
        public event Action<int, int>? Resized
        {
            add { }
            remove { }
        }
        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        public ValueTask WriteOutputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask FlushAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EnterRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ExitRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public (int Row, int Column) GetCursorPosition() => (0, 0);

        public async ValueTask DisposeAsync()
        {
            DisposeEntered.TrySetResult();
            await ReleaseDispose.Task.ConfigureAwait(false);
        }
    }

    private sealed class RecordingNativePresentation(int width, int height) :
        IHex1bTerminalPresentationAdapter,
        IGeometryGatedPresentationAdapter,
        ICursorPositionSource,
        IFlowTerminalHostProfileSource,
        IFlowCurrentGeometrySource
    {
        private readonly object _sync = new();
        private readonly StringBuilder _output = new();

        internal bool GeometryGateCalled { get; private set; }
        internal string OutputText
        {
            get
            {
                lock (_sync)
                    return _output.ToString();
            }
        }

        public int Width => width;
        public int Height => height;
        public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;
        public event Action<int, int>? Resized
        {
            add { }
            remove { }
        }
        public event Action? Disconnected
        {
            add { }
            remove { }
        }

        FlowTerminalHostProfile IFlowTerminalHostProfileSource.FlowHostProfile =>
            FlowTerminalHostProfile.Ghostty_1_3_1;

        (int Width, int Height) IFlowCurrentGeometrySource.ReadCurrentGeometry() =>
            (width, height);

        public ValueTask WriteOutputAsync(
            ReadOnlyMemory<byte> data,
            CancellationToken ct = default)
        {
            lock (_sync)
                _output.Append(Encoding.UTF8.GetString(data.Span));
            return ValueTask.CompletedTask;
        }

        public ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
            => ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);

        public ValueTask FlushAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask EnterRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask ExitRawModeAsync(CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public (int Row, int Column) GetCursorPosition() => (0, 0);

        public Task<(int Column, int Row)?> ObserveCursorPositionAsync(
            CancellationToken cancellationToken)
            => Task.FromResult<(int Column, int Row)?>((0, 0));

        public ValueTask<NativeDeliveryOutcome> WriteOutputIfGeometryAsync(
            ReadOnlyMemory<byte> data,
            int expectedWidth,
            int expectedHeight,
            CancellationToken ct = default)
        {
            GeometryGateCalled = true;
            throw new InvalidOperationException(
                "the native gate must not be used when a filter transforms output");
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class TransformingPresentationFilter(string marker) :
        IHex1bTerminalPresentationFilter
    {
        public ValueTask<IReadOnlyList<AnsiToken>> OnOutputAsync(
            IReadOnlyList<AppliedToken> appliedTokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
        {
            var output = appliedTokens.Select(item => item.Token).ToList();
            output.Add(new TextToken(marker));
            return ValueTask.FromResult<IReadOnlyList<AnsiToken>>(output);
        }

        public ValueTask OnSessionStartAsync(
            int width,
            int height,
            DateTimeOffset timestamp,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnInputAsync(
            IReadOnlyList<AnsiToken> tokens,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnResizeAsync(
            int width,
            int height,
            TimeSpan elapsed,
            CancellationToken ct = default)
            => ValueTask.CompletedTask;

        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
    }

    private sealed class OneUnitSource : FlowCommitSource
    {
        public override int UnitCount => 1;

        public override Task<FlowCommitUnit> UnitAsync(
            int index,
            int width,
            CancellationToken cancellationToken)
        {
            var surface = new Surface(width, 1);
            surface.WriteText(0, 0, "FLOW-NATIVE-ORDINARY");
            return Task.FromResult(new FlowCommitUnit("ordinary", surface));
        }
    }

    private static string ComposedBatch() => $"\x1b[1;1H{Marker}\x1b[K";
}
