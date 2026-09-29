using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP wrapper around the shared native delivery contract result.
/// </summary>
public class DeliveryToolResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("delivery")]
    public required JsonElement Delivery { get; init; }
}
