using Hex1b.Kgp;
using Hex1b.Surfaces;

namespace Hex1b;

/// <summary>A detached surface and its desired graphics from the same render.</summary>
internal sealed record LiveRenderSnapshot(Surface Surface, IReadOnlyList<KgpFragment> Graphics);
