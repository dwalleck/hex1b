namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Estimates the memory a frame projection retains while it waits in the case queue, so the queue's byte
/// bound holds for frames: a fixed cost per node, focusable and popup, plus the text each carries (the
/// focused editor's full text under <c>editor-text</c>). One walk of the projection, when it is offered.
/// </summary>
internal static class CaseFrameSize
{
    private const int FrameBytes = 512;
    private const int NodeBytes = 384;
    private const int EntryBytes = 128;
    internal const int MaxEstimate = 1 << 30;

    internal static int Estimate(DiagnosticApplicationFrame? frame)
    {
        if (frame is null)
            return FrameBytes;
        long bytes = FrameBytes + (long)frame.Focus.Focusables.Count * EntryBytes + (long)frame.Popups.Count * EntryBytes
            + Editor(frame.FocusedEditor);
        if (frame.Root is { } root)
            bytes += Node(root);
        // Capped well inside int arithmetic (the queue adds a payload to it); anything this large is refused anyway.
        return (int)Math.Min(bytes, MaxEstimate);
    }

    private static long Node(DiagnosticFrameNode node)
    {
        long bytes = NodeBytes + Chars(node.Text) + Editor(node.Editor);
        if (node.Properties is { } properties)
            foreach (var (key, value) in properties)
                bytes += Chars(key) + Chars(value);
        foreach (var child in node.Children)
            bytes += Node(child);
        return bytes;
    }

    private static long Editor(DiagnosticEditorState? editor) =>
        editor is null ? 0 : EntryBytes + Chars(editor.Text) + (long)(editor.Carets.Count + editor.Selections.Count) * EntryBytes;

    private static long Chars(string? text) => 2L * (text?.Length ?? 0);
}
