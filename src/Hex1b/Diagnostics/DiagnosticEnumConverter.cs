using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hex1b.Diagnostics;

/// <summary>
/// Serializes diagnostic contract enums as kebab-case strings so every client
/// (CLI, MCP, socket) exchanges the same stable wire names.
/// </summary>
/// <typeparam name="TEnum">The contract enum type.</typeparam>
public sealed class DiagnosticEnumConverter<TEnum> : JsonStringEnumConverter<TEnum>
    where TEnum : struct, Enum
{
    /// <summary>
    /// Creates a converter that uses kebab-case names and rejects numeric values.
    /// </summary>
    public DiagnosticEnumConverter()
        : base(JsonNamingPolicy.KebabCaseLower, allowIntegerValues: false)
    {
    }
}
