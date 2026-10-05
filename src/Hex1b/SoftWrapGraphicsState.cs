using System.Text;
using Hex1b.Kgp;
using Hex1b.Layout;
using Hex1b.Tokens;

namespace Hex1b;

/// <summary>
/// Delivered graphics for one live owner. A private candidate remains separate
/// until the processing receipt settles; failed writes retain both identities.
/// </summary>
internal sealed class SoftWrapGraphicsState
{
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private KgpPlacementTracker _accepted = new();
    private KgpPlacementTracker? _candidate;
    private (int Width, int Height)? _acceptedGeometry;
    private (int Width, int Height) _candidateGeometry;
    private bool _failed;

    internal bool RequiresProcessingReceipt => _accepted.HasTransmittedImages
        || (_candidate?.HasTransmittedImages ?? false);

    internal async ValueTask<IDisposable> AcquireAsync(CancellationToken ct = default)
    {
        await _operationGate.WaitAsync(ct).ConfigureAwait(false);
        return new Lease(_operationGate);
    }

    internal string PrepareFrame(SoftWrapRenderFrame frame, int rowOrigin, int width, int height)
    {
        BeginCandidate(width, height);
        var cleanup = string.Empty;
        if (_acceptedGeometry is { } geometry && geometry != (width, height))
        {
            cleanup = Serialize(_candidate!.GenerateOwnedDeletionCommands(freeData: true));
            _candidate.Reset();
        }

        var (before, after) = _candidate!.GenerateCommands(OffsetFragments(ClipFragments(frame.Graphics, frame.Width, frame.Height), rowOrigin));
        // Cleanup belongs inside BSU, before resize clears in the frame prefix.
        if (!frame.Prefix.StartsWith(SoftWrapRenderFrame.SynchronizedBegin, StringComparison.Ordinal))
            throw new InvalidOperationException("A graphics frame must start with synchronized update begin.");
        var output = new StringBuilder(frame.Text.Length + cleanup.Length + 256);
        output.Append(SoftWrapRenderFrame.SynchronizedBegin).Append(cleanup);
        output.Append(frame.Prefix.AsSpan(SoftWrapRenderFrame.SynchronizedBegin.Length));
        output.Append(Serialize(before)).Append(frame.Body).Append(Serialize(after)).Append(frame.Suffix);
        return output.ToString();
    }

    internal string PrepareRelocation(int width, int height, bool invalidateUploads)
    {
        BeginCandidate(width, height);
        var freeData = invalidateUploads || (_acceptedGeometry is { } geometry && geometry != (width, height));
        var cleanup = Serialize(_candidate!.GenerateOwnedDeletionCommands(freeData));
        if (freeData) _candidate.Reset();
        else _candidate.ResetPlacements();
        return cleanup;
    }

    internal (string BeforeText, string AfterText) PreparePlacements(IReadOnlyList<KgpFragment> graphics, int rowOrigin, int width, int height)
    {
        var candidate = _candidate ?? throw new InvalidOperationException("Relocation requires an active graphics candidate.");
        var (before, after) = candidate.GenerateCommands(OffsetFragments(ClipFragments(graphics, width, height), rowOrigin));
        return (Serialize(before), Serialize(after));
    }

    internal void Complete(NativeDeliveryOutcome outcome)
    {
        if (_candidate is not { } candidate)
            throw new InvalidOperationException("No graphics delivery is awaiting a receipt.");
        if (outcome == NativeDeliveryOutcome.Applied)
        {
            _accepted = candidate;
            _acceptedGeometry = _candidateGeometry;
        }
        _candidate = null;
    }

    internal void DeliveryFailed() => _failed = true;

    internal string PrepareOwnedCleanup()
    {
        // Temporary union is bounded by accepted and the one uncertain candidate.
        // No discarded frame adds identities to a persistent ledger.
        var commands = _accepted.GenerateOwnedDeletionCommands(freeData: true);
        if (_candidate is { } candidate)
            commands.AddRange(candidate.GenerateOwnedDeletionCommands(freeData: true));
        return Serialize(commands.OfType<UnrecognizedSequenceToken>()
            .DistinctBy(token => token.Sequence).Cast<AnsiToken>().ToList());
    }

    internal void CleanupCompleted()
    {
        _accepted.Reset();
        _candidate = null;
        _acceptedGeometry = null;
    }

    private void BeginCandidate(int width, int height)
    {
        if (_failed || _candidate is not null)
            throw new InvalidOperationException("An unresolved graphics delivery prevents another update.");
        _candidate = _accepted.Clone();
        _candidateGeometry = (width, height);
    }

    private static IReadOnlyList<KgpFragment> ClipFragments(IReadOnlyList<KgpFragment> graphics, int width, int height)
    {
        var result = new List<KgpFragment>(graphics.Count);
        foreach (var fragment in graphics)
        {
            var left = Math.Max(0, fragment.AbsoluteX);
            var top = Math.Max(0, fragment.AbsoluteY);
            var right = Math.Min(width, fragment.AbsoluteX + fragment.CellWidth);
            var bottom = Math.Min(height, fragment.AbsoluteY + fragment.CellHeight);
            if (right <= left || bottom <= top) continue;
            if (left == fragment.AbsoluteX && top == fragment.AbsoluteY
                && right == fragment.AbsoluteX + fragment.CellWidth
                && bottom == fragment.AbsoluteY + fragment.CellHeight)
            {
                result.Add(fragment);
                continue;
            }
            var data = fragment.Data.WithClip(fragment.ClipX, fragment.ClipY,
                fragment.ClipW, fragment.ClipH, fragment.CellWidth, fragment.CellHeight);
            if (data.UsesNativeSize)
            {
                var clipped = data.ClipNativeToCells(left - fragment.AbsoluteX, top - fragment.AbsoluteY, right - left, bottom - top);
                if (clipped is not null)
                    result.Add(new KgpFragment(clipped.ImageId, left, top, right - left, bottom - top,
                        clipped.ClipX, clipped.ClipY, clipped.ClipW, clipped.ClipH, clipped));
            }
            else
            {
                var rect = new Rect(fragment.AbsoluteX, fragment.AbsoluteY, fragment.CellWidth, fragment.CellHeight);
                result.Add(KgpOcclusionSolver.CreateFragment(new KgpImageEntry(data, rect.X, rect.Y, 0),
                    rect, new Rect(left, top, right - left, bottom - top)));
            }
        }
        return result;
    }

    private static List<KgpFragment> OffsetFragments(IReadOnlyList<KgpFragment> graphics, int rowOrigin)
    {
        var fragments = new List<KgpFragment>(graphics.Count);
        foreach (var fragment in graphics)
            fragments.Add(fragment with { AbsoluteY = fragment.AbsoluteY + rowOrigin });
        return fragments;
    }

    private static string Serialize(IReadOnlyList<AnsiToken> commands)
        => commands.Count == 0 ? string.Empty : Encoding.UTF8.GetString(AnsiTokenUtf8Serializer.Serialize(commands).Span);

    private sealed class Lease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }
}
