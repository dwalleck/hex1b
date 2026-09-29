using System.Runtime.CompilerServices;
using System.Text.Json;
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

    /// <summary>The preview formats: the reconstructed model through the existing exporters.</summary>
    internal static readonly IReadOnlyList<string> PreviewFormats = ["text", "ansi", "svg", "html"];

    private static readonly TimeSpan SynchronizedUpdateTimeout = TimeSpan.FromSeconds(1);

    internal static DiagnosticCaseReapplyResult Reapply(DiagnosticCaseReapplyRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
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

        var inspection = CaseArtifactReader.Inspect(new DiagnosticCaseInspectRequest { Path = path });
        if (inspection.Outcome != DiagnosticOutcome.Captured)
            return new DiagnosticCaseReapplyResult { Outcome = inspection.Outcome, Problem = inspection.Problem, Path = path };
        var manifest = inspection.Manifest!;

        // Format and configuration: refused before anything is built.
        if (manifest.FormatVersion != CaseArtifactWriter.FormatVersion)
            return Incompatible(path, $"formatVersion: format {manifest.FormatVersion} does not record its configuration structurally; re-application needs format {CaseArtifactWriter.FormatVersion}.");
        if (manifest.Checkpoint.Profile != DiagnosticCaseCheckpointProfiles.FreshModel)
            return Incompatible(path, $"checkpoint.profile: unknown profile '{manifest.Checkpoint.Profile}'.");
        var interval = inspection.Intervals.FirstOrDefault();
        if (manifest.Checkpoint.Status != DiagnosticCaseCheckpointStatus.Complete || manifest.Checkpoint.Configuration is not { } configuration
            || interval is not { Valid: true, ToModelSequence: { } intervalEnd })
        {
            return Problem(DiagnosticOutcome.Unavailable, "no-valid-interval",
                $"The case has no re-applicable interval: {interval?.EndReason ?? manifest.Checkpoint.Reason}.") with { Path = path, IntervalEndReason = interval?.EndReason };
        }
        if (CaseConfiguration.CreateReflowStrategy(configuration.ReflowStrategy) is not { } strategy)
            return Incompatible(path, $"reflowStrategy: '{configuration.ReflowStrategy}' is not a strategy this build can rebuild.");
        if (CaseConfiguration.TerminalCapabilities(configuration.Capabilities, out var capabilitiesProblem) is not { } capabilities)
            return Incompatible(path, capabilitiesProblem!);
        if (configuration.Graphics.Unknown is { Count: > 0 } unknownGraphics)
            return Incompatible(path, $"graphics.{unknownGraphics.Keys.Order(StringComparer.Ordinal).First()}: unknown field");

        // The target, resolved from the verified events without applying any.
        var eventsPath = System.IO.Path.Combine(path, CaseArtifactWriter.EventsFile);
        var resolution = ResolveTarget(request, eventsPath);
        if (resolution.Problem is { } targetProblem)
            return targetProblem with { Path = path };
        var target = resolution.ModelSequence;
        var chosen = resolution.Checkpoint;
        if (chosen is { Profile: var profile } && profile != DiagnosticCaseCheckpointProfiles.TextState)
            return Incompatible(path, $"checkpoint.profile: unknown projection profile '{profile}'.");
        var targetRecord = new DiagnosticCaseReapplyTarget
        {
            ModelSequence = target,
            Label = chosen?.Label,
            CaseSequence = chosen?.CaseSequence,
            CheckpointOrdinal = chosen?.Ordinal,
        };
        if (target > intervalEnd)
        {
            return Problem(DiagnosticOutcome.Unavailable, "beyond-interval",
                $"Model sequence {target} is beyond the case's re-applicable interval, which ends at {intervalEnd}: {interval.EndReason}.") with
            {
                Path = path,
                Target = targetRecord,
                LastValidModelSequence = intervalEnd,
                IntervalEndReason = interval.EndReason,
            };
        }

        string run;
        try
        {
            run = CaseStorage.CreateRunDirectory(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            return Problem(DiagnosticOutcome.Failed, "storage-refused", error.Message) with { Path = path, Target = targetRecord };
        }

        var result = new DiagnosticCaseReapplyResult { Outcome = DiagnosticOutcome.Captured, Path = path, RunPath = run, Target = targetRecord };
        var clock = new ReapplicationClock(manifest.StartedAt);
        var workload = new DetachedWorkload();
        var presentation = new HeadlessPresentationAdapter(configuration.Width, configuration.Height, capabilities)
            .WithReflowStrategy(strategy, configuration.ReflowEnabled);
        var options = new Hex1bTerminalOptions
        {
            PresentationAdapter = presentation,
            WorkloadAdapter = workload,
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
        ApplyGraphics(options.Graphics, configuration.Graphics);

        DiagnosticCaseCheckpointEvent? recorded = null;
        long applied = 0;
        Hex1bTerminal? replica = null;
        try
        {
            replica = new Hex1bTerminal(options);
            ReplicaForTesting.Value?.Invoke(replica);
            foreach (var item in CaseArtifactReader.ReadEvents(eventsPath))
            {
                if (chosen is not null && item.CaseSequence == chosen.CaseSequence)
                    recorded = item.Checkpoint;
                if (item.Stream == "model" && item.ModelSequence is { } sequence && sequence <= target)
                {
                    if (Apply(replica, clock, item, sequence) is { } divergence)
                        return Finish(result with { AppliedThrough = applied, Comparison = "different", ComparisonReason = divergence }, run, null, null);
                    applied = sequence;
                    if (AppliedEventsForTesting.Value is { } counter)
                        Interlocked.Increment(ref counter.Value);
                    AfterEventForTesting.Value?.Invoke(sequence);
                }

                if (applied >= target && (chosen is null || recorded is not null))
                    break;
            }

            if (applied != target)
                return Finish(result with { AppliedThrough = applied, Comparison = "different", ComparisonReason = $"The verified events end at model sequence {applied}, before the target {target}." }, run, null, null);

            var reconstructed = replica.CaptureModelState();
            ReconstructedForTesting.Value?.Invoke(replica);
            var previews = RenderPreviews(replica, request.Previews ?? []);
            var injected = new List<DiagnosticCaseInjectedFault>();
            foreach (var kind in faults)
            {
                if (ModelStateFault.Apply(reconstructed, kind, out var faultPath, out var faultProblem) is not { } faulted)
                {
                    return Finish(result with
                    {
                        Outcome = DiagnosticOutcome.InvalidRequest,
                        Problem = new DiagnosticProblem { Code = "fault-not-applicable", Message = faultProblem! },
                        AppliedThrough = applied,
                    }, run, reconstructed, null, previews);
                }
                reconstructed = faulted;
                injected.Add(new DiagnosticCaseInjectedFault { Kind = kind, Path = faultPath! });
            }

            result = result with { AppliedThrough = applied, FaultInjected = injected.Count > 0, Faults = injected };
            var recordedState = recorded?.State;
            if (chosen is null)
                result = result with { Comparison = "unavailable", ComparisonReason = $"no-checkpoint: no checkpoint was recorded at model sequence {target}." };
            else if (recorded is null || recorded.Status != "recorded" || recordedState is null)
                result = result with { Comparison = "unavailable", ComparisonReason = $"checkpoint {recorded?.Status ?? chosen.Status}: {recorded?.Reason ?? "its state was not read"}." };
            else if (recordedState.Unsupported.Count > 0 || reconstructed.Unsupported.Count > 0)
                result = result with
                {
                    Comparison = "unavailable",
                    ComparisonReason = $"unsupported: {string.Join(", ", recordedState.Unsupported.Union(reconstructed.Unsupported).Order(StringComparer.Ordinal))}.",
                };
            else
            {
                var comparison = ModelStateComparer.Compare(recordedState, reconstructed, maxDifferences);
                result = result with { Comparison = comparison.Total == 0 ? "matched" : "different", Differences = comparison };
            }

            // The recorded checkpoint's preview is its text, rendered from its projection (approval item 3).
            if (recordedState is not null && previews.ContainsKey("reapplied.txt"))
                previews["recorded.txt"] = ProjectionText(recordedState);
            return Finish(result, run, reconstructed, recordedState, previews);
        }
        catch (Exception error)
        {
            return Finish(result with
            {
                Outcome = DiagnosticOutcome.Failed,
                Problem = new DiagnosticProblem
                {
                    Code = "reapplication-failed",
                    Message = DiagnosticCaseRecorder.Bounded($"{error.GetType().Name}: {error.Message}"),
                },
                AppliedThrough = applied,
            }, run, null, null);
        }
        finally
        {
            replica?.Dispose();
        }
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
                clock.Advance(SynchronizedUpdateTimeout);
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
            if (!ModelStateFault.Kinds.Contains(fault))
                return Problem(DiagnosticOutcome.InvalidRequest, "invalid-fault", $"Unknown fault '{fault}'; declared faults are {string.Join(", ", ModelStateFault.Kinds)}.");
        }
        return null;
    }

    private sealed record CheckpointLine(long CaseSequence, long ModelSequence, long Ordinal, string Label, string Status, string Profile);

    private sealed record Resolution(long ModelSequence, CheckpointLine? Checkpoint, DiagnosticCaseReapplyResult? Problem);

    // One streaming pass over the verified events: the checkpoints (without their state) and, for a case-sequence
    // target, the line it names.
    private static Resolution ResolveTarget(DiagnosticCaseReapplyRequest request, string eventsPath)
    {
        var checkpoints = new List<CheckpointLine>();
        DiagnosticCaseEvent? named = null;
        foreach (var item in CaseArtifactReader.ReadEvents(eventsPath))
        {
            if (item.Checkpoint is { } checkpoint && item.ModelSequence is { } at)
                checkpoints.Add(new CheckpointLine(item.CaseSequence, at, checkpoint.Ordinal, checkpoint.Label, checkpoint.Status, checkpoint.Profile));
            if (item.CaseSequence == request.ToCaseSequence)
                named = item with { Checkpoint = null, Data = null };
        }

        if (request.ToLabel is { } label)
        {
            var matches = checkpoints.Where(c => c.Label == label).ToList();
            if (matches.Count == 0)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "unknown-label", $"No checkpoint is labelled '{label}'."));
            if (matches.Count > 1)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "ambiguous-label",
                    $"{matches.Count} checkpoints are labelled '{label}', at case sequences {string.Join(", ", matches.Select(m => m.CaseSequence))}; name one by case sequence."));
            return new Resolution(matches[0].ModelSequence, matches[0], null);
        }

        if (request.ToCaseSequence is { } caseSequence)
        {
            if (named is null)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "unknown-case-sequence", $"No verified event has case sequence {caseSequence}."));
            if (checkpoints.FirstOrDefault(c => c.CaseSequence == caseSequence) is { } line)
                return new Resolution(line.ModelSequence, line, null);
            if (named.Stream != "model" || named.ModelSequence is not { } modelSequence)
                return new Resolution(0, null, Problem(DiagnosticOutcome.InvalidRequest, "not-a-boundary",
                    $"Case sequence {caseSequence} is a {named.Stream} {named.Kind} event, not a model event or a checkpoint."));
            return new Resolution(modelSequence, AtSequence(checkpoints, modelSequence), null);
        }

        var target = request.ToModelSequence!.Value;
        return new Resolution(target, AtSequence(checkpoints, target), null);
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
    internal static string ProjectionText(DiagnosticModelState state) =>
        string.Join("\n", state.Screen.Select(row => string.Concat(row.Cells.Select(cell => cell.Text))));

    private static DiagnosticCaseReapplyResult Finish(DiagnosticCaseReapplyResult result, string run, DiagnosticModelState? reconstructed,
        DiagnosticModelState? recorded, IReadOnlyDictionary<string, string>? previews = null)
    {
        var files = new List<string>();
        void Write(string name, byte[] bytes)
        {
            using var stream = CaseStorage.CreateFile(System.IO.Path.Combine(run, name));
            stream.Write(bytes);
            stream.Flush(flushToDisk: true);
            files.Add(name);
        }

        if (reconstructed is not null)
            Write("reapplied.json", JsonSerializer.SerializeToUtf8Bytes(reconstructed, DiagnosticsJsonContext.Default.DiagnosticModelState));
        if (recorded is not null)
            Write("recorded.json", JsonSerializer.SerializeToUtf8Bytes(recorded, DiagnosticsJsonContext.Default.DiagnosticModelState));
        foreach (var (name, content) in previews ?? new Dictionary<string, string>())
            Write(name, System.Text.Encoding.UTF8.GetBytes(content));
        files.Add("result.json");
        var final = result with { Files = files };
        Write("result.json", JsonSerializer.SerializeToUtf8Bytes(final, DiagnosticsJsonContext.Default.DiagnosticCaseReapplyResult));
        return final;
    }

    private static DiagnosticCaseReapplyResult Incompatible(string path, string message) =>
        Problem(DiagnosticOutcome.Unavailable, "incompatible", message) with { Path = path };

    private static DiagnosticCaseReapplyResult Problem(DiagnosticOutcome outcome, string code, string message) => new()
    {
        Outcome = outcome,
        Problem = new DiagnosticProblem { Code = code, Message = message },
    };

    // The detached model's workload: its pumps never start, so nothing reads it or writes input to it. Those
    // calls are counted, so a test can show none was made.
    internal sealed class DetachedWorkload : IHex1bTerminalWorkloadAdapter
    {
        private int _calls;

        internal int Calls => Volatile.Read(ref _calls);

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
