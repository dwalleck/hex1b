using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using Hex1b.Input;

namespace Hex1b.Flow;

/// <summary>
/// Workload adapter for inline step rendering. Renders in the normal terminal buffer
/// without entering the alternate screen. All cursor positioning is offset by the
/// step's row origin in the terminal.
/// </summary>
internal enum InlineOutputFrameKind
{
    Data,
    DiscardBoundary,
}

internal readonly record struct InlineOutputFrame(
    byte[] Bytes,
    long Epoch,
    InlineOutputFrameKind Kind);
internal sealed partial class InlineStepAdapter : IHex1bAppTerminalWorkloadAdapter, IDisposable
{

    private readonly Channel<InlineOutputFrame> _outputChannel;
    private readonly Channel<Hex1bEvent> _inputChannel;
    private readonly TerminalCapabilities _capabilities;
    private readonly object _outputGenerationSync = new();
    private int _width;
    private int _height;
    private int _rowOrigin;
    private long _outputEpoch;
    private bool _disposed;
    private int _outputQueueDepth;
    private bool _inTuiMode;

    // Regex to match ANSI CUP sequences: ESC [ row ; col H  or  ESC [ row H  or  ESC [ H
    [GeneratedRegex(@"\x1b\[(\d*)(;(\d*))?(H|f)", RegexOptions.Compiled)]
    private static partial Regex CursorPositionRegex();

    // Regex to match clear screen: ESC [ 2 J
    [GeneratedRegex(@"\x1b\[2J")]
    private static partial Regex ClearScreenRegex();

    public InlineStepAdapter(int width, int height, int rowOrigin, TerminalCapabilities? capabilities = null)
    {
        _width = width;
        _height = height;
        _rowOrigin = rowOrigin;
        _capabilities = capabilities ?? new TerminalCapabilities
        {
            SupportsMouse = false,
            Supports256Colors = true,
            SupportsTrueColor = true,
        };

        _outputChannel = Channel.CreateUnbounded<InlineOutputFrame>(new UnboundedChannelOptions
        {
            SingleReader = false,
            SingleWriter = false
        });

        _inputChannel = Channel.CreateUnbounded<Hex1bEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
    }
    public int RowOrigin
    {
        get => _rowOrigin;
        set
        {
            lock (_outputGenerationSync)
            {
                if (_rowOrigin == value)
                    return;

                _rowOrigin = value;
                Interlocked.Increment(ref _outputEpoch);
            }
        }
    }
    // === App-side APIs ===
    public void Write(string text)
    {
        if (_disposed) return;

        lock (_outputGenerationSync)
        {
            // Rewrite and tag under the same lock as RowOrigin, ResizeAsync,
            // and DiscardQueuedOutput so a frame can never carry bytes laid out
            // for one origin with another origin's generation.
            var rewritten = RewriteCursorPositions(text);
            var bytes = Encoding.UTF8.GetBytes(rewritten);
            if (_outputChannel.Writer.TryWrite(
                    new InlineOutputFrame(
                        bytes,
                        Volatile.Read(ref _outputEpoch),
                        InlineOutputFrameKind.Data)))
            {
                Interlocked.Increment(ref _outputQueueDepth);
            }
        }
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        if (_disposed) return;
        // Convert to string for ANSI rewriting, then back to bytes
        var text = Encoding.UTF8.GetString(data);
        Write(text);
    }

    public void Flush() { }

    public ChannelReader<Hex1bEvent> InputEvents => _inputChannel.Reader;
    public int Width => _width;
    public int Height => _height;
    public TerminalCapabilities Capabilities => _capabilities;
    public int OutputQueueDepth => _outputQueueDepth;
    internal long OutputEpoch => Volatile.Read(ref _outputEpoch);

    /// <summary>
    /// Discards every data frame already queued for the terminal, returning how
    /// many data items were dropped. A typed boundary sentinel is then queued so
    /// the output pump also invalidates a frame whose BSU was already removed
    /// before the pump read it.
    /// </summary>
    internal int DiscardQueuedOutput()
    {
        lock (_outputGenerationSync)
        {
            // Invalidate a frame already dequeued by the pump, and tag the
            // sentinel plus every later write as the new generation.
            var epoch = Interlocked.Increment(ref _outputEpoch);
            var dropped = 0;
            while (_outputChannel.Reader.TryRead(out var queued))
            {
                Interlocked.Decrement(ref _outputQueueDepth);
                if (queued.Kind == InlineOutputFrameKind.Data)
                {
                    dropped++;
                }
            }

            // The sentinel is a control item, not a data frame: keep it out of
            // the returned discard count while tracking it in queue depth.
            if (!_disposed
                && _outputChannel.Writer.TryWrite(
                    new InlineOutputFrame(
                        Array.Empty<byte>(),
                        epoch,
                        InlineOutputFrameKind.DiscardBoundary)))
            {
                Interlocked.Increment(ref _outputQueueDepth);
            }

            return dropped;
        }
    }

    /// <summary>
    /// Enter TUI mode for inline step — hides cursor and enables mouse if supported.
    /// Does NOT enter alternate screen.
    /// </summary>
    public void EnterTuiMode()
    {
        lock (_outputGenerationSync)
        {
            if (_disposed || _inTuiMode) return;
            _inTuiMode = true;

            var sb = new StringBuilder();
            sb.Append("\x1b[?25l"); // Hide cursor
            if (_capabilities.SupportsMouse)
            {
                sb.Append("\x1b[?1003h"); // Enable mouse tracking (all motion)
                sb.Append("\x1b[?1006h"); // Enable SGR mouse mode
            }
            if (_capabilities.SupportsBracketedPaste)
            {
                sb.Append("\x1b[?2004h"); // Enable bracketed paste mode
            }
            WriteRaw(sb.ToString());
        }
    }

    public void ExitTuiMode()
    {
        lock (_outputGenerationSync)
        {
            if (_disposed || !_inTuiMode) return;
            _inTuiMode = false;

            var sb = new StringBuilder();
            if (_capabilities.SupportsBracketedPaste)
            {
                sb.Append("\x1b[?2004l"); // Disable bracketed paste mode
            }
            if (_capabilities.SupportsMouse)
            {
                sb.Append("\x1b[?1006l"); // Disable SGR mouse mode
                sb.Append("\x1b[?1003l"); // Disable mouse tracking
            }
            sb.Append("\x1b[0m");   // Reset text attributes
            sb.Append("\x1b[?25h"); // Show cursor
            sb.Append($"\x1b[{_rowOrigin + _height + 1};1H");
            WriteRaw(sb.ToString());
        }
    }

    /// <summary>
    /// Clear the step region only (not the full screen).
    /// </summary>
    public void Clear()
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return;
            var sb = new StringBuilder();
            for (int row = 0; row < _height; row++)
            {
                sb.Append($"\x1b[{_rowOrigin + row + 1};1H");
                sb.Append("\x1b[2K");
            }
            WriteRaw(sb.ToString());
        }
    }

    /// <summary>
    /// Set cursor position with row offset applied.
    /// </summary>
    public void SetCursorPosition(int left, int top)
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return;
            WriteRaw($"\x1b[{_rowOrigin + top + 1};{left + 1}H");
        }
    }

    // === Terminal-side APIs ===

    public async ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        => (await ReadOutputFrameAsync(ct).ConfigureAwait(false)).Bytes;

    internal async ValueTask<InlineOutputFrame> ReadOutputFrameAsync(CancellationToken ct = default)
    {
        if (_disposed)
        {
            return new InlineOutputFrame(
                Array.Empty<byte>(),
                OutputEpoch,
                InlineOutputFrameKind.Data);
        }

        try
        {
            if (await _outputChannel.Reader.WaitToReadAsync(ct).ConfigureAwait(false)
                && _outputChannel.Reader.TryRead(out var frame))
            {
                Interlocked.Decrement(ref _outputQueueDepth);
                return frame;
            }
        }
        catch (OperationCanceledException) { }
        catch (ChannelClosedException) { }

        return new InlineOutputFrame(
            Array.Empty<byte>(),
            OutputEpoch,
            InlineOutputFrameKind.Data);
    }

    public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        // Raw byte input is not used for flow steps — events arrive via WriteInputEventAsync
        return ValueTask.CompletedTask;
    }

    public ValueTask WriteInputEventAsync(Hex1bEvent evt, CancellationToken ct = default)
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return ValueTask.CompletedTask;

            // Translate mouse coordinates from absolute terminal space to step-relative space
            if (evt is Hex1bMouseEvent mouse)
            {
                var relativeY = mouse.Y - _rowOrigin;
                if (relativeY < 0 || relativeY >= _height)
                    return ValueTask.CompletedTask;
                evt = mouse with { Y = relativeY };
            }

            return _inputChannel.Writer.WriteAsync(evt, ct);
        }
    }

    public bool TryWriteInputEvent(Hex1bEvent evt)
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return false;

            if (evt is Hex1bMouseEvent mouse)
            {
                var relativeY = mouse.Y - _rowOrigin;
                if (relativeY < 0 || relativeY >= _height)
                    return false;
                evt = mouse with { Y = relativeY };
            }

            return _inputChannel.Writer.TryWrite(evt);
        }
    }

    public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default)
    {
        // Resize changes the layout generation before the inner app receives
        // its event. Any frame already composed for the old dimensions is
        // therefore rejected by the runner if it is dequeued after this point.
        lock (_outputGenerationSync)
        {
            Interlocked.Increment(ref _outputEpoch);
            _width = width;
            _height = height;
            _inputChannel.Writer.TryWrite(new Hex1bResizeEvent(width, height));
        }

        return ValueTask.CompletedTask;
    }

    public event Action? Disconnected;

    internal void RaiseDisconnected() => Disconnected?.Invoke();

    public void Dispose()
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return;
            _disposed = true;
            _outputChannel.Writer.TryComplete();
            _inputChannel.Writer.TryComplete();
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    // === ANSI Rewriting ===

    /// <summary>
    /// Write raw bytes without ANSI rewriting (for sequences we generate ourselves).
    /// </summary>
    private void WriteRaw(string text)
    {
        lock (_outputGenerationSync)
        {
            if (_disposed) return;
            var bytes = Encoding.UTF8.GetBytes(text);
            if (_outputChannel.Writer.TryWrite(
                    new InlineOutputFrame(
                        bytes,
                        Volatile.Read(ref _outputEpoch),
                        InlineOutputFrameKind.Data)))
            {
                Interlocked.Increment(ref _outputQueueDepth);
            }
        }
    }

    /// <summary>
    /// Rewrites ANSI cursor position sequences to apply the row offset,
    /// and converts full-screen clears to step-region clears.
    /// </summary>
    private string RewriteCursorPositions(string text)
    {
        if (_rowOrigin == 0) return text;

        // Replace clear screen (ESC[2J) with step-region clear
        text = ClearScreenRegex().Replace(text, _ =>
        {
            var sb = new StringBuilder();
            for (int row = 0; row < _height; row++)
            {
                sb.Append($"\x1b[{_rowOrigin + row + 1};1H");
                sb.Append("\x1b[2K");
            }
            return sb.ToString();
        });

        // Rewrite CUP sequences: ESC[row;colH → ESC[row+offset;colH
        text = CursorPositionRegex().Replace(text, match =>
        {
            var rowStr = match.Groups[1].Value;
            var colStr = match.Groups[3].Value;
            var suffix = match.Groups[4].Value; // H or f

            int row = string.IsNullOrEmpty(rowStr) ? 1 : int.Parse(rowStr);
            int col = string.IsNullOrEmpty(colStr) ? 1 : int.Parse(colStr);

            int offsetRow = row + _rowOrigin;

            if (col == 1 && !match.Groups[2].Success)
            {
                return $"\x1b[{offsetRow}{suffix}";
            }
            return $"\x1b[{offsetRow};{col}{suffix}";
        });

        return text;
    }
}
