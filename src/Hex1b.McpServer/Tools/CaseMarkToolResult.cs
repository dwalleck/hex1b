using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP wrapper around the shared case mark result.
/// </summary>
public class CaseMarkToolResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("sessionId")]
    public string? SessionId { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("mark")]
    public required JsonElement Mark { get; init; }
}
