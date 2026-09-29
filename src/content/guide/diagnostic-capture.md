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
receives one id per character. A send's ids are consecutive: other input waits until the send has
been queued. A send that delivered nothing returns no ids, including a send to a PTY child that has
exited, which fails. Acceptance only means the input was
queued. The later stages are
milestones that a capture can wait for:

| Milestone | Met when |
|-----------|----------|
| `input-accepted` | The input was queued for the application; for a PTY, written to the child process. |
| `input-processed` | The application loop consumed the input: its processed-input watermark reached the id, or the flow runner consumed the input itself. |
| `frame-published` | A frame whose processed-input watermark covers the input was published. Frames published earlier, such as timer, animation or invalidation frames, are never attributed to the input. |
| `model-applied` | The terminal's output pump has handled everything the application enqueued up to that frame, including when the frame wrote nothing. Handled means applied to the model, or not applied with its producer told why: a geometry-gated batch refused for a superseded geometry (the producer recomposes it), a gated batch the terminal cannot enforce (the producer's delivery fails), or a queued resize that failed. So `model-applied` proves the pump has finished with that output, not that every byte reached the model. Unavailable for inline flow steps, whose output is re-segmented by the flow relay. |

A capture request (`capture` or `application-frame`) may carry `milestone {milestone, inputId,
timeoutMs}`. The timeout is 1–60,000 ms, with a default of 5,000. Without a milestone, a capture is
immediate and unchanged. With one, the capture waits and then returns a `milestone` block: the
requested stage and id, whether it was met, the accepted and processed watermarks, the processing
application instance, the first covering frame (`applicationInstanceId`, `frameId`, its watermark,
`wroteOutput`), the model sequence (for `model-applied`, and whenever the milestone was not met), and
the input's record. The observed values can exceed the request: a past id is met at once and
reports the current state. A milestone never waits for a synchronized update to end; the capture
discloses it as usual. The request is validated before any wait. Waiting is asynchronous: the
synchronous `Capture` and `CaptureApplicationFrame` methods stay immediate and return
`invalid-request` / `milestone-requires-async` for a milestone request. A socket client that
disconnects ends its wait. Closing only the write side after sending the request, as `nc -N`
does, also counts as a disconnect, so keep the connection fully open until the response arrives.

In a flow, an input queued between steps waits for the next step. An input handed to a step that
ends before processing it fails with `application-stopped`, even after later inputs are processed.

Outcomes:

| Outcome / code | When |
|----------------|------|
| `timed-out` / `milestone-timed-out` | Not met within the timeout. No content; the milestone block reports the watermarks, the first covering frame and the model sequence observed at the deadline. |
| `failed` / `application-stopped`, `target-disposed`, `input-closed`, `output-pump-failed`, `application-frame-projection-failed` | The awaited event can no longer happen. The wait ends at once. |
| `unavailable` / `input-tracking-unavailable` | The application does not track input (built without `WithDiagnostics()`). |
| `unavailable` / `input-consumption-unobservable` | A PTY target: only acceptance is observable. |
| `unavailable` / `model-application-unobservable` | An inline flow step's `model-applied`. |
| `unavailable` / `too-many-pending-waits` | 64 waits are already pending in the session. |
| `invalid-request` / `unknown-input-id`, `missing-input-id`, `invalid-milestone-timeout`, `unsupported-milestone` | The request cannot be served. |
| `invalid-request` / `milestone-requires-async` | A milestone passed to a synchronous in-process capture method. |
| `failed` / `incompatible-target` | Reported by the client: the target returned a capture without the milestone result, so it predates input milestones. |

The input record (`id`, `kind`, `source`, `acceptedAt`, `processedAt`, and the process-monotonic
`acceptedTimestamp` and `processedTimestamp` in the clock domain of `identity.acquisition`) is
included by default for the last 1,024 inputs. Its `payload` (key and modifiers, typed text, mouse button and position)
appears only with the `raw-input` authorization; paste content streams to the application and is
not retained. Capabilities list each milestone, its guarantee, and whether this target supports it.
Milestones are observations of this process's application and terminal model; none of them is a
native delivery or presentation acknowledgment.

## Native delivery

A diagnostics-enabled terminal whose presentation can report its writes records every write it
makes to that presentation. Each record says whether the write was `accepted`, `refused` or
`failed`. The record stream is separate from application frames and the terminal model: it shows
what the host was sent, not what the model or the application holds.

- `accepted`: the presentation's write returned normally, so the host operating system or
  transport took the bytes. It is not an acknowledgment that anything was displayed.
- `refused`: the presentation declined to write. For example, a geometry-gated batch composed for
  a geometry the host no longer reports (`geometry-changed`), a WebSocket that is not open
  (`socket-not-open`), or a disposed presentation (`presentation-disposed`). A geometry-gated
  write to a disposed console presentation is `failed` instead, with `bytesAccepted` 0, because
  it throws.
- `failed`: the write raised an error, which the record reports with its type and message. The
  error then propagates exactly as it does without diagnostics. A WebSocket presentation
  swallows socket errors, and still does.

Request it with `hex1b capture delivery <id>`, the MCP tool `capture_native_delivery`, or the
socket method `delivery`, with optional `since` (a sequence), `limit` (1–4,096) and `authorize`.
The result carries:

- `deliveryLayer`: `console` for a native terminal, `websocket` for a WebSocket transport;
- `coverageStartedAt`: recording starts when a diagnostics engine attaches;
- `records`: oldest first;
- `totals`: counts per outcome and bytes accepted since coverage started, never evicted;
- `evictedRecords`;
- `writesInProgress`: a write still in progress holds back every later record until it
  completes. To read incrementally, pass the last returned record's `sequence` as the next
  `since`. `totals.lastSequence` can be ahead of it and would skip the pending write.

Each record carries:

- its per-session `sequence`, assigned when the write started;
- `source`: `workload-output`, `gated-delivery` (a batch composed for a particular geometry) or
  `terminal-control` (the terminal's own mode and exit sequences);
- `phase`, labelled by the order in which the write actually happened:
  - `before-model` for raw passthrough, and for a batch the console presentation gates on its
    own geometry, which is written before the model applies it;
  - `after-model` for filtered output and for a batch the terminal gates against its own model
    (for example a WebSocket presentation). A diagnostics-enabled Hex1b application
    (`WithDiagnostics()`) adds a presentation filter, so its ordinary output is `after-model`;
  - absent for `terminal-control` writes, and listed in `unavailableFields`;
- `length` and `bytesAccepted`: for a failed console write, the bytes the host took before the
  error. It is absent, and listed in `unavailableFields`, when the presentation cannot observe
  it, as for a failed WebSocket send;
- its interval in the `process-monotonic` clock domain of capture acquisitions;
- `modelSequenceAtStart`;
- `outputSequence`, when the session tracks input milestones. It links the write to the frame
  whose output mark covers it; a frame is never inferred from timing.

Written bytes appear only with the `native-output` authorization, base64 in `content`. The
session keeps the most recent 4,096 records and 1 MiB of written bytes (64 KiB per record);
`truncated` and `bytesEvicted` flag what was cut. Reading the record never writes to the
terminal.

Outcomes:

- Headless terminals and MCP local sessions report `unavailable` / `no-native-presentation`.
- Presentations that cannot report their writes (HMP1 and HWT1 relays, the terminal widget,
  third-party adapters) report `presentation-delivery-unobservable`.
- A terminal without a diagnostics engine records nothing.
- A native write failure is observable only while the process survives it. Closing a host
  terminal that is the process's controlling terminal delivers SIGHUP, which ends the process
  before any write fails. `failed` records come from hosts whose output terminal is not the
  controlling terminal, or from transports.
- A terminal that has been disposed keeps its record readable in-process, including the exit
  sequences written during disposal. The diagnostics socket closes at session end, so the CLI
  and MCP cannot read those last records.
- Writes a presentation makes on its own are not terminal writes and are not recorded: the
  console presentation's cursor-position queries and capability probes. A WebSocket presentation
  holds back an incomplete UTF-8 sequence at the end of a write. The held bytes count in the
  `length` and `content` of the write that carried them, which is `accepted`. They go out at the
  start of the first later message, outside that write's `length` and `content`: completed, or as
  U+FFFD once a later write makes them invalid. A write that only extends them sends nothing and is
  still `accepted`. If no later write resolves them, they are sent as U+FFFD when the presentation
  is disposed, with no record.
- Socket clients read one JSON line per request. The reply begins with a UTF-8 byte-order mark,
  which a raw client must skip (it may arrive in its own read).

## Diagnostic cases

A diagnostic case records a bounded stretch of a terminal's life to a local artifact, so a problem
can be studied after the process is gone. It records the terminal model's events in order: each
output application, resize, and synchronized-update timeout. When authorized, each application
carries the original bytes the model read. The case also records input acceptance and processing,
each published application frame, and each native delivery record. Everything is written on the
target's own filesystem. Nothing is uploaded.

Start a case in one of two ways:

- **At construction**, before the model's first event: `Hex1bTerminalBuilder.WithDiagnosticCase`,
  `hex1b terminal start --record-case`, or `recordCase` on the MCP `start_bash_terminal` /
  `start_pwsh_terminal` tools. The case is armed before the terminal's pumps read any output. The
  case's checkpoint (`fresh-model/1`) is then the complete fresh model, and the recorded bytes
  re-apply from model sequence 0.
- **On a running target**: `hex1b capture case start <id>`, the MCP tool `start_diagnostic_case`,
  or the socket method `case-start`. A model that has already applied output has no re-applicable
  checkpoint, so the case records from now on with its checkpoint `unsupported` (`not-fresh`).

Stop the case with `hex1b capture case stop`, `stop_diagnostic_case`, or `case-stop`. Mark a
boundary while it records with `hex1b capture case mark`, `mark_diagnostic_case`, or `case-mark`.
Read its progress with `hex1b capture case status`, `get_diagnostic_case_status`, or `case-status`.
Re-apply it offline with `hex1b capture case reapply` or `reapply_diagnostic_case`. Inspect
a finished (or broken) artifact offline, without the process that wrote it, with
`hex1b capture case inspect <path>` or `inspect_diagnostic_case`.

### Start request

| Field | Wire name | Values | Default |
|-------|-----------|--------|---------|
| Size bound | `maxBytes` | 1 MiB – 1 GiB | 64 MiB |
| Time bound | `maxSeconds` | 1 – 86,400 | 600 |
| Authorizations | `authorizations` | `reapplication-data` (model input bytes), `raw-input` (sent input text), `editor-text` (the focused editor's text in frames), `native-output` (written bytes) | none: metadata only |
| Storage root | `directory` | an owner-only directory | `~/.hex1b/cases` |

The case directory is created under the root with mode 0700, and every file in it with 0600. Both
modes are verified after creation. An existing root is refused (`storage-refused`) before anything is
written when it is group- or world-accessible, not owned by the current user, a symbolic link, or not
a directory. A root whose mode cannot be set (a read-only mount) is refused as unconfirmed. Ownership
is checked by setting the root's existing mode, which only its owner may do. A superuser therefore
passes for any owner: run cases as an ordinary user when the root may be shared. Clients resolve a
relative directory against their own working directory, not the target's. Without `reapplication-data`, the checkpoint is `excluded` and no model bytes are copied.

### Result

`case-start`, `case-stop` and `case-status` return one result shape:

- `caseId`, `path`, `state` (`recording`, `stopping`, `stopped`), `startPath` (`construction`,
  `live`);
- `bounds`, the granted `authorizations`, and the `checkpoint` (`fresh-model/1`: `complete`,
  `unsupported` or `excluded`, with the reason and the model configuration that determines the
  fresh state);
- `startedAt`, `elapsedSeconds`, `bytesWritten`;
- `streams`: `offered`, `written` and `dropped` for each recorded stream;
- `stopReason` once stopped: `requested`, `size-limit`, `time-limit`, `collector-failed` or
  `target-disposed`.

Problem codes: `invalid-bounds`, `unsupported-authorization` and `invalid-directory`
(`invalid-request`); `case-active`, `no-active-case` and `target-disposed` (`unavailable`);
`storage-refused` (`failed`). One case records a terminal at a time.

### Artifact

A case directory holds three files:

- `manifest.json`: identities, bounds, authorizations, start path, checkpoint, and each stream's
  coverage (`included`, or `unavailable` with why). Format 2 records the model configuration
  structurally: dimensions, scrollback and command-mark capacity, the reflow strategy by name
  (`none`, `kitty`, `xterm`, ...; `custom:` and a type for one this build cannot name), every
  capability field, and the graphics limits;
- `events.jsonl`: one event per line, prefixed by its CRC-32 in hex and a tab;
- `completion.json`: written last, by rename. It holds the stop reason, the last case sequence,
  bytes written, per-stream counts, and checkpoint counts (`checkpoints`: taken, written, and
  declared missing).

Events are numbered by `caseSequence` in file order. Each stream also numbers its own events by
`ordinal`:

| Stream | Events |
|--------|--------|
| `model` | `application` (with the original bytes as base64 `data` under `reapplication-data`), `application-without-ingress`, `resize`, `synchronized-update-timeout`, each with its `modelSequence` and geometry |
| `input` | `accepted` and `processed`, with input ids (text only with `raw-input`) |
| `frames` | `published`: the frame's identity, and its projection (editor text only with `editor-text`) |
| `delivery` | `delivery`: the native delivery record (bytes only with `native-output`) |
| `case` | `missing` ranges, `interval-end`, `stream-failed`, and `checkpoint` |

Streams a target cannot observe are declared `unavailable` in the manifest. For example, frames
are unavailable for PTY workloads, and delivery for headless terminals.

### Checkpoints and marks

A case records a `checkpoint` when it stops (requested, time limit, size limit, or disposal, before
disposal resets anything) and at each mark, but not after a collector failure. A checkpoint is
taken in one hold of the model lock, between two model events, at the model sequence it names.

- With `reapplication-data` it carries the model's full text state (`state`, profile
  `text-state/1`): both screens, retained history, styles, the cursor and saved cursors, modes,
  margins, tab stops, character sets, rendition, titles and the title stack, activity, command
  marks, grapheme continuation, and output held between chunks. Graphics state or a DCS in
  progress is named in `state.unsupported`; such a checkpoint is never compared.
- Without it, the checkpoint records the boundary only (`status: unavailable`,
  `reason: requires reapplication-data`).
- A state too large for the case's size bound is written without it (`status: missing`,
  `reason: size-limit`), and the case keeps recording. A size-limit stop estimates the state's size
  first and does not take one that cannot fit. A checkpoint that cannot be written at all is declared
  by a `missing` range of stream `checkpoint` (checkpoint ordinals).
- State awaiting the writer is bounded at 256 MiB, estimated from the model's geometry before any
  state is taken. A checkpoint past that budget records the boundary only (`unavailable`,
  `pending-state budget`).
- A stop waits at most 2 s for the model lock. When the lock is held longer, the case stops anyway,
  and its stop checkpoint is `unavailable: model-lock-busy`.
- A checkpoint records the model as of its model sequence. A chunk the output pump has read and
  decoded, but not yet applied, is not part of it.
- A checkpoint taken inside an application (a mark or stop from one of its callbacks) would see it
  half applied. It records the boundary only (`unavailable: mid-application`). While the case has
  not yet recorded the unfinished application, the boundary is the model event just before it. For
  a nested application, that event lies inside the unfinished outer one: the outer application
  itself, or a model event raised in its callback. Otherwise the boundary is the model's current
  sequence: a nested model event recorded the application early, the case started inside the
  callback, or its model stream failed.
- A stop that could not take the model lock names the last model event the case recorded, never an
  application the case has not yet recorded (or the model's sequence when the case started, if it
  recorded none). That event can lie inside an unfinished application, or be one: an outer
  application that a nested one recorded early, or the application a case started inside.

A mark (`case-mark`) takes an optional label of 1–64 printable ASCII characters. By default the
label is `mark-` and the checkpoint's ordinal. Labels need not be unique: a re-application target
naming several is refused, with their case sequences. The mark returns the label, the checkpoint
ordinal, the model sequence, and `stateRecorded` (with `stateReason` when false). A mark never
waits for the case writer, so it cannot return the checkpoint's case sequence; that sequence is in
the artifact once the line is written. At most 64 marks can await the writer; a mark beyond that is
refused `busy` before any state is taken. Problem codes: `invalid-label` (`invalid-request`);
`no-active-case` and `busy` (`unavailable`).

A checkpoint costs one pass over the model's cells under its lock: about 0.1–0.25 s and 60 MiB for
250 columns with 10,000 history rows. A case takes none until a mark or its stop.

### Bounds and losses

- **Overload:** the recording queue holds 4,096 events or 8 MiB. Past that it drops the newest
  events and never blocks the terminal. Each drop is recorded as a `missing` range (`overload`)
  outside the queue.
- **Size bound:** the case stops with `size-limit` before a line would cross it. The events it
  could not write become `size-limit` missing ranges. Missing ranges count toward the bound too.
  When they no longer fit, a stream's loss from that point is summarized as one range of unknown
  extent (`size-limit-unknown-extent`). The artifact, completion record included, never exceeds
  `maxBytes`.
- **Time bound:** the case stops with `time-limit`, on the terminal's `TimeProvider`.
- **Stop drain:** a stop waits at most 10 s for queued events. Anything still unwritten becomes a
  `drain-timeout` missing range.
- **Delivery at the stop:** delivery records are the case's up to the last one the terminal made when
  the case stopped; later records are neither written nor counted. Records made before the stop that
  the writer could not pull are declared: `evicted` when the ring overwrote them, `not-pulled`
  otherwise (for example, behind a write still in progress).
- **Failures:** a storage or writer failure stops the case with `collector-failed`. Whatever was
  not written is declared missing (`collector-failed`) when storage still allows it.
- **Disposal:** disposing the terminal stops the case with `target-disposed`. Disposal waits (at
  most the 10 s drain bound) for the artifact to be finished, so a process that exits right after
  disposing its terminal leaves a complete case.
- **Stream failures:** a failure inside one stream marks that stream `failed`, and the others keep
  recording. A model-stream failure also ends the re-applicable interval. It never reaches the
  terminal operation (output, resize) that raised the event.
- **Re-applicable interval:** the model interval ends at the first model event the case cannot
  reproduce:
  - an application without recorded bytes;
  - graphics state the case does not hold;
  - a model event raised in the middle of an application (`reentrant-model-event`, for example a
    title handler that resizes), or a nested application (`reentrant-application`);
  - a missing model event;
  - a failed model stream;
  - the end of the verified file.
- **Re-entrant disposal:** a terminal disposed from inside one of its own callbacks (scrollback,
  title) cannot wait for the case writer. It stops the case and returns; the artifact is finished
  moments later.

### Inspect

Inspection verifies every line's checksum before using it. It reports:

- `completionState`: `complete`, `interrupted` (no completion record: the writer died), or
  `truncated`, with `truncatedAtLine` (the first line that failed its checksum or was torn; the
  verified prefix is every line before it);
- each stream's event count, ordinal range, `state` (`complete`, `incomplete`, `failed`) and
  `missing` ranges. A gap in ordinals that no recorded range explains is reported with reason
  `unknown`. Events the completion record counts as offered, but that were neither written nor
  declared lost, are an `unaccounted` range of unknown extent after the last ordinal;
- the re-applicable model `intervals`;
- a page of events: `since` (a case sequence) and `limit` (1–4,096; none by default).

Problem codes: `invalid-path`, `invalid-limit` and `invalid-since` (`invalid-request`);
`case-not-found` (`unavailable`); `invalid-artifact` (a manifest that does not parse or lacks its
checkpoint, bounds or streams), `unreadable-artifact` and `unsupported-format` (`failed`).

Each inspection reads and verifies the whole artifact, because coverage and the verified prefix
depend on every line. Paging with `since` does not skip that work. Checkpoint state is skipped
while scanning, never deserialized: a page carries checkpoints without it (`stateOmitted: true`),
and re-application reads only its target's. The inspection's `checkpoints` reports checkpoint
coverage: lines written, ranges declared missing, and an `unaccounted` tail when the completion
counts more taken than written or declared. A format 1 case (written before
checkpoints) is still inspected, with its configuration strings left out.

### Re-apply

Re-application reads a case offline and needs nothing from the process that wrote it. It checks
everything first: the case directory must be owner-only, the artifact is verified as inspection
does, and the format must be 2. The configuration must hold every field (a missing or unknown one
is refused, named), with values a model can be built with, and the target must be one this build
can rebuild. Nothing is written until all of that holds. Then it builds a detached model from the recorded configuration, whose pumps never
start and whose presentation is never written, on a virtual clock. It streams the verified
events to that model one line at a time, up to the target:

- each `application` as one raw chunk through the output pump's own path (the model's decoder,
  escape prefix and DCS framer carry across chunks, as they did live);
- each `resize` with its recorded geometry;
- each `synchronized-update-timeout` by advancing the virtual clock exactly the 1 s timeout. The
  clock moves at no other time, so a slow re-application never fires a timeout on its own.

After every event the model's sequence must equal the recorded one; a mismatch ends the run as
`different` at that event. The reconstructed state is then compared with the checkpoint recorded at
the target, field by field:

| `comparison` | Meaning |
|--------------|---------|
| `matched` | every field equal |
| `different` | typed `differences`: paths such as `screen[3][5].text`, `history.rows[12][0].style.foreground` or `modes.wraparound`, in the projection's order, counted per surface (`bySurface`), listed up to `maxDifferences` (default 1,000) |
| `unavailable` | no checkpoint at the target, one without state, or one naming `graphics` or `dcs-continuation` (`comparisonReason` says which) |

The target is a model sequence (`12`; `0` is the fresh model), a case sequence (`case:34`, of a
checkpoint or a model event), or a checkpoint label (`label:name`, or the bare name; a label that is
all digits or starts with `case:` needs `label:`). A label that names several checkpoints is
refused with their case sequences. In a complete case, a model sequence past its last recorded
event is `unknown-model-sequence`. In an interrupted or truncated case, whose tail is unknown, it is
`beyond-interval`.

`--inject-fault` (`faults`) changes the compared state at a declared path, as `kind` or
`kind:target`:
- `cell-text` and `cell-style` take `:<row>/<column>`;
- `mode` takes `:<name>`;
- `history-row` takes `:<index>`, and changes that row's text;
- `cursor`, `title`, `charset`, `tab-stop`, `pending-input` and `history-rows` take no target.

Such a result is labelled `faultInjected`, and is never the recorded path's outcome. A fault the
state has nothing to change for (a history fault without history) makes the comparison
`unavailable` (`fault-not-applicable`).

The result also names what was compared and with what:
- `checkpoint`: the case's `fresh-model/1` checkpoint, with the configuration the model was rebuilt
  from;
- `coverage`: the profile, the surfaces compared, and what it leaves out (clock stamps, write
  sequences, text identities, caller anchors, graphics);
- `producer`: the recording build and process;
- `consumerHex1bVersion`: the build that re-applied it.

Each run writes a new owner-only directory, `reapplications/<n>` in the case, and never changes
the case's own files. It holds `result.json`, the complete reconstructed state (`reapplied.json`),
the recorded state (`recorded.json`), the faulted state that was compared (`faulted.json`, with a
fault), and any previews:

- `reapplied.txt`, `.ansi`, `.svg` and `.html`: the reconstructed model through the capture exporters;
- `recorded.txt`: the recorded checkpoint's screen text (with a `text` preview).

Problem codes: `invalid-path`, `invalid-target`, `invalid-max-differences`, `invalid-fault`,
`invalid-preview`, `unknown-label`, `ambiguous-label`, `unknown-case-sequence`, `not-a-boundary`
and `unknown-model-sequence` (`invalid-request`); `case-not-found`, `incompatible` (an old format;
a missing or unknown configuration field; an impossible value; or an unknown reflow strategy,
capability or projection profile; each named in the message), `no-valid-interval` and
`beyond-interval` (with `lastValidModelSequence` and `intervalEndReason`) (`unavailable`);
`storage-refused`, `storage-failed` (the run's files could not all be written) and
`reapplication-failed` (with `appliedThrough`) (`failed`). The re-applicable interval also ends at
a geometry-gated batch that was refused after its bytes were decoded (`unapplied-output`).

Re-application streams the events file, so its memory is the model and one event, not the file: a
119 MiB events file re-applies in about 133 MiB. It compares only at recorded checkpoints, and only
on the build that recorded the case: a later build may render the same bytes differently.

## Capabilities

`hex1b capture capabilities <id>` and the MCP tool `get_terminal_diagnostic_capabilities`
describe what a target supports: the `capture` operation with its formats, `immediate` timing,
model-history support, and authorizations. They also report each evidence layer:

| Layer | Available | Notes |
|-------|-----------|-------|
| `terminal-model` | yes | Cells, cursor, modes, and retained model history. |
| `application-frame` | Hex1b applications with `WithDiagnostics()` | Operation `application-frame`, timing `latest-published`, authorization `editor-text`. Otherwise unavailable with the reason the operation reports. |
| `native-delivery` | console and WebSocket presentations with a diagnostics engine | Operation `delivery`, timing `immediate`, authorization `native-output`. Otherwise unavailable: `no-native-presentation` or `presentation-delivery-unobservable`. |
| `native-presentation` | no | What a host terminal physically displayed is not observable by Hex1b. |

The operations list also names `case-start` (timing `recorded`, the four case authorizations, and
its limitations): see [Diagnostic cases](#diagnostic-cases).

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
  `timeout` failure. Input, resize, and recording requests are not timed out by the client, and
  neither are case start and stop. Case status is timed out like capture.
- A case started on a running target has no re-applicable checkpoint; only a case started at
  construction can be re-applied from its first byte.
