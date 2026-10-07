using System.Collections.Concurrent;
using System.Runtime.CompilerServices;

namespace Hex1b;

/// <summary>
/// Runs an asynchronous loop on one dedicated background thread. Work posted to this context runs on
/// that thread, so an await that resumes through it does not wait for a thread-pool thread.
/// </summary>
/// <remarks>
/// Issue 61: a large paste on a 2-CPU host starved the thread pool, and the terminal's output pump,
/// which resumed on the pool, consumed a Flow cursor barrier only after its 250 ms bound. The loop
/// installs this context only where it must not wait for the pool (see the output pump). Work posted
/// after the loop has finished still runs, on the thread pool.
/// </remarks>
internal sealed class DedicatedThreadSynchronizationContext : SynchronizationContext
{
    private readonly BlockingCollection<(SendOrPostCallback Callback, object? State)> _work = new();
    private readonly Func<DedicatedThreadSynchronizationContext, Task> _loop;
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread _thread;

    private DedicatedThreadSynchronizationContext(string name, Func<DedicatedThreadSynchronizationContext, Task> loop)
    {
        _loop = loop;
        _thread = new Thread(Run) { IsBackground = true, Name = name };
    }

    /// <summary>The context's thread (for tests).</summary>
    internal Thread Thread => _thread;

    /// <summary>Whether the caller runs on this context's thread.</summary>
    public bool IsCurrentThread => Thread.CurrentThread == _thread;

    /// <summary>
    /// Starts <paramref name="loop"/> on a new background thread with this context installed. The
    /// returned task completes with the loop's outcome once the thread has stopped taking work.
    /// </summary>
    public static Task Start(string name, Func<DedicatedThreadSynchronizationContext, Task> loop)
    {
        var context = new DedicatedThreadSynchronizationContext(name, loop);
        context._thread.Start(); // flows the creator's ExecutionContext, as Task.Run did
        return context._completion.Task;
    }

    /// <summary>An awaitable that resumes on this context's thread (at once when already on it).</summary>
    public SwitchAwaitable SwitchTo() => new(this);

    public override void Post(SendOrPostCallback d, object? state)
    {
        ArgumentNullException.ThrowIfNull(d);
        if (!_work.IsAddingCompleted)
        {
            try
            {
                _work.Add((d, state));
                return;
            }
            catch (InvalidOperationException)
            {
                // The loop finished between the check and the add.
            }
        }

        ThreadPool.UnsafeQueueUserWorkItem(static work => work.Callback(work.State), (Callback: d, State: state), preferLocal: false);
    }

    public override SynchronizationContext CreateCopy() => this;

    private void Run()
    {
        SetSynchronizationContext(this);
        Task loop;
        try
        {
            loop = _loop(this);
        }
        catch (Exception error)
        {
            loop = Task.FromException(error);
        }

        // Stop taking work once the loop has finished, wherever its last step ran.
        loop.ContinueWith(static (_, work) => ((BlockingCollection<(SendOrPostCallback, object?)>)work!).CompleteAdding(),
            _work, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);

        try
        {
            foreach (var (callback, state) in _work.GetConsumingEnumerable())
            {
                SetSynchronizationContext(this);
                callback(state);
            }
        }
        finally
        {
            // A callback that throws ends the thread (and, unhandled, the process, as on the pool); the
            // loop's completion is still published.
            if (!loop.IsCompleted)
                _completion.TrySetException(new InvalidOperationException("The dedicated loop thread ended before its loop."));
            else if (loop.IsFaulted)
                _completion.TrySetException(loop.Exception!.InnerExceptions);
            else if (loop.IsCanceled)
                _completion.TrySetCanceled(CanceledToken(loop));
            else
                _completion.TrySetResult();
        }
    }

    private static CancellationToken CanceledToken(Task canceled)
    {
        try
        {
            canceled.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException error)
        {
            return error.CancellationToken;
        }
        return default;
    }

    /// <summary>Resumes the awaiting method on the context's thread.</summary>
    internal readonly struct SwitchAwaitable : ICriticalNotifyCompletion
    {
        private readonly DedicatedThreadSynchronizationContext _context;

        public SwitchAwaitable(DedicatedThreadSynchronizationContext context) => _context = context;

        public SwitchAwaitable GetAwaiter() => this;

        public bool IsCompleted => _context.IsCurrentThread;

        public void GetResult()
        {
        }

        public void OnCompleted(Action continuation) => _context.Post(static action => ((Action)action!)(), continuation);

        public void UnsafeOnCompleted(Action continuation) => OnCompleted(continuation);
    }
}
