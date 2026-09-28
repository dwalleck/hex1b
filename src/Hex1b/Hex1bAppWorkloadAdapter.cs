using System.Text;
using System.Threading.Channels;
using Hex1b.Input;
using Hex1b.Tokens;

namespace Hex1b;

/// <summary>
/// Workload adapter for Hex1bApp TUI applications.
/// Bridges the raw byte interface of <see cref="IHex1bTerminalWorkloadAdapter"/>
/// with the higher-level APIs that Hex1bApp needs.
/// </summary>
/// <remarks>
/// <para>
/// This adapter has two faces:
/// <list type="bullet">
///   <item>Terminal side: Implements <see cref="IHex1bTerminalWorkloadAdapter"/> for Hex1bTerminal</item>
///   <item>App side: Provides <see cref="Write(string)"/>, <see cref="InputEvents"/>, etc. for Hex1bApp</item>
/// </list>
/// </para>
/// <para>
/// Data flow:
/// <list type="bullet">
///   <item>App calls Write() → bytes queued → Terminal calls ReadOutputAsync()</item>
///   <item>Terminal calls WriteInputAsync() → parsed to events → App reads InputEvents</item>
/// </list>
/// </para>
/// </remarks>
/// <example>
/// <code>
/// var workload = new Hex1bAppWorkloadAdapter();
/// var terminal = new Hex1bTerminal(workload, 80, 24);
/// var app = new Hex1bApp(workload, ctx => ctx.Text("Hello"));
/// await app.RunAsync();
/// </code>
/// </example>
public sealed class Hex1bAppWorkloadAdapter :
    IHex1bAppTerminalWorkloadAdapter,
    IHex1bTerminalTokenWorkloadAdapter,
    IRepaintableWorkloadAdapter,
    ICursorPositionSource,
    Hex1b.Flow.IFlowCurrentGeometrySource,
    IDisposable
{
    private readonly Channel<WorkloadOutputItem> _outputChannel;
    private readonly Channel<Hex1bEvent> _inputChannel;
    private readonly IHex1bTerminalPresentationAdapter? _presentationAdapter;
    private readonly TerminalCapabilities? _staticCapabilities;
    private int _width;
    private int _height;
    private bool _disposed;
    private bool _inTuiMode;
    private bool _dimensionsInitialized;
    private int _outputQueueDepth; // Manual tracking since unbounded channels don't support Count
    private readonly int _maxQueuedOutputItems;

    // Resize admission and Flow's final geometry check + enqueue are one transaction.
    // The consumer never takes this gate: a bounded producer may be waiting for it.
    internal SemaphoreSlim OutputGeometryGate { get; } = new(1, 1);
    private long _requestedGeometry; // Packed width/height; zero means no queued resize.
    private volatile bool _outputProcessingStopped;

    // In-flight observation barriers. Each observation enqueues an empty item carrying
    // one of these and awaits it; the terminal completes it when the item is consumed,
    // which proves everything queued before it has been applied. Disposal fails any
    // still-outstanding barrier so an observation never reports a stale model.
    private readonly object _barrierSync = new();
    private readonly HashSet<TaskCompletionSource<bool>> _pendingBarriers = new();

    // In-flight geometry-gated deliveries. Each one is completed by the output pump
    // once the native presentation has either written the batch or refused it, so a
    // producer learns the fate of its own bytes rather than of the queue position.
    // Disposal and shutdown fault every outstanding delivery, because a batch that
    // was never offered cannot be retried and must not be reported as applied.
    private readonly HashSet<GeometryGatedDelivery> _pendingDeliveries = new();

    /// <summary>
    /// The application that publishes frames for diagnostics. Set by Hex1bApp when it starts running.
    /// </summary>
    internal Diagnostics.IApplicationFrameSource? ApplicationFrameSource { get; set; }

    /// <summary>
    /// Whether this adapter hosts Hex1b applications (an app or a flow), so a missing frame
    /// source means no application is running rather than that there is no application layer.
    /// </summary>
    internal bool HostsApplications { get; set; }

    /// <summary>
    /// The session's input milestone tracker, when diagnostics are enabled: every event written to
    /// the input channel is numbered by it, atomically with the write.
    /// </summary>
    internal Diagnostics.InputMilestoneTracker? InputMilestones { get; set; }

    // Sequence numbers must follow channel order, so assignment and write are one step under this
    // gate. A semaphore rather than a lock, so the async path can await a full channel inside it.
    private readonly SemaphoreSlim _milestoneOutputGate = new(1, 1);
    private long _milestoneOutputSequence;

    /// <summary>The last output sequence enqueued, when input milestones are tracked.</summary>
    internal long MilestoneOutputSequence => Interlocked.Read(ref _milestoneOutputSequence);

    // Every input-channel write goes through here so a tracked session numbers each event in
    // channel order; an untracked session writes exactly as before.
    private bool TryWriteInput(Hex1bEvent evt) => InputMilestones is { } tracker
        ? tracker.Accept(evt, _inputChannel.Writer.TryWrite)
        : _inputChannel.Writer.TryWrite(evt);

    private ValueTask WriteInputTrackedAsync(Hex1bEvent evt, CancellationToken ct)
    {
        if (InputMilestones is null)
            return _inputChannel.Writer.WriteAsync(evt, ct);
        if (!TryWriteInput(evt))
            throw new ChannelClosedException();
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// The terminal's own applied cursor model, attached by <see cref="Hex1bTerminal"/>
    /// during construction when this adapter is the terminal's workload.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately separate from an attached native presentation: it exposes
    /// the cursor of the terminal's emulated screen (0-based <c>(Column, Row)</c>) and
    /// is the authoritative source only when there is no native presentation to
    /// observe. It is never reported as host state: when a native presentation is
    /// attached, that presentation is observed instead (and its failure is reported as
    /// <see langword="null"/>, not as this model).
    /// </para>
    /// </remarks>
    internal Func<(int Column, int Row)>? HeadlessCursorProvider { get; set; }

    internal bool HasCursorSource =>
        _presentationAdapter is ICursorPositionSource || HeadlessCursorProvider is not null;

    // Callback installed by Hex1bApp.RunAsync so an outer multiplexer
    // (e.g. PlaceholderWorkloadAdapter) can ask the app to drop diff
    // state and emit a full screen on the next frame. Null until the
    // owning Hex1bApp has started running.
    private Action? _repaintRequestHandler;

    /// <summary>
    /// Installs the callback invoked by <see cref="RequestFullRepaint"/>.
    /// Owned by <see cref="Hex1bApp"/>; set once when the app starts and
    /// cleared on dispose. Internal because the wiring is between the
    /// adapter and its owning Hex1bApp — external callers should go
    /// through <see cref="IRepaintableWorkloadAdapter.RequestFullRepaint"/>.
    /// </summary>
    internal void SetRepaintRequestHandler(Action? handler) => _repaintRequestHandler = handler;

    /// <inheritdoc />
    public void RequestFullRepaint() => _repaintRequestHandler?.Invoke();

    /// <summary>
    /// When true, Hex1bApp collects per-node timing metrics during reconcile and render.
    /// Set by the terminal builder when WithDiagnostics() is applied.
    /// </summary>
    internal bool DiagnosticTimingEnabled { get; set; }

    /// <summary>
    /// When true, <see cref="EnterTuiMode"/> emits the DECSET sequences to enable mouse
    /// tracking (1003) and SGR coordinates (1006), and <see cref="ExitTuiMode"/> emits
    /// the matching DECRST sequences. When false (default) the mouse-enable bytes are
    /// suppressed even if <see cref="TerminalCapabilities.SupportsMouse"/> is true.
    /// <para>
    /// This is important when the adapter is composed inside a host terminal
    /// (for example, as the placeholder inside a <c>TerminalWidget</c>) — unconditional
    /// mouse-enable would flip the host's mouse-tracking flag, which would then forward
    /// host mouse events into the embedded workload even after a later swap that has
    /// no opportunity to emit a DECRST.
    /// </para>
    /// Set by <see cref="Hex1bApp"/> from its <see cref="Hex1bAppOptions.EnableMouse"/>.
    /// </summary>
    public bool EnableMouse { get; set; }

    /// <summary>
    /// Creates a new app workload adapter.
    /// </summary>
    /// <param name="capabilities">Terminal capabilities. If null, defaults with full support.</param>
    /// <param name="maxQueuedOutputItems">
    /// Maximum number of pending output items (frame buffers, control sequences, etc.) the
    /// adapter will buffer before applying backpressure. <c>0</c> (default) means unbounded —
    /// producers never block, at the cost of unbounded memory growth if a slow consumer falls
    /// behind. A small positive value (e.g. <c>8</c>) caps memory and makes the render loop's
    /// effective frame rate track the consumer's drain rate. Drop semantics are deliberately
    /// not offered: each output item carries an incremental diff, so dropping a queued item
    /// would desynchronise the host terminal's surface state.
    /// </param>
    /// <remarks>
    /// Dimensions are set by the terminal via <see cref="ResizeAsync"/>.
    /// Initial dimensions default to 0x0 until the terminal notifies.
    /// </remarks>
    public Hex1bAppWorkloadAdapter(TerminalCapabilities? capabilities = null, int maxQueuedOutputItems = 0)
    {
        if (maxQueuedOutputItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedOutputItems), "Must be >= 0.");
        }
        ThrowIfBoundedChannelOnBrowser(maxQueuedOutputItems);

        _width = 0;
        _height = 0;
        _maxQueuedOutputItems = maxQueuedOutputItems;
        _staticCapabilities = capabilities ?? new TerminalCapabilities
        {
            SupportsMouse = true,
            Supports256Colors = true,
            SupportsTrueColor = true,
            SupportsStyledUnderlines = true,
            SupportsUnderlineColor = true,
        };

        _outputChannel = CreateOutputChannel(maxQueuedOutputItems);

        _inputChannel = Channel.CreateUnbounded<Hex1bEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    /// <summary>
    /// Creates a new app workload adapter connected to a presentation adapter.
    /// </summary>
    /// <param name="presentationAdapter">The presentation adapter to delegate capabilities to.</param>
    /// <param name="maxQueuedOutputItems">
    /// See <see cref="Hex1bAppWorkloadAdapter(TerminalCapabilities?, int)"/>.
    /// </param>
    /// <remarks>
    /// When a presentation adapter is provided, capabilities are read live from it,
    /// allowing updates to cell dimensions (e.g., after resize) to be reflected.
    /// </remarks>
    public Hex1bAppWorkloadAdapter(IHex1bTerminalPresentationAdapter presentationAdapter, int maxQueuedOutputItems = 0)
    {
        if (maxQueuedOutputItems < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxQueuedOutputItems), "Must be >= 0.");
        }
        ThrowIfBoundedChannelOnBrowser(maxQueuedOutputItems);

        _width = 0;
        _height = 0;
        _maxQueuedOutputItems = maxQueuedOutputItems;
        _presentationAdapter = presentationAdapter;
        _staticCapabilities = null;

        _outputChannel = CreateOutputChannel(maxQueuedOutputItems);

        _inputChannel = Channel.CreateUnbounded<Hex1bEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = true
        });
    }

    private static Channel<WorkloadOutputItem> CreateOutputChannel(int maxQueuedOutputItems)
    {
        if (maxQueuedOutputItems > 0)
        {
            return Channel.CreateBounded<WorkloadOutputItem>(new BoundedChannelOptions(maxQueuedOutputItems)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                // SingleWriter intentionally false: clipboard/paste/exit paths may
                // write from threads other than the render loop.
                SingleWriter = false,
            });
        }

        return Channel.CreateUnbounded<WorkloadOutputItem>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>
    /// Single-threaded WebAssembly cannot recover from the bounded-channel
    /// backpressure path in <see cref="EnqueueOutput"/>: that path blocks the
    /// producer with <c>writeTask.AsTask().GetAwaiter().GetResult()</c>, and the
    /// only thread that could drain the channel is the same one that's being
    /// blocked, so it deadlocks. Surface this as a clear construction-time error
    /// on browser hosts instead of an opaque hang at runtime.
    /// </summary>
    private static void ThrowIfBoundedChannelOnBrowser(int maxQueuedOutputItems)
    {
        if (maxQueuedOutputItems > 0 && OperatingSystem.IsBrowser())
        {
            throw new PlatformNotSupportedException(
                "Hex1bAppWorkloadAdapter does not support a bounded output channel " +
                "(maxQueuedOutputItems > 0) on browser/WebAssembly hosts. The bounded " +
                "backpressure path performs a sync-over-async wait that deadlocks " +
                "single-threaded WASM. Use the default (0, unbounded) on browser.");
        }
    }

    // ========================================
    // App-side APIs (used by Hex1bApp)
    // ========================================

    /// <summary>
    /// Write ANSI-encoded output to the terminal.
    /// </summary>
    public void Write(string text)
    {
        if (_disposed) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        EnqueueOutput(new WorkloadOutputItem(bytes, Tokens: null));
    }

    /// <summary>
    /// Write raw bytes to the terminal.
    /// </summary>
    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        EnqueueOutput(new WorkloadOutputItem(data.ToArray(), Tokens: null));
    }

    /// <summary>
    /// Write raw bytes to the terminal without forcing a copy.
    /// </summary>
    public void Write(ReadOnlyMemory<byte> data)
    {
        if (_disposed) return;
        EnqueueOutput(new WorkloadOutputItem(data, Tokens: null));
    }

    /// <summary>
    /// Writes output that the terminal must accept, throwing instead of silently
    /// dropping it when the adapter is disposed or its output channel is closed.
    /// </summary>
    /// <param name="text">The text to write.</param>
    /// <exception cref="ObjectDisposedException">The adapter has been disposed.</exception>
    /// <exception cref="ChannelClosedException">The output channel is closed.</exception>
    /// <remarks>
    /// Used by atomic hand-offs (for example Flow's history commit) where a silent
    /// drop would lose already-composed output. Ordinary rendering writes keep the
    /// best-effort <see cref="Write(string)"/> semantics and never throw during
    /// shutdown cleanup.
    /// </remarks>
    internal void WriteRequired(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnqueueOutput(new WorkloadOutputItem(Encoding.UTF8.GetBytes(text), Tokens: null), requireAcceptance: true);
    }

    /// <summary>
    /// The presentation adapter this workload delivers to, when one is attached.
    /// </summary>
    /// <remarks>
    /// Used to decide whether this workload's consumer can condition native
    /// delivery on the geometry the bytes were composed for.
    /// </remarks>
    internal IHex1bTerminalPresentationAdapter? PresentationAdapter => _presentationAdapter;

    /// <summary>
    /// Whether a composed batch can be refused before it is applied, so the delivery receipt
    /// reports what the terminal did with the batch rather than only that it was queued.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the terminal that owns the presentation and its filters, because that is where
    /// both enforcement points are known: a native presentation can refuse at write time
    /// against the device, while an in-process presentation can check and apply the batch under
    /// the model's resize lock before forwarding it. Both paths require every presentation
    /// filter to be an observer; a filter that can transform output keeps the ordinary route.
    /// </para>
    /// <para>
    /// A producer must consult this before offering a batch through
    /// <see cref="WriteRequiredIfGeometry"/>: when it is false the unconditional write path is
    /// the only correct one, and a gated delivery would fault instead of degrading.
    /// </para>
    /// </remarks>
    internal bool GeometryGatedDeliveryEnforceable { get; set; }

    /// <summary>
    /// Hands a composed batch to the terminal together with the geometry it was
    /// composed for, and reports what the presentation did with it.
    /// </summary>
    /// <remarks>
    /// Distinct from <see cref="WriteRequired"/> in what it acknowledges: that one
    /// returns once the batch is queued, which says nothing about whether the
    /// native presentation still had the geometry the bytes assume. The returned
    /// task completes only after the presentation has written the bytes or refused
    /// them, so a caller can recompose a refused batch and must never recompose an
    /// applied one.
    /// </remarks>
    /// <param name="text">The composed batch.</param>
    /// <param name="expectedWidth">Columns the batch was composed for.</param>
    /// <param name="expectedHeight">Rows the batch was composed for.</param>
    /// <returns>The delivery's outcome.</returns>
    internal Task<NativeDeliveryOutcome> WriteRequiredIfGeometry(
        string text,
        int expectedWidth,
        int expectedHeight)
    {
        ArgumentNullException.ThrowIfNull(text);
        var delivery = new GeometryGatedDelivery(expectedWidth, expectedHeight);

        lock (_barrierSync)
        {
            if (_disposed || _outputProcessingStopped)
            {
                throw new ObjectDisposedException(nameof(Hex1bAppWorkloadAdapter));
            }

            _pendingDeliveries.Add(delivery);
        }

        try
        {
            EnqueueOutput(
                new WorkloadOutputItem(Encoding.UTF8.GetBytes(text), Tokens: null)
                {
                    Delivery = delivery
                },
                requireAcceptance: true);
        }
        catch
        {
            lock (_barrierSync)
            {
                _pendingDeliveries.Remove(delivery);
            }

            delivery.Completion.TrySetException(
                new ObjectDisposedException(nameof(Hex1bAppWorkloadAdapter)));
            throw;
        }

        return delivery.Completion.Task;
    }

    /// <summary>
    /// Records the presentation's verdict on a gated batch.
    /// </summary>
    /// <remarks>
    /// Called by the terminal's output pump. <paramref name="outcome"/> is
    /// <see cref="NativeDeliveryOutcome.GeometryChanged"/> only when nothing was
    /// written.
    /// </remarks>
    internal void CompleteDelivery(GeometryGatedDelivery delivery, NativeDeliveryOutcome outcome)
    {
        lock (_barrierSync)
        {
            _pendingDeliveries.Remove(delivery);
        }

        delivery.Completion.TrySetResult(outcome);
    }

    /// <summary>
    /// Fails a gated batch that could not be offered to the presentation.
    /// </summary>
    /// <remarks>
    /// Always an error rather than a retryable outcome: the pump reaches this only
    /// when the batch could not be delivered as a whole, so whether any of it
    /// reached the device is not observable from here.
    /// </remarks>
    internal void FaultDelivery(GeometryGatedDelivery delivery, Exception error)
    {
        lock (_barrierSync)
        {
            _pendingDeliveries.Remove(delivery);
        }

        delivery.Completion.TrySetException(error);
    }

    /// <summary>
    /// Writes output with an already-tokenized representation to avoid terminal-side UTF-8 decode + tokenization.
    /// </summary>
    /// <param name="tokens">The tokens to ship to the consumer.</param>
    /// <param name="bytes">The serialised bytes of <paramref name="tokens"/>.</param>
    /// <param name="pooledBuffer">
    /// Optional array rented from <see cref="System.Buffers.ArrayPool{T}.Shared"/> that backs
    /// <paramref name="bytes"/>. When provided, the consumer side returns it to the pool after
    /// processing the item. If the write fails (channel closed), the buffer is returned here so
    /// callers don't have to worry about double-free.
    /// </param>
    /// <param name="pooledTokens">
    /// Optional list backing <paramref name="tokens"/> that should be returned to a pool after
    /// the consumer has finished with it. <paramref name="pooledTokensReturn"/> is the callback
    /// the consumer invokes to return it. Both must be supplied together or both null.
    /// </param>
    /// <param name="pooledTokensReturn">See <paramref name="pooledTokens"/>.</param>
    /// <param name="cancellationToken">Cancellation token for awaiting backpressure.</param>
    /// <returns>
    /// A completed <see cref="ValueTask"/> when the item was enqueued without contention;
    /// otherwise a task that completes once backpressure has cleared. The hot path
    /// (unbounded channel or bounded channel with capacity) allocates no async state machine.
    /// </returns>
    internal ValueTask WriteTokensWithBytesAsync(
        IReadOnlyList<AnsiToken> tokens,
        ReadOnlyMemory<byte> bytes,
        byte[]? pooledBuffer = null,
        List<AnsiToken>? pooledTokens = null,
        Action<List<AnsiToken>>? pooledTokensReturn = null,
        CancellationToken cancellationToken = default)
    {
        var item = new WorkloadOutputItem(bytes, tokens)
        {
            PooledBuffer = pooledBuffer,
            PooledTokens = pooledTokens,
            PooledTokensReturn = pooledTokensReturn,
        };
        if (_disposed)
        {
            ReturnPooledResources(item);
            return ValueTask.CompletedTask;
        }
        return EnqueueOutputAsync(item, cancellationToken);
    }

    /// <summary>
    /// Centralised enqueue for output items. Handles the bounded-channel
    /// backpressure case (block the producer until a slot frees) and ensures
    /// pooled resources carried by the item are returned to their pools on
    /// any failure path so callers can't leak them.
    /// </summary>
    /// <param name="item">The output item to enqueue.</param>
    /// <param name="requireAcceptance">
    /// When <see langword="true"/>, a rejected item (adapter disposed or channel
    /// closed) throws after its pooled resources are returned instead of being
    /// silently dropped. When <see langword="false"/> (default), rejection is the
    /// best-effort shutdown behaviour ordinary rendering relies on.
    /// </param>
    private void EnqueueOutput(WorkloadOutputItem item, bool requireAcceptance = false)
    {
        if (InputMilestones is not null)
        {
            _milestoneOutputGate.Wait();
            var accepted = false;
            try
            {
                accepted = EnqueueOutputCore(item with { MilestoneSequence = Interlocked.Increment(ref _milestoneOutputSequence) }, requireAcceptance);
            }
            finally
            {
                // A rejected item's sequence never reaches the pump; give it back so a frame's
                // output mark always names an item that was enqueued.
                if (!accepted)
                    Interlocked.Decrement(ref _milestoneOutputSequence);
                _milestoneOutputGate.Release();
            }

            return;
        }

        EnqueueOutputCore(item, requireAcceptance);
    }

    // Returns whether the item was enqueued.
    private bool EnqueueOutputCore(WorkloadOutputItem item, bool requireAcceptance)
    {
        if (_disposed)
        {
            ReturnPooledResources(item);
            if (requireAcceptance)
            {
                throw new ObjectDisposedException(nameof(Hex1bAppWorkloadAdapter));
            }
            return false;
        }

        // Fast path: unbounded channel always accepts; bounded channel
        // accepts when capacity is available.
        if (_outputChannel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _outputQueueDepth);
            return true;
        }

        // Slow path: bounded channel is full. Block the producer until a
        // slot frees. This is the deliberate backpressure: if the consumer
        // can't keep up, the producer's effective frame rate falls to match
        // the consumer's drain rate, which is exactly what we want.
        //
        // sync-over-async is acceptable here: the producer is the render
        // loop (or a control-sequence writer), already inside a Task-based
        // async chain, and slowing it down is the goal.
        try
        {
            var writeTask = _outputChannel.Writer.WriteAsync(item);
            if (!writeTask.IsCompletedSuccessfully)
                writeTask.AsTask().GetAwaiter().GetResult();
            Interlocked.Increment(ref _outputQueueDepth);
            return true;
        }
        catch (ChannelClosedException)
        {
            // Channel completed while we were waiting; return pooled
            // resources so they aren't leaked.
            ReturnPooledResources(item);
            if (requireAcceptance)
            {
                throw;
            }
            return false;
        }
        catch (InvalidOperationException)
        {
            // Same: channel may surface a writer-completed state as IOE.
            ReturnPooledResources(item);
            if (requireAcceptance)
            {
                throw;
            }
            return false;
        }
    }

    private static void ReturnPooledResources(WorkloadOutputItem item)
    {
        if (item.PooledBuffer is not null)
            System.Buffers.ArrayPool<byte>.Shared.Return(item.PooledBuffer);
        if (item.PooledTokens is not null && item.PooledTokensReturn is not null)
            item.PooledTokensReturn(item.PooledTokens);
    }

    /// <summary>
    /// Async counterpart of <see cref="EnqueueOutput"/>. Awaits a slot instead of
    /// blocking the producer thread when the bounded channel is full — required by
    /// callers hosted under single-threaded sync contexts where sync-over-async would
    /// deadlock.
    /// </summary>
    private async ValueTask EnqueueOutputAsync(WorkloadOutputItem item, CancellationToken cancellationToken)
    {
        if (InputMilestones is not null)
        {
            await _milestoneOutputGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            var accepted = false;
            try
            {
                accepted = await EnqueueOutputCoreAsync(
                    item with { MilestoneSequence = Interlocked.Increment(ref _milestoneOutputSequence) }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                if (!accepted)
                    Interlocked.Decrement(ref _milestoneOutputSequence);
                _milestoneOutputGate.Release();
            }

            return;
        }

        await EnqueueOutputCoreAsync(item, cancellationToken).ConfigureAwait(false);
    }

    // Returns whether the item was enqueued.
    private async ValueTask<bool> EnqueueOutputCoreAsync(WorkloadOutputItem item, CancellationToken cancellationToken)
    {
        if (_outputChannel.Writer.TryWrite(item))
        {
            Interlocked.Increment(ref _outputQueueDepth);
            return true;
        }

        try
        {
            await _outputChannel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _outputQueueDepth);
            return true;
        }
        catch (ChannelClosedException)
        {
            ReturnPooledResources(item);
        }
        catch (InvalidOperationException)
        {
            ReturnPooledResources(item);
        }
        catch (OperationCanceledException)
        {
            ReturnPooledResources(item);
            throw;
        }

        return false;
    }

    /// <summary>
    /// Flush any buffered output.
    /// </summary>
    public void Flush()
    {
        // Channel-based, no buffering needed
    }

    /// <summary>
    /// Channel of parsed input events from the terminal.
    /// </summary>
    public ChannelReader<Hex1bEvent> InputEvents => _inputChannel.Reader;

    /// <summary>
    /// Current terminal width.
    /// </summary>
    public int Width => _width;

    /// <summary>
    /// Current terminal height.
    /// </summary>
    public int Height => _height;

    /// <summary>
    /// Reads the current native presentation dimensions when the presentation
    /// itself exposes a live geometry source. Synthetic/static presentations
    /// use this adapter's dimensions, including the latest admitted model resize.
    /// A cursor observation fences behind that resize before its position is usable.
    /// Static presentation Width/Height properties are not resize authorities.
    /// </summary>
    /// <remarks>
    /// Native terminals can apply a resize before their input event reaches this
    /// workload adapter. Flow calls this at its final serialized emission
    /// boundary so cursor placement and width-sensitive bytes use the same
    /// dimensions the native presentation currently reports. Model resize admission
    /// publishes its requested geometry atomically with enqueue; the output consumer
    /// publishes the applied dimensions and notifies the live step afterward.
    /// </remarks>
    (int Width, int Height) Hex1b.Flow.IFlowCurrentGeometrySource.ReadCurrentGeometry()
    {
        if (_presentationAdapter is Hex1b.Flow.IFlowCurrentGeometrySource source)
        {
            return source.ReadCurrentGeometry();
        }

        var requested = Volatile.Read(ref _requestedGeometry);
        return requested == 0
            ? (_width, _height)
            : ((int)(requested >> 32), (int)requested);
    }
    /// <summary>
    /// Terminal capabilities. Returns live capabilities from presentation adapter if available,
    /// otherwise returns the static capabilities provided at construction.
    /// </summary>
    public TerminalCapabilities Capabilities =>
        _presentationAdapter?.Capabilities ?? _staticCapabilities ?? TerminalCapabilities.Modern;

    /// <summary>
    /// Gets the number of output items waiting to be consumed by the terminal.
    /// Can be used to detect back pressure and adjust input processing accordingly.
    /// </summary>
    public int OutputQueueDepth => _outputQueueDepth;

    /// <summary>
    /// Enter TUI mode. Writes standard ANSI sequences for alternate screen, hide cursor, enable mouse.
    /// </summary>
    public void EnterTuiMode()
    {
        if (_inTuiMode) return;
        _inTuiMode = true;

        // Standard TUI mode sequences
        var sb = new StringBuilder();
        sb.Append("\x1b[?1049h");  // Enter alternate screen
        sb.Append("\x1b[?25l");    // Hide cursor
        if (EnableMouse && Capabilities.SupportsMouse)
        {
            sb.Append("\x1b[?1003h");  // Enable mouse tracking
            sb.Append("\x1b[?1006h");  // SGR mouse mode
        }
        if (Capabilities.SupportsBracketedPaste)
        {
            sb.Append("\x1b[?2004h");  // Enable bracketed paste mode
        }
        Write(sb.ToString());
    }

    /// <summary>
    /// Exit TUI mode. Restores terminal state.
    /// </summary>
    public void ExitTuiMode()
    {
        if (!_inTuiMode) return;
        _inTuiMode = false;

        var sb = new StringBuilder();
        if (Capabilities.SupportsBracketedPaste)
        {
            sb.Append("\x1b[?2004l");  // Disable bracketed paste mode
        }
        if (EnableMouse && Capabilities.SupportsMouse)
        {
            sb.Append("\x1b[?1006l");  // Disable SGR mouse mode
            sb.Append("\x1b[?1003l");  // Disable mouse tracking
        }
        sb.Append("\x1b[0m");      // Reset text attributes (prevents inverted text from leaking)
        sb.Append("\x1b[?25h");    // Show cursor
        sb.Append("\x1b[?1049l");  // Exit alternate screen
        Write(sb.ToString());
    }

    /// <summary>
    /// Clear the screen.
    /// </summary>
    public void Clear()
    {
        Write("\x1b[2J\x1b[H");
    }

    /// <summary>
    /// Set the cursor position.
    /// </summary>
    public void SetCursorPosition(int left, int top)
    {
        Write($"\x1b[{top + 1};{left + 1}H");
    }

    // ========================================
    // Terminal-side APIs (IHex1bTerminalWorkloadAdapter)
    // ========================================

    /// <summary>
    /// Tries to read output without blocking. Returns true if data was available.
    /// </summary>
    /// <param name="data">The output data if available, or empty if not.</param>
    /// <returns>True if data was available, false otherwise.</returns>
    public bool TryReadOutput(out ReadOnlyMemory<byte> data)
    {
        data = ReadOnlyMemory<byte>.Empty;
        if (_disposed) return false;

        if (_outputChannel.Reader.TryRead(out var item))
        {
            Interlocked.Decrement(ref _outputQueueDepth);
            data = item.Bytes;
            return true;
        }
        return false;
    }

    internal bool TryReadOutputItem(out WorkloadOutputItem item)
    {
        item = default;
        if (_disposed) return false;

        if (_outputChannel.Reader.TryRead(out item))
        {
            Interlocked.Decrement(ref _outputQueueDepth);
            return true;
        }

        return false;
    }

    /// <inheritdoc />
    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
    {
        var item = await ReadOutputItemAsync(ct);
        return item.Bytes;
    }

    /// <inheritdoc />
    public async ValueTask<WorkloadOutputItem> ReadOutputItemAsync(CancellationToken ct = default)
    {
        if (_disposed) return default;

        try
        {
            if (await _outputChannel.Reader.WaitToReadAsync(ct))
            {
                if (_outputChannel.Reader.TryRead(out var item))
                {
                    Interlocked.Decrement(ref _outputQueueDepth);
                    return item;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }
        catch (ChannelClosedException)
        {
            // Channel completed
        }

        return default;
    }

    /// <inheritdoc />
    public async ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disposed) return;

        // Parse raw bytes into events and write to input channel
        // For now, we assume the terminal has already parsed bytes into events
        // and calls WriteInputEventAsync instead

        // If we receive raw bytes, we need to parse them
        // This is a simplified version - full parsing is in Hex1bTerminal
        var text = Encoding.UTF8.GetString(data.Span);
        foreach (var c in text)
        {
            var evt = ParseKeyInput(c);
            if (evt != null)
            {
                await WriteInputTrackedAsync(evt, ct);
            }
        }
    }

    /// <summary>
    /// Write a parsed input event directly (used by Hex1bTerminal after parsing).
    /// </summary>
    public ValueTask WriteInputEventAsync(Hex1bEvent evt, CancellationToken ct = default)
    {
        if (_disposed) return ValueTask.CompletedTask;
        return WriteInputTrackedAsync(evt, ct);
    }

    /// <summary>
    /// Write a parsed input event directly (synchronous).
    /// </summary>
    public bool TryWriteInputEvent(Hex1bEvent evt)
    {
        if (_disposed) return false;
        return TryWriteInput(evt);
    }

    /// <summary>
    /// Orders a terminal-owned model resize with accepted output. Admission must
    /// not block the output pump, which can itself raise a resize notification.
    /// </summary>
    internal async Task<bool> QueueResizeAsync(int? width, int? height, CancellationToken ct = default)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_barrierSync)
        {
            if (_disposed || _outputProcessingStopped)
                return false;
            _pendingBarriers.Add(completion);
        }
        try
        {
            await OutputGeometryGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (_disposed || _outputProcessingStopped)
                    return false;
                // Resolve partial diagnostics requests under the same admission gate.
                // Applied dimensions may lag an earlier, already-admitted resize.
                var requested = Volatile.Read(ref _requestedGeometry);
                var targetWidth = width ?? (requested == 0 ? _width : (int)(requested >> 32));
                var targetHeight = height ?? (requested == 0 ? _height : (int)requested);
                await EnqueueOutputAsync(new WorkloadOutputItem(ReadOnlyMemory<byte>.Empty, null)
                {
                    Resize = (targetWidth, targetHeight),
                    ProcessingBarrier = completion
                }, ct).ConfigureAwait(false);
                if (!_disposed && !_outputProcessingStopped)
                    Volatile.Write(ref _requestedGeometry, ((long)(uint)targetWidth << 32) | (uint)targetHeight);
            }
            finally
            {
                OutputGeometryGate.Release();
            }
            return await completion.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_barrierSync)
            {
                _pendingBarriers.Remove(completion);
            }
        }
    }

    /// <inheritdoc />
    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        Volatile.Write(ref _requestedGeometry, 0);
        return ApplyQueuedResize(width, height);
    }

    // Does not overwrite requested geometry: a later resize may already be admitted.
    internal ValueTask ApplyQueuedResize(int width, int height)
    {
        var wasInitialized = _dimensionsInitialized;
        _dimensionsInitialized = true;

        var changed = _width != width || _height != height;
        _width = width;
        _height = height;

        // Only fire resize event if dimensions changed AND we were already initialized
        // (skip the initial dimension setup from terminal constructor)
        if (changed && wasInitialized)
        {
            TryWriteInput(new Hex1bResizeEvent(width, height));
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public event Action? Disconnected;

    // ========================================
    // Testing APIs
    // ========================================

    /// <summary>
    /// Injects a key input event (for testing).
    /// </summary>
    public void SendKey(ConsoleKey key, char keyChar = '\0', bool shift = false, bool alt = false, bool control = false)
    {
        var evt = KeyMapper.ToHex1bKeyEvent(key, keyChar, shift, alt, control);
        TryWriteInput(evt);
    }

    /// <summary>
    /// Injects a key input event using Hex1bKey (for testing).
    /// </summary>
    public void SendKey(Hex1bKey key, char keyChar = '\0', Hex1bModifiers modifiers = Hex1bModifiers.None)
    {
        var evt = new Hex1bKeyEvent(key, keyChar, modifiers);
        TryWriteInput(evt);
    }

    /// <summary>
    /// Injects a mouse input event (for testing).
    /// </summary>
    public void SendMouse(MouseButton button, MouseAction action, int x, int y, Hex1bModifiers modifiers = Hex1bModifiers.None, int clickCount = 1)
    {
        var evt = new Hex1bMouseEvent(button, action, x, y, modifiers, clickCount);
        TryWriteInput(evt);
    }

    /// <summary>
    /// Types a string of characters (for testing).
    /// </summary>
    public void TypeText(string text)
    {
        foreach (var c in text)
        {
            var key = CharToConsoleKey(c);
            var shift = char.IsUpper(c);
            SendKey(key, c, shift: shift);
        }
    }

    // ========================================
    // Private helpers
    // ========================================

    private static Hex1bKeyEvent? ParseKeyInput(char c)
    {
        return c switch
        {
            '\0' => new Hex1bKeyEvent(Hex1bKey.Spacebar, c, Hex1bModifiers.Control), // Ctrl+Space sends NUL
            '\r' or '\n' => new Hex1bKeyEvent(Hex1bKey.Enter, c, Hex1bModifiers.None),
            '\t' => new Hex1bKeyEvent(Hex1bKey.Tab, c, Hex1bModifiers.None),
            '\x1b' => new Hex1bKeyEvent(Hex1bKey.Escape, c, Hex1bModifiers.None),
            '\x7f' or '\b' => new Hex1bKeyEvent(Hex1bKey.Backspace, c, Hex1bModifiers.None),
            ' ' => new Hex1bKeyEvent(Hex1bKey.Spacebar, c, Hex1bModifiers.None),
            >= 'a' and <= 'z' => new Hex1bKeyEvent(
                KeyMapper.ToHex1bKey((ConsoleKey)((int)ConsoleKey.A + (c - 'a'))), c, Hex1bModifiers.None),
            >= 'A' and <= 'Z' => new Hex1bKeyEvent(
                KeyMapper.ToHex1bKey((ConsoleKey)((int)ConsoleKey.A + (c - 'A'))), c, Hex1bModifiers.Shift),
            >= '0' and <= '9' => new Hex1bKeyEvent(
                KeyMapper.ToHex1bKey((ConsoleKey)((int)ConsoleKey.D0 + (c - '0'))), c, Hex1bModifiers.None),
            '+' or '=' => new Hex1bKeyEvent(Hex1bKey.OemPlus, c, Hex1bModifiers.None),
            '-' => new Hex1bKeyEvent(Hex1bKey.OemMinus, c, Hex1bModifiers.None),
            ',' => new Hex1bKeyEvent(Hex1bKey.OemComma, c, Hex1bModifiers.None),
            '.' => new Hex1bKeyEvent(Hex1bKey.OemPeriod, c, Hex1bModifiers.None),
            '/' or '?' => new Hex1bKeyEvent(Hex1bKey.OemQuestion, c, Hex1bModifiers.None),
            _ when !char.IsControl(c) => new Hex1bKeyEvent(Hex1bKey.None, c, Hex1bModifiers.None),
            _ => null
        };
    }

    private static ConsoleKey CharToConsoleKey(char c)
    {
        return c switch
        {
            >= 'a' and <= 'z' => (ConsoleKey)((int)ConsoleKey.A + (c - 'a')),
            >= 'A' and <= 'Z' => (ConsoleKey)((int)ConsoleKey.A + (c - 'A')),
            >= '0' and <= '9' => (ConsoleKey)((int)ConsoleKey.D0 + (c - '0')),
            ' ' => ConsoleKey.Spacebar,
            '\r' or '\n' => ConsoleKey.Enter,
            '\t' => ConsoleKey.Tab,
            '\b' => ConsoleKey.Backspace,
            _ => ConsoleKey.NoName
        };
    }

    // ========================================
    // Cursor position observation
    // ========================================

    /// <inheritdoc />
    async Task<(int Column, int Row)?> ICursorPositionSource.ObserveCursorPositionAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // The terminal model is only an authority for a headless terminal; a native
        // presentation (when attached) is the sole authority and its failure is never
        // replaced by that model.
        var nativeSource = _presentationAdapter as ICursorPositionSource;
        var headlessProvider = HeadlessCursorProvider;
        if (nativeSource is null && headlessProvider is null)
        {
            return null;
        }

        // Fence FIRST: only once everything this caller queued before the call has been
        // applied can either source be asked for a position, otherwise a native query
        // could observe a cursor that predates the caller's own writes.
        if (!await TryAwaitOutputBarrierAsync(cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }

        if (nativeSource is not null)
        {
            return await nativeSource.ObserveCursorPositionAsync(cancellationToken).ConfigureAwait(false);
        }

        return headlessProvider!();
    }

    /// <summary>
    /// How long an observation waits for its output barrier before giving up with a
    /// <see langword="null"/> observation.
    /// </summary>
    private static readonly TimeSpan CursorObservationBarrierTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// Enqueues an empty barrier item and waits until the terminal's output pump has
    /// consumed it, proving every item enqueued before it has already been applied.
    /// </summary>
    /// <returns>
    /// <see langword="true"/> when the barrier was consumed; <see langword="false"/> when
    /// it could not be enqueued or was not consumed within
    /// <see cref="CursorObservationBarrierTimeout"/> (including adapter disposal) — the
    /// caller then reports no observation rather than a stale model. Cancellation throws.
    /// </returns>
    private async Task<bool> TryAwaitOutputBarrierAsync(CancellationToken cancellationToken)
    {
        var barrier = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var item = new WorkloadOutputItem(ReadOnlyMemory<byte>.Empty, Tokens: null)
        {
            ProcessingBarrier = barrier
        };

        lock (_barrierSync)
        {
            _pendingBarriers.Add(barrier);
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(CursorObservationBarrierTimeout);

        try
        {
            if (!await EnqueueBarrierAsync(item, timeoutCts.Token).ConfigureAwait(false))
            {
                return false;
            }

            return await barrier.Task.WaitAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw; // Caller cancellation is not a bounded-failure outcome.
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        finally
        {
            lock (_barrierSync)
            {
                _pendingBarriers.Remove(barrier);
            }
        }
    }

    /// <summary>
    /// Enqueues a barrier item, returning <see langword="false"/> when the adapter is
    /// disposed or the channel is closed instead of throwing.
    /// </summary>
    private async ValueTask<bool> EnqueueBarrierAsync(WorkloadOutputItem item, CancellationToken cancellationToken)
    {
        if (_disposed)
        {
            return false;
        }

        try
        {
            await _outputChannel.Writer.WriteAsync(item, cancellationToken).ConfigureAwait(false);
            Interlocked.Increment(ref _outputQueueDepth);
            return true;
        }
        catch (ChannelClosedException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    internal void CompleteOutputProcessing()
    {
        lock (_barrierSync)
        {
            _outputProcessingStopped = true;
            foreach (var barrier in _pendingBarriers)
                barrier.TrySetResult(false);
            _pendingBarriers.Clear();

            // A batch that was queued but never offered to the presentation was
            // never written, so it must fail loudly instead of resolving to an
            // outcome a producer could mistake for a refusal it may retry.
            foreach (var delivery in _pendingDeliveries)
            {
                delivery.Completion.TrySetException(
                    new ObjectDisposedException(nameof(Hex1bAppWorkloadAdapter)));
            }

            _pendingDeliveries.Clear();
        }
        // Release bounded writers too: their consumer can no longer make space.
        _outputChannel.Writer.TryComplete();
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        CompleteOutputProcessing();

        // Note: ExitTuiMode is NOT called here because Hex1bTerminal handles
        // writing mouse-disable and screen-restore sequences directly to the
        // presentation layer during its disposal to avoid race conditions.

        _inputChannel.Writer.TryComplete();
        InputMilestones?.Terminate("input-closed", "The application input channel was closed.");
        Disconnected?.Invoke();
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }
}
