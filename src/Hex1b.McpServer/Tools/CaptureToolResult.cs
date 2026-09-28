using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.McpServer.Tools;

/// <summary>
/// Result of every MCP capture tool. <see cref="Capture"/> is the shared diagnostic contract
/// result serialized by the contract itself, byte-for-byte the shape the CLI returns.
/// </summary>
public class CaptureToolResult
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

    [JsonPropertyName("savedPath")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SavedPath { get; init; }

    [JsonPropertyName("hasExited")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasExited { get; init; }

    [JsonPropertyName("exitCode")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? ExitCode { get; init; }

    [JsonPropertyName("capture")]
    public required JsonElement Capture { get; init; }
}
