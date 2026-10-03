# Hex1b MCP Server Skill

This skill provides guidance for AI agents working with the Hex1b TUI (Terminal User Interface) library and its MCP diagnostic tools.

## Overview

**Hex1b** is a .NET library for building terminal user interfaces with a React-inspired declarative API. The library ships to NuGet as `Hex1b`.

- **Documentation**: https://hex1b.dev
- **Repository**: https://github.com/AsciiCraft/hex1b
- **NuGet**: https://www.nuget.org/packages/Hex1b

## MCP Tools Reference

The Hex1b MCP server provides tools for interacting with running Hex1b applications that have diagnostics enabled.

### Discovery Tools

#### `GetHex1bStacksWithDiagnosticsEnabled`
Lists all running Hex1b applications with diagnostics enabled.
- Returns: Process IDs, app names, dimensions, and socket paths
- Use this first to discover available applications to connect to

#### `DiscoverHex1bStacks`
Discovers and connects to all Hex1b applications with diagnostics enabled.
- Returns session IDs for each connected application
- Use session IDs with other tools

### Capture Tools

All capture tools return `capture`, the shared diagnostic capture contract result (the same
shape the `hex1b capture screenshot --json` CLI returns). Check `capture.outcome` first: only
`captured` carries content. `unavailable`, `invalid-request`, and `failed` include
`capture.problem.code` and a message. Absent fields are listed in `capture.unavailableFields`
with a reason. They are never reported as zero.

Default content is the rendered screen, plus retained model history when requested. Hyperlink
targets, titles, editor text, and raw input are `excluded` in `capture.contentCoverage` unless
authorized. Rendered text can still contain secrets.

#### `CaptureHex1bTerminal`
Captures a Hex1b application by process ID and saves the content to a file.
- Parameters:
  - `processId`: Process ID of the Hex1b application
  - `savePath`: File path to save the capture
  - `format`: "ansi", "svg", "html", or "text" (default: "ansi")
  - `historyRows`: Rows of retained terminal-model history (not native scrollback)
  - `authorize`: Comma-separated `non-screen-metadata`, `editor-text`, `raw-input`

#### `CaptureTerminalScreen`
Captures any local or remote target.
- Parameters:
  - `sessionId`: Session ID of connected terminal
  - `format`: "text", "ansi" (keeps cell styles), "svg", or "html"
  - `savePath`: Optional file path; content then goes to the file instead of `capture.content`
  - `historyRows`, `authorize`: as above

#### `GetTerminalDiagnosticCapabilities`
Describes supported operations, formats, timing (`immediate` only), authorizations, and
evidence layers. The application-frame, native-delivery, and native-presentation layers are
reported unavailable, each with a reason.

### Input Tools

#### `SendInputToHex1bTerminal`
Sends input characters to a Hex1b application.
- Parameters:
  - `processId`: Process ID of the target application
  - `input`: Text to send (supports `\n`, `\t`, `\x1b` escape sequences)
- Use for simulating user input

#### `SendTerminalKey`
Sends a special key to a terminal.
- Parameters:
  - `sessionId`: Session ID of terminal
  - `key`: Key name (Enter, Tab, Escape, Up, Down, Left, Right, F1-F12, etc.)
  - `modifiers`: Optional array ["Ctrl", "Alt", "Shift"]

#### `SendTerminalMouseClick`
Sends a mouse click to a terminal.
- Parameters:
  - `sessionId`: Session ID of terminal
  - `x`, `y`: Cell coordinates (0-based)
  - `button`: "left", "middle", or "right"

### Diagnostic Tools

#### `GetHex1bTree` / `capture_application_frame`
**IMPORTANT**: Use these tools to debug layout, clipping, hit testing, and focus issues.
- Parameters:
  - `processId` (`GetHex1bTree`) or `sessionId` (`capture_application_frame`)
  - `authorize`: optional `editor-text` to include the focused editor's text
- Return `applicationFrame`, the latest frame the app published at the end of a render pass:
  - `frame.root`: node hierarchy with `bounds`, `hitTestBounds`, `visibleBounds`, `clipState`, and properties
  - `frame.popups`: popup stack with anchor type, bounds, staleness (`anchorIsStale`), and position
  - `frame.focus`: focus ring (`currentIndex`, `focusedNodeType`, `focusables`, `lastHitTest`)
  - `frame.focusedEditor`: carets and selections (offset, 0-based line/column), length, line count
  - `identity.applicationFrame`: the frame's identity
- Local PTY sessions report `no-application-layer`; a flow between steps reports `no-active-application`
- Inline flow-step frames use step-local coordinates (row 0 is the step's first row): translate before clicking
- To see the effect of an input, send it (sends return `acceptedInput.lastId`), then capture with `milestone` = `frame-published` (or `model-applied`) and `inputId`: the result names the frame that actually processed the input instead of whatever frame came next
- Essential for understanding why clicks aren't working or focus is wrong

#### `capture_native_delivery`
Use it to tell a wrong layout or a slow model from output that never reached the host.
- Parameters: `sessionId`, optional `since` (a record sequence), `limit`, and `authorize` (`native-output` for the written bytes)
- Returns `delivery.records`: each write's `outcome` (`accepted`, `refused` with a `reason`, `failed` with an `error`), `source`, `phase` (`before-model`/`after-model`), `bytesAccepted`, `modelSequenceAtStart`, and `outputSequence`
- `accepted` means the host took the bytes, not that anything was displayed; local PTY sessions report `no-native-presentation`
- To read incrementally, pass the last returned record's `sequence` as the next `since`; `totals.lastSequence` can skip a write still in progress

#### Diagnostic cases (`start_diagnostic_case`, `stop_diagnostic_case`, `get_diagnostic_case_status`, `mark_diagnostic_case`, `recover_diagnostic_case`, `inspect_diagnostic_case`, `reapply_diagnostic_case`)
Use a case to keep evidence of a problem you cannot reproduce on demand: it records to a local directory you can inspect after the process is gone.
- To record from the first byte, start the session with `recordCase: true` (with `caseAuthorize: "reapplication-data"` to keep the model's input bytes). A case started later with `reapplication-data` owns a cumulative `text-state/3` checkpoint (retained history, titles, command marks, repeated buffer-cell write equality and output held between chunks included) and re-applies from there. Intact bounded non-Sixel DCS, including DECRQSS and ignored DCS, is supported at introducer, payload, malformed-introducer and held-ESC boundaries. Refused surfaces are `sixel-continuation` (identified unfinished Sixel), `dcs-retention-limit` (discarded required content) and `graphics`; `size-limit`, the pending-state budget, a start inside an application or an unrebuildable configuration also prevent a complete start. Retained content is never truncated to fit
- A not-yet-classified DCS header is supported until later input identifies Sixel and ends the affected interval, even if it cancels or finishes in that chunk. Keep inspecting the original recording; a later complete supported recovery may open a new interval, while a mark alone does not. The required nullable `pendingInput.dcs` holder and `configuration.dcsFraming` producer limits must be understood by the consumer. The current consumer refuses `/1` and `/2` text-state profiles as `incompatible`; older consumers decline `/3`. Artifact format `2` and diagnostics contract `1` are unchanged. See the [DCS checkpoint contract](https://hex1b.dev/guide/diagnostic-capture#pending-dcs-continuation) for required JSON members and framing policy
- Pass `scrollback` (rows, 1 to 1,000,000) to `start_bash_terminal` / `start_pwsh_terminal` for a session that retains history; without it there is none
- `start_diagnostic_case` takes `sessionId`, optional `maxBytes`, `maxSeconds`, `authorize` (comma-separated: `reapplication-data`, `raw-input`, `editor-text`, `native-output`) and `directory`; one case per terminal (`case-active`)
- `stop_diagnostic_case` returns `case.path` and `case.stopReason`; pass the path to `inspect_diagnostic_case` (optional `since`, `limit`) for `completionState`, per-stream `missing` ranges and the re-applicable `intervals`
- `mark_diagnostic_case` (`sessionId`, optional `label`) records a checkpoint at the current model sequence while the case records; the stop records one labelled `stop`. Mark just before and just after the moment you want to examine
- `recover_diagnostic_case` (`sessionId`, optional `label`) takes a recovery checkpoint after the case lost events (an `overload` range in `inspect_diagnostic_case`): the model's full state at that moment, a new origin that `reapply_diagnostic_case` restores from for targets after the gap, so the case stays re-applicable. Needs `reapplication-data`; `unsupported` names why (`sixel-continuation`, `dcs-retention-limit`, `graphics`, `size-limit` for the remaining room, the pending-state budget, inside an application). `inspect_diagnostic_case` then lists one interval per origin, each naming it
- `reapply_diagnostic_case` (`path`, `to`: `12`, `case:34`, or a label such as `stop`) rebuilds the model offline and compares it with the recorded checkpoint. `matched` means the recording reproduces that state; `different` lists typed differences (`screen[r][c].text`, `modes.<name>`, …) with per-surface counts; `unavailable` says why nothing was compared (no checkpoint, no state, `sixel-continuation`, `dcs-retention-limit` or `graphics`). `injectFault` (e.g. `cell-text` or `cell-text:3/5`) proves the comparison catches a change and labels the result `faultInjected`; never report a faulted result as the case's outcome. It restores from the earliest origin whose interval covers the target (the result's `origin` says which); `from` names one instead (`start`, or a recovery by label, `case:34` or `checkpoint:2`). A target inside a gap, before every covering origin, or outside the named origin's interval is `beyond-interval` (with `lastValidModelSequence`) and writes nothing; a mark is `not-an-origin`. Any build whose declarations match the case's can re-apply it, so a candidate build's own `reapply_diagnostic_case` compares it with the recording build: the result's `producer` and `consumer` name both builds (version and `hex1bBuild`), `compatibility` lists seven checks (`formatVersion`, `contractVersion`, `checkpoint.profile`, `checkpoint.coveredSurfaces`, `configuration`, `configuration.capabilities`, `origin`) with both sides' values and `sameBuild`; a mismatch is `incompatible` naming the check, and the message says what the case declares and what this build supports
- To check retained DCS state, run `reapply_diagnostic_case` separately with `injectFault: "dcs-bytes"` and `"dcs-state"` against an in-progress checkpoint (`to: "start"`, or a mark/stop taken while pending). They omit nonempty `pendingInput.dcs.retainedBytes` and the present `pendingInput.dcs.state`, respectively, and report `different` at those exact paths. An absent DCS holder makes either fault `unavailable` / `fault-not-applicable`; empty retained bytes make `dcs-bytes` not applicable. Faults change only the comparison copy, keep `reapplied.json` unmodified, and label the result `faultInjected`
- Re-application restores authorized original terminal output, not raw keyboard input. It executes no application code and produces no native-terminal, clipboard or upload side effects
- Losses are always recorded: overload drops the newest events past 4,096 queued, and size limits, drain timeouts and failures each leave a `missing` range with its reason; past 1,024 ranges a stream's further loss is one range of unknown extent, bounded by the envelope the case writes when it stops (`extent: "envelope"` in the inspection)

### Session Management

#### `ConnectToHex1bStack`
Connects to a remote Hex1b application by process ID.
- Returns a session ID for use with other tools

#### `ListTerminals`
Lists all active terminal sessions.

#### `ListAllTerminalTargets`
Lists both local terminals and remote Hex1b connections.

## Enabling Diagnostics in Your Application

To enable MCP diagnostics in a Hex1b application:

```csharp
await using var terminal = Hex1bTerminal.CreateBuilder()
    .WithDiagnostics()  // Enable MCP diagnostics
    .WithHex1bApp((app, options) => ctx => ctx.Text("Hello!"))
    .Build();

await terminal.RunAsync();
```

**Note**: `WithDiagnostics()` is automatically disabled in Release builds for security. To force enable in Release:

```csharp
.WithMcpDiagnostics(forceEnable: true)
```

## Testing Best Practices

### Unit Testing with Headless Mode

For unit tests, use headless mode to avoid terminal I/O:

```csharp
[Fact]
public async Task MyWidget_Click_PerformsAction()
{
    // Arrange
    await using var terminal = Hex1bTerminal.CreateBuilder()
        .WithHeadless(80, 24)  // No real terminal
        .WithDiagnostics()  // Enable for debugging if needed
        .WithHex1bApp((app, options) => ctx => 
            ctx.Button("Click me", e => { /* handler */ }))
        .Build();
    
    // Act
    await terminal.StartAsync();
    terminal.SendMouseClick(MouseButton.Left, 5, 0);
    await terminal.ProcessEventsAsync();
    
    // Assert
    var snapshot = terminal.CreateSnapshot();
    Assert.Contains("expected text", snapshot.GetText());
}
```

### Key Testing Patterns

1. **Use `WithHeadless(width, height)`** - Runs without a real terminal
2. **Use `terminal.SendMouseClick()` / `terminal.SendKey()`** - Inject input
3. **Use `terminal.ProcessEventsAsync()`** - Process pending events
4. **Use `terminal.CreateSnapshot()`** - Capture screen for assertions
5. **Use `WithDiagnostics()`** - Enable when debugging test failures

### Debugging Test Failures

When tests fail unexpectedly:

1. Enable `WithDiagnostics()` in the test
2. Add a breakpoint or delay
3. Use `GetHex1bTree` MCP tool to inspect:
   - Node bounds, hit test bounds, and visible (clipped) bounds
   - Focus ring state
   - Popup stack

## Architecture Quick Reference

### Widget/Node Pattern

- **Widgets** (`*Widget`): Immutable records describing what to render
- **Nodes** (`*Node`): Mutable classes managing state and rendering
- Reconciliation diffs widgets against nodes to preserve state

### Common Node Types

| Node | Purpose |
|------|---------|
| `ZStackNode` | Popup host, layers children on Z-axis |
| `VStackNode` / `HStackNode` | Vertical/horizontal layout |
| `ButtonNode` | Clickable button |
| `TextBlockNode` | Text display |
| `TableNode` | Data table with scrolling |
| `PickerNode` | Dropdown selection |
| `MenuNode` | Menu bar item |
| `BackdropNode` | Modal backdrop with click-away |
| `AnchoredNode` | Positions popup relative to anchor |

### Hit Testing

- `FocusRing.HitTest(x, y)` finds the focusable node at a position
- Iterates focusables in **reverse order** (last = topmost)
- Uses `node.HitTestBounds` which may differ from `node.Bounds`
- Nodes with zero `Bounds` return `Rect.Zero` for `HitTestBounds` to prevent ghost hits

## Common Issues and Solutions

### Click not working / wrong element responding
1. Use `GetHex1bTree` to inspect `frame.focus.focusables`
2. Look for nodes with mismatched `bounds` vs `hitTestBounds`
3. Check for nodes with `bounds: (0,0,0,0)` but non-zero `hitTestBounds`

### Popup appearing at wrong position
1. Check the `frame.popups` array
2. Look for `anchorIsStale: true` indicating stale anchor reference
3. Stale anchors happen when the anchor node is replaced during reconciliation

### Focus not on expected element
1. Check `frame.focus.currentIndex` and `focusedNodeType`
2. Verify the element is in the `frame.focus.focusables` array
3. Check `isFocusable: true` on the node

## Getting This Skill

If you're an AI agent and want to save this skill for future use:

1. Call `GetHex1bSkill` to get this content
2. Save to `.github/skills/hex1b-mcp.md` or your project's skill directory
3. Reference when working with Hex1b applications or debugging TUI issues
