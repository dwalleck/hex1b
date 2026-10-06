namespace Hex1b.Input;

// One shared transport lifetime across the terminal, parent queue and each Flow step.
// It does not retain paste operations or application receivers.
internal sealed class OrderedInputState(int capacity)
{
    private Exception? _failure;
    private readonly object _flowSync = new();
    private FlowEpoch? _flowEpoch;
    private bool _firstFlowOwner = true;
    private bool _flowOwnerActive;
    private bool _inputEnded;
    private readonly TaskCompletionSource _inputDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task InputDrained => _inputDrained.Task;
    internal void CompleteInput()
    {
        lock (_flowSync)
        {
            _inputEnded = true;
            // Before the first step, admitted startup input retains its reserved owner.
            if (!_flowOwnerActive && !_firstFlowOwner) _inputDrained.TrySetResult();
        }
    }
    internal bool IsFlow { get; private set; }
    internal void EnableFlow()
    {
        lock (_flowSync) { IsFlow = true; _flowEpoch = new FlowEpoch(); }
    }
    internal FlowEpoch? CaptureFlowEpoch() { lock (_flowSync) return _flowEpoch; }
    internal FlowEpoch? BeginFlowOwner()
    {
        lock (_flowSync)
        {
            if (!IsFlow) return null;
            _flowOwnerActive = true;
            if (_firstFlowOwner) _firstFlowOwner = false;
            else _flowEpoch = new FlowEpoch();
            return _flowEpoch;
        }
    }
    internal void EndFlowOwner(FlowEpoch? epoch)
    {
        if (epoch is null) return;
        lock (_flowSync)
        {
            epoch.Close();
            if (ReferenceEquals(_flowEpoch, epoch))
            {
                _flowEpoch = null;
                _flowOwnerActive = false;
                if (_inputEnded) _inputDrained.TrySetResult();
            }
        }
    }
    internal sealed class FlowEpoch
    {
        private int _closed;
        internal bool IsClosed => Volatile.Read(ref _closed) != 0;
        internal void Close() => Interlocked.Exchange(ref _closed, 1);
    }
    internal int Capacity { get; } = capacity;
    internal Exception? Failure => Volatile.Read(ref _failure);
    internal void Fail(Exception error) => Interlocked.CompareExchange(ref _failure, error, null);
}
