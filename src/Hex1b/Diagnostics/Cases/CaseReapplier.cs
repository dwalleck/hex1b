using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Hex1b.Automation;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// Re-applies a recorded case offline. Everything is validated before anything is applied: the request, the
/// case directory, the artifact (its verified prefix and valid interval), the format and configuration, and
/// the target. A detached model is then built from the recorded configuration on a virtual clock, and the
/// verified events are streamed to it one line at a time, up to the target: each application as one raw
/// chunk, each resize with its geometry, each synchronized-update timeout by advancing the clock exactly the
/// timeout. After every event its model sequence must equal the recorded one. The reconstructed state is then
/// compared with the checkpoint recorded at the target. The case's own files are only read; each run writes
/// its own owner-only directory.
/// </summary>
internal static class CaseReapplier
{
    /// <summary>Counts model events applied in the current flow while a test has set a counter.</summary>
    internal static readonly AsyncLocal<StrongBox<int>?> AppliedEventsForTesting = new();

    /// <summary>Runs after each applied model event (with its model sequence) while a test has set it.</summary>
    internal static readonly AsyncLocal<Action<long>?> AfterEventForTesting = new();

    /// <summary>A model sequence whose application the reapplier skips while a test has set it.</summary>
    internal static readonly AsyncLocal<long?> SkipApplicationForTesting = new();

    /// <summary>Receives the detached model once built, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action<Hex1bTerminal>?> ReplicaForTesting = new();

    /// <summary>Receives the detached model once it reached the target, before previews, while a test has set it.</summary>
    internal static readonly AsyncLocal<Action<Hex1bTerminal>?> ReconstructedForTesting = new();

    /// <summary>Runs as the detached model is built, while a test has set it (to fail allocation there).</summary>
    internal static readonly AsyncLocal<Action?> BuildReplicaForTesting = new();

    /// <summary>Runs before a run's files are written, while a test has set it (to fail storage there).</summary>
    internal static readonly AsyncLocal<Action?> FinishWritingForTesting = new();

    /// <summary>The preview formats: the reconstructed model through the existing exporters.</summary>
    internal static readonly IReadOnlyList<string> PreviewFormats = ["text", "ansi", "svg", "html"];

    // What every comparison covers and leaves out (spec: the state surface decision).
    private static readonly DiagnosticModelStateCoverage Coverage = new()
    {
        Compared = ModelStateComparer.ComparedSurfaces,
        Excluded = ModelStateComparer.Exclusions,
    };

    internal static DiagnosticCaseReapplyResult Reapply(DiagnosticCaseReapplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        // Every result carries the compatibility checks, however far they got (ticket 14).
        var checks = CaseCompatibility.Begin();
        try
        {
            return checks.Stamp(ReapplyCore(request, checks));
        }
        catch (Exception error)
        {
            // Nothing a case holds may escape as an exception: whatever failed is a result.
            return checks.Stamp(Problem(DiagnosticOutcome.Failed, "reapplication-failed",
                DiagnosticCaseRecorder.Bounded($"{error.GetType().Name}: {error.Message}")) with { Path = request.Path });
        }
    }

    private static DiagnosticCaseReapplyResult ReapplyCore(DiagnosticCaseReapplyRequest request, CaseCompatibility.Checks checks)
    {
        if (ValidateRequest(request) is { } invalid)
            return invalid;
        var maxDifferences = request.MaxDifferences ?? ModelStateComparer.DefaultMaxDifferences;
        var faults = request.Faults ?? [];

        string path;
        try
        {
            path = System.IO.Path.GetFullPath(request.Path);
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-path", "path must name a case directory.");
        }

        if (!Directory.Exists(path))
            return Problem(DiagnosticOutcome.Unavailable, "case-not-found", $"No case directory at '{path}'.");
        if (CaseStorage.CheckCaseDirectory(path) is { } refused)
            return Problem(DiagnosticOutcome.Failed, "storage-refused", refused) with { Path = path };

        // The artifact's format, read raw before the artifact is read further: a format this build does not read is
        // incompatible whatever else the manifest holds (a missing or damaged manifest is the inspection's to describe).
        var rawManifest = ReadRawManifest(path);
        checks.Producer(rawManifest?["identity"] is JsonObject identity && identity["hex1bBuild"] is JsonValue id && id.TryGetValue<string>(out var build) ? build : null);
        if (rawManifest?["formatVersion"] is JsonValue declaredFormat && declaredFormat.TryGetValue<int>(out var format)
            && format != CaseArtifactWriter.LegacyFormatVersion && checks.FormatVersion(format) is { } formatProblem)
            return Problem(DiagnosticOutcome.Unavailable, formatProblem) with { Path = path, Producer = ReadIdentity(rawManifest), Coverage = Coverage };

        var inspection = CaseArtifactReader.Inspect(new DiagnosticCaseInspectRequest { Path = path });
        if (inspection.Outcome != DiagnosticOutcome.Captured)
        {
            // A wrong JSON type in the new required framing policy cannot deserialize as a manifest, but is still
            // a configuration incompatibility, not a case that this build may replay with defaults.
            if (rawManifest?["formatVersion"] is JsonValue currentFormat && currentFormat.TryGetValue<int>(out var current)
                && current == CaseArtifactWriter.FormatVersion && rawManifest["checkpoint"]?["configuration"] is JsonObject rawConfiguration
                && rawConfiguration["dcsFraming"] is not null
                && CaseConfiguration.DcsFramingProblem(rawConfiguration["dcsFraming"]) is { } framingProblem)
                return Problem(DiagnosticOutcome.Unavailable, checks.InvalidDcsFraming(rawConfiguration, framingProblem)) with
                { Path = path, Producer = ReadIdentity(rawManifest), Coverage = Coverage };
            return new DiagnosticCaseReapplyResult { Outcome = inspection.Outcome, Problem = inspection.Problem, Path = path, Producer = rawManifest is null ? null : ReadIdentity(rawManifest) };
        }
        var manifest = inspection.Manifest!;

        // What is compared, with what: every later result carries it.
        var described = new DiagnosticCaseReapplyResult
        {
            Path = path,
            Checkpoint = manifest.Checkpoint,
            Coverage = Coverage,
            Producer = manifest.Identity,
        };
        DiagnosticCaseReapplyResult Refuse(DiagnosticOutcome outcome, string code, string message) =>
            described with { Outcome = outcome, Problem = new DiagnosticProblem { Code = code, Message = message } };
        DiagnosticCaseReapplyResult Refused(DiagnosticProblem problem) => described with { Outcome = DiagnosticOutcome.Unavailable, Problem = problem };

        // The declarations, judged in order before anything is built (CaseCompatibility). A newer format was refused
        // above, before the artifact was read; the legacy format the reader still inspects is refused here, with the
        // checkpoint and coverage the refusal has always carried.
        if (checks.FormatVersion(manifest.FormatVersion) is { } legacyProblem)
            return Refused(legacyProblem);
        if (checks.ContractVersion(manifest.ContractVersion) is { } contractProblem)
            return Refused(contractProblem);
        if (checks.StartProfile(manifest.Checkpoint.Profile, manifest.Checkpoint.ModelSequence) is { } profileProblem)
            return Refused(profileProblem);
        // The initial origin: a fresh model at model sequence 0, or a case started on a model that had applied output
        // at its text-state/3 start's sequence. The complete recovery checkpoints are the later origins; the inspection
        // lists one interval per origin, and the one restored from is the earliest valid one covering the target, or
        // the one the request names.
        var start = manifest.Checkpoint.Profile == DiagnosticCaseCheckpointProfiles.TextState ? manifest.Checkpoint.ModelSequence!.Value : 0;
        var intervals = inspection.Intervals;
        if (manifest.Checkpoint.Configuration is not { } configuration || !intervals.Any(i => i.Valid))
        {
            var first = intervals.FirstOrDefault();
            return Refuse(DiagnosticOutcome.Unavailable, "no-valid-interval",
                $"The case has no re-applicable interval: {first?.EndReason ?? manifest.Checkpoint.Reason}.") with { IntervalEndReason = first?.EndReason };
        }
        if (checks.CoveredSurfaces(manifest.Checkpoint.CoveredSurfaces, manifest.Checkpoint.Status == DiagnosticCaseCheckpointStatus.Complete) is { } surfacesProblem)
            return Refused(surfacesProblem);
        var raw = rawManifest?["checkpoint"]?["configuration"] as JsonObject;
        if (checks.Configuration(raw, configuration) is { } configurationProblem)
            return Refused(configurationProblem);
        if (checks.Capabilities(raw?["capabilities"] as JsonObject, configuration.Capabilities, out var capabilities) is { } capabilitiesProblem)
            return Refused(capabilitiesProblem);
        var strategy = CaseConfiguration.CreateReflowStrategy(configuration.ReflowStrategy)!;

        // The target, resolved from the verified events without applying any (and without reading any state).
        var eventsPath = System.IO.Path.Combine(path, CaseArtifactWriter.EventsFile);
        var resolution = ResolveTarget(request, eventsPath, start);
        if (resolution.Problem is { } targetProblem)
            return described with { Outcome = targetProblem.Outcome, Problem = targetProblem.Problem };
        var target = resolution.ModelSequence;
        var chosen = resolution.Checkpoint;
        if (chosen is { Profile: var profile } && checks.TargetProfile(profile) is { } projectionProblem)
            return Refused(projectionProblem);
        if (target < start)
            return Refuse(DiagnosticOutcome.InvalidRequest, "unknown-model-sequence",
                $"Model sequence {target} is before the case's start, at model sequence {start}: the case recorded nothing before it.");

        var targetRecord = new DiagnosticCaseReapplyTarget
        {
            ModelSequence = target,
            Label = chosen?.Label,
            CaseSequence = chosen?.CaseSequence,
            CheckpointOrdinal = chosen?.Ordinal,
        };

        // A complete case whose model stream lost nothing knows every model sequence it has: one past its last is
        // not a boundary of this case. Otherwise (a lost or unknown tail) it is beyond the re-applicable interval.
        var modelComplete = inspection.Streams.FirstOrDefault(s => s.Stream == "model") is { State: "complete" };
        if (request.ToModelSequence is not null && inspection.CompletionState == DiagnosticCaseCompletionState.Complete && modelComplete
            && target > resolution.LastModelSequence)
            return Refuse(DiagnosticOutcome.InvalidRequest, "unknown-model-sequence",
                $"Model sequence {target} is past the case's last recorded model event, {resolution.LastModelSequence}.") with { Target = targetRecord };

        // The origin, and the interval it covers.
        var selection = CaseOrigins.Select(request.From, intervals, resolution.Checkpoints, target);
        if (selection.Problem is { } originProblem)
            return described with { Outcome = DiagnosticOutcome.InvalidRequest, Problem = originProblem, Target = targetRecord };
        if (selection.Interval is not { Valid: true, Origin: { } origin, FromModelSequence: { } originFrom, ToModelSequence: { } intervalEnd }
            || target < originFrom || target > intervalEnd)
        {
            // Refused before anything is built. A named origin that is not re-applicable says so; otherwise the nearest
            // valid interval before the target says where coverage ends, or, before every valid interval, the initial
            // checkpoint says why it is not an origin.
            var namedInvalid = selection.Interval is { Valid: false } ? selection.Interval : null;
            var nearest = selection.Interval is { Valid: true } named ? named
                : intervals.Where(i => i.Valid && i.FromModelSequence <= target).OrderByDescending(i => i.ToModelSequence).FirstOrDefault();
            var reason = namedInvalid?.EndReason ?? nearest?.EndReason ?? intervals.FirstOrDefault(i => !i.Valid)?.EndReason;
            var message = namedInvalid is { } notReapplicable
                ? $"The origin {(notReapplicable.Origin is { } o ? $"'{o.Label ?? o.Trigger}'" + (o.CheckpointOrdinal is { } ordinal ? $" (checkpoint {ordinal})" : "") : "'start'")} is not re-applicable: {reason}."
                : nearest is { } n
                    ? $"Model sequence {target} is beyond the case's re-applicable interval from model sequence {n.FromModelSequence}, which ends at {n.ToModelSequence}: {reason}."
                    : $"Model sequence {target} is before the case's first re-applicable interval, from model sequence {intervals.First(i => i.Valid).FromModelSequence}; the initial checkpoint is not re-applicable: {reason}.";
            return Refuse(DiagnosticOutcome.Unavailable, "beyond-interval", message) with
            {
                Target = targetRecord,
                LastValidModelSequence = namedInvalid is null ? nearest?.ToModelSequence : null,
                IntervalEndReason = reason,
            };
        }

        // The origin under the profile its line declares, then its state, read and checked before anything is built or written.
        var declaredProfile = origin.Trigger == "start" ? manifest.Checkpoint.Profile
            : resolution.Checkpoints.FirstOrDefault(c => c.CaseSequence == origin.CaseSequence)?.Profile ?? origin.Profile;
        if (checks.Origin(origin, declaredProfile) is { } foreignOrigin)
            return Refused(foreignOrigin);
        DiagnosticModelState? originState = null;
        DiagnosticCaseCheckpointEvent? originCheckpoint = null;
        if (origin.Profile == DiagnosticCaseCheckpointProfiles.TextState)
        {
            try
            {
                originCheckpoint = origin.CaseSequence is { } originLine ? ReadCheckpoint(eventsPath, originLine) : null;
            }
            catch (JsonException error)
            {
                return Refused(checks.Restored(error));
            }
            if (originCheckpoint?.State is not { } state)
                return Refuse(DiagnosticOutcome.Unavailable, "missing-start",
                    $"The case's origin '{origin.Label}' at model sequence {originFrom} is missing from its verified events, or holds no state.");
            if (StartCheckpoint.Unsupported(state) is { Count: > 0 } surfaces)
                return Refuse(DiagnosticOutcome.Unavailable, "unsupported-start",
                    $"The case's origin '{origin.Label}' holds {string.Join(", ", surfaces)}, which this build cannot restore.");
            originState = state;
        }

        DiagnosticCaseCheckpointEvent? recorded = null;
        DiagnosticModelPendingInput? targetPending = null;
        if (chosen is not null)
        {
            try
            {
                recorded = chosen.CaseSequence == origin.CaseSequence ? originCheckpoint : ReadCheckpoint(eventsPath, chosen.CaseSequence);
                // A target that is also the origin is validated by the single restore reconstruction below.
                if (chosen.CaseSequence != origin.CaseSequence && recorded?.State is { Unsupported.Count: 0 } targetState)
                {
                    targetPending = targetState.PendingInput;
                    if (TargetPendingInputProblem(targetPending, configuration.Graphics!.MaximumRetainedInputBytesPerImage) is { } pendingProblem)
                        return Refused(checks.TargetState(new InvalidOperationException(pendingProblem)));
                }
            }
            catch (JsonException error)
            {
                return Refused(checks.TargetState(error));
            }
        }

        // The detached model is built before anything is written: a configuration it refuses writes nothing.
        var clock = new ReapplicationClock(manifest.StartedAt);
        Hex1bTerminal replica;
        try
        {
            replica = BuildReplica(configuration, strategy, capabilities!, clock);
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or OverflowException or OutOfMemoryException)
        {
            return Refused(checks.Rebuilt(error));
        }

        // A distinct comparison target gets one parser-only reconstruction under the rebuilt producer policy.
        // Reject impossible derived state before the origin restores any model fields; never install this candidate.
        if (targetPending?.Dcs is not null)
        {
            try
            {
                using var targetDcs = replica.PrepareDcsRestore(targetPending);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or OverflowException)
            {
                return Refused(checks.TargetState(error));
            }
        }

        if (originState is not null)
        {
            try
            {
                replica.RestoreModelState(originState);
            }
            catch (Exception error) when (error is InvalidOperationException or ArgumentException or FormatException or OverflowException or IndexOutOfRangeException)
            {
                return Refused(checks.Restored(error));
            }
        }
        checks.Restored(fresh: originState is null);

        string run;
        try
        {
            run = CaseStorage.CreateRunDirectory(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Refuse(DiagnosticOutcome.Failed, "storage-refused", error.Message) with { Target = targetRecord };
        }

        // The replica is never started and never disposed: disposal writes terminal-control sequences to its
        // presentation. It holds no process, file or real timer, so the collector releases it.
        var result = described with { Outcome = DiagnosticOutcome.Captured, RunPath = run, Target = targetRecord, Origin = origin };
        var reachedCheckpoint = chosen is null;
        long applied = originFrom;
        try
        {
            ReplicaForTesting.Value?.Invoke(replica);
            foreach (var item in CaseArtifactReader.ReadEvents(eventsPath))
            {
                if (chosen is not null && item.CaseSequence == chosen.CaseSequence)
                    reachedCheckpoint = true;
                if (item.Stream == "model" && item.ModelSequence is { } sequence && sequence > originFrom && sequence <= target)
                {
                    if (Apply(replica, clock, item, sequence) is { } divergence)
                        return Finish(checks, result with { AppliedThrough = applied, Comparison = "different", ComparisonReason = divergence }, run);
                    applied = sequence;
                    if (AppliedEventsForTesting.Value is { } counter)
                        Interlocked.Increment(ref counter.Value);
                    AfterEventForTesting.Value?.Invoke(sequence);
                }

                if (applied >= target && reachedCheckpoint)
                    break;
            }

            if (applied != target)
                return Finish(checks, result with { AppliedThrough = applied, Comparison = "different", ComparisonReason = $"The verified events end at model sequence {applied}, before the target {target}." }, run);

            var reconstructed = replica.CaptureModelState();
            ReconstructedForTesting.Value?.Invoke(replica);
            var previews = RenderPreviews(replica, request.Previews ?? []);
            result = result with { AppliedThrough = applied };

            // Faults change a copy: reapplied.json is always the reconstruction, faulted.json what was compared.
            var compared = reconstructed;
            var injected = new List<DiagnosticCaseInjectedFault>();
            foreach (var fault in faults)
            {
                if (ModelStateFault.Apply(compared, fault, out var faultPath, out var faultProblem) is not { } faulted)
                {
                    return Finish(checks, result with { Comparison = "unavailable", ComparisonReason = $"fault-not-applicable: {faultProblem}" },
                        run, reconstructed, null, null, previews);
                }
                compared = faulted;
                injected.Add(new DiagnosticCaseInjectedFault { Kind = fault, Path = faultPath! });
            }

            result = result with { FaultInjected = injected.Count > 0, Faults = injected };
            var recordedState = recorded?.State;
            if (chosen is null)
                result = result with { Comparison = "unavailable", ComparisonReason = $"no-checkpoint: no checkpoint was recorded at model sequence {target}." };
            else if (recorded is null || recorded.Status != "recorded" || recordedState is null)
                result = result with { Comparison = "unavailable", ComparisonReason = $"checkpoint {recorded?.Status ?? chosen.Status}: {recorded?.Reason ?? "its state was not read"}." };
            else if (recordedState.Unsupported.Count > 0 || compared.Unsupported.Count > 0)
                result = result with
                {
                    Comparison = "unavailable",
                    ComparisonReason = $"unsupported: {string.Join(", ", recordedState.Unsupported.Union(compared.Unsupported).Order(StringComparer.Ordinal))}.",
                };
            else
            {
                var comparison = ModelStateComparer.Compare(recordedState, compared, maxDifferences);
                result = result with { Comparison = comparison.Total == 0 ? "matched" : "different", Differences = comparison };
            }

            // The recorded checkpoint's preview is its text, rendered from its projection (approval item 3).
            if (recordedState is not null && previews.ContainsKey("reapplied.txt"))
                previews["recorded.txt"] = ProjectionText(recordedState);
            return Finish(checks, result, run, reconstructed, recordedState, injected.Count > 0 ? compared : null, previews);
        }
        catch (Exception error)
        {
            return Finish(checks, result with
            {
                Outcome = DiagnosticOutcome.Failed,
                Problem = new DiagnosticProblem
                {
                    Code = "reapplication-failed",
                    Message = DiagnosticCaseRecorder.Bounded($"{error.GetType().Name}: {error.Message}"),
                },
                AppliedThrough = applied,
            }, run);
        }
    }

    private static Hex1bTerminal BuildReplica(DiagnosticCaseModelConfiguration configuration, Reflow.ITerminalReflowProvider strategy,
        TerminalCapabilities capabilities, ReapplicationClock clock)
    {
        BuildReplicaForTesting.Value?.Invoke();
        var presentation = new HeadlessPresentationAdapter(configuration.Width, configuration.Height, capabilities)
            .WithReflowStrategy(strategy, configuration.ReflowEnabled);
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = new DetachedWorkload(),
            Width = configuration.Width,
            Height = configuration.Height,
            ScrollbackCapacity = configuration.ScrollbackCapacity,
            CommandMarkHistoryCapacity = configuration.CommandMarkHistoryCapacity,
            EscapeSequenceTimeout = TimeSpan.FromMilliseconds(configuration.EscapeSequenceTimeoutMs),
            TimeProvider = clock,
            DeferStart = true,
        };
        if (configuration.CustomMarkerLimit is { } markers)
            options.CustomMarkerLimit = markers;
        ApplyGraphics(options.Graphics, configuration.Graphics!);
        options.SixelPolicy = options.CreateSixelPolicy() with
        {
            MaximumDcsHeaderParameters = configuration.DcsFraming!.MaximumHeaderParameters,
            MaximumNumericValue = configuration.DcsFraming.MaximumNumericValue,
        };
        return new Hex1bTerminal(options);
    }

    // Applies one model event; returns where the reconstructed model diverged from the recording, or null.
    private static string? Apply(Hex1bTerminal replica, ReapplicationClock clock, DiagnosticCaseEvent item, long sequence)
    {
        switch (item.Kind)
        {
            case "application":
                if (item.Width is { } width && item.Height is { } height && (replica.Width != width || replica.Height != height))
                    return $"Model sequence {sequence} (case sequence {item.CaseSequence}) began at {width}x{height}; the reconstructed model is {replica.Width}x{replica.Height}.";
                var payload = item.Data is { } data ? Convert.FromBase64String(data)
                    : throw new InvalidDataException($"Application {sequence} has no payload.");
                if (SkipApplicationForTesting.Value != sequence)
                    replica.ApplyRecordedOutput(payload);
                break;
            case "resize":
                replica.Resize(item.Width ?? throw new InvalidDataException($"Resize {sequence} has no width."),
                    item.Height ?? throw new InvalidDataException($"Resize {sequence} has no height."));
                break;
            case "synchronized-update-timeout":
                clock.Advance(Hex1bTerminal.SynchronizedOutputTimeout);
                break;
            default:
                throw new InvalidDataException($"Model event {sequence} has kind '{item.Kind}', which cannot be re-applied.");
        }

        var reached = replica.CurrentModelSequence;
        return reached == sequence
            ? null
            : $"After model sequence {sequence} (case sequence {item.CaseSequence}, {item.Kind}) the reconstructed model is at {reached}.";
    }

    private static DiagnosticCaseReapplyResult? ValidateRequest(DiagnosticCaseReapplyRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Path))
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-path", "path must name a case directory.");
        var targets = (request.ToModelSequence is null ? 0 : 1) + (request.ToLabel is null ? 0 : 1) + (request.ToCaseSequence is null ? 0 : 1);
        if (targets != 1)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-target", "Name exactly one target: a model sequence, a label, or a case sequence.");
        if (request.ToModelSequence is < 0 || request.ToCaseSequence is < 1 || request.ToLabel is { Length: 0 })
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-target", "A model sequence is 0 or more, a case sequence 1 or more, and a label is not empty.");
        if (request.MaxDifferences is < 1 or > ModelStateComparer.MaxMaxDifferences)
            return Problem(DiagnosticOutcome.InvalidRequest, "invalid-max-differences", $"maxDifferences must be 1 to {ModelStateComparer.MaxMaxDifferences:N0}.");
        foreach (var preview in request.Previews ?? [])
        {
            if (!PreviewFormats.Contains(preview))
                return Problem(DiagnosticOutcome.InvalidRequest, "invalid-preview", $"Unknown preview '{preview}'; previews are {string.Join(", ", PreviewFormats)}.");
        }
        foreach (var fault in request.Faults ?? [])
        {
            if (ModelStateFault.Validate(fault) is { } faultProblem)
                return Problem(DiagnosticOutcome.InvalidRequest, "invalid-fault", faultProblem);
        }
        return null;
    }

    internal sealed record CheckpointLine(long CaseSequence, long ModelSequence, long Ordinal, string Label, string Status, string Profile, string Trigger);

    private sealed record Resolution(long ModelSequence, CheckpointLine? Checkpoint, DiagnosticCaseReapplyResult? Problem, long LastModelSequence = 0)
    {
        /// <summary>The start checkpoint line (trigger <c>start</c>), when the case has one.</summary>
        public CheckpointLine? Start { get; init; }

        /// <summary>Every verified checkpoint line, for naming an origin.</summary>
        public List<CheckpointLine> Checkpoints { get; init; } = [];
    }

    // One requested checkpoint, read before replay so required schema failures cannot be mistaken for absent state.
    private static DiagnosticCaseCheckpointEvent? ReadCheckpoint(string eventsPath, long caseSequence)
    {
        foreach (var item in CaseArtifactReader.ReadEvents(eventsPath, caseSequence))
        {
            if (item.CaseSequence == caseSequence)
                return item.Checkpoint;
        }
        return null;
    }

    // Bound and validate the target's required DCS fields before rebuilding the replica. Its actual base64 and derived
    // parser state are checked once by the production candidate reconstruction, before any model mutation or replay.
    private static string? TargetPendingInputProblem(DiagnosticModelPendingInput? pending, int maximumRetainedDcsBytes)
    {
        if (pending is null)
            return "pendingInput: missing";
        if (pending.Dcs is not { } dcs)
            return null;
        if (dcs.ByteCount > maximumRetainedDcsBytes)
            return "pendingInput.dcs.byteCount: exceeds the configured retention limit";
        return Hex1bTerminal.PendingInputProblem(pending);
    }

    // One streaming pass over the verified events: the checkpoints (without their state), the start line, and, for a
    // case-sequence target, the line it names. Model sequences before `start` are not the case's.
    private static Resolution ResolveTarget(DiagnosticCaseReapplyRequest request, string eventsPath, long start)
    {
        var checkpoints = new List<CheckpointLine>();
        DiagnosticCaseEvent? named = null;
        long last = start;
        foreach (var item in CaseArtifactReader.ReadEvents(eventsPath))
        {
            if (item.Stream == "model" && item.ModelSequence is { } modelEvent)
                last = Math.Max(last, modelEvent);
            if (item.Checkpoint is { } checkpoint && item.ModelSequence is { } at)
                checkpoints.Add(new CheckpointLine(item.CaseSequence, at, checkpoint.Ordinal, checkpoint.Label, checkpoint.Status, checkpoint.Profile, checkpoint.Trigger));
            if (item.CaseSequence == request.ToCaseSequence)
                named = item with { Checkpoint = null, Data = null };
        }

        // The start is the first checkpoint line, and only it has trigger start.
        var startLine = checkpoints.FirstOrDefault() is { Trigger: "start" } first ? first : null;
        if (request.ToLabel is { } label)
        {
            var matches = checkpoints.Where(c => c.Label == label).ToList();
            if (matches.Count == 0)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "unknown-label", $"No checkpoint is labelled '{label}'."));
            if (matches.Count > 1)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "ambiguous-label",
                    $"{matches.Count} checkpoints are labelled '{label}', at case sequences {string.Join(", ", matches.Select(m => m.CaseSequence))}; name one by case sequence."));
            return new Resolution(matches[0].ModelSequence, matches[0], null, last) { Start = startLine, Checkpoints = checkpoints };
        }

        if (request.ToCaseSequence is { } caseSequence)
        {
            if (named is null)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "unknown-case-sequence", $"No verified event has case sequence {caseSequence}."));
            if (checkpoints.FirstOrDefault(c => c.CaseSequence == caseSequence) is { } line)
                return new Resolution(line.ModelSequence, line, null, last) { Start = startLine, Checkpoints = checkpoints };
            if (named.Stream != "model" || named.ModelSequence is not { } modelSequence)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "not-a-boundary",
                    $"Case sequence {caseSequence} is a {named.Stream} {named.Kind} event, not a model event or a checkpoint."));
            return new Resolution(modelSequence, AtSequence(checkpoints, modelSequence), null, last) { Start = startLine, Checkpoints = checkpoints };
        }

        var target = request.ToModelSequence!.Value;
        return new Resolution(target, AtSequence(checkpoints, target), null, last) { Start = startLine, Checkpoints = checkpoints };
    }

    // The checkpoint compared at a model sequence: the first with state, else the first.
    private static CheckpointLine? AtSequence(List<CheckpointLine> checkpoints, long modelSequence) =>
        checkpoints.Where(c => c.ModelSequence == modelSequence).OrderBy(c => c.Status == "recorded" ? 0 : 1).ThenBy(c => c.Ordinal).FirstOrDefault();

    private static void ApplyGraphics(Hex1bTerminalGraphicsOptions options, DiagnosticCaseGraphicsLimits recorded)
    {
        options.MaximumRetainedInputBytesPerImage = recorded.MaximumRetainedInputBytesPerImage;
        options.MaximumRasterPixelsPerImage = recorded.MaximumRasterPixelsPerImage;
        options.MaximumRasterOperationsPerImage = recorded.MaximumRasterOperationsPerImage;
        options.MaximumImagesPerScreen = recorded.MaximumImagesPerScreen;
        options.MaximumPlacementsPerScreen = recorded.MaximumPlacementsPerScreen;
        options.MaximumHistoryPlacements = recorded.MaximumHistoryPlacements;
        options.MaximumRetainedLogicalPixelsPerScreen = recorded.MaximumRetainedLogicalPixelsPerScreen;
        options.MaximumRetainedBytesPerScreen = recorded.MaximumRetainedBytesPerScreen;
    }

    // Writes the run's files: the complete reconstructed and recorded states (when there are any) and the result.
    // Previews of the reconstructed model, through the existing exporters; a fault changes only the compared
    // projection, never the model these render.
    private static Dictionary<string, string> RenderPreviews(Hex1bTerminal replica, IReadOnlyList<string> formats)
    {
        var previews = new Dictionary<string, string>(StringComparer.Ordinal);
        if (formats.Count == 0)
            return previews;
        using var snapshot = replica.CreateSnapshot();
        foreach (var format in formats.Distinct())
        {
            previews[$"reapplied.{(format == "text" ? "txt" : format)}"] = format switch
            {
                "text" => snapshot.GetScreenText(),
                "ansi" => snapshot.ToAnsi(),
                "svg" => snapshot.ToSvg(),
                _ => snapshot.ToHtml(),
            };
        }
        return previews;
    }

    // A projection's screen as text: each row's cells in order, rows separated by newlines.
    // With the text exporter's rules (GetLine), so the recorded and reconstructed text previews compare: an empty
    // cell is a space unless it continues a wide glyph, and the private-use and NUL fillers are spaces.
    internal static string ProjectionText(DiagnosticModelState state) =>
        string.Join("\n", state.Screen.Select(row =>
        {
            var text = new System.Text.StringBuilder(row.Cells.Count);
            for (var column = 0; column < row.Cells.Count; column++)
            {
                var cell = row.Cells[column].Text;
                if (string.IsNullOrEmpty(cell))
                {
                    var previous = column > 0 ? row.Cells[column - 1].Text : null;
                    var continuation = !string.IsNullOrEmpty(previous) && previous != "\uE000" && previous != "\0"
                        && DisplayWidth.GetGraphemeWidth(previous) > 1;
                    if (!continuation)
                        text.Append(' ');
                }
                else
                {
                    text.Append(cell is "\uE000" or "\0" ? " " : cell);
                }
            }
            return text.ToString();
        }));

    // Writes the run's files: the complete reconstructed, recorded and (with faults) compared states, any previews,
    // and the result. Best effort: a write that fails makes the result `failed` (naming what was written) rather
    // than an exception.
    private static DiagnosticCaseReapplyResult Finish(CaseCompatibility.Checks checks, DiagnosticCaseReapplyResult result, string run, DiagnosticModelState? reconstructed = null,
        DiagnosticModelState? recorded = null, DiagnosticModelState? faulted = null, IReadOnlyDictionary<string, string>? previews = null)
    {
        var files = new List<string>();
        void Write(string name, byte[] bytes)
        {
            using var stream = CaseStorage.CreateFile(System.IO.Path.Combine(run, name));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            files.Add(name);
        }

        try
        {
            FinishWritingForTesting.Value?.Invoke();
            if (reconstructed is not null)
                Write("reapplied.json", JsonSerializer.SerializeToUtf8Bytes(reconstructed, DiagnosticsJsonContext.Default.DiagnosticModelState));
            if (recorded is not null)
                Write("recorded.json", JsonSerializer.SerializeToUtf8Bytes(recorded, DiagnosticsJsonContext.Default.DiagnosticModelState));
            if (faulted is not null)
                Write("faulted.json", JsonSerializer.SerializeToUtf8Bytes(faulted, DiagnosticsJsonContext.Default.DiagnosticModelState));
            foreach (var (name, content) in previews ?? new Dictionary<string, string>())
                Write(name, System.Text.Encoding.UTF8.GetBytes(content));
            var final = checks.Stamp(result with { Files = [.. files, "result.json"] });
            Write("result.json", JsonSerializer.SerializeToUtf8Bytes(final, DiagnosticsJsonContext.Default.DiagnosticCaseReapplyResult));
            return final;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return result with
            {
                Outcome = DiagnosticOutcome.Failed,
                Problem = new DiagnosticProblem
                {
                    Code = "storage-failed",
                    Message = DiagnosticCaseRecorder.Bounded($"The run's files could not all be written: {error.Message}"),
                },
                Files = files,
            };
        }
    }

    private static DiagnosticCaseReapplyResult Problem(DiagnosticOutcome outcome, string code, string message) =>
        Problem(outcome, new DiagnosticProblem { Code = code, Message = message });

    private static DiagnosticCaseReapplyResult Problem(DiagnosticOutcome outcome, DiagnosticProblem problem) => new()
    {
        Outcome = outcome,
        Problem = problem,
    };

    // The recording build's identity from a manifest the inspection has not read (a format this build does not read):
    // best effort, so a refusal still names the producer when the identity's shape is the one this build knows.
    private static DiagnosticObservationIdentity? ReadIdentity(JsonObject manifest)
    {
        try
        {
            return manifest["identity"] is { } identity ? identity.Deserialize(DiagnosticsJsonContext.Default.DiagnosticObservationIdentity) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // The manifest as raw JSON, for the declarations the typed manifest does not keep (the configuration's field
    // names) and the format judged before the artifact is read: null when it is missing or damaged, which the
    // inspection describes.
    private static JsonObject? ReadRawManifest(string path)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllBytes(System.IO.Path.Combine(path, CaseArtifactWriter.ManifestFile))) as JsonObject;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    // The detached model's workload: its pumps never start, so nothing reads it or writes input to it. Those
    // calls are counted, so a test can show none was made.
    internal sealed class DetachedWorkload : IHex1bTerminalWorkloadAdapter
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

        // The replica answers no protocol query (DA1, DSR, ...) found in the recorded output: nothing is listening.
        public bool HandlesProtocolQueries => true;

        public ValueTask<ReadOnlyMemory<byte>> ReadOutputAsync(CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.FromResult(ReadOnlyMemory<byte>.Empty);
        }

        public ValueTask WriteInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
        {
            Interlocked.Increment(ref _calls);
            return ValueTask.CompletedTask;
        }

        // A resize is a notification the detached model sends; nothing receives it.
        public ValueTask ResizeAsync(int width, int height, CancellationToken ct = default) => ValueTask.CompletedTask;

        public event Action? Disconnected { add { } remove { } }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
