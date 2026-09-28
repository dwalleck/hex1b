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
| `editor-text` | excluded | unavailable: capture does not collect application editor text (see [Application frames](#application-frames)) |
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

## Application frames

The `application-frame` operation returns the latest frame a Hex1b application **published** at
the end of a completed render pass. It is a separate operation from `capture`, with its own
result, and is served by `hex1b app tree`, the MCP tools `capture_application_frame` (by session)
and `GetHex1bTree` (by process ID), and the socket method `application-frame`. Every client
returns the engine's result unchanged.

Frames are published only by applications built with `WithDiagnostics()` (including inline flow
steps, which publish through the flow's terminal); applications without it never project frames.
The projection runs on the application loop, after rendering and before the completed-pass count
advances, and holds copies only. A capture reads the latest published frame and never drives a
render, waits for a frame, or reads live application state, so a retained result never changes.

### Request

| Field | Wire name | Values | Default |
|-------|-----------|--------|---------|
| Authorizations | `authorizations` | `editor-text` (other values are accepted and add nothing) | none |

### Result

| Field | Meaning |
|-------|---------|
| `contractVersion`, `outcome`, `problem` | As for `capture`. |
| `frame.applicationInstanceId` | The application instance that published the frame. Each app run, and each inline flow step, is its own instance. |
| `frame.frameId` | The instance's completed-pass count for this frame; also `identity.applicationFrame`. Results with the same `applicationInstanceId` and `frameId` in one `sessionId` are the same frame. |
| `frame.columns`, `frame.rows` | The pass's screen size. |
| `frame.wroteOutput` | Whether the pass wrote cell or graphics changes to the terminal. Cursor-only updates and synchronized-update markers do not count. |
| `frame.root` | Node tree: `type`, `widgetType`, `bounds`, `hitTestBounds`, `contentBounds`, `visibleBounds`, `clipState` (`visible`, `partially-clipped`, `fully-clipped`), the node's own `clipRect`/`clipMode`, focus flags, rendered `text` (text blocks, buttons, menu items, checkboxes, hyperlinks, tabs, tree items, notification cards, border titles, FIGlet text; a window's title is its title bar's text), type-specific `properties` (for example a list's or picker's `selectedText`), `editor` metadata for TextBox and Editor nodes (never their text), and `timing`. `visibleBounds` is what the renderer drew: while rendering, each node records the clip it was composited through (the clip regions current at that moment, whatever their `clipMode`); its visible rect is its bounds intersected with that clip and with its parent's visible surface. A window title bar or an accordion section is therefore reported as drawn, even though it lies outside its container's `clipRect`. A node with no recorded clip (drawn by its parent directly, or outside a diagnostics render) falls back to its ancestors' clip rects. Clipping is not occlusion: content drawn later, such as a popup, can cover a node reported `visible`. |
| `frame.popups` | Popup stack, bottom first: content type and bounds, barrier and anchored flags, focus-restore node, and anchor type, bounds, staleness, and position. |
| `frame.focus` | Focus ring: `currentIndex`, `focusedNodeType`, `lastHitTest`, and each focusable's bounds and hit-test bounds. |
| `frame.focusedEditor` | For a focused TextBox or Editor: `kind` (`text-box`, `editor`), `bounds`, `length`, `lineCount`, every caret, and every non-empty selection. Positions are `{ offset, line, column }`: UTF-16 offsets and 0-based line and column. `text` is present only with `editor-text` authorization. Editor metadata comes from one read of the editor's text, with the cursors read immediately after; a cursor offset past that text is clamped to it, so under concurrent edits carets can mix positions from just before and after an edit but never exceed the reported length. |
| `frame.timings` | Build, reconcile, and render milliseconds of the pass. |
| `identity` | As for `capture`, with `sourceLayer` `application-frame`, `applicationFrame` and `applicationInstanceId` (together they identify the frame within the session), and `acquisition` bracketing the projection. `modelSequence` is unavailable: a frame is not a terminal-model observation. |
| `contentCoverage` | `application-text` is included. `editor-text` is `excluded` without authorization, `included` with it, and `unavailable` when no editor had focus. |
| `unavailableFields` | Explains an absent `frame.root`, `frame.focusedEditor`, or `frame.timings`. |

Every editor reports its metadata on its node. Only the focused editor's text can be returned, and
only with `editor-text`.

### Problem codes

| Code | Outcome | Cause |
|------|---------|-------|
| `no-application-layer` | `unavailable` | The workload is not a Hex1b application (MCP local sessions, PTYs hosted by `hex1b terminal start`, raw workloads). |
| `no-active-application` | `unavailable` | The terminal hosts Hex1b applications, but none is running: a flow between steps, or an application that has not started or has exited. Capabilities still report the layer available. |
| `application-frame-publication-disabled` | `unavailable` | The application was built without `WithDiagnostics()`. |
| `no-application-frame-yet` | `unavailable` | No render pass has completed since publication started. |
| `application-frame-projection-failed` | `failed` | Projecting the latest completed pass threw; the message names that frame. Rendering is unaffected, and the next pass publishes again. |

Transport, target, and request codes are the same as for `capture`.

## Input milestones

A diagnostics-enabled Hex1b application numbers every input as it enters the application's input
channel. This covers diagnostic sends (text, keys, clicks, drags), native keyboard, mouse and paste
input, and resizes, in one sequence per terminal session. Sends through the socket, CLI or MCP
return the ids their events received as `acceptedInput {firstId, lastId, meaning}`; a text send
receives one id per character. Acceptance only means the input was queued. The later stages are
milestones that a capture can wait for:

| Milestone | Met when |
|-----------|----------|
| `input-accepted` | The input was queued for the application; for a PTY, written to the child process. |
| `input-processed` | The application loop consumed the input: its processed-input watermark reached the id, or the flow runner consumed the input itself. |
| `frame-published` | A frame whose processed-input watermark covers the input was published. Frames published earlier, such as timer, animation or invalidation frames, are never attributed to the input. |
| `model-applied` | Everything the application enqueued up to that frame was applied to the terminal model, including when the frame wrote nothing. Unavailable for inline flow steps, whose output is re-segmented by the flow relay. |

A capture request (`capture` or `application-frame`) may carry `milestone {milestone, inputId,
timeoutMs}`. The timeout is 1–60,000 ms, with a default of 5,000. Without a milestone, a capture is
immediate and unchanged. With one, the capture waits and then returns a `milestone` block: the
requested stage and id, whether it was met, the accepted and processed watermarks, the processing
application instance, the first covering frame (`applicationInstanceId`, `frameId`, its watermark,
`wroteOutput`), the model sequence for `model-applied`, and the input's record. The observed values
can exceed the request: a past id is met at once and reports the current state. A milestone never
waits for a synchronized update to end; the capture discloses it as usual.

Outcomes:

| Outcome / code | When |
|----------------|------|
| `timed-out` / `milestone-timed-out` | Not met within the timeout. No content; the milestone block reports what was observed at the deadline. |
| `failed` / `application-stopped`, `target-disposed`, `input-closed`, `output-pump-failed`, `application-frame-projection-failed` | The awaited event can no longer happen. The wait ends at once. |
| `unavailable` / `input-tracking-unavailable` | The application does not track input (built without `WithDiagnostics()`). |
| `unavailable` / `input-consumption-unobservable` | A PTY target: only acceptance is observable. |
| `unavailable` / `model-application-unobservable` | An inline flow step's `model-applied`. |
| `unavailable` / `too-many-pending-waits` | 64 waits are already pending in the session. |
| `invalid-request` / `unknown-input-id`, `missing-input-id`, `invalid-milestone-timeout`, `unsupported-milestone` | The request cannot be served. |

The input record (`id`, `kind`, `source`, `acceptedAt`, `processedAt`) is included by default for
the last 1,024 inputs. Its `payload` (key and modifiers, typed text, mouse button and position)
appears only with the `raw-input` authorization; paste content streams to the application and is
not retained. Capabilities list each milestone, its guarantee, and whether this target supports it.
Milestones are observations of this process's application and terminal model; none of them is a
native delivery or presentation acknowledgment.

## Capabilities

`hex1b capture capabilities <id>` and the MCP tool `get_terminal_diagnostic_capabilities`
describe what a target supports: the `capture` operation with its formats, `immediate` timing,
model-history support, and authorizations. They also report each evidence layer:

| Layer | Available | Notes |
|-------|-----------|-------|
| `terminal-model` | yes | Cells, cursor, modes, and retained model history. |
| `application-frame` | Hex1b applications with `WithDiagnostics()` | Operation `application-frame`, timing `latest-published`, authorization `editor-text`. Otherwise unavailable with the reason the operation reports. |
| `native-delivery` | no | Native delivery outcomes are not observed. |
| `native-presentation` | no | What a host terminal physically displayed is not observable by Hex1b. |

## Current limitations

- Only immediate capture is supported; waiting for named processing milestones is not. A capture
  taken during a synchronized update returns the partially applied content, reports it in
  `synchronizedUpdate`, and is not a completed application frame.
- KGP animation playback advances on a timer without a model event, so SVG and HTML renderings of
  animated KGP images can differ at the same `modelSequence`. Graphics placements change only
  through output batches and are covered by the sequence.
- A terminal-model capture's `identity.applicationFrame` and `identity.applicationInstanceId` are
  absent: a model capture is not an application frame. Correlate the two through identities and acquisition intervals.
- An idle application returns its last published frame; compare its acquisition interval with
  the time of the request.
- Each inline flow step is its own application instance, so `frameId` restarts at each step while
  `applicationInstanceId` changes; between steps the flow reports `no-active-application`.
- An inline flow step's frame uses the step's own coordinates: row 0 is the step's first row, not
  the terminal's, and `rows` is the step's height. Translate before comparing with terminal-model
  captures or sending mouse input.
- Diagnostics enable per-node timing as well as frame publication, including in flows. Projection
  cost (one tree walk and one allocation per node per pass, plus a line scan per editor) is paid
  only by applications built with `WithDiagnostics()`.
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
