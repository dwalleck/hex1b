using System.Threading.Channels;

namespace Hex1b;

public partial class Hex1bApp
{
    private readonly Queue<DispatchOperation> _dispatchQueue = new();
    private readonly Channel<bool> _dispatchWakeup = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true
    });
    private readonly int _dispatchQueueCapacity;
    private bool _dispatchAccepting;

    /// <summary>Queues a short synchronous callback on the application's serialized input/render loop.</summary>
    /// <param name="callback">A short synchronous state transition. Never supply an async-void callback.</param>
    /// <param name="completion">For accepted work, completes after the callback returns, faults with its exception,
    /// or is canceled if cancellation is observed before execution. For refused work, a completed placeholder.</param>
    /// <param name="cancellationToken">Cancels work before it starts; cannot interrupt a running callback.</param>
    /// <returns>Accepted, QueueFull (retry after progress), or NotRunning (not started, stopping or disposed).</returns>
    /// <exception cref="ArgumentNullException">The callback is null.</exception>
    /// <remarks>
    /// Callbacks never run inline. They must not block on I/O, dispatch completion or frame-dependent operations
    /// such as Flow commitment. Launch asynchronous effects outside the callback and dispatch their short results.
    /// Completion acknowledges execution, not rendering or native presentation. Callback failure does not stop
    /// the app. Accepted callbacks still pending when the run ends fault with InvalidOperationException.
    /// A pre-canceled token on a running app produces an accepted, canceled completion without occupying the queue.
    /// Await completions outside input/render callbacks; waiting there would prevent this loop from progressing.
    /// </remarks>
    public Hex1bDispatchAdmission Dispatch(Action callback, out Task completion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(callback);
        lock (_runLifecycleSync)
        {
            completion = Task.CompletedTask;
            if (!_dispatchAccepting || _stopRequested || _disposeRequested)
                return Hex1bDispatchAdmission.NotRunning;
            if (cancellationToken.IsCancellationRequested)
            {
                completion = Task.FromCanceled(cancellationToken);
                return Hex1bDispatchAdmission.Accepted;
            }
            if (_dispatchQueue.Count >= _dispatchQueueCapacity)
                return Hex1bDispatchAdmission.QueueFull;
            var operation = new DispatchOperation(callback, cancellationToken);
            _dispatchQueue.Enqueue(operation);
            completion = operation.Completion.Task;
            _dispatchWakeup.Writer.TryWrite(true);
            return Hex1bDispatchAdmission.Accepted;
        }
    }

    private void DrainDispatch(CancellationToken runCancellation)
    {
        // Limit each turn, including callbacks that enqueue successors, so input and rendering get a turn.
        for (var count = 0; count < 32; count++)
        {
            DispatchOperation operation;
            lock (_runLifecycleSync)
            {
                if (_stopRequested || _disposeRequested || runCancellation.IsCancellationRequested || !_dispatchQueue.TryDequeue(out operation!))
                    break;
            }
            if (operation.CancellationToken.IsCancellationRequested)
                operation.Completion.TrySetCanceled(operation.CancellationToken);
            else
            {
                try
                {
                    operation.Callback();
                    operation.Completion.TrySetResult();
                }
                catch (Exception error) { operation.Completion.TrySetException(error); }
            }
        }
        lock (_runLifecycleSync)
            if (_dispatchQueue.Count != 0) _dispatchWakeup.Writer.TryWrite(true);
    }

    private void CloseDispatch()
    {
        DispatchOperation[] pending;
        lock (_runLifecycleSync)
        {
            _dispatchAccepting = false;
            pending = _dispatchQueue.ToArray();
            _dispatchQueue.Clear();
        }
        foreach (var operation in pending)
        {
            if (operation.CancellationToken.IsCancellationRequested)
                operation.Completion.TrySetCanceled(operation.CancellationToken);
            else
                operation.Completion.TrySetException(new InvalidOperationException("The application stopped before the callback could execute."));
        }
    }

    private sealed class DispatchOperation(Action callback, CancellationToken cancellationToken)
    {
        public Action Callback { get; } = callback;
        public CancellationToken CancellationToken { get; } = cancellationToken;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
