using System.CommandLine;
using System.Text.Json;
using Hex1b.Diagnostics;
using Hex1b.Tool.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Hex1b.Tool.Commands.App;

/// <summary>
/// Inspects a TUI application's latest published frame (node tree, focus, popups, focused
/// editor) through the shared application-frame contract.
/// </summary>
internal sealed class AppTreeCommand : BaseCommand
{
    private readonly TerminalIdResolver _resolver;
    private readonly DiagnosticsSocketClient _client;

    private static readonly Argument<string> s_idArgument = new("id") { Description = "Terminal ID (or prefix)" };
    private static readonly Option<bool> s_focusOption = new("--focus") { Description = "Include the focus ring and focused editor" };
    private static readonly Option<bool> s_popupsOption = new("--popups") { Description = "Include the popup stack" };
    private static readonly Option<int?> s_depthOption = new("--depth") { Description = "Limit printed tree depth (text output only)" };
    private static readonly Option<bool> s_noPerfOption = new("--no-perf") { Description = "Hide performance timing (text output only)" };
    private static readonly MilestoneOptions s_milestone = new();
    private static readonly Option<string[]> s_authorizeOption = new("--authorize")
    {
        Description = "Opt in to content beyond the frame's defaults: editor-text includes the focused editor's text (repeatable or comma-separated)"
    };

    public AppTreeCommand(
        TerminalIdResolver resolver,
        DiagnosticsSocketClient client,
        OutputFormatter formatter,
        ILogger<AppTreeCommand> logger)
        : base("tree", "Inspect the latest published frame of a TUI application", formatter, logger)
    {
        _resolver = resolver;
        _client = client;

        Arguments.Add(s_idArgument);
        Options.Add(s_focusOption);
        Options.Add(s_popupsOption);
        Options.Add(s_depthOption);
        Options.Add(s_noPerfOption);
        Options.Add(s_authorizeOption);
        s_milestone.AddTo(this);
    }

    protected override async Task<int> ExecuteAsync(ParseResult parseResult, CancellationToken cancellationToken)
    {
        var id = parseResult.GetValue(s_idArgument)!;
        var showFocus = parseResult.GetValue(s_focusOption);
        var showPopups = parseResult.GetValue(s_popupsOption);
        var depth = parseResult.GetValue(s_depthOption);
        var hidePerf = parseResult.GetValue(s_noPerfOption);
        var authorizations = parseResult.GetValue(s_authorizeOption)?
            .SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        var json = parseResult.GetValue(RootCommand.JsonOption);

        var (request, invalid) = DiagnosticContractNames.ParseApplicationFrameRequest(authorizations);
        if (invalid != null)
            return WriteFailure(invalid, json);
        var (milestone, invalidMilestone) = s_milestone.Parse(parseResult);
        if (invalidMilestone != null)
            return WriteFailure(TerminalDiagnostics.FrameProblem(DiagnosticOutcome.InvalidRequest, "invalid-milestone", invalidMilestone), json);
        if (milestone != null)
            request = new DiagnosticApplicationFrameRequest { Authorizations = request!.Authorizations, Milestone = milestone };

        var resolved = _resolver.Resolve(id);
        if (!resolved.Success)
        {
            Formatter.WriteError(resolved.Error!);
            return 1;
        }

        var result = await _client.CaptureApplicationFrameAsync(resolved.SocketPath!, request!, cancellationToken);
        if (result.Outcome != DiagnosticOutcome.Captured)
            return WriteFailure(result, json);

        // JSON is the whole contract result; the display options only shape text output.
        if (json)
        {
            WriteResult(result);
            return 0;
        }

        var frame = result.Frame!;
        var showPerf = !hidePerf && frame.Timings is not null;
        if (result.Milestone is { } observed)
            Formatter.WriteLine(Safe(MilestoneOptions.Describe(observed)));
        Formatter.WriteLine($"Frame {frame.FrameId}: {frame.Columns}x{frame.Rows}, {(frame.WroteOutput ? "wrote output" : "no output")}");
        if (showPerf)
            Formatter.WriteLine($"Timing: build={frame.Timings!.BuildMs:F2}ms reconcile={frame.Timings.ReconcileMs:F2}ms render={frame.Timings.RenderMs:F2}ms");
        Formatter.WriteLine("");

        if (frame.Root != null)
            PrintTree(frame.Root, "", true, showPerf, depth ?? int.MaxValue);

        if (showPopups && frame.Popups.Count > 0)
        {
            Formatter.WriteLine("");
            Formatter.WriteLine("Popups:");
            foreach (var popup in frame.Popups)
            {
                var anchor = popup.IsAnchored
                    ? $", anchor={Safe(popup.AnchorNodeType)} {popup.AnchorBounds} {Safe(popup.AnchorPosition)}{(popup.AnchorIsStale == true ? " (stale)" : "")}"
                    : "";
                Formatter.WriteLine($"  [{popup.Index}] {Safe(popup.ContentType)} (barrier={popup.IsBarrier}, anchored={popup.IsAnchored}{anchor})");
            }
        }

        if (showFocus)
        {
            var focus = frame.Focus;
            Formatter.WriteLine("");
            Formatter.WriteLine($"Focus: {Safe(focus.FocusedNodeType ?? "none")} (index {focus.CurrentIndex}/{focus.Focusables.Count})");
            if (frame.FocusedEditor is { } editor)
            {
                var carets = string.Join(", ", editor.Carets.Select(c => $"{c.Offset}@{c.Line}:{c.Column}"));
                var selections = string.Join(", ", editor.Selections.Select(s => $"{s.Start.Offset}-{s.End.Offset}"));
                Formatter.WriteLine($"Editor: {Safe(editor.Kind)} {editor.Bounds}, length={editor.Length}, lines={editor.LineCount}, carets=[{carets}], selections=[{selections}]");
                if (editor.Text != null)
                    Formatter.WriteLine($"Editor text: {Quote(editor.Text)}");
            }
        }

        return 0;
    }

    private void PrintTree(DiagnosticFrameNode node, string indent, bool isLast, bool showPerf, int remainingDepth)
    {
        var connector = isLast ? "└─ " : "├─ ";
        var focused = node.IsFocused ? " [FOCUSED]" : "";
        var text = node.Text is { } content ? $" {Quote(content)}" : "";

        Formatter.WriteLine($"{indent}{connector}{Safe(node.Type)}{focused}{text}");

        // Detail lines use extra indentation under the connector
        var detailIndent = indent + (isLast ? "   " : "│  ") + "   ";

        if (node.Properties is { Count: > 0 })
        {
            var props = string.Join(", ", node.Properties.Select(p => $"{Safe(p.Key)}={Safe(p.Value)}"));
            Formatter.WriteLine($"{detailIndent}Properties: {props}");
        }

        var clipping = node.ClipState == DiagnosticClipState.Visible
            ? ""
            : $" ({DiagnosticContractNames.Of(node.ClipState)}, visible {node.VisibleBounds})";
        Formatter.WriteLine($"{detailIndent}Geometry:   {node.Bounds}{clipping}");

        if (showPerf && node.Timing?.ToString() is { Length: > 0 } timing)
            Formatter.WriteLine($"{detailIndent}Performance: {timing}");

        var children = node.Children ?? [];
        if (children.Count == 0)
            return;

        if (remainingDepth <= 1)
        {
            Formatter.WriteLine($"{detailIndent}Children:   {children.Count} not shown (--depth)");
            return;
        }

        var childIndent = indent + (isLast ? "   " : "│  ");
        Formatter.WriteLine($"{detailIndent}Children:");
        for (var i = 0; i < children.Count; i++)
            PrintTree(children[i], childIndent + "   ", i == children.Count - 1, showPerf, remainingDepth - 1);
    }

    // Target-supplied strings are escaped before they reach the terminal, so application content
    // (or a hostile socket peer) cannot inject control sequences.
    private static string Quote(string text) => JsonSerializer.Serialize(text, DiagnosticsJsonContext.Default.String);

    private static string Safe(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return "";
        var escaped = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length;)
        {
            // Lone surrogates are escaped; anything decoded is judged as a whole scalar, so format
            // characters outside the BMP (for example tag characters) are caught too.
            if (System.Text.Rune.DecodeFromUtf16(text.AsSpan(i), out var rune, out var consumed) != System.Buffers.OperationStatus.Done)
            {
                escaped.Append("\\u").Append(((int)text[i]).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                i++;
                continue;
            }

            if (IsUnsafe(rune))
                escaped.Append(rune.IsBmp ? "\\u" : "\\U").Append(rune.Value.ToString(rune.IsBmp ? "x4" : "x8", System.Globalization.CultureInfo.InvariantCulture));
            else
                escaped.Append(text, i, consumed);
            i += consumed;
        }

        return escaped.ToString();
    }

    // Control and format characters (bidirectional overrides, tags) and line or paragraph
    // separators change what the reader sees without being visible themselves.
    private static bool IsUnsafe(System.Text.Rune rune) => System.Text.Rune.GetUnicodeCategory(rune) is
        System.Globalization.UnicodeCategory.Control or System.Globalization.UnicodeCategory.Format
        or System.Globalization.UnicodeCategory.LineSeparator or System.Globalization.UnicodeCategory.ParagraphSeparator;

    private int WriteFailure(DiagnosticApplicationFrameResult result, bool json)
    {
        if (json)
            WriteResult(result);
        Formatter.WriteError($"{DiagnosticContractNames.Of(result.Outcome)} ({Safe(result.Problem?.Code)}): {Safe(result.Problem?.Message)}");
        if (result.Milestone is { } observed)
            Formatter.WriteError(Safe(MilestoneOptions.Describe(observed)));
        return 1;
    }

    private static void WriteResult(DiagnosticApplicationFrameResult result) =>
        Console.WriteLine(JsonSerializer.Serialize(result, DiagnosticsJsonOptions.Indented.GetTypeInfo(typeof(DiagnosticApplicationFrameResult))));
}
