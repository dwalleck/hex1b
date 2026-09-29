# Hex1b.McpServer

A Model Context Protocol (MCP) server for terminal session management using Hex1b. This tool provides AI assistants and automation tools with the ability to create, control, and capture terminal sessions.

## Installation

Install as a .NET global tool:

```bash
dotnet tool install -g Hex1b.McpServer
```

## Usage

Run the MCP server:

```bash
hex1b-mcp
```

The server communicates via stdio using the MCP protocol.

## Available Tools

### Session Management

- **start_bash_terminal** - Start a new bash terminal session (Linux/macOS); `recordCase` records a diagnostic case from its first byte
- **start_pwsh_terminal** - Start a new PowerShell terminal session (Windows/cross-platform); `recordCase` as for bash
- **stop_terminal** - Stop a terminal session's process
- **remove_session** - Remove a terminal session and dispose resources
- **list_terminals** - List all active terminal sessions
- **resize_terminal** - Resize a terminal session

### Input

- **send_terminal_input** - Send text input to a terminal session
- **send_terminal_key** - Send a special key (Enter, Tab, Arrow keys, F1-F12, etc.)

### Capture

- **capture_terminal_screen** - Capture any local or remote target as `text`, styled `ansi`, `svg`, or `html`, with optional retained model history (`historyRows`) and opt-in `authorize` (`non-screen-metadata`, `editor-text`, `raw-input`)
- **capture_terminal_text** - Capture a local session as plain text
- **capture_terminal_screenshot** - Capture a local session as an SVG file
- **capture_hex1b_terminal** - Capture a Hex1b application by process ID to a file
- **get_terminal_diagnostic_capabilities** - Describe supported operations, formats, authorizations, and unavailable evidence layers
- **wait_for_terminal_text** - Wait for specific text to appear on the terminal

Every capture tool returns `capture`, the shared [diagnostic capture contract](https://hex1b.dev/guide/diagnostic-capture) result the CLI also returns: `outcome` (`captured`, `unavailable`, `invalid-request`, `failed`), `problem`, `content`, `geometry`, `history` coverage, `identity` (process, session, build, configuration, acquisition clock), `contentCoverage` (included, excluded, or unavailable, with reasons), `unavailableFields`, and `limitations`. When a tool saves to `savePath`, the content is in the file and omitted from `capture.content`.

### Application frames

- **capture_application_frame** - Return the latest frame a Hex1b application published (node tree with visible bounds and clip state, focus ring, popups, focused-editor carets and selections, timings), with optional `authorize` `editor-text` for the focused editor's text. Local PTY sessions report `no-application-layer`.
- **get_hex1b_tree** - The same result for a Hex1b application by process ID.

Send tools (`send_terminal_input`, `send_terminal_mouse_click`, `send_input_to_hex1b_terminal`) return `acceptedInput {firstId, lastId, meaning}`. `capture_terminal_screen`, `capture_application_frame` and `get_hex1b_tree` accept optional `milestone` (`input-accepted`, `input-processed`, `frame-published`, `model-applied`), `inputId` and `milestoneTimeoutMs`, and report what was observed in a `milestone` block. See [input milestones](https://hex1b.dev/guide/diagnostic-capture#input-milestones).

Both return `applicationFrame`, the shared [application-frame result](https://hex1b.dev/guide/diagnostic-capture#application-frames) that `hex1b app tree --json` also returns. Capturing never drives a render.

### Native delivery

- **capture_native_delivery** - Return what the terminal's native presentation did with each write (accepted, refused, failed), with source, phase relative to model application, byte counts and links; optional `since`, `limit`, and `authorize` `native-output` for the written bytes. Local PTY sessions report `no-native-presentation`. See [native delivery](https://hex1b.dev/guide/diagnostic-capture#native-delivery).

### Diagnostic cases

- **start_diagnostic_case** - Start recording a bounded diagnostic case on a target: model events (with the original input bytes under `authorize` `reapplication-data`), input, application frames and native delivery, written by the target to an owner-only local directory; optional `maxBytes`, `maxSeconds`, `authorize`, `directory`. A case started on a running target has no re-applicable checkpoint; use `recordCase` at session start for that.
- **stop_diagnostic_case** - Stop the active case (at most 10 s of draining) and return its final state and stop reason.
- **get_diagnostic_case_status** - The active case's state, bounds, progress and per-stream counts.
- **inspect_diagnostic_case** - Read a case artifact offline: `complete`, `interrupted` or `truncated`, coverage and missing ranges, the re-applicable interval, and a page of events. See [diagnostic cases](https://hex1b.dev/guide/diagnostic-capture#diagnostic-cases).

### Recording

- **start_asciinema_recording** - Start recording a terminal session to an asciinema file. Captures the current terminal state as the initial frame, then records all subsequent output. Supports `idle_time_limit` parameter (default 2s) to compress long pauses during playback.
- **stop_asciinema_recording** - Stop an active recording and finalize the file. Returns the path to the completed `.cast` file.

### Utility

- **ping** - Verify the MCP server is running

## Recording Example

```
1. start_bash_terminal → returns sessionId
2. send_terminal_input (run some commands)
3. start_asciinema_recording(sessionId, "/path/to/demo.cast")
   → Captures current screen state as initial frame
4. ... interact with terminal ...
5. stop_asciinema_recording(sessionId)
   → Finalizes recording

Play with: asciinema play /path/to/demo.cast
```

## Configuration

### VS Code MCP Configuration

Add to your VS Code settings:

```json
{
  "mcp": {
    "servers": {
      "terminal-mcp": {
        "command": "hex1b-mcp"
      }
    }
  }
}
```

### Claude Desktop Configuration

Add to your `claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "terminal-mcp": {
      "command": "hex1b-mcp"
    }
  }
}
```

## Requirements

- .NET 10.0 or later
- Linux or macOS (PTY support via native interop)

## License

MIT - See LICENSE file in the repository.
