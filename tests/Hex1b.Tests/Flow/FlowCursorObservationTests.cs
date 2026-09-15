using System.Diagnostics;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Hex1b;
using Hex1b.Diagnostics;
using Hex1b.Flow;
using Hex1b.Reflow;
using Hex1b.Tokens;

namespace Hex1b.Tests.Flow;

/// <summary>
/// Fences the pump-owned cursor-observation contract Flow uses for its anchor: a
/// single stdin reader services a bounded cursor-position query without opening a
/// competing reader, surrounding input survives byte-for-byte, an ambiguous
/// modified-F3-shaped report is never swallowed, and a native observation failure
/// is reported as no anchor rather than as the terminal's own model.
/// </summary>
[TestClass]
public class FlowCursorObservationTests
{
    private static readonly TimeSpan WaitTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FastProbeTimeout = TimeSpan.FromMilliseconds(5);

    [TestMethod]
    [DataRow("ghostty 1.3.1", true)]
    [DataRow("ghostty 1.3.1-arch2.1", true)]
    [DataRow("ghostty 1.3.10", false)]
    [DataRow("ghostty 1.3.1-arch2.10", false)]
    [DataRow("ghostty 1.3.1a", false)]
    [DataRow("Ghostty 1.3.1", false)]
    public async Task StartupHostProbe_FragmentedIdentity_QualifiesOnlyPinnedVersionsAndPreservesInput(
        string identity, bool expectedQualified)
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(
            driver, kgpProbeTimeout: TimeSpan.FromMilliseconds(100));
        var ct = TestContext.Current.CancellationToken;
        driver.Enqueue("α\x1bP>");
        driver.Enqueue("|" + identity);
        driver.Enqueue("\x1b");
        driver.Enqueue("\\β");

        await adapter.EnterRawModeAsync(ct);

        var profile = ((Hex1b.Flow.IFlowTerminalHostProfileSource)adapter).FlowHostProfile;
        Assert.AreEqual(expectedQualified, profile == Hex1b.Flow.FlowTerminalHostProfile.Ghostty_1_3_1,
            "Only the pinned upstream and installed-package identities may enable the native resize route.");
        var input = await adapter.ReadInputAsync(ct);
        Assert.AreEqual("αβ", Encoding.UTF8.GetString(input.Span),
            "The startup identity probe must consume its reply without losing or duplicating surrounding input.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_Native_FragmentedReplyWithInterleavedText_ReturnsPositionAndPreservesInput()
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        var source = (ICursorPositionSource)adapter;

        // Stand in for the terminal's presentation reader — the sole stdin reader that
        // services a pending query.
        var reader = Task.Run(async () => await adapter.ReadInputAsync(ct), ct);

        var observation = source.ObserveCursorPositionAsync(ct);

        await WaitForWrittenAsync(driver, "\x1b[6n");
        driver.Enqueue("t");
        driver.Enqueue("\x1b[1");
        driver.Enqueue("2;5");
        driver.Enqueue("Rx");

        var position = await observation;
        Assert.IsTrue(position.HasValue, "Expected a decoded cursor position.");
        var decoded = position.GetValueOrDefault();
        Assert.AreEqual(4, decoded.Column, "CPR column 5 is 0-based column 4.");
        Assert.AreEqual(11, decoded.Row, "CPR row 12 is 0-based row 11.");

        var input = await reader;
        Assert.AreEqual("tx", Encoding.UTF8.GetString(input.Span),
            "Typed text around a fragmented report must be delivered in order, once.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_Native_WhenNoReportArrives_TimesOutNullAndPreservesBufferedInput()
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        var source = (ICursorPositionSource)adapter;
        var reader = Task.Run(async () => await adapter.ReadInputAsync(ct), ct);
        var observation = source.ObserveCursorPositionAsync(ct);

        await WaitForWrittenAsync(driver, "\x1b[6n");
        driver.Enqueue("hi");
        driver.Enqueue("\x1b[12;5"); // An unterminated report fragment.

        var position = await observation;
        Assert.IsNull(position, "A query with no usable report must observe nothing.");

        var input = await reader;
        Assert.AreEqual("hi\x1b[12;5", Encoding.UTF8.GetString(input.Span),
            "Non-reply bytes seen during the query window must survive the timeout.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_Native_AmbiguousModifiedF3Report_PassesThroughAndObservesNull()
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        var source = (ICursorPositionSource)adapter;
        var reader = Task.Run(async () => await adapter.ReadInputAsync(ct), ct);
        var observation = source.ObserveCursorPositionAsync(ct);

        await WaitForWrittenAsync(driver, "\x1b[6n");

        // Shift-F3 in xterm encoding, and simultaneously a valid row 1 / column 2 CPR.
        driver.Enqueue("\x1b[1;2R");

        var position = await observation;
        Assert.IsNull(position, "An ambiguous modified-F3-shaped report must not be consumed as an observation.");

        var input = await reader;
        Assert.AreEqual("\x1b[1;2R", Encoding.UTF8.GetString(input.Span),
            "A modified F3 must reach the key path unchanged rather than being swallowed as a CPR.");
    }

    [TestMethod]
    public async Task ReadInputAsync_WithoutPendingObservation_PreservesCprAndModifiedF3ShapedBytes()
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        driver.Enqueue("\x1b[1;2R\x1b[12;5R");

        var input = await adapter.ReadInputAsync(ct);

        Assert.AreEqual("\x1b[1;2R\x1b[12;5R", Encoding.UTF8.GetString(input.Span),
            "The report filter must be inert whenever no observation is pending.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_WhenCancelled_ThrowsOperationCanceled()
    {
        using var driver = new ScriptedConsoleDriver();
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        var source = (ICursorPositionSource)adapter;

        using var cts = new CancellationTokenSource();
        var observation = source.ObserveCursorPositionAsync(cts.Token);
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(async () => await observation);
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_WhenDisposedWhilePending_CompletesWithNull()
    {
        using var driver = new ScriptedConsoleDriver();
        var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        await adapter.EnterRawModeAsync(TestContext.Current.CancellationToken);

        var source = (ICursorPositionSource)adapter;
        var observation = source.ObserveCursorPositionAsync(TestContext.Current.CancellationToken);

        await adapter.DisposeAsync();

        Assert.IsNull(await observation, "Shutdown must fail a pending observation deterministically.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_WhenDriverAnswersOutOfBand_ReturnsPositionWithoutWritingDsrQuery()
    {
        using var driver = new ScriptedConsoleDriver { OutOfBandPosition = (4, 7) };
        await using var adapter = new ConsolePresentationAdapter(driver, kgpProbeTimeout: FastProbeTimeout);
        var ct = TestContext.Current.CancellationToken;
        await adapter.EnterRawModeAsync(ct);

        var source = (ICursorPositionSource)adapter;
        var position = await source.ObserveCursorPositionAsync(ct);

        Assert.IsTrue(position.HasValue, "An out-of-band platform must answer directly.");
        var decoded = position.GetValueOrDefault();
        Assert.AreEqual(4, decoded.Column);
        Assert.AreEqual(7, decoded.Row);
        Assert.IsFalse(driver.WrittenText.Contains("\x1b[6n", StringComparison.Ordinal),
            "An out-of-band cursor read must not issue a DSR query.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_WhenNativeObservationFails_DoesNotFallBackToTerminalModel()
    {
        var presentation = new NullCursorPresentationAdapter();
        using var workload = new Hex1bAppWorkloadAdapter(presentation);
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithPresentation(presentation)
            .WithDimensions(20, 6)
            .Build();

        // Stand in for the terminal lifecycle attaching its own model provider: the
        // native presentation remains the only source considered, so this value must
        // never be reported as host state. The terminal's pump does consume the barrier,
        // so this really exercises the native-then-null path.
        workload.HeadlessCursorProvider = () => (9, 9);

        var source = (ICursorPositionSource)workload;
        var position = await source.ObserveCursorPositionAsync(TestContext.Current.CancellationToken);

        Assert.IsNull(position,
            "A failed native observation must report no anchor, never the terminal's own model.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_WhenBarrierIsNeverConsumed_FailsBoundedInsteadOfReturningStaleModel()
    {
        using var workload = new Hex1bAppWorkloadAdapter();

        // No terminal consumes the output channel, so the barrier can never clear.
        workload.HeadlessCursorProvider = () => (9, 9);

        var source = (ICursorPositionSource)workload;
        var started = Stopwatch.GetTimestamp();
        var position = await source.ObserveCursorPositionAsync(TestContext.Current.CancellationToken);

        Assert.IsNull(position, "An unconsumed barrier must report no anchor, never a stale model.");
        Assert.IsTrue(Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(3),
            "An unconsumed barrier must fail within its bound rather than hanging.");
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_ConcurrentObservations_EachSeeQueuedOutputApplied()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .Build();
        var ct = TestContext.Current.CancellationToken;
        var source = (ICursorPositionSource)workload;

        workload.Write("\x1b[3;4H");

        // Both observations fence behind the same queued write with their own barrier
        // item; neither may overwrite the other's pending wait.
        var first = source.ObserveCursorPositionAsync(ct);
        var second = source.ObserveCursorPositionAsync(ct);
        var results = await Task.WhenAll(first, second);

        foreach (var result in results)
        {
            Assert.IsTrue(result.HasValue, "Each concurrent observation must resolve.");
            var decoded = result.GetValueOrDefault();
            Assert.AreEqual(3, decoded.Column);
            Assert.AreEqual(2, decoded.Row);
        }
    }

    [TestMethod]
    public async Task ObserveCursorPositionAsync_Headless_ReportsAppliedModelCursorAfterWriteAndResize()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .Build();
        var ct = TestContext.Current.CancellationToken;
        var source = (ICursorPositionSource)workload;

        workload.Write("\x1b[3;4H");

        var position = await source.ObserveCursorPositionAsync(ct);
        Assert.IsTrue(position.HasValue, "Headless observation must report the applied model cursor.");
        var decoded = position.GetValueOrDefault();
        Assert.AreEqual(3, decoded.Column);
        Assert.AreEqual(2, decoded.Row);

        // A shrink clamps the model cursor along with the buffer.
        terminal.Resize(20, 2);

        var resized = await source.ObserveCursorPositionAsync(ct);
        Assert.IsTrue(resized.HasValue);
        var clamped = resized.GetValueOrDefault();
        Assert.AreEqual(3, clamped.Column);
        Assert.AreEqual(1, clamped.Row);
    }

    [TestMethod]
    public async Task Resize_FromOutputFilterWithFullQueue_AppliesAfterPriorOutputWithoutBlockingPump()
    {
        using var workload = new Hex1bAppWorkloadAdapter(maxQueuedOutputItems: 1);
        var gate = new ResizeFromOutputFilter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .AddWorkloadFilter(gate)
            .Build();
        gate.Resize = () => _ = terminal.ResizeWithWorkloadAsync(10, 6);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            workload.WriteRequired("\x1b[1;1HABCDEFGHIJKLMNO");
            await gate.Entered.Task.WaitAsync(WaitTimeout, ct);
            workload.WriteRequired("\x1b[2;1HPQRST"); // Fill the queue while its reader is gated.
            gate.Release.TrySetResult();
            await gate.ResizeRequested.Task.WaitAsync(WaitTimeout, ct);
            var cursor = await ((ICursorPositionSource)workload).ObserveCursorPositionAsync(ct);

            Assert.IsNotNull(cursor);
            Assert.AreEqual((5, 2), cursor.Value);
            Assert.AreEqual(10, terminal.Width);
            var lines = terminal.GetScreenText().Split('\n');
            Assert.AreEqual("ABCDEFGHIJ", lines[0].TrimEnd());
            Assert.AreEqual("KLMNO", lines[1].TrimEnd());
            Assert.AreEqual("PQRST", lines[2].TrimEnd());
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Resize_DuringGeometryProtectedWrite_CannotOvertakeOldGeometryOutput()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .Build();
        var ct = TestContext.Current.CancellationToken;
        Task resize;
        await workload.OutputGeometryGate.WaitAsync(ct);
        try
        {
            // The same transaction Flow holds from its final geometry read through handoff.
            var geometry = ((IFlowCurrentGeometrySource)workload).ReadCurrentGeometry();
            resize = workload.QueueResizeAsync(10, 6);
            workload.WriteRequired($"\x1b[1;{geometry.Width - 5}HX");
        }
        finally
        {
            workload.OutputGeometryGate.Release();
        }
        await resize.WaitAsync(WaitTimeout, ct);
        var cursor = await ((ICursorPositionSource)workload).ObserveCursorPositionAsync(ct);
        Assert.IsNotNull(cursor);
        Assert.AreEqual((5, 1), cursor.Value);
        Assert.AreEqual('X', terminal.GetScreenText().Split('\n')[1][4]);
    }

    [TestMethod]
    public async Task AutomationResize_AfterQueuedResize_PreservesLatestGeometryAndPriorOutput()
    {
        using var workload = new Hex1bAppWorkloadAdapter();
        var gate = new ResizeFromOutputFilter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .WithReflow(GhosttyReflowStrategy.Instance)
            .AddWorkloadFilter(gate)
            .Build();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            workload.WriteRequired("\x1b[1;15HX");
            await gate.Entered.Task.WaitAsync(WaitTimeout, ct);
            var earlier = terminal.ResizeWithWorkloadAsync(10, 6, ct);
            var latest = terminal.ResizeForAutomationAsync(12, 8, ct);
            gate.Release.TrySetResult();
            await Task.WhenAll(earlier, latest).WaitAsync(WaitTimeout, ct);
            var cursor = await ((ICursorPositionSource)workload).ObserveCursorPositionAsync(ct);

            Assert.AreEqual(12, terminal.Width);
            Assert.AreEqual(8, terminal.Height);
            Assert.IsNotNull(cursor);
            Assert.AreEqual((3, 1), cursor.Value);
            Assert.AreEqual('X', terminal.GetScreenText().Split('\n')[1][2]);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [TestMethod]
    [DoNotParallelize] // Diagnostics uses one socket path per process.
    public async Task DiagnosticsResize_WithPendingOutput_ReportsAppliedSizeAndPreservesOmittedAxis()
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(WaitTimeout);
        var ct = timeout.Token;
        using var workload = new Hex1bAppWorkloadAdapter();
        var gate = new ResizeFromOutputFilter();
        await using var diagnostics = new McpDiagnosticsPresentationFilter();
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .AddWorkloadFilter(gate)
            .AddPresentationFilter(diagnostics)
            .Build();

        async Task<DiagnosticsResponse> RequestAsync(string request)
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            await socket.ConnectAsync(new UnixDomainSocketEndPoint(diagnostics.SocketPath), ct);
            await using var stream = new NetworkStream(socket, ownsSocket: false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            await using var writer = new StreamWriter(stream, Encoding.UTF8) { AutoFlush = true };
            await writer.WriteLineAsync(request.AsMemory(), ct);
            var line = await reader.ReadLineAsync(ct);
            Assert.IsNotNull(line);
            var response = JsonSerializer.Deserialize(line, DiagnosticsJsonContext.Default.DiagnosticsResponse);
            Assert.IsNotNull(response);
            return response;
        }

        try
        {
            workload.WriteRequired("prior output");
            await gate.Entered.Task.WaitAsync(ct);
            var presentationResize = terminal.ResizeWithWorkloadAsync(30, 6, ct);
            var heightRequest = RequestAsync("""{"method":"resize","y":8}""");
            while (((IFlowCurrentGeometrySource)workload).ReadCurrentGeometry().Height != 8)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Yield();
            }
            gate.Release.TrySetResult();
            await presentationResize;
            var heightResponse = await heightRequest;
            Assert.IsTrue(heightResponse.Success, heightResponse.Error);
            Assert.AreEqual(30, heightResponse.Width);
            Assert.AreEqual(8, heightResponse.Height);
            Assert.AreEqual(30, terminal.Width);
            Assert.AreEqual(8, terminal.Height);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    [TestMethod]
    public async Task Resize_WhenOutputPumpFaults_FailsPendingBoundedAndSubsequentRequests()
    {
        using var workload = new Hex1bAppWorkloadAdapter(maxQueuedOutputItems: 1);
        var gate = new ResizeFromOutputFilter { Failure = new IOException("output failed") };
        using var terminal = Hex1bTerminal.CreateBuilder()
            .WithWorkload(workload)
            .WithHeadless()
            .WithDimensions(20, 6)
            .AddWorkloadFilter(gate)
            .Build();
        var ct = TestContext.Current.CancellationToken;
        try
        {
            workload.WriteRequired("held output");
            await gate.Entered.Task.WaitAsync(WaitTimeout, ct);
            var queued = terminal.ResizeWithWorkloadAsync(30, 6, ct);
            var waitingForSpace = terminal.ResizeWithWorkloadAsync(40, 8, ct);
            gate.Release.TrySetResult();

            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => queued.WaitAsync(WaitTimeout, ct));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => waitingForSpace.WaitAsync(WaitTimeout, ct));
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                terminal.ResizeWithWorkloadAsync(50, 9, ct).WaitAsync(WaitTimeout, ct));
            Assert.AreEqual(20, terminal.Width);
            Assert.AreEqual(6, terminal.Height);
        }
        finally
        {
            gate.Release.TrySetResult();
        }
    }

    private sealed class ResizeFromOutputFilter : IHex1bTerminalWorkloadFilter
    {
        private bool _held;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ResizeRequested { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Action? Resize { get; set; }
        public Exception? Failure { get; init; }

        public async ValueTask OnOutputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
        {
            if (_held)
                return;
            _held = true;
            Entered.TrySetResult();
            await Release.Task.WaitAsync(ct);
            if (Failure is not null)
                throw Failure;
            Resize?.Invoke();
            ResizeRequested.TrySetResult();
        }

        public ValueTask OnSessionStartAsync(int width, int height, DateTimeOffset timestamp, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask OnFrameCompleteAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
        public ValueTask OnInputAsync(IReadOnlyList<AnsiToken> tokens, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask OnResizeAsync(int width, int height, TimeSpan elapsed, CancellationToken ct = default)
            => ValueTask.CompletedTask;
        public ValueTask OnSessionEndAsync(TimeSpan elapsed, CancellationToken ct = default) => ValueTask.CompletedTask;
    }

    [TestMethod]
    public void WriteRequired_WhenDisposed_ThrowsWhileOrdinaryWriteStaysSilent()
    {
        var workload = new Hex1bAppWorkloadAdapter();
        workload.Dispose();

        Assert.ThrowsExactly<ObjectDisposedException>(() => workload.WriteRequired("x"));
        workload.Write("x"); // Ordinary render cleanup must keep best-effort semantics.
    }

    private static async Task WaitForWrittenAsync(ScriptedConsoleDriver driver, string expected)
    {
        var deadline = DateTime.UtcNow + WaitTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (driver.WrittenText.Contains(expected, StringComparison.Ordinal))
            {
                return;
            }

            await Task.Delay(5);
        }

        Assert.Fail($"Driver was never written '{expected}'. Written text: {driver.WrittenText}");
    }
}

/// <summary>
/// A console driver whose input arrives in explicit chunks at test-controlled times
/// and whose writes are recorded, so the adapter's reader can park in a blocking read
/// exactly as it does against a real stdin.
/// </summary>
internal sealed class ScriptedConsoleDriver : IConsoleDriver
{
    private readonly object _sync = new();
    private readonly Queue<byte[]> _chunks = new();
    private readonly SemaphoreSlim _available = new(0);
    private readonly List<byte> _written = new();

    public (int Column, int Row)? OutOfBandPosition { get; set; }

    public string WrittenText
    {
        get
        {
            lock (_sync)
            {
                return Encoding.ASCII.GetString(_written.ToArray());
            }
        }
    }

    public bool DataAvailable
    {
        get
        {
            lock (_sync)
            {
                return _chunks.Count > 0;
            }
        }
    }

    public int Width => 80;

    public int Height => 24;

    public Encoding InputEncoding { get; set; } = Encoding.UTF8;

    public event Action<int, int>? Resized;

    /// <summary>Test hook to simulate a terminal resize notification.</summary>
    public void RaiseResized(int width, int height) => Resized?.Invoke(width, height);

    public void Enqueue(string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        lock (_sync)
        {
            _chunks.Enqueue(bytes);
        }

        _available.Release();
    }

    public async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
    {
        while (true)
        {
            lock (_sync)
            {
                if (_chunks.Count > 0)
                {
                    var chunk = _chunks.Dequeue();
                    if (chunk.Length <= buffer.Length)
                    {
                        chunk.AsSpan().CopyTo(buffer.Span);
                        return chunk.Length;
                    }

                    chunk.AsSpan(0, buffer.Length).CopyTo(buffer.Span);
                    _chunks.Enqueue(chunk[buffer.Length..]);
                    _available.Release();
                    return buffer.Length;
                }
            }

            try
            {
                await _available.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return 0;
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        lock (_sync)
        {
            _written.AddRange(data.ToArray());
        }
    }

    public void Flush()
    {
    }

    public void DrainInput()
    {
        lock (_sync)
        {
            _chunks.Clear();
        }

        while (_available.Wait(0))
        {
        }
    }

    public bool TryGetWindowPixelSize(out int pixelWidth, out int pixelHeight)
    {
        pixelWidth = 0;
        pixelHeight = 0;
        return false;
    }

    public bool TryGetCursorPosition(out int column, out int row)
    {
        if (OutOfBandPosition is { } position)
        {
            column = position.Column;
            row = position.Row;
            return true;
        }

        column = 0;
        row = 0;
        return false;
    }

    public void EnterRawMode(bool preserveOPost = false)
    {
    }

    public void ExitRawMode()
    {
    }

    public void Dispose()
    {
    }
}

/// <summary>
/// A native-shaped presentation that owns a real cursor it refuses to observe: it
/// implements <see cref="ICursorPositionSource"/> and always reports no anchor, so a
/// cursor observation that substitutes the terminal's model for it is detectable.
/// </summary>
internal sealed class NullCursorPresentationAdapter : IHex1bTerminalPresentationAdapter, ICursorPositionSource
{
    public Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken cancellationToken)
        => Task.FromResult<(int Column, int Row)?>(null);

    public ValueTask WriteOutputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        => ValueTask.CompletedTask;

    public async ValueTask<ReadOnlyMemory<byte>> ReadInputAsync(CancellationToken ct = default)
    {
        try
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        return ReadOnlyMemory<byte>.Empty;
    }

    public int Width => 20;

    public int Height => 6;

    public TerminalCapabilities Capabilities => TerminalCapabilities.Modern;

    public event Action<int, int>? Resized;

    public event Action? Disconnected;

    /// <summary>Test hook; also keeps the interface events live rather than unused.</summary>
    public void RaiseEvents()
    {
        Resized?.Invoke(Width, Height);
        Disconnected?.Invoke();
    }

    public ValueTask FlushAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask EnterRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public ValueTask ExitRawModeAsync(CancellationToken ct = default) => ValueTask.CompletedTask;

    public (int Row, int Column) GetCursorPosition() => (0, 0);

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
