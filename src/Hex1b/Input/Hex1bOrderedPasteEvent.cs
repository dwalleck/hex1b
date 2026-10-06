using Hex1b.Events;

namespace Hex1b.Input;

// A null update is Begin. All phases share the ordinary ordered input channel.
internal sealed record Hex1bOrderedPasteEvent(OrderedPasteStart Start, OrderedPasteUpdate? Update = null) : Hex1bEvent;
