using System.Text;
using Hex1b.Flow;
using Hex1b.Surfaces;
using Hex1b.Tokens;
using Hex1b.Widgets;

namespace Hex1b.Tests.Flow;

[TestClass]
public sealed class FlowCommitReservationTests
{
    [TestMethod]
    public async Task CommitAsync_ShrinkAfterReservationGeometrySample_DoesNotScrollLiveRowsIntoHistory()
    {
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation)
        {
            ShrinkAfterPayloadGeometrySample = true,
        };
        await using var parent = new RecordingParentAdapter(40, 34);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.IsTrue(live.ShrinkObserved, "The armed geometry race must actually execute.");
        Assert.AreEqual(11, live.TerminalHeight);
        Assert.AreEqual(1, result.CompletedUnits);
        Assert.AreEqual(FlowCommitStatus.Cancelled, result.Status);
        Assert.IsTrue(result.CancellationRequested);
        AssertCleanHistory(live);
        Assert.Contains("PAYLOAD-0", live.ReadHistory(),
            "The accepted payload preceding the full-height live region must survive reservation.");
        Assert.IsTrue(live.ScrollRequests.All(rows => rows <= 3),
            "Only the three rows preceding the live boundary can need reservation; " +
            "a 24-row reservation in an 11-row terminal must not scroll 16 rows.");
    }

    [TestMethod]
    public async Task CommitAsync_CancelAfterRendererClampsOrigin_RecoveryDoesNotCommitLiveRows()
    {
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation)
        {
            ShrinkOnReanchor = true,
        };
        await using var parent = new RecordingParentAdapter(40, 34);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.IsTrue(live.ShrinkObserved, "The renderer must encounter the smaller viewport.");
        Assert.AreEqual(1, result.CompletedUnits);
        Assert.AreEqual(FlowCommitStatus.Cancelled, result.Status);
        Assert.IsTrue(result.CancellationRequested);
        AssertCleanHistory(live);
        Assert.AreEqual(0, live.ScrollRequests.Count,
            "Recovery already has a full-height live image at row zero. Scrolling the " +
            "pre-clamp requested origin would commit mutable UI into history.");
    }

    [TestMethod]
    public async Task CommitAsync_NativeShrinkBeforeNativeWrite_RefusesStaleBatchAndRetries()
    {
        // The device leaves the geometry the unit was composed for after the last
        // sample and before the write — the window the captured native runs showed.
        // The composed reservation targets rows below an 11-row viewport, where each
        // clamped clear's linefeed scrolls a live row into history.
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation)
        {
            SupportsGeometryGatedDelivery = true,
            SupportsCursorObservation = true,
            AcceptLiveLayout = true,
            CancelOnFirstReanchor = false,
            ResizeBeforeNativeWrite = (1, 11),
        };
        await using var parent = new RecordingParentAdapter(40, 34);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.IsNotEmpty(live.Refusals,
            "A batch composed for the taller viewport must be refused rather than written.");
        Assert.AreEqual(34, live.Refusals[0].ExpectedHeight,
            "The refused batch must be the one composed for the viewport the device has left.");
        Assert.AreEqual(11, live.Refusals[0].DeviceHeight);
        Assert.AreEqual(11, live.TerminalHeight, "The retry must observe the narrower viewport.");
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(2, result.CompletedUnits);
        AssertCleanHistory(live);

        // Refusing the stale batch is only useful if the unit is then composed again
        // at the geometry that is actually there, so both payloads must exist
        // somewhere the host retained: history or the visible screen.
        var durable = live.ReadHistory() + live.ScreenText;
        Assert.Contains("PAYLOAD-0", durable,
            "A payload accepted before the resize must not be scrolled out by a " +
            "reservation composed for the viewport that no longer exists.");
        Assert.Contains("PAYLOAD-1", durable,
            "The retried unit must reach the host at the geometry it now composes for.");
    }

    [TestMethod]
    public async Task CommitAsync_NativeGrowBeforeNativeWrite_RefusesStaleBatchAndRetries()
    {
        // The mirror case from the captured runs: the reservation's bottom-row
        // linefeed was composed to push a payload out of an 11-row viewport, and the
        // device has since grown, where that linefeed no longer scrolls and the live
        // repaint erases the row it was meant to push into history.
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation, initialTerminalHeight: 11)
        {
            SupportsGeometryGatedDelivery = true,
            SupportsCursorObservation = true,
            AcceptLiveLayout = true,
            CancelOnFirstReanchor = false,
            ResizeBeforeNativeWrite = (1, 34),
        };
        await using var parent = new RecordingParentAdapter(40, 11);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.IsNotEmpty(live.Refusals,
            "A batch composed for the shorter viewport must be refused rather than written.");
        Assert.AreEqual(11, live.Refusals[0].ExpectedHeight,
            "The refused batch must be the one composed for the viewport the device has left.");
        Assert.AreEqual(34, live.Refusals[0].DeviceHeight);
        Assert.AreEqual(34, live.TerminalHeight, "The retry must observe the taller viewport.");
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(2, result.CompletedUnits);
        AssertCleanHistory(live);

        var durable = live.ReadHistory() + live.ScreenText;
        Assert.Contains("PAYLOAD-0", durable,
            "The payload the stale batch would have erased must still exist.");
        Assert.Contains("PAYLOAD-1", durable,
            "The retried unit must reach the host at the grown geometry.");
    }

    [TestMethod]
    public async Task CommitAsync_NativeShrinkBeforeFinalReservationDelivery_RefusesStaleBatchAndRetries()
    {
        // The captured shrink corruption was the reservation and the repaint that
        // follows it reaching the device as one batch for a viewport the host had
        // already left: the reservation's linefeeds clamped onto the new bottom row,
        // and each one scrolled a live row into history. That batch is the delivery
        // after the last unit, and it restores and retries through its own path —
        // separate from the per-unit loop the two tests above exercise.
        const int unitsInCommit = 2;
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation)
        {
            SupportsGeometryGatedDelivery = true,
            SupportsCursorObservation = true,
            AcceptLiveLayout = true,
            CancelOnFirstReanchor = false,
            ResizeBeforeNativeWrite = (unitsInCommit + 1, 11),
        };
        await using var parent = new RecordingParentAdapter(40, 34);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.AreEqual(1, live.Refusals.Count,
            "Exactly the final reservation batch must be refused.");
        Assert.AreEqual(unitsInCommit + 1, live.Refusals[0].SubmitIndex,
            "The refusal must come from the final reservation batch, not from a unit — " +
            "otherwise this case tests the path the per-unit regressions already cover.");
        Assert.AreEqual(34, live.Refusals[0].ExpectedHeight);
        Assert.AreEqual(11, live.Refusals[0].DeviceHeight);
        Assert.AreEqual(11, live.TerminalHeight);
        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(unitsInCommit, result.CompletedUnits);
        Assert.AreEqual(11, live.LiveHeight,
            "The retried reservation must fit the viewport that is actually there, not the " +
            "one the refused batch was composed for.");
        AssertCleanHistory(live);

        var durable = live.ReadHistory() + live.ScreenText;
        Assert.Contains("PAYLOAD-0", durable);
        Assert.Contains("PAYLOAD-1", durable);
    }

    [TestMethod]
    public async Task CommitAsync_ResizeDuringObservation_RepaintsWithinCursorRoundTripBudget()
    {
        using var cancellation = new CancellationTokenSource();
        using var live = new ReservationLiveHandle(cancellation)
        {
            SupportsGeometryGatedDelivery = true,
            SupportsCursorObservation = true,
            AcceptLiveLayout = true,
            CancelOnFirstReanchor = false,
            ResizeDuringFirstObservation = (30, 11),
            SimulatedCursorRoundTrip = TimeSpan.FromMilliseconds(30),
        };
        await using var parent = new RecordingParentAdapter(40, 34);
        var coordinator = new FlowCommitCoordinator(live, parent, CancellationToken.None);

        var result = await coordinator.CommitAsync(
            new TwoRowSource(), UnexpectedNextLayout, cancellation.Token);

        Assert.AreEqual(FlowCommitStatus.Emitted, result.Status);
        Assert.AreEqual(2, result.CompletedUnits);
        Assert.AreEqual(30, live.TerminalWidth);
        Assert.AreEqual(11, live.TerminalHeight);
        Assert.IsNotNull(live.FirstRepaintAfterResize,
            "The resized device must receive both the payload and the retained live image.");
        Assert.IsTrue(live.FirstRepaintAfterResize <= TimeSpan.FromMilliseconds(100),
            $"The first complete repaint incurred {live.FirstRepaintAfterResize} of " +
            "serialized cursor-response latency; superseded observations must not delay it.");
        AssertCleanHistory(live);
        var durable = live.ReadHistory() + live.ScreenText;
        Assert.Contains("PAYLOAD-0", durable);
        Assert.Contains("PAYLOAD-1", durable);
    }

    private static Task<Hex1bWidget> UnexpectedNextLayout(FlowStepContext context)
        => throw new InvalidOperationException("A cancelled prefix must retain its current live layout.");

    private static void AssertCleanHistory(ReservationLiveHandle live)
    {
        var history = live.ReadHistory();
        Assert.Contains("PRIOR-COMMITTED-ROW", history);
        Assert.IsFalse(history.Contains("MUTABLE-LIVE-", StringComparison.Ordinal),
            $"Reservation/recovery must not promote the live image into history.\n{history}");
        Assert.Contains("MUTABLE-LIVE-00", live.ScreenText);
    }

    private sealed class TwoRowSource : FlowCommitSource
    {
        public override int UnitCount => 2;

        public override Task<FlowCommitUnit> UnitAsync(
            int index, int width, CancellationToken cancellationToken)
        {
            var surface = new Surface(width, 1);
            surface.WriteText(0, 0, $"PAYLOAD-{index}");
            return Task.FromResult(new FlowCommitUnit($"row-{index}", surface));
        }
    }

    // The coordinator is real; this seam supplies synchronous frames and a real
    // terminal buffer, without an application pump or timing-dependent resize.
    // Crop resize deliberately does not model a native host's retention policy:
    // the oracle is unwanted live-row scrollback, not payload survival at the
    // instant a late renderer clamp moves the live image over existing content.
    private sealed class ReservationLiveHandle : ILiveStepHandle, IDisposable
    {
        private const int Width = 40;
        private readonly CancellationTokenSource _cancellation;
        private readonly Hex1bAppWorkloadAdapter _workload = new();
        private readonly Hex1bTerminal _terminal;
        private readonly Surface _liveSurface = new(Width, 24);
        private StringBuilder? _pendingOutput;
        private bool _shrinkArmed;
        private bool _firstReanchor = true;
        private bool _muted;
        private int _terminalHeight = 34;
        private int _terminalWidth = Width;
        private TimeSpan _observationElapsed;
        private bool _measureResizeRepaint;

        public ReservationLiveHandle(CancellationTokenSource cancellation, int initialTerminalHeight = 34)
        {
            _cancellation = cancellation;
            _terminalHeight = initialTerminalHeight;
            _terminal = Hex1bTerminal.CreateBuilder()
                .WithWorkload(_workload)
                .WithHeadless()
                .WithDimensions(Width, initialTerminalHeight)
                .WithScrollback(100)
                .Build();

            // Put a real pre-existing committed row into scrollback, then seed
            // the mutable image that an excessive reservation would leak.
            Apply($"\x1b[1;1HPRIOR-COMMITTED-ROW\x1b[{initialTerminalHeight};1H\n\x1b[2J");
            for (var row = 0; row < _liveSurface.Height; row++)
            {
                var text = $"MUTABLE-LIVE-{row:00}";
                _liveSurface.WriteText(0, row, text);
                Apply($"\x1b[{RowOrigin + row + 1};1H{text}");
            }
            Apply($"\x1b[{RowOrigin + 1};1H");
        }

        public bool ShrinkAfterPayloadGeometrySample { get; init; }
        public bool ShrinkOnReanchor { get; init; }

        /// <summary>
        /// Cancels the commit from the first reanchor, so the recovery path runs.
        /// </summary>
        /// <remarks>
        /// Off for a case that must reach a completed commitment: both original
        /// regressions assert recovery, and neither can observe a retry after a
        /// refused delivery because the token is already cancelled.
        /// </remarks>
        public bool CancelOnFirstReanchor { get; init; } = true;
        public bool ShrinkObserved { get; private set; }
        public (int Width, int Height)? ResizeDuringFirstObservation { get; set; }
        public TimeSpan SimulatedCursorRoundTrip { get; init; }
        public TimeSpan? FirstRepaintAfterResize { get; private set; }
        public List<int> ScrollRequests { get; } = [];
        public string ScreenText => _terminal.GetScreenText();
        public int TerminalWidth => ReadCurrentGeometry().Width;
        public int TerminalHeight => ReadCurrentGeometry().Height;
        public int RowOrigin { get; private set; } = 2;
        public int LiveHeight { get; private set; } = 24;
        public long FrameCount { get; private set; } = 1;
        public long ResizeVersion { get; private set; }

        /// <summary>
        /// Whether this handle can report its own cursor row.
        /// </summary>
        /// <remarks>
        /// A host that cannot report a cursor row cannot re-anchor mid-commit, so the
        /// coordinator refuses a resize during commitment outright. Enabling this is
        /// what makes the retry after a refused delivery reachable at all, which is
        /// also true of the native hosts this behaviour exists for.
        /// </remarks>
        public bool SupportsCursorObservation { get; init; }

        /// <summary>
        /// Whether a finished commitment may install its next live layout.
        /// </summary>
        /// <remarks>
        /// Off for a case that ends in recovery, which asserts the cancelled prefix
        /// keeps its current layout.
        /// </remarks>
        public bool AcceptLiveLayout { get; init; }

        /// <summary>
        /// Whether this handle's device can refuse a batch composed for a
        /// geometry it no longer reports.
        /// </summary>
        public bool SupportsGeometryGatedDelivery { get; init; }

        /// <summary>
        /// A native resize applied immediately before the Nth geometry-gated
        /// delivery, counting from one, and the height the device moves to.
        /// </summary>
        /// <remarks>
        /// <para>
        /// This is the window the captured native runs exposed: the bytes are
        /// internally consistent and correctly positioned for the geometry they
        /// were composed against, and the device has already left it.
        /// </para>
        /// <para>
        /// Indexed rather than one-shot because the commitment has several distinct
        /// delivery sites with separate restore and retry logic. The delivery after
        /// the last unit is the final reservation batch — the one that composes the
        /// live region's linefeeds and its repaint together, which is what the
        /// captures showed being discarded as a unit.
        /// </para>
        /// </remarks>
        public (int SubmitIndex, int Height)? ResizeBeforeNativeWrite { get; set; }

        /// <summary>Batches the device refused, by delivery index and geometry.</summary>
        public List<(int SubmitIndex, int ExpectedWidth, int ExpectedHeight, int DeviceWidth, int DeviceHeight)>
            Refusals { get; } = [];

        /// <summary>Geometry-gated deliveries this handle has offered.</summary>
        public int SubmitCount { get; private set; }

        /// <summary>
        /// Applies the armed native resize when this delivery is the one it targets.
        /// </summary>
        private void ApplyArmedResizeBeforeDelivery()
        {
            SubmitCount++;
            if (ResizeBeforeNativeWrite is not { } armed || armed.SubmitIndex != SubmitCount)
            {
                return;
            }

            ResizeBeforeNativeWrite = null;
            ResizeDevice(armed.Height);
        }

        public FlowAtomicCheckpoint CaptureAtomicCheckpoint() => new(
            CursorRow: 0,
            InitialRowOrigin: 0,
            StepRowOrigin: RowOrigin,
            StepHeight: LiveHeight,
            StepTerminalWidth: TerminalWidth);

        public void RestoreAtomicCheckpoint(FlowAtomicCheckpoint checkpoint)
        {
            RowOrigin = checkpoint.StepRowOrigin;
            LiveHeight = checkpoint.StepHeight;
        }

        public (int Width, int Height) ReadCurrentGeometry()
        {
            var observed = (_terminalWidth, _terminalHeight);
            if (_shrinkArmed)
            {
                _shrinkArmed = false;
                // A host may change immediately after a sample. Every geometry
                // access uses this same observation path, including properties;
                // changing the coordinator's accessor cannot bypass the race.
                Shrink();
            }
            return observed;
        }

        public (int Width, int Height, long ResizeVersion) ReadFreshGeometryForEmission()
        {
            var geometry = ReadCurrentGeometry();
            return (geometry.Width, geometry.Height, ResizeVersion);
        }

        private void Shrink()
        {
            ResizeDevice(11);
            ShrinkObserved = true;
        }

        /// <summary>
        /// Applies a resize to the device itself, independently of any sample the
        /// coordinator has already taken.
        /// </summary>
        private void ResizeDevice(int height)
        {
            _terminalHeight = height;
            ResizeVersion++;
            _terminal.Resize(_terminalWidth, height);
        }

        public Surface SnapshotLiveSurface() => _liveSurface;
        public void EnsureHistoryCommitSupported() { }
        public Task<bool> PrepareForCommitAsync(CancellationToken cancellationToken)
            => Task.FromResult(SetLiveOutputMuted(true));

        public void WriteTerminalAt(int row, string text)
        {
            Write($"\x1b[{Math.Clamp(row, 0, _terminalHeight - 1) + 1};1H{text}");
            if (ShrinkAfterPayloadGeometrySample && !ShrinkObserved)
            {
                // Arm on an actual payload emission, not a numbered property read.
                _shrinkArmed = true;
            }
        }

        public IAtomicTerminalUpdate BeginAtomicTerminalUpdate()
        {
            if (_pendingOutput is not null)
                throw new InvalidOperationException("Nested terminal update.");
            _pendingOutput = new StringBuilder();
            return new AtomicUpdate(this);
        }

        public void ScrollViewportUp(int rows)
        {
            ScrollRequests.Add(rows);
            Write($"\x1b[{_terminalHeight};1H{new string('\n', rows)}");
        }

        public void MarkCommittedRow(int row) { }

        public void ReanchorLive(int rowOrigin, int liveHeight, Surface liveSurface)
        {
            if (_firstReanchor && ShrinkOnReanchor)
                Shrink();

            // Match the renderer's viewport clamp, not reservation arithmetic.
            LiveHeight = Math.Clamp(liveHeight, 1, _terminalHeight);
            RowOrigin = Math.Clamp(rowOrigin, 0, _terminalHeight - LiveHeight);
            for (var row = 0; row < LiveHeight; row++)
            {
                Write($"\x1b[{RowOrigin + row + 1};1H\x1b[2K");
                if (row < liveSurface.Height)
                    Write(SoftWrapEmitter.RenderRowText(liveSurface, row));
            }
            Write($"\x1b[{RowOrigin + 1};1H");

            if (_firstReanchor)
            {
                _firstReanchor = false;
                // Recovery is the next consumer of the append boundary. No
                // cursor observation repairs a stale return value on its behalf.
                if (CancelOnFirstReanchor)
                    _cancellation.Cancel();
            }
        }

        public void ResizeLive(int width, int liveHeight) => LiveHeight = liveHeight;
        public void ResizeLiveToTerminalGeometry(int width, int terminalHeight)
            => LiveHeight = Math.Min(24, terminalHeight);
        public void ApplyLiveLayout(Func<FlowStepContext, Task<Hex1bWidget>> builder)
        {
            if (!AcceptLiveLayout)
                throw new InvalidOperationException("The uncommitted suffix still owns the layout.");

            FrameCount++;
        }
        public Task<int?> ObserveCursorRowAsync(CancellationToken cancellationToken)
        {
            if (ResizeDuringFirstObservation is { } geometry)
            {
                ResizeDuringFirstObservation = null;
                _terminalWidth = geometry.Width;
                ResizeDevice(geometry.Height);
                _measureResizeRepaint = true;
            }

            // Virtual response time isolates the serial round-trip cost from
            // scheduler jitter. No response is withheld and no wall clock sleeps.
            _observationElapsed += SimulatedCursorRoundTrip;
            return Task.FromResult<int?>(RowOrigin);
        }
        public void RecordCommittedRows(Surface surface) { }
        public int DiscardQueuedLiveOutput() => 0;
        public void RequestLiveFrame() => FrameCount++;
        public bool SetLiveOutputMuted(bool muted)
        {
            var previous = _muted;
            _muted = muted;
            return previous;
        }
        public Task<bool> WaitForLiveFrameAfterAsync(
            long afterFrame, TimeSpan timeout, CancellationToken cancellationToken)
            => Task.FromResult(FrameCount > afterFrame);
        public void RecordCommitEvent(string message) { }

        public string ReadHistory()
        {
            var text = new StringBuilder();
            foreach (var row in _terminal.GetScrollbackRows(_terminal.ScrollbackCount))
            {
                foreach (var cell in row.Cells)
                    text.Append(cell.Character);
                text.Append('\n');
            }
            return text.ToString();
        }

        private void Write(string text)
        {
            if (_pendingOutput is null)
                Apply(text);
            else
                _pendingOutput.Append(text);
        }

        private void Apply(string text)
        {
            _terminal.ApplyTokens(AnsiTokenizer.Tokenize(text));
            if (_measureResizeRepaint && FirstRepaintAfterResize is null
                && ScreenText.Contains("MUTABLE-LIVE-00", StringComparison.Ordinal)
                && (ReadHistory() + ScreenText).Contains("PAYLOAD-0", StringComparison.Ordinal))
            {
                FirstRepaintAfterResize = _observationElapsed;
            }
        }

        public void Dispose()
        {
            _terminal.Dispose();
            _workload.Dispose();
        }

        private sealed class AtomicUpdate(ReservationLiveHandle owner) : IAtomicTerminalUpdate
        {
            private bool _resolved;

            public bool FlushCompleted { get; private set; }

            public bool SupportsGeometryGatedDelivery => owner.SupportsGeometryGatedDelivery;

            public Task<NativeDeliveryOutcome>? SubmitIfGeometry(
                int expectedWidth, int expectedHeight)
            {
                var output = TakePendingOutput();

                if (!SupportsGeometryGatedDelivery)
                {
                    owner.Apply(output);
                    FlushCompleted = true;
                    return Task.FromResult(NativeDeliveryOutcome.Applied);
                }

                // The device can resize between composition and the write. The
                // bytes are still positioned for the geometry they were composed
                // against, so writing them here is exactly the corruption the
                // gated path exists to prevent.
                owner.ApplyArmedResizeBeforeDelivery();

                var device = owner.ReadCurrentGeometry();
                if (device.Width != expectedWidth || device.Height != expectedHeight)
                {
                    owner.Refusals.Add(
                        (owner.SubmitCount, expectedWidth, expectedHeight, device.Width, device.Height));
                    return Task.FromResult(NativeDeliveryOutcome.GeometryChanged);
                }

                owner.Apply(output);
                FlushCompleted = true;
                return Task.FromResult(NativeDeliveryOutcome.Applied);
            }

            public void Dispose()
            {
                if (_resolved)
                {
                    return;
                }

                owner.Apply(TakePendingOutput());
                FlushCompleted = true;
            }

            private string TakePendingOutput()
            {
                _resolved = true;
                var output = owner._pendingOutput
                    ?? throw new InvalidOperationException("Terminal update already resolved.");
                owner._pendingOutput = null;
                return output.ToString();
            }
        }
    }
}
