using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Source-generated JSON serializer context for diagnostics protocol types.
/// Eliminates reflection-based serialization for native AOT compatibility.
/// </summary>
[JsonSerializable(typeof(DiagnosticsRequest))]
[JsonSerializable(typeof(DiagnosticsResponse))]
[JsonSerializable(typeof(DiagnosticRect))]
[JsonSerializable(typeof(DiagnosticCaptureRequest))]
[JsonSerializable(typeof(DiagnosticCaptureResult))]
[JsonSerializable(typeof(DiagnosticCapabilities))]
[JsonSerializable(typeof(DiagnosticApplicationFrameRequest))]
[JsonSerializable(typeof(DiagnosticApplicationFrameResult))]
[JsonSerializable(typeof(DiagnosticAcceptedInput))]
[JsonSerializable(typeof(DiagnosticAcceptedInput[]))]
[JsonSerializable(typeof(DiagnosticDeliveryRequest))]
[JsonSerializable(typeof(DiagnosticDeliveryResult))]
[JsonSerializable(typeof(DiagnosticCaseStartRequest))]
[JsonSerializable(typeof(DiagnosticCaseResult))]
[JsonSerializable(typeof(DiagnosticCaseManifest))]
[JsonSerializable(typeof(DiagnosticCaseCompletion))]
[JsonSerializable(typeof(DiagnosticCaseEvent))]
[JsonSerializable(typeof(DiagnosticCaseInspectRequest))]
[JsonSerializable(typeof(DiagnosticCaseInspection))]
[JsonSerializable(typeof(DiagnosticModelState))]
[JsonSerializable(typeof(DiagnosticCaseMarkResult))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(bool))]
// Application frames nest two JSON levels per node level; the default depth of 64 would reject
// trees deeper than about 30 nodes (spec Q10: every node is projected).
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    MaxDepth = 1024)]
internal sealed partial class DiagnosticsJsonContext : JsonSerializerContext
{
}
