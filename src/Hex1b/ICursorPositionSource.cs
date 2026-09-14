namespace Hex1b;

/// <summary>
/// A source that can observe an authoritative cursor position.
/// </summary>
/// <remarks>
/// <para>
/// Implemented by <see cref="Hex1bAppWorkloadAdapter"/> (which forwards to an
/// attached native presentation, or to the terminal's own applied model when the
/// terminal is headless) and directly by <see cref="ConsolePresentationAdapter"/>
/// (the native host cursor). Faithful recording/replay adapters may implement it
/// too. Flow prefers a source over any modeled position, and a <see langword="null"/>
/// result means "no authoritative anchor" — never a cue to fall back to a model.
/// </para>
/// <para>
/// The returned position is 0-based <c>(Column, Row)</c>, matching
/// <see cref="IHex1bTerminalPresentationAdapter.GetCursorPosition"/>. On a native
/// presentation the observation is serviced by that presentation's existing input
/// reader — the sole owner of stdin on Unix — so no competing reader is ever opened.
/// </para>
/// </remarks>
internal interface ICursorPositionSource
{
    /// <summary>
    /// Observes the cursor position.
    /// </summary>
    /// <param name="cancellationToken">
    /// Cancels the observation. Cancellation is surfaced as
    /// <see cref="OperationCanceledException"/> rather than a <see langword="null"/>
    /// result, so callers can distinguish "no answer" from "we were told to stop".
    /// </param>
    /// <returns>
    /// The observed 0-based <c>(Column, Row)</c>, or <see langword="null"/> when the
    /// position cannot be authoritatively observed within the bounded deadline.
    /// </returns>
    Task<(int Column, int Row)?> ObserveCursorPositionAsync(CancellationToken cancellationToken);
}
