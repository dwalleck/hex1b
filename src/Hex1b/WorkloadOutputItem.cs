using Hex1b.Tokens;

namespace Hex1b;

/// <summary>
/// Represents a chunk of output read from a workload.
/// </summary>
/// <remarks>
/// <para>
/// Workloads normally expose output as raw UTF-8 bytes via
/// <see cref="IHex1bTerminalWorkloadAdapter.ReadOutputAsync"/>, which the terminal then
/// decodes and tokenizes.
/// </para>
/// <para>
/// Some workloads can also provide a pre-tokenized representation of that same output.
/// When <see cref="Tokens"/> is provided, <see cref="Hex1bTerminal"/> can skip UTF-8 decode and
/// <see cref="AnsiTokenizer"/> tokenization, reducing allocations and CPU.
/// </para>
/// </remarks>
/// <param name="Bytes">The raw UTF-8 output bytes from the workload.</param>
/// <param name="Tokens">
/// Optional pre-tokenized representation of <paramref name="Bytes"/>. When provided, it should
/// represent the same output as <paramref name="Bytes"/>.
/// </param>
public readonly record struct WorkloadOutputItem(
    ReadOnlyMemory<byte> Bytes,
    IReadOnlyList<AnsiToken>? Tokens)
{
    /// <summary>
    /// Optional pooled array backing <see cref="Bytes"/>. When non-null, the terminal returns
    /// it to <see cref="System.Buffers.ArrayPool{T}.Shared"/> after the bytes have been fully
    /// forwarded. This is an internal mechanism used by the in-process Hex1bApp ↔ Hex1bTerminal
    /// bridge to avoid per-frame LOH allocations for large ANSI output buffers (a busy
    /// fullscreen frame is well over the 85KB LOH threshold). External workload adapters
    /// should not set this — they should manage any pooling internally and present
    /// <see cref="Bytes"/> as a stable window valid until their next read call.
    /// </summary>
    internal byte[]? PooledBuffer { get; init; }

    /// <summary>
    /// Optional pooled token list backing <see cref="Tokens"/>. When non-null, the terminal
    /// returns it to the originating <see cref="Hex1bApp"/> via
    /// <see cref="Hex1bApp.ReturnTokenList"/> after consumption. Same purpose and constraints
    /// as <see cref="PooledBuffer"/>: this is an internal in-process mechanism and external
    /// workload adapters should leave it null.
    /// </summary>
    internal List<AnsiToken>? PooledTokens { get; init; }

    /// <summary>
    /// Internal callback the consumer invokes to return <see cref="PooledTokens"/> to its
    /// owning pool. Decoupled from <see cref="Hex1bApp"/> to avoid a circular type reference.
    /// </summary>
    internal Action<List<AnsiToken>>? PooledTokensReturn { get; init; }

    /// <summary>
    /// Optional completion barrier. Empty observation items complete on consumption,
    /// after all preceding output has been applied. Resize items complete after their
    /// own model update and workload/filter notifications, propagating notification errors.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The output channel has a single reader that consumes items in order, so a consumed
    /// barrier proves every item enqueued before it has already been fully applied to the
    /// terminal. A cursor observation enqueues an empty item carrying this barrier and
    /// awaits it, which gives an exact FIFO fence over its own queued writes without any
    /// counter arithmetic or per-item bookkeeping: the only cost on ordinary items is the
    /// null check that completes them.
    /// </para>
    /// <para>
    /// The terminal completes it with <see langword="true"/>. Adapter disposal and the
    /// observation's own deadline complete it with <see langword="false"/>, meaning "not
    /// known to be processed" — never that it was.
    /// </para>
    /// </remarks>
    internal TaskCompletionSource<bool>? ProcessingBarrier { get; init; }

    /// <summary>
    /// In-process model resize, ordered after preceding output application and
    /// before subsequent observation barriers. Only the owning terminal enqueues it.
    /// </summary>
    internal (int Width, int Height)? Resize { get; init; }
}

