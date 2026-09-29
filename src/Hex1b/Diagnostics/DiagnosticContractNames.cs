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
