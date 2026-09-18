using System.Text.Json.Serialization;

namespace ConexyAI.Contract;

/// <summary>Arguments for the <c>web_search</c> tool (deserialized from the model's tool call).</summary>
public class WebSearchRequest
{
    [JsonPropertyName("query")]
    public required string Query { get; set; }
}

/// <summary>Outcome of a web search, already formatted as text for the model.</summary>
public class WebSearchResult
{
    public required bool Success { get; set; }
    public required string Output { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// Shared OpenAI-style tool schema for <c>web_search</c>, used by both the coder agent
/// (always available) and the flash/pro chat models (only when the Smart Search toggle
/// is enabled for the message).
/// </summary>
public static class WebSearchTool
{
    public const string Name = "web_search";

    public static object Schema() => new
    {
        type = "function",
        function = new
        {
            name = Name,
            description = "Search the internet for up-to-date information — library/API documentation, error solutions, current package versions, anything beyond the model's training data.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "Search query, short and specific" }
                },
                required = new[] { "query" }
            }
        }
    };
}
