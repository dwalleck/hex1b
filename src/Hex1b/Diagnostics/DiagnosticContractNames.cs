using System.Text.Json;

namespace Hex1b.Diagnostics;

/// <summary>
/// Parses the contract's wire names from client input so every client accepts and rejects
/// the same spellings and reports the same invalid-request outcome.
/// </summary>
internal static class DiagnosticContractNames
{
    public static string Of<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonNamingPolicy.KebabCaseLower.ConvertName(value.ToString());

    public static IReadOnlyList<string> All<TEnum>() where TEnum : struct, Enum =>
        Enum.GetValues<TEnum>().Select(Of).ToArray();

    public static bool TryParse<TEnum>(string? text, out TEnum value) where TEnum : struct, Enum
    {
        foreach (var candidate in Enum.GetValues<TEnum>())
        {
            if (string.Equals(Of(candidate), text?.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                value = candidate;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Builds a capture request from client text, or an invalid-request result describing the
    /// first unrecognized value.
    /// </summary>
    public static (DiagnosticCaptureRequest? Request, DiagnosticCaptureResult? Invalid) ParseCaptureRequest(
        string format, int historyRows, IEnumerable<string>? authorizations, string? fontFamily = null)
    {
        if (!TryParse<DiagnosticCaptureFormat>(format, out var parsedFormat))
            return (null, TerminalDiagnostics.Problem(DiagnosticOutcome.InvalidRequest, "unsupported-format",
                $"Unsupported capture format '{format}'. Supported: {string.Join(", ", All<DiagnosticCaptureFormat>())}."));

        if (!TryParseAuthorizations(authorizations, out var parsedAuthorizations, out var unsupported))
            return (null, TerminalDiagnostics.Problem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", unsupported));

        return (new DiagnosticCaptureRequest
        {
            Format = parsedFormat,
            HistoryRows = historyRows,
            FontFamily = fontFamily,
            Authorizations = parsedAuthorizations,
        }, null);
    }

    /// <summary>
    /// Builds an application-frame request from client text, or an invalid-request result
    /// describing the first unrecognized authorization.
    /// </summary>
    public static (DiagnosticApplicationFrameRequest? Request, DiagnosticApplicationFrameResult? Invalid) ParseApplicationFrameRequest(
        IEnumerable<string>? authorizations)
    {
        if (!TryParseAuthorizations(authorizations, out var parsed, out var unsupported))
            return (null, TerminalDiagnostics.FrameProblem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", unsupported));

        return (new DiagnosticApplicationFrameRequest { Authorizations = parsed }, null);
    }

    /// <summary>
    /// Builds a delivery request from client values, or an invalid-request result describing the first
    /// unrecognized authorization. Range checks belong to the engine.
    /// </summary>
    public static (DiagnosticDeliveryRequest? Request, DiagnosticDeliveryResult? Invalid) ParseDeliveryRequest(
        long? since, int? limit, IEnumerable<string>? authorizations)
    {
        if (!TryParseAuthorizations(authorizations, out var parsed, out var unsupported))
            return (null, TerminalDiagnostics.DeliveryProblem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", unsupported));

        return (new DiagnosticDeliveryRequest { Since = since, Limit = limit, Authorizations = parsed }, null);
    }

    /// <summary>
    /// Builds a case start request from client text, the one parser every client uses: authorizations are
    /// repeatable and comma-separated, bounds are checked as the engine checks them, and a relative
    /// directory is resolved here, in the client, rather than against an attached target's working directory.
    /// The engine validates again, and alone checks storage.
    /// </summary>
    public static (DiagnosticCaseStartRequest? Request, DiagnosticCaseResult? Invalid) ParseCaseStartRequest(
        long? maxBytes, int? maxSeconds, IEnumerable<string>? authorizations, string? directory)
    {
        var names = authorizations?.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        if (!TryParseAuthorizations(names, out var parsed, out var unsupported))
            return (null, TerminalDiagnostics.CaseProblem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization", unsupported));
        if (TerminalDiagnostics.CaseBoundsProblem(maxBytes, maxSeconds) is { } invalidBounds)
            return (null, invalidBounds);

        string? root = null;
        if (!string.IsNullOrWhiteSpace(directory))
        {
            try
            {
                root = Path.GetFullPath(directory);
            }
            catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException)
            {
                return (null, TerminalDiagnostics.CaseProblem(DiagnosticOutcome.InvalidRequest, "invalid-directory", "directory must be a valid path."));
            }
        }

        return (new DiagnosticCaseStartRequest { MaxBytes = maxBytes, MaxSeconds = maxSeconds, Authorizations = parsed, Directory = root }, null);
    }

    /// <summary>
    /// Builds a re-application request from client text, the one parser every client uses. The target is
    /// a model sequence (<c>12</c>), a case sequence (<c>case:34</c>), or a label (<c>label:name</c>, or any
    /// other text); faults and previews are repeatable and comma-separated. The reapplier validates the rest.
    /// </summary>
    public static (DiagnosticCaseReapplyRequest? Request, DiagnosticCaseReapplyResult? Invalid) ParseCaseReapplyRequest(
        string path, string? to, IEnumerable<string>? faults, int? maxDifferences, IEnumerable<string>? previews, string? from = null)
    {
        static DiagnosticCaseReapplyResult Invalid(string code, string message) => new()
        {
            Outcome = DiagnosticOutcome.InvalidRequest,
            Problem = new DiagnosticProblem { Code = code, Message = message },
        };

        if (string.IsNullOrWhiteSpace(to))
            return (null, Invalid("invalid-target", "Name the target: a model sequence (12), a case sequence (case:34), or a label (label:name)."));
        var request = new DiagnosticCaseReapplyRequest { Path = path, MaxDifferences = maxDifferences };
        if (to.StartsWith("case:", StringComparison.Ordinal))
        {
            if (!long.TryParse(to.AsSpan(5), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var caseSequence))
                return (null, Invalid("invalid-target", $"'{to}' is not case: followed by a case sequence."));
            request = request with { ToCaseSequence = caseSequence };
        }
        else if (long.TryParse(to, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var modelSequence))
        {
            request = request with { ToModelSequence = modelSequence };
        }
        else
        {
            request = request with { ToLabel = to.StartsWith("label:", StringComparison.Ordinal) ? to[6..] : to };
        }

        static string[]? Split(IEnumerable<string>? values) =>
            values?.SelectMany(value => value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToArray() is { Length: > 0 } list
                ? list
                : null;
        // The origin is named as a target is (start, a label, case:N or checkpoint:N); the reapplier resolves it, and a
        // numeric form that is not one is refused here as such a target is.
        var origin = string.IsNullOrWhiteSpace(from) ? null : from.Trim();
        if (origin is not null && (origin.StartsWith("case:", StringComparison.Ordinal) || origin.StartsWith("checkpoint:", StringComparison.Ordinal))
            && !long.TryParse(origin.AsSpan(origin.IndexOf(':') + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _))
            return (null, Invalid("invalid-origin", $"'{origin}' is not case: or checkpoint: followed by a number."));
        return (request with { Faults = Split(faults), Previews = Split(previews), From = origin }, null);
    }

    private static bool TryParseAuthorizations(IEnumerable<string>? names, out List<DiagnosticAuthorization> parsed, out string unsupported)
    {
        parsed = [];
        unsupported = "";
        foreach (var name in names ?? [])
        {
            if (!TryParse<DiagnosticAuthorization>(name, out var authorization))
            {
                unsupported = $"Unsupported authorization '{name}'. Supported: {string.Join(", ", All<DiagnosticAuthorization>())}.";
                return false;
            }

            parsed.Add(authorization);
        }

        return true;
    }

    /// <summary>
    /// Builds a milestone request from client text: <see langword="null"/> when no milestone is
    /// named; otherwise the request, or the reason the text is not a milestone.
    /// </summary>
    public static (DiagnosticMilestoneRequest? Milestone, string? Invalid) ParseMilestone(string? name, long? inputId, int? timeoutMs)
    {
        if (string.IsNullOrWhiteSpace(name))
            return inputId is null && timeoutMs is null
                ? (null, null)
                : (null, "An input id or milestone timeout was given without a milestone.");
        if (!TryParse<DiagnosticMilestone>(name, out var milestone))
            return (null, $"Unsupported milestone '{name}'. Supported: {string.Join(", ", All<DiagnosticMilestone>())}.");
        return (new DiagnosticMilestoneRequest { Milestone = milestone, InputId = inputId, TimeoutMs = timeoutMs }, null);
    }
}
