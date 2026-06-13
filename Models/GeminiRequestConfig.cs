using System.Text.Json.Serialization;

namespace Backend.Models;

public sealed class GeminiRequestConfig
{
    public string Url { get; set; } = string.Empty;

    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    [JsonPropertyName("post_data")]
    public string PostData { get; set; } = string.Empty;
}
