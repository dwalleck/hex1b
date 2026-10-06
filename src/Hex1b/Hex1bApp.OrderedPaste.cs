using Hex1b.Events;
using Hex1b.Input;
using Hex1b.Nodes;
using Hex1b.Widgets;

namespace Hex1b;

public partial class Hex1bApp
{
    private int InputDrainBudget => _adapter switch
    {
        Hex1bAppWorkloadAdapter { OrderedPasteCapacity: > 0 } => 64,
        Hex1b.Flow.InlineStepAdapter { OrderedPasteCapacity: > 0 } => 64,
        _ => int.MaxValue
    };

    private OrderedInputState.FlowEpoch? _orderedFullScreenEpoch;
    private OrderedPasteStart? _orderedPasteStart;
    private Action<OrderedPasteUpdate>? _orderedPasteReceiver;
    private Hex1bNode? _orderedPasteTarget;
    private TextBoxState? _orderedPasteState;

    private void ProcessOrderedPaste(Hex1bOrderedPasteEvent input)
    {
        if (input.Start.LostFlowOwner ||
            (_adapter is Hex1b.Flow.InlineStepAdapter inline && input.Start.IsFlowInput &&
             !ReferenceEquals(input.Start.FlowEpoch, inline.OrderedEpoch)) ||
            (_adapter is Hex1bAppWorkloadAdapter && input.Start.IsFlowInput &&
             !ReferenceEquals(input.Start.FlowEpoch, _orderedFullScreenEpoch)))
        {
            input.Start.Cancel();
            _inputMilestones?.Abandoned(input, "ordered paste lost its captured Flow input owner");
            return;
        }
        if (input.Update is null)
        {
            EndOrderedPaste(OrderedPastePhase.Cancelled);
            if (input.Start.IsCancellationRequested || _rootNode is null) { input.Start.Cancel(); return; }
            try
            {
                var path = InputRouter.OrderedPastePath(_rootNode);
                for (var index = path.Count - 1; index >= 0; index--)
                {
                    var node = path[index];
                    if (node.BeginOrderedPaste(input.Start) is not { } receiver) continue;
                    _orderedPasteStart = input.Start;
                    input.Start.SetOwnerWake(Invalidate);
                    _orderedPasteReceiver = receiver;
                    _orderedPasteTarget = node;
                    node.OrderedPasteDetaching = () => EndOrderedPaste(OrderedPastePhase.Cancelled);
                    _orderedPasteState = (node as TextBoxNode)?.State;
                    if (node is TextBoxNode textBox)
                        textBox.OrderedPasteStateChanging = () => EndOrderedPaste(OrderedPastePhase.Cancelled);
                    if (input.Start.IsCancellationRequested) EndOrderedPaste(OrderedPastePhase.Cancelled);
                    return;
                }
                input.Start.Cancel();
            }
            catch { input.Start.Cancel(); throw; }
            return;
        }
        if (!ReferenceEquals(input.Start, _orderedPasteStart)) return;
        if (input.Start.IsCancellationRequested) { EndOrderedPaste(OrderedPastePhase.Cancelled); return; }
        if (input.Update.Phase != OrderedPastePhase.Chunk)
        {
            EndOrderedPaste(input.Update.Phase, input.Update.Error);
            return;
        }
        try
        {
            _orderedPasteReceiver!(input.Update);
            _orderedPasteTarget?.MarkDirty();
            if (input.Start.IsCancellationRequested) EndOrderedPaste(OrderedPastePhase.Cancelled);
        }
        catch (Exception error)
        {
            try { EndOrderedPaste(OrderedPastePhase.Failed, error); }
            catch (Exception terminalFailure) { throw new AggregateException(error, terminalFailure); }
            throw;
        }
    }

    private void EndOrderedPaste(OrderedPastePhase phase, Exception? error = null)
    {
        var start = _orderedPasteStart;
        var receiver = _orderedPasteReceiver;
        // Detach first: a failing terminal callback can never receive a second outcome.
        _orderedPasteStart = null;
        _orderedPasteReceiver = null;
        if (_orderedPasteTarget is { } target) target.OrderedPasteDetaching = null;
        if (_orderedPasteTarget is TextBoxNode textBox) textBox.OrderedPasteStateChanging = null;
        _orderedPasteTarget = null;
        _orderedPasteState = null;
        if (start is null) return;
        start.SetOwnerWake(null);
        var transportFailure = _adapter switch
        {
            Hex1bAppWorkloadAdapter parent => parent.OrderedInput?.Failure,
            Hex1b.Flow.InlineStepAdapter inline => inline.OrderedInput?.Failure,
            _ => null
        };
        // A processed End is final. Transport failure only explains an incomplete shutdown.
        if (phase != OrderedPastePhase.Completed && start.Error is { } sourceFailure)
        { phase = OrderedPastePhase.Failed; error = sourceFailure; }
        else if (phase == OrderedPastePhase.Shutdown && transportFailure is not null)
        { phase = OrderedPastePhase.Failed; error = transportFailure; }
        if (phase != OrderedPastePhase.Completed) start.Cancel();
        try { receiver!(new OrderedPasteUpdate(phase, error: error)); }
        finally { Invalidate(); }
    }

    private void ValidateOrderedPasteTarget()
    {
        if (_orderedPasteTarget is not { } target) return;
        if (_orderedPasteStart!.IsCancellationRequested ||
            (target is TextBoxNode textBox && !ReferenceEquals(textBox.State, _orderedPasteState)) ||
            _rootNode is null || !ContainsOrderedPasteTarget(_rootNode, target))
            EndOrderedPaste(OrderedPastePhase.Cancelled);
    }

    private static bool ContainsOrderedPasteTarget(Hex1bNode node, Hex1bNode target)
        => ReferenceEquals(node, target) || node.GetChildren().Any(child => ContainsOrderedPasteTarget(child, target));
}
