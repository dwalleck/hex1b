using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// MCP wrapper around the shared offline re-application result.
/// </summary>
public class CaseReapplyToolResult
{
    [JsonPropertyName("success")]
    public required bool Success { get; init; }

    [JsonPropertyName("message")]
    public required string Message { get; init; }

    [JsonPropertyName("reapplication")]
    public required JsonElement Reapplication { get; init; }
}
