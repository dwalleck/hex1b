using System.Text.Json.Nodes;

namespace Hex1b.Diagnostics.Cases;

/// <summary>
/// The compatibility judgement between a recorded case and the build re-applying it (ticket 14): what this build
/// declares, and the seven checks in the order the reapplier runs them, each recording the artifact's value, this
/// build's value and a verdict. A check that fails refuses the re-application with problem code
/// <see cref="Code"/>; the checks after it stay <c>not-checked</c>. The reapplier drives one
/// <see cref="Checks"/> per run, calling each check when it holds its input, and attaches
/// <see cref="Checks.Record"/> to every result.
/// </summary>
internal static class CaseCompatibility
{
    /// <summary>The problem code of a refusal by any check.</summary>
    internal const string Code = "incompatible";

    /// <summary>What a consumer build declares: the versions it reads and speaks, the checkpoint profiles it projects and the surfaces they cover.</summary>
    internal sealed record Consumer(int FormatVersion, int ContractVersion, IReadOnlyList<string> Profiles, IReadOnlyList<string> Surfaces,
        string Hex1bVersion, string? Hex1bBuild)
    {
        /// <summary>This build's declarations.</summary>
        internal static readonly Consumer Build = new(CaseArtifactWriter.FormatVersion, TerminalDiagnostics.ContractVersion,
            [DiagnosticCaseCheckpointProfiles.FreshModel, DiagnosticCaseCheckpointProfiles.TextState], FreshModelCheckpoint.CoveredSurfaces,
            TerminalDiagnostics.Hex1bVersion, null);

        /// <summary>Another build's declarations, while a test has set them: the consumer-side fences' seam.</summary>
        internal static readonly AsyncLocal<Consumer?> CurrentForTesting = new();

        internal static Consumer Current => CurrentForTesting.Value ?? Build;

        /// <summary>Whether this build projects a checkpoint's state under a profile (a fresh model is not a projection).</summary>
        internal bool Projects(string profile) => profile != DiagnosticCaseCheckpointProfiles.FreshModel && Profiles.Contains(profile);
    }

    /// <summary>Begins one run's checks against the current consumer.</summary>
    internal static Checks Begin() => new(Consumer.Current);

    /// <summary>One run's checks: each records both values and its verdict; <see cref="Record"/> snapshots them.</summary>
    internal sealed class Checks
    {
        private const int FormatIndex = 0, ContractIndex = 1, ProfileIndex = 2, SurfacesIndex = 3, ConfigurationIndex = 4, CapabilitiesIndex = 5, OriginIndex = 6;

        private readonly Consumer _consumer;
        private readonly string?[] _producer = new string?[DiagnosticCaseCompatibility.CheckNames.Count];
        private readonly string?[] _consumerValue = new string?[DiagnosticCaseCompatibility.CheckNames.Count];
        private readonly string[] _verdict = [.. DiagnosticCaseCompatibility.CheckNames.Select(_ => DiagnosticCaseCompatibility.NotChecked)];

        internal Checks(Consumer consumer)
        {
            _consumer = consumer;
            _consumerValue[FormatIndex] = consumer.FormatVersion.ToString();
            _consumerValue[ContractIndex] = consumer.ContractVersion.ToString();
            _consumerValue[ProfileIndex] = Join(consumer.Profiles);
            _consumerValue[SurfacesIndex] = Join(consumer.Surfaces);
            _consumerValue[ConfigurationIndex] = Join(CaseConfiguration.KnownFields);
            _consumerValue[CapabilitiesIndex] = Join(CaseConfiguration.KnownCapabilities);
        }

        /// <summary>The checks so far, for any result.</summary>
        internal DiagnosticCaseCompatibility Record => new()
        {
            Checks = [.. DiagnosticCaseCompatibility.CheckNames.Select((name, i) => new DiagnosticCompatibilityCheck
            {
                Check = name,
                Producer = _producer[i],
                Consumer = _consumerValue[i],
                Verdict = _verdict[i],
            })],
        };

        /// <summary>The artifact's format version against the one this build reads (before the artifact is read further).</summary>
        internal DiagnosticProblem? FormatVersion(int declared) => Judge(FormatIndex, declared.ToString(),
            declared == _consumer.FormatVersion ? null
            : declared == CaseArtifactWriter.LegacyFormatVersion
                ? $"formatVersion: format {declared} does not record its configuration structurally; re-application needs format {_consumer.FormatVersion}."
                : $"formatVersion: the artifact declares {declared}; this build reads {_consumer.FormatVersion}.");

        /// <summary>The artifact's diagnostics contract version against the one this build speaks.</summary>
        internal DiagnosticProblem? ContractVersion(int declared) => Judge(ContractIndex, declared.ToString(),
            declared == _consumer.ContractVersion ? null : $"contractVersion: the artifact declares {declared}; this build speaks {_consumer.ContractVersion}.");

        /// <summary>The start checkpoint's profile: one this build knows; a text-state start names its model sequence.</summary>
        internal DiagnosticProblem? StartProfile(string profile, long? modelSequence) => Judge(ProfileIndex, $"start: {profile}",
            !_consumer.Profiles.Contains(profile) ? $"checkpoint.profile: unknown profile '{profile}'; this build projects {Join(_consumer.Profiles)}."
            : profile == DiagnosticCaseCheckpointProfiles.TextState && modelSequence is not >= 0
                ? "checkpoint.modelSequence: a text-state/1 start names no model sequence."
                : null);

        /// <summary>The target checkpoint's profile: a projection this build makes, so the recorded state compares with its own.</summary>
        internal DiagnosticProblem? TargetProfile(string profile) => Judge(ProfileIndex, $"{_producer[ProfileIndex]}; target: {profile}",
            _consumer.Projects(profile) ? null : $"checkpoint.profile: unknown projection profile '{profile}'; this build projects {Join(_consumer.Profiles)}.");

        /// <summary>
        /// The complete start's declared surfaces against the set this build's profile covers (as sets: a profile names a
        /// set). A start that is not complete declares none (its case is re-applicable only from a recovery, whose
        /// profile the <c>checkpoint.profile</c> and <c>origin</c> checks judge): recorded as such, nothing to compare.
        /// </summary>
        internal DiagnosticProblem? CoveredSurfaces(IReadOnlyList<string> declared, bool complete)
        {
            if (!complete && declared.Count == 0)
                return Judge(SurfacesIndex, "(none declared: the start is not complete)", null);
            var missing = _consumer.Surfaces.Except(declared, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            var unknown = declared.Except(_consumer.Surfaces, StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();
            return Judge(SurfacesIndex, Join(declared), missing.Count == 0 && unknown.Count == 0 ? null
                : $"checkpoint.coveredSurfaces: the start declares {Join(declared)}; this build covers {Join(_consumer.Surfaces)}"
                  + (missing.Count > 0 ? $"; missing: {Join(missing)}" : "") + (unknown.Count > 0 ? $"; unknown: {Join(unknown)}" : "") + ".");
        }

        /// <summary>The configuration's fields, values, graphics limits and reflow strategy, as this build rebuilds them.</summary>
        internal DiagnosticProblem? Configuration(JsonObject? raw, DiagnosticCaseModelConfiguration configuration) =>
            Judge(ConfigurationIndex, raw is null ? "(none)" : Keys(raw), raw is null ? "configuration: missing" : CaseConfiguration.FieldProblem(raw, configuration));

        /// <summary>The recorded capabilities, as this build rebuilds them.</summary>
        internal DiagnosticProblem? Capabilities(JsonObject? raw, DiagnosticCaseCapabilities? recorded, out TerminalCapabilities? capabilities) =>
            Judge(CapabilitiesIndex, raw is null ? "(none)" : Keys(raw), CaseConfiguration.CapabilitiesProblem(raw, recorded, out capabilities));

        /// <summary>The replica could not be built from a configuration that passed its checks: the configuration is incompatible after all.</summary>
        internal DiagnosticProblem Rebuilt(Exception error) =>
            Judge(ConfigurationIndex, _producer[ConfigurationIndex], DiagnosticCaseRecorder.Bounded($"configuration: {error.Message}"))!;

        /// <summary>The origin the replica restores from (or, for a fresh model, is built as); its verdict comes with <see cref="Restored(bool)"/> or <see cref="Restored(Exception)"/>.</summary>
        internal void Origin(DiagnosticCaseOrigin origin) => _producer[OriginIndex] = origin.Profile == DiagnosticCaseCheckpointProfiles.FreshModel
            ? origin.Profile
            : $"{origin.Profile} at model sequence {origin.ModelSequence}";

        /// <summary>The origin's state restored (or the fresh model built): compatible.</summary>
        internal void Restored(bool fresh)
        {
            _consumerValue[OriginIndex] = fresh ? "rebuilt" : "restored";
            _verdict[OriginIndex] = DiagnosticCaseCompatibility.Compatible;
        }

        /// <summary>The origin's state could not be restored by this build.</summary>
        internal DiagnosticProblem Restored(Exception error)
        {
            _consumerValue[OriginIndex] = DiagnosticCaseRecorder.Bounded(error.Message);
            return Judge(OriginIndex, _producer[OriginIndex], DiagnosticCaseRecorder.Bounded($"origin: {error.Message}"))!;
        }

        private DiagnosticProblem? Judge(int check, string? producer, string? problem)
        {
            _producer[check] = producer;
            _verdict[check] = problem is null ? DiagnosticCaseCompatibility.Compatible : DiagnosticCaseCompatibility.Incompatible;
            return problem is null ? null : new DiagnosticProblem { Code = Code, Message = problem };
        }

        private static string Join(IEnumerable<string> values) => string.Join(", ", values);

        private static string Keys(JsonObject raw) => Join(raw.Select(p => p.Key).Order(StringComparer.Ordinal));
    }
}
