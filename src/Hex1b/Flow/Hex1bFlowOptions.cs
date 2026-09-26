using System.Text;
using Hex1b.Input;
using Hex1b.Layout;
using Hex1b.Surfaces;
using Hex1b.Theming;
using Hex1b.Widgets;

namespace Hex1b.Flow;

/// <summary>
/// Options for the Hex1b Flow system.
/// </summary>
public sealed class Hex1bFlowOptions
{
    /// <summary>
    /// Theme for all steps and full-screen apps in the flow.
    /// </summary>
    public Hex1bTheme? Theme { get; set; }

    /// <summary>
    /// Presentation-owned host profile provider. The builder binds this to a
    /// lazy source so a profile is read only after the presentation's startup
    /// capability probe has completed.
    /// </summary>
    internal Func<FlowTerminalHostProfile>? HostProfileProvider { get; set; }

    /// <summary>
    /// Whether to enable mouse input for full-screen steps.
    /// </summary>
    public bool EnableMouse { get; set; }

    /// <summary>
    /// Initial cursor row (0-based) where the flow starts rendering.
    /// Set this to <c>Console.GetCursorPosition().Top</c> before calling RunAsync.
    /// If null, defaults to 0.
    /// </summary>
    public int? InitialCursorRow { get; set; }

    /// <summary>
    /// Optional delegate that returns the host terminal's current cursor row
    /// (0-based). When set, the flow runner uses this to read the initial
    /// cursor position at startup, before the input pump is running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Implemented on top of <c>Console.GetCursorPosition()</c> by
    /// <c>Hex1bTerminalBuilder.WithHex1bFlow</c> when a presentation adapter
    /// is available. On Windows this resolves through Win32
    /// <c>GetConsoleScreenBufferInfo</c> (no DSR roundtrip); on Unix the BCL
    /// uses a raw DSR query that reads the response from stdin.
    /// </para>
    /// <para>
    /// <strong>Must not be called from within a resize handler.</strong>
    /// On Unix, calling this while Hex1b's input pump is running causes a
    /// deadlock: the DSR response arrives on the same stdin file descriptor
    /// that the input pump owns, so <c>Console.GetCursorPosition()</c>
    /// blocks forever waiting for bytes it will never receive.
    /// </para>
    /// <para>
    /// Returns <c>null</c> to indicate the cursor row could not be
    /// determined.
    /// </para>
    /// </remarks>
    public Func<int?>? CursorRowProvider { get; set; }

    /// <summary>
    /// When <c>true</c>, tombstoned (yield) widget output is emitted as
    /// soft-wrap-friendly logical lines (text + <c>ESC[K</c> + LF) so the host
    /// terminal can reflow tombstones during a resize and scroll older
    /// tombstones into the scrollback buffer naturally. When <c>false</c>
    /// (the default), tombstones are emitted with absolute cursor positioning
    /// and the entire visible area is cleared on every resize, which causes
    /// tombstones to disappear when the terminal is resized.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This option is experimental and gates two related behaviours together:
    /// the tombstone emission path and the resize handler. The new resize
    /// handler only clears the active step region and trusts the host
    /// terminal to have reflowed tombstones above it — that is only a valid
    /// assumption when tombstones were emitted as proper logical lines, so
    /// both behaviours move together under this single flag.
    /// </para>
    /// <para>
    /// Once validated across the supported terminal matrix this will become
    /// the default and the legacy cell-positioning path will be removed.
    /// </para>
    /// </remarks>
    public bool UseSoftWrapTombstones { get; set; }

    /// <summary>
    /// Optional quiet window after the last <c>Hex1bResizeEvent</c> used only
    /// for final resize cleanup. Every resize still publishes the current
    /// geometry and repaints the live prompt immediately; this delay does not
    /// debounce prompt visibility or hold the live region blank.
    /// When <c>null</c> (the default), the resize path restores its transient
    /// ownership as soon as the immediate repaint completes.
    /// </summary>
    /// <remarks>
    /// Only takes effect when <see cref="UseSoftWrapTombstones"/> is also
    /// <c>true</c>: the cleanup path relies on tombstones above the active step
    /// being hard-newline-terminated paragraphs that the host terminal will not
    /// reflow across paragraph boundaries. Recommended value: 50–100 ms.
    /// </remarks>
    public TimeSpan? ResizeSettleDelay { get; set; }

}
