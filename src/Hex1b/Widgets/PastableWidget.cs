using Hex1b.Events;
using Hex1b.Input;
using Hex1b.Nodes;

namespace Hex1b.Widgets;

/// <summary>
/// A container widget that intercepts bracketed paste events for all descendants.
/// When paste data arrives and no child handles it, the <see cref="PasteHandler"/> receives the 
/// <see cref="PasteContext"/> for streaming processing.
/// </summary>
/// <example>
/// <code>
/// ctx.Pastable(
///     child: ctx.VStack(v => [
///         v.Text("Drop zone"),
///         v.TextBlock($"Received: {bytesReceived} bytes"),
///     ]),
///     onPaste: async paste =>
///     {
///         await foreach (var chunk in paste.ReadChunksAsync())
///         {
///             bytesReceived += chunk.Length;
///         }
///     }
/// )
/// </code>
/// </example>
public sealed record PastableWidget(Hex1bWidget Child) : Hex1bWidget
{
    /// <summary>
    /// The async paste handler. Called when a bracketed paste event bubbles up to this container.
    /// </summary>
    internal Func<PasteEventArgs, Task>? PasteHandler { get; init; }

    /// <summary>
    /// Maximum number of characters to accept. If exceeded, the paste is auto-cancelled.
    /// Null means unlimited.
    /// </summary>
    internal int? MaxPasteSize { get; init; }

    /// <summary>
    /// Maximum duration for the paste operation. If exceeded, the paste is auto-cancelled.
    /// Null means no timeout.
    /// </summary>
    internal TimeSpan? PasteTimeout { get; init; }

    /// <summary>
    /// Sets the async paste handler that receives streaming paste data.
    /// </summary>
    public PastableWidget OnPaste(Func<PasteEventArgs, Task> handler)
        => this with { PasteHandler = handler };

    /// <summary>
    /// Sets the async paste handler using a synchronous action.
    /// </summary>
    public PastableWidget OnPaste(Action<PasteEventArgs> handler)
        => this with { PasteHandler = e => { handler(e); return Task.CompletedTask; } };

    internal Func<OrderedPasteStart, Action<OrderedPasteUpdate>?>? OrderedPasteFactory { get; init; }

    /// <summary>Captures a synchronous receiver once per ordered paste that descendants decline.</summary>
    /// <param name="begin">Called on the input owner. Return a receiver for this paste, or null
    /// to let an ancestor handle it.</param>
    /// <returns>A widget configured with the factory.</returns>
    /// <exception cref="ArgumentNullException">The factory is null.</exception>
    /// <remarks>
    /// <para>Requires <see cref="Hex1bTerminalBuilder.WithOrderedPasteInput"/>. Routing visits the
    /// focused descendant before this container. An explicit receiver owns processing and limits;
    /// this container does not buffer chunks or call its legacy OnPaste handler in ordered mode.
    /// MaxSize and Timeout configure legacy streaming paste only: combining either option with an
    /// explicit ordered factory throws InvalidOperationException at paste Begin, before calling
    /// the factory. Legacy streaming input continues to use OnPaste and its configured limits.</para>
    /// <para>The factory captures once, including for an empty paste. Literal chunks are followed
    /// by exactly one terminal update. Completed means the closing delimiter and preceding chunks
    /// were consumed, not that a frame has been published. Start.Cancel requests cancellation;
    /// terminal notification runs on the input owner. No cancellation rolls back or replays data.</para>
    /// <para>Moving focus or replacing the factory during reconciliation does not retarget an
    /// existing receiver. Detaching this container reports Cancelled; replacing a descendant's
    /// state does not itself cancel this container's receiver. The receiver owns any additional
    /// captured-state validity checks. Application or Flow shutdown reports Shutdown, or Failed
    /// when a failure cause is known. Callback failures retain the existing ordered-input failure
    /// and cleanup behavior; a factory that throws before returning has no receiver to notify.</para>
    /// </remarks>
    /// <example>
    /// <code>
    /// using Hex1b;
    /// using Hex1b.Documents;
    /// using Hex1b.Widgets;
    ///
    /// var document = new EditorState(new Hex1bDocument("Read-only content")) { IsReadOnly = true };
    /// var notice = "Paste is discarded here.";
    /// await using var terminal = Hex1bTerminal.CreateBuilder()
    ///     .WithOrderedPasteInput(64)
    ///     .WithHex1bApp(_ =&gt; new VStackWidget([
    ///         new TextBlockWidget(notice),
    ///         new PastableWidget(new EditorWidget(document)).OnOrderedPaste(start =&gt;
    ///         {
    ///             notice = "Paste received; document unchanged.";
    ///             return update =&gt; { }; // No text buffer is needed to discard chunks.
    ///         }),
    ///         new ButtonWidget("Quit").OnClick(e =&gt; e.Context.RequestStop())
    ///     ]))
    ///     .Build();
    /// await terminal.RunAsync();
    /// </code>
    /// </example>
    public PastableWidget OnOrderedPaste(Func<OrderedPasteStart, Action<OrderedPasteUpdate>?> begin)
        => this with { OrderedPasteFactory = begin ?? throw new ArgumentNullException(nameof(begin)) };

    /// <summary>
    /// Sets the maximum legacy streaming paste size in characters. If exceeded, the paste is auto-cancelled.
    /// Cannot be combined with an explicit ordered-paste receiver.
    /// </summary>
    public PastableWidget MaxSize(int maxCharacters)
        => this with { MaxPasteSize = maxCharacters };

    /// <summary>
    /// Sets a timeout for legacy streaming paste. If exceeded, the paste is auto-cancelled.
    /// Cannot be combined with an explicit ordered-paste receiver.
    /// </summary>
    public PastableWidget Timeout(TimeSpan timeout)
        => this with { PasteTimeout = timeout };

    internal override async Task<Hex1bNode> ReconcileAsync(Hex1bNode? existingNode, ReconcileContext context)
    {
        var node = existingNode as PastableNode ?? new PastableNode();
        node.Child = await context.ReconcileChildAsync(node.Child, Child, node);
        node.PasteAction = PasteHandler;
        node.OrderedPasteFactory = OrderedPasteFactory;
        node.MaxSize = MaxPasteSize;
        node.PasteTimeout = PasteTimeout;
        return node;
    }

    internal override Type GetExpectedNodeType() => typeof(PastableNode);
}
