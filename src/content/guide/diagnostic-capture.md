# Diagnostic capture contract

Every way of capturing a Hex1b terminal — the `hex1b capture screenshot` CLI command, the MCP
capture tools, and the diagnostics socket — goes through one engine, `TerminalDiagnostics`, and
returns one result shape. A local target (an MCP terminal session, or a PTY hosted by
`hex1b terminal start`) and an attached target (an application built with `WithDiagnostics()`)
produce the same fields, outcomes, and permission behavior.

A capture is an **immediate observation of Hex1b's terminal model**. It returns current cells
without waiting for a later frame. It does not repaint, query the host terminal, send control
sequences, or send input.

## Request

| Field | Wire name | Values | Default |
|-------|-----------|--------|---------|
| Format | `format` | `text`, `ansi`, `svg`, `html` | `text` |
| Model history | `historyRows` | rows of retained terminal-model history to include above the screen | `0` |
| Font | `fontFamily` | font family for `svg`/`html` | exporter default |
| Authorizations | `authorizations` | `non-screen-metadata`, `editor-text`, `raw-input` | none |

`ansi` keeps the covered rendition of every cell. That covers foreground and background colors
in their original encoding (standard, bright, 256-color, RGB) and the bold, dim, italic,
underline, blink, hidden, strikethrough, and overline attributes. Reverse video is rendered by
swapping colors. Underline color and underline style variants are not represented. `text` has no
rendition. The CLI's `png` format is rasterized on the client from an `svg` capture.

## Result

| Field | Meaning |
|-------|---------|
| `contractVersion` | Contract version (currently `1`). |
| `outcome` | `captured`, `unavailable`, `invalid-request`, or `failed`. Only `captured` carries content. |
| `problem` | `{ code, message }` when the outcome is not `captured`. |
| `format`, `content` | Rendered content. When a client saves the content to a file (MCP `savePath`, CLI `--output`), `content` is omitted and the path is reported separately. |
| `geometry` | Model columns/rows, alternate-screen state, and cursor position. |
| `synchronizedUpdate` | `active` when a synchronized update (DEC mode 2026) had begun and not ended at the model read. `startedAtSequence` names the batch that began it and is present exactly while `active` is true — a conditional field, not an unavailable one. Clients also print a partial-content warning (MCP message, CLI stderr) while it is active. |
| `history` | `requestedRows`, `availableRows`, `returnedRows`, `croppedRows`, `truncated`, `retentionCapacity`, and `reason`. |
| `identity` | Process ID and start time, terminal-model `sessionId`, `modelSequence`, `sourceLayer` (`terminal-model`), application name/version, loaded `hex1bVersion`, `configuration` (workload, presentation, reflow, history retention), and `acquisition` (clock domain, monotonic start/end timestamps and frequency, wall-clock start/end). |
| `contentCoverage` | For each content class, whether it is `included`, `excluded`, or `unavailable`, and why. |
| `nonScreenMetadata` | Window title and icon name, only with `non-screen-metadata` authorization. |
| `unavailableFields` | Each field absent because it could not be observed, and the reason. Absent values are never reported as zero or empty. Conditional fields (such as `synchronizedUpdate.startedAtSequence`) are absent when they do not apply and are documented here instead. |
| `limitations` | What this observation cannot support. |

### Problem codes

| Code | Outcome | Cause |
|------|---------|-------|
| `unsupported-format`, `unsupported-authorization`, `invalid-history-rows` | `invalid-request` | The request cannot be served. |
| `target-unreachable` | `unavailable` | No live diagnostics socket answered. |
| `target-disposed`, `target-not-initialized`, `session-not-found` | `unavailable` | The terminal or session is gone or not ready. |
| `timeout`, `protocol-error`, `transport-failed` | `failed` | The exchange with an attached target failed. |
| `incompatible-target` | `failed` | The target runs a Hex1b build without this contract. |
| `capture-failed`, `save-failed` | `failed` | Rendering or saving the capture failed. |

## Content policy

By default a capture includes the **rendered screen** and any requested **rendered model
history**. Hidden metadata is withheld and listed as `excluded`, not returned empty:

| Content class | Default | With authorization |
|---------------|---------|--------------------|
| `rendered-screen` | included | included |
| `concealed-text` | excluded: concealed (SGR 8) cells are returned blank in every format | excluded |
| `rendered-history` | included when requested; `excluded` when not requested; `unavailable` when the model retains no history or the alternate screen is active | same |
| `hyperlink-targets` | excluded | included in `ansi` (OSC 8) and `html`; unavailable in `text` and `svg` |
| `window-title` | excluded | included as `nonScreenMetadata` |
| `editor-text` | excluded | unavailable: capture does not collect application editor text |
| `raw-input` | excluded | unavailable: capture does not collect keyboard input |

Each authorization is independent. Rendered screen and history text can itself contain secrets.
The default policy does not make captured content secret-free.

## Coherent model observation

A capture reads the terminal model once, inside the model's own critical section. The returned
cells and rendition, geometry, cursor, modes, retained-history rows, `modelSequence`, and
`synchronizedUpdate` all describe that one read; output, resizes, or history eviction after it do
not change the result. The observation is not atomic with application, native delivery, or host
observations — correlate those through their identities and acquisition intervals.

## Model sequence

`identity.modelSequence` counts the model events — output application batches, geometry
changes, and the release of a synchronized update by its timeout — that the terminal model has
applied in this session. It is read in the same
critical section as the captured cells, so two captures reporting the same value observed the
same model state, and a larger value means later events were applied. An event can leave the
visible state unchanged. Values are comparable only within one `sessionId`.

## Model history

`historyRows` asks for rows that Hex1b's terminal model retained after they scrolled off the
screen. This is not the host terminal's native scrollback. Retention exists only when the
terminal is built with `WithScrollback(capacity)`. Without it, `history.retentionCapacity` is
`0`, `availableRows` is absent with a reason, and `rendered-history` is `unavailable`. A requested
export is not a complete continuation checkpoint.

History rows are returned at the current screen width. A row that retained non-blank content
wider than the screen (for example output written before a resize without reflow) is cropped;
`croppedRows` counts those rows, `truncated` becomes `true`, and `reason` says so.

## Capabilities

`hex1b capture capabilities <id>` and the MCP tool `get_terminal_diagnostic_capabilities`
describe what a target supports: the `capture` operation with its formats, `immediate` timing,
model-history support, and authorizations. They also report each evidence layer:

| Layer | Available | Notes |
|-------|-----------|-------|
| `terminal-model` | yes | Cells, cursor, modes, and retained model history. |
| `application-frame` | no | Application frames are not yet published with identities. Generic PTY targets have no application layer. |
| `native-delivery` | no | Native delivery outcomes are not observed. |
| `native-presentation` | no | What a host terminal physically displayed is not observable by Hex1b. |

## Current limitations

- Only immediate capture is supported; waiting for named processing milestones is not. A capture
  taken during a synchronized update returns the partially applied content, reports it in
  `synchronizedUpdate`, and is not a completed application frame.
- KGP animation playback advances on a timer without a model event, so SVG and HTML renderings of
  animated KGP images can differ at the same `modelSequence`. Graphics placements change only
  through output batches and are covered by the sequence.
- `identity.applicationFrame` is absent: frames are not yet published with identities.
- `identity.applicationVersion` is reported only for in-process Hex1b applications.
- Independently acquired observations (for example a capture and a widget tree) are correlated by
  their identities and acquisition intervals, never presented as one atomic moment.
- Attached targets running a Hex1b build without this contract, or a different contract
  version, return `incompatible-target` rather than a legacy plain capture.
- `identity.acquisition` brackets the model read only; rendering the content happens afterwards.
- `identity.hex1bVersion` is the loaded assembly's informational version. A build made from
  uncommitted changes reports the revision it was based on.
- Capture and capability requests to an attached target time out after 10 seconds with a
  `timeout` failure. Input, resize, and recording requests are not timed out by the client.
