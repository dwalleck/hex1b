using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP result for application-frame tools. <see cref="ApplicationFrame"/> is the shared
/// application-frame contract result, serialized with the contract's own serializer.
/// </summary>
public class ApplicationFrameToolResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("processId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ProcessId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("applicationFrame")]
    public required JsonElement ApplicationFrame { get; init; }
}
