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

        var parsedAuthorizations = new List<DiagnosticAuthorization>();
        foreach (var name in authorizations ?? [])
        {
            if (!TryParse<DiagnosticAuthorization>(name, out var authorization))
                return (null, TerminalDiagnostics.Problem(DiagnosticOutcome.InvalidRequest, "unsupported-authorization",
                    $"Unsupported authorization '{name}'. Supported: {string.Join(", ", All<DiagnosticAuthorization>())}."));
            parsedAuthorizations.Add(authorization);
        }

        return (new DiagnosticCaptureRequest
        {
            Format = parsedFormat,
            HistoryRows = historyRows,
            FontFamily = fontFamily,
            Authorizations = parsedAuthorizations,
        }, null);
    }
}
