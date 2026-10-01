# CLI Reference

Complete reference for the Hex1b CLI tool (`dotnet hex1b` / `hex1b`).

For an introduction and usage guide, see [CLI Tool](/guide/cli).

## Global Options

These options apply to all commands:

| Option | Type | Description |
|--------|------|-------------|
| `--json` | flag | Output results as JSON |

---

## `terminal`

Manage terminal lifecycle, metadata, and connections.

### `terminal list`

List all known terminals.

```bash
hex1b terminal list
```

Discovers terminals via their diagnostics sockets. Shows terminal ID, dimensions, process info, and recording status.

### `terminal start`

Start a hosted terminal.

```bash
hex1b terminal start [options] -- <command...>
```

| Argument | Description |
|----------|-------------|
| `command` | Command and arguments to run (after `--`). If omitted, Hex1b starts PowerShell on Windows or bash on Linux/macOS. |

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--width` | int | `120` | Terminal width in columns |
| `--height` | int | `30` | Terminal height in rows |
| `--cwd` | string | | Working directory for the command |
| `--record` | string | | Record session to an asciinema `.cast` file |
| `--attach` | flag | | Immediately attach to the terminal after starting |
| `--scrollback` | int | | Rows of scrollback the terminal retains (1–1,000,000); none when omitted |
| `--record-case` | flag | | Record a bounded [diagnostic case](../guide/diagnostic-capture.md#diagnostic-cases) from construction; the output names the case and its path |
| `--case-max-bytes` | long | `67108864` | With `--record-case`: largest artifact (1 MiB–1 GiB) |
| `--case-max-seconds` | int | `600` | With `--record-case`: longest recording (1–86400) |
| `--case-authorize` | string | | With `--record-case`: `reapplication-data`, `raw-input`, `editor-text`, `native-output` (repeatable or comma-separated) |
| `--case-dir` | string | `~/.hex1b/cases` | With `--record-case`: owner-only root for the case directory |

**Examples:**

```bash
# Start the default interactive shell
# (PowerShell on Windows, bash on Linux/macOS)
hex1b terminal start

# Start htop in a custom-sized terminal
hex1b terminal start --width 160 --height 50 -- htop

# Start and attach immediately
hex1b terminal start --attach -- vim README.md

# Start with recording
hex1b terminal start --record session.cast
```

### `terminal stop`

Stop a hosted terminal.

```bash
hex1b terminal stop <id>
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

### `terminal info`

Show terminal details.

```bash
hex1b terminal info <id>
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

Returns terminal dimensions, cursor position, process information, recording status, and other metadata.

### `terminal attach`

Attach to a terminal with an interactive TUI mirror.

```bash
hex1b terminal attach <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Description |
|--------|------|-------------|
| `--resize` | flag | Resize remote terminal to match local terminal dimensions |
| `--lead` | flag | Claim resize leadership (only the leader's resize events control the remote terminal) |
| `--web` | flag | Attach via a web browser using xterm.js instead of the TUI |
| `--port` | int | Port for the web server (0 for random). Only used with `--web` |

**Examples:**

```bash
# Basic attach
hex1b terminal attach abc123

# Attach and resize the remote terminal to match
hex1b terminal attach abc123 --resize --lead

# Attach via web browser
hex1b terminal attach abc123 --web --port 8080
```

**Keyboard shortcuts (TUI mode):**

| Key | Action |
|-----|--------|
| `Ctrl+]` | Open command menu |

### `terminal resize`

Resize a terminal.

```bash
hex1b terminal resize <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Description |
|--------|------|-------------|
| `--width` | int | New width in columns |
| `--height` | int | New height in rows |

At least one of `--width` or `--height` must be specified.

### `terminal clean`

Remove stale terminal sockets.

```bash
hex1b terminal clean
```

Scans for diagnostics sockets that no longer have a running process and removes them.

### `terminal host`

Run as a terminal host process (internal). This command is used internally by `terminal start` and is not intended for direct use.

---

## `capture`

Capture terminal output including screenshots and recordings.

### `capture screenshot`

Capture the terminal model through the shared [diagnostic capture contract](/guide/diagnostic-capture).

```bash
hex1b capture screenshot <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--format` | string | `text` | Output format: `text`, `ansi`, `svg`, `html`, or `png` |
| `--output` | string | | Save to file instead of stdout (required for `png`) |
| `--wait` | string | | Wait for text to appear before capturing |
| `--timeout` | int | `30` | Timeout in seconds for `--wait` |
| `--scrollback` | int | `0` | Rows of retained terminal-model history to include (not native scrollback) |
| `--authorize` | string | | Opt in to `non-screen-metadata`, `editor-text`, or `raw-input` (repeatable or comma-separated) |
| `--milestone` | string | | Wait for `input-accepted`, `input-processed`, `frame-published` or `model-applied` of `--input-id` before capturing ([input milestones](../guide/diagnostic-capture.md#input-milestones)) |
| `--input-id` | long | | The input id to wait for (the last id `keys` or `mouse` printed) |
| `--milestone-timeout` | int | `5000` | Maximum milestone wait in milliseconds (1–60000) |

Without `--json`, the command writes the rendered content (or saves it with `--output`). With
`--json`, it writes the full contract result: outcome, content, geometry, history coverage,
identity, content coverage, unavailable fields, and limitations. When `--output` is also given,
the content is only in the file and is omitted from the JSON; `png` is rasterized locally from
the SVG capture that the JSON describes. A non-`captured` outcome exits with code `1` and writes
`outcome (code): message` to stderr.

**Examples:**

```bash
# Plain text to stdout
hex1b capture screenshot abc123

# Styled ANSI with correlation and coverage metadata
hex1b capture screenshot abc123 --format ansi --json

# SVG with colors saved to file
hex1b capture screenshot abc123 --format svg --output screen.svg

# Wait for app to be ready, then capture
hex1b capture screenshot abc123 --wait "Ready" --timeout 10 --format ansi

# Include 100 rows of retained model history (requires WithScrollback on the target)
hex1b capture screenshot abc123 --scrollback 100

# Include hyperlink targets and the window title
hex1b capture screenshot abc123 --format ansi --authorize non-screen-metadata --json
```

### `capture capabilities`

Describe the diagnostic operations, formats, authorizations, and evidence layers a terminal
supports, with the reason for each unavailable layer. Always writes JSON.

```bash
hex1b capture capabilities <id>
```

### `capture delivery`

Show what the terminal's native presentation did with each write: accepted, refused or failed,
with source, phase relative to model application, byte counts and links
([native delivery](../guide/diagnostic-capture.md#native-delivery)). Reading never writes to the
terminal.

```bash
hex1b capture delivery <id> [options]
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--since` | long | | Only records after this sequence; to continue, pass the last returned record's sequence |
| `--limit` | int | `4096` | Most records to return (1–4096) |
| `--authorize` | string | | `native-output` adds each record's written bytes (base64) |

Without `--json` it prints totals and one line per record. With `--json` it writes the full
contract result. Headless targets exit 1 with `no-native-presentation`.

### `capture case`

Record a bounded [diagnostic case](../guide/diagnostic-capture.md#diagnostic-cases): the terminal
model's events with (when authorized) the original bytes it read, plus input, application frames
and native delivery, written by the target to an owner-only local directory.

```bash
hex1b capture case start <id> [options]
hex1b capture case status <id>
hex1b capture case stop <id>
hex1b capture case mark <id> [--label NAME]
hex1b capture case recover <id> [--label NAME]
hex1b capture case inspect <path> [--since N] [--limit N]
hex1b capture case reapply <path> --to TARGET [--from ORIGIN] [--inject-fault KIND] [--max-differences N] [--preview FORMAT]
```

| `start` option | Type | Default | Description |
|----------------|------|---------|-------------|
| `--max-bytes` | long | `67108864` | Largest artifact (1 MiB–1 GiB) |
| `--max-seconds` | int | `600` | Longest recording (1–86400) |
| `--authorize` | string | | `reapplication-data`, `raw-input`, `editor-text`, `native-output` (repeatable or comma-separated) |
| `--dir` | string | `~/.hex1b/cases` | Owner-only root for the case directory |

A case started on a running terminal with `reapplication-data` owns a `text-state/1` start
checkpoint and re-applies from it; a terminal holding a surface the start cannot restore yet
(a DCS in progress, graphics) makes it `unsupported`, and the output names the surfaces. Output
held between chunks (an unfinished escape sequence or UTF-8 scalar) is owned by the start. To record from the first byte, use `hex1b terminal start --record-case`. `stop` waits at most 10 s for queued events.
`inspect` reads the artifact offline, verifies every line's checksum, and reports whether the case
is `complete`, `interrupted` or `truncated`. It also reports per-stream coverage and missing
ranges (a range of unknown extent bounded by the envelope the case wrote when it stopped says so),
the re-applicable model intervals, one per origin (the start, and each complete recovery
checkpoint), each naming its origin, and, with `--limit`, a page of events.

`mark` records a checkpoint in the active case at the model's current sequence. With
`reapplication-data` the checkpoint holds the model's full text state; otherwise it records the
boundary only (and says why). `--label` is 1–64 printable ASCII characters. The default is `mark-`
and the checkpoint's ordinal; labels need not be unique.

`recover` takes a recovery checkpoint in the active case after recording loss (an `overload`
range: the queue dropped the newest events). It holds the model's full text state at the current
model sequence and is a new origin: a new re-applicable interval starts there, and `reapply`
restores from it for targets after the gap. It needs `reapplication-data`, and is classified like a
live start: `complete`, or `unsupported` with the reason (a DCS in progress or graphics; a state too
large for what the case's events tier leaves after the queued events and pending checkpoint states;
the pending-state budget, counted with the pending marks; a recovery inside an application;
unapplied output; a configuration a re-application could not rebuild), recorded as a boundary only.
A complete recovery's line is reserved in the case's size bound until it is written, so it always
lands with its state; later output that would cross the reduced bound is declared `size-limit`. Earlier loss and the case's initial checkpoint never
change. `--label` is as for `mark`; the default is `recovery-` and the checkpoint's ordinal.

`reapply` rebuilds the model offline from the case's recorded configuration, restores it from the
case's origin (its start, or the earliest recovery checkpoint whose interval covers the target, or
the one `--from` names; the result's `origin` says which) and applies the recorded events after
it up to the target. It then compares the result with the checkpoint recorded there. Each run
writes its own directory, `reapplications/<n>`, inside the case.

| `reapply` option | Type | Default | Description |
|------------------|------|---------|-------------|
| `--to` | string | (required) | A model sequence (`12`), a case sequence (`case:34`), or a checkpoint label (`label:name`, or the bare name; `stop` is the stop checkpoint, `start` a live start's checkpoint; a label several checkpoints share is ambiguous, so name one by `case:<n>`) |
| `--from` | string | | The origin to restore from: `start`, or a recovery checkpoint by label (`label:name`, or the bare name), case sequence (`case:34`) or ordinal (`checkpoint:2`). Default: the earliest origin whose re-applicable interval covers the target. A mark is `not-an-origin`; an unknown label, case sequence or ordinal is `unknown-label`, `unknown-case-sequence` or `unknown-checkpoint`; a numeric form without its number is `invalid-origin`; a target outside the named origin's interval is `beyond-interval` |
| `--inject-fault` | string | | A declared fault to inject before comparing, as `kind` or `kind:target` (repeatable or comma-separated): `cell-text[:row/column]`, `cell-style[:row/column]`, `cursor`, `mode[:name]`, `title`, `charset`, `tab-stop`, `pending-input`, `history-row[:index]`, `history-rows`, `pending-wrap`, `last-printed`, `rendition`, `margins`, `saved-cursor`, `pending-grapheme`, `activity`, `synchronized-update`, `title-stack`, `command-mark`, `pending-escape`, `pending-ground-escape`, `pending-framer` (the `pending-*` faults drop one holder of a live start's pending input; target the start). The result is labelled `faultInjected` |
| `--max-differences` | int | `1000` | Most differences listed (1–100000); every difference is counted |
| `--preview` | string | | `text`, `ansi`, `svg`, `html` (repeatable or comma-separated) |

`reapply` exits 0 when the comparison is `matched`, and 2 when it is `different` or `unavailable`.

Without `--json` each command prints a summary. With `--json` it writes the full contract result.
Refusals exit 1 with the problem code (`case-active`, `no-active-case`, `storage-refused`,
`invalid-bounds`, `busy`, `invalid-label`, `incompatible`, `beyond-interval`, `unknown-model-sequence`,
`not-an-origin`, `unknown-checkpoint`, `invalid-origin`, …).

### `capture recording start`

Start recording a terminal session in asciinema `.cast` format.

```bash
hex1b capture recording start <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Description |
|--------|------|-------------|
| `--output` | string | **(required)** Output `.cast` file path |
| `--title` | string | Recording title (embedded in the cast file header) |
| `--idle-limit` | double | Max idle time in seconds between frames |

**Examples:**

```bash
# Basic recording
hex1b capture recording start abc123 --output demo.cast

# With title and idle limiting
hex1b capture recording start abc123 --output demo.cast --title "Setup Guide" --idle-limit 2.0
```

### `capture recording stop`

Stop recording a terminal session.

```bash
hex1b capture recording stop <id>
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

Returns the path of the completed recording file.

### `capture recording status`

Show recording status of a terminal session.

```bash
hex1b capture recording status <id>
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

Shows whether a recording is active and the output file path.

### `capture recording playback`

Play back an asciinema recording.

```bash
hex1b capture recording playback [options]
```

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--file` | string | | **(required)** Path to `.cast` file |
| `--speed` | double | `1.0` | Playback speed multiplier |
| `--player` | flag | | Launch interactive TUI player with controls |

**Examples:**

```bash
# Simple playback to stdout
hex1b capture recording playback --file demo.cast

# Double speed
hex1b capture recording playback --file demo.cast --speed 2.0

# Interactive TUI player
hex1b capture recording playback --file demo.cast --player
```

**TUI player controls (`--player`):**

| Key | Action |
|-----|--------|
| `Space` | Play / Pause |
| `←` | Seek backward 5 seconds |
| `→` | Seek forward 5 seconds |
| `Q` | Quit |

---

## `keys`

Send keystrokes to a terminal.

On success, a diagnostics-enabled target reports the ids its events received: `Accepted inputs 3-5: …`, or `{firstId, lastId, meaning}` with `--json`. With both `--text` and `--key`, `--json` prints one array with the text send's range, then the key's. `mouse click` and `mouse drag` do the same. Pass the last id to `--input-id` on a capture.

```bash
hex1b keys <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Description |
|--------|------|-------------|
| `--text` | string | Type text as keystrokes |
| `--key` | string | Named key (see below) |
| `--ctrl` | flag | Ctrl modifier |
| `--shift` | flag | Shift modifier |
| `--alt` | flag | Alt modifier |

Provide either `--text` or `--key` (not both).

**Named keys:** `Enter`, `Tab`, `Escape`, `Backspace`, `Delete`, `Space`, `ArrowUp`, `ArrowDown`, `ArrowLeft`, `ArrowRight`, `Home`, `End`, `PageUp`, `PageDown`, `Insert`, `F1`–`F12`.

**Examples:**

```bash
# Type text
hex1b keys abc123 --text "hello world"

# Send Enter
hex1b keys abc123 --key Enter

# Ctrl+C
hex1b keys abc123 --key c --ctrl

# Alt+Tab
hex1b keys abc123 --key Tab --alt
```

---

## `mouse`

Send mouse input to a terminal.

### `mouse click`

Send a mouse click at coordinates.

```bash
hex1b mouse click <id> <x> <y> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |
| `x` | Column (0-based) |
| `y` | Row (0-based) |

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--button` | string | `left` | Mouse button: `left`, `right`, or `middle` |

### `mouse drag`

Drag from one coordinate to another.

```bash
hex1b mouse drag <id> <x1> <y1> <x2> <y2> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |
| `x1` | Start column (0-based) |
| `y1` | Start row (0-based) |
| `x2` | End column (0-based) |
| `y2` | End row (0-based) |

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--button` | string | `left` | Mouse button: `left`, `right`, or `middle` |

---

## `app`

TUI application diagnostics for inspecting published application frames.

### `app tree`

Inspect the latest frame a TUI application published: node tree with geometry and clipping,
focus ring, popups, focused editor, and timing. The application must use `WithDiagnostics()`.
`--json` prints the shared [application-frame result](../guide/diagnostic-capture.md#application-frames);
targets that are not Hex1b applications report `no-application-layer` and exit with code 1.

```bash
hex1b app tree <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Description |
|--------|------|-------------|
| `--focus` | flag | Include focus ring and focused-editor metadata (text output) |
| `--popups` | flag | Include popup stack (text output) |
| `--depth` | int | Limit printed tree depth (text output) |
| `--no-perf` | flag | Hide timing (text output) |
| `--authorize` | string[] | `editor-text` includes the focused editor's text (repeatable or comma-separated) |
| `--milestone` | string | Wait for `input-accepted`, `input-processed`, `frame-published` or `model-applied` of `--input-id` before capturing |
| `--input-id` | long | The input id to wait for (the last id `keys` or `mouse` printed) |
| `--milestone-timeout` | int | Maximum wait in milliseconds (1–60000; default 5000) |

**Examples:**

```bash
# Full widget tree
hex1b app tree abc123

# With focus info, limited depth
hex1b app tree abc123 --focus --depth 3

# As the contract JSON, including the focused editor's text
hex1b app tree abc123 --json --authorize editor-text
```

---

## `assert`

Assert on terminal content for scripting and CI. Exits with code 0 on success, non-zero on failure.

```bash
hex1b assert <id> [options]
```

| Argument | Description |
|----------|-------------|
| `id` | Terminal ID (or unique prefix) |

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| `--text-present` | string | | Assert text is visible on screen |
| `--text-absent` | string | | Assert text is NOT visible on screen |
| `--timeout` | int | `5` | How long to wait in seconds |

Provide at least one of `--text-present` or `--text-absent`.

**Examples:**

```bash
# Wait for text to appear
hex1b assert abc123 --text-present "Login successful" --timeout 10

# Verify error is not shown
hex1b assert abc123 --text-absent "Error"

# Use in a CI script
hex1b assert abc123 --text-present "Ready" --timeout 30 || exit 1
```

---

## `agent`

AI agent integration commands.

### `agent init`

Initialize a Hex1b agent skill file in a repository. This generates a skill configuration that AI coding agents (GitHub Copilot, Claude, Cursor, etc.) can use to understand how to work with Hex1b in your project.

```bash
hex1b agent init [options]
```

| Option | Type | Description |
|--------|------|-------------|
| `--path` | string | Explicit repo root path (skips auto-detection) |
| `--stdout` | flag | Write skill file to stdout instead of disk |
| `--force` | flag | Overwrite existing skill file |

### `agent mcp`

Start the MCP server (stdio transport). This is used for integration with AI agents that support the Model Context Protocol.

```bash
hex1b agent mcp
```

::: info Coming Soon
The `agent mcp` command is planned for a future release. For MCP integration today, see the standalone [MCP Server](/guide/mcp-server).
:::
