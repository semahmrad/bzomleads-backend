using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class GeminiProxyService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly GeminiProxyOptions _options;
    private readonly SemaphoreSlim _configLock = new(1, 1);

    private GeminiRequestConfig? _cachedConfig;
    private DateTime _cachedConfigWriteTimeUtc;

    public GeminiProxyService(HttpClient httpClient, IOptions<GeminiProxyOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var config = await LoadConfigAsync(cancellationToken);
        var request = BuildRequest(config, normalizedPrompt);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutCts.Token);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Gemini request failed with status {(int)response.StatusCode}");
        }

        var responseText = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        return ParseGeminiText(responseText);
    }

    private async Task<GeminiRequestConfig> LoadConfigAsync(CancellationToken cancellationToken)
    {
        var configPath = ResolveConfigPath();
        var writeTimeUtc = File.GetLastWriteTimeUtc(configPath);

        if (_cachedConfig is not null && writeTimeUtc == _cachedConfigWriteTimeUtc)
        {
            return _cachedConfig;
        }

        await _configLock.WaitAsync(cancellationToken);
        try
        {
            writeTimeUtc = File.GetLastWriteTimeUtc(configPath);
            if (_cachedConfig is not null && writeTimeUtc == _cachedConfigWriteTimeUtc)
            {
                return _cachedConfig;
            }

            var raw = await File.ReadAllTextAsync(configPath, cancellationToken);
            var config = JsonSerializer.Deserialize<GeminiRequestConfig>(raw, JsonOptions)
                ?? throw new InvalidOperationException("Gemini request config is invalid.");

            ValidateConfig(config, configPath);

            _cachedConfig = config;
            _cachedConfigWriteTimeUtc = writeTimeUtc;
            return config;
        }
        finally
        {
            _configLock.Release();
        }
    }

    private string ResolveConfigPath()
    {
        if (string.IsNullOrWhiteSpace(_options.RequestConfigPath))
        {
            throw new InvalidOperationException("GeminiProxy:RequestConfigPath is not configured.");
        }

        var fullPath = Path.GetFullPath(_options.RequestConfigPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("gemini_request.json was not found.", fullPath);
        }

        return fullPath;
    }

    private static void ValidateConfig(GeminiRequestConfig config, string configPath)
    {
        if (string.IsNullOrWhiteSpace(config.Url))
        {
            throw new InvalidOperationException($"Missing url in {configPath}");
        }

        if (string.IsNullOrWhiteSpace(config.PostData))
        {
            throw new InvalidOperationException($"Missing post_data in {configPath}");
        }

        if (config.Headers.Count == 0)
        {
            throw new InvalidOperationException($"Missing headers in {configPath}");
        }
    }

    private HttpRequestMessage BuildRequest(GeminiRequestConfig config, string prompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, BuildUrl(config.Url))
        {
            Content = new StringContent(BuildPayload(config.PostData, prompt), Encoding.UTF8)
        };

        request.Content.Headers.Remove("Content-Type");

        foreach (var header in config.Headers)
        {
            if (header.Key.Equals("content-type", StringComparison.OrdinalIgnoreCase))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                continue;
            }

            if (!request.Headers.TryAddWithoutValidation(header.Key, header.Value))
            {
                request.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        return request;
    }

    private static string BuildUrl(string rawUrl)
    {
        var builder = new UriBuilder(rawUrl);
        var pairs = ParseFormEncoded(builder.Query.TrimStart('?'));
        pairs["_reqid"] = Random.Shared.Next(100000, 1_000_000).ToString();
        builder.Query = BuildFormEncoded(pairs);
        return builder.Uri.ToString();
    }

    private static string BuildPayload(string basePayload, string prompt)
    {
        var payload = basePayload.EndsWith('&')
            ? basePayload[..^1]
            : basePayload;

        var pairs = ParseFormEncoded(payload);
        if (!pairs.TryGetValue("f.req", out var rawFReq) || string.IsNullOrWhiteSpace(rawFReq))
        {
            return basePayload;
        }

        try
        {
            var outerNode = JsonNode.Parse(rawFReq) as JsonArray;
            var innerJson = outerNode?[1]?.GetValue<string>();

            if (!string.IsNullOrWhiteSpace(innerJson))
            {
                var innerNode = JsonNode.Parse(innerJson) as JsonArray;
                if (innerNode?[0] is JsonArray promptNode && promptNode.Count > 0)
                {
                    promptNode[0] = prompt;
                    outerNode![1] = innerNode.ToJsonString();
                }
            }

            pairs["f.req"] = outerNode?.ToJsonString() ?? rawFReq;
            return $"{BuildFormEncoded(pairs)}&";
        }
        catch
        {
            return basePayload;
        }
    }

    private static string ParseGeminiText(string streamText)
    {
        var cleaned = streamText.StartsWith(")]}'", StringComparison.Ordinal)
            ? streamText[4..].TrimStart('\r', '\n')
            : streamText;

        var bestText = string.Empty;

        foreach (var rawLine in cleaned.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.Contains("wrb.fr", StringComparison.Ordinal))
            {
                continue;
            }

            JsonNode? parsedLine;
            try
            {
                parsedLine = JsonNode.Parse(line);
            }
            catch
            {
                continue;
            }

            foreach (var entry in ExtractWrbEntries(parsedLine))
            {
                var payload = entry[2]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(payload))
                {
                    continue;
                }

                JsonNode? innerNode;
                try
                {
                    innerNode = JsonNode.Parse(payload);
                }
                catch
                {
                    continue;
                }

                var text = ExtractTextFromInner(innerNode);
                if (text.Length > bestText.Length)
                {
                    bestText = text;
                }
            }
        }

        return bestText.Trim();
    }

    private static IEnumerable<JsonArray> ExtractWrbEntries(JsonNode? node)
    {
        if (node is not JsonArray array)
        {
            yield break;
        }

        if (array.Count > 2 && array[0]?.GetValue<string>() == "wrb.fr")
        {
            yield return array;
            yield break;
        }

        foreach (var item in array)
        {
            if (item is JsonArray child &&
                child.Count > 2 &&
                child[0]?.GetValue<string>() == "wrb.fr")
            {
                yield return child;
            }
        }
    }

    private static string ExtractTextFromInner(JsonNode? node)
    {
        if (node is not JsonArray inner || inner.Count <= 4 || inner[4] is not JsonArray candidates)
        {
            return string.Empty;
        }

        var best = string.Empty;

        foreach (var candidate in candidates)
        {
            if (candidate is not JsonArray candidateArray ||
                candidateArray.Count <= 1 ||
                candidateArray[1] is not JsonArray parts)
            {
                continue;
            }

            var builder = new StringBuilder();
            foreach (var part in parts)
            {
                if (part is JsonValue value && value.TryGetValue<string>(out var textPart))
                {
                    builder.Append(textPart);
                }
            }

            var current = builder.ToString().Trim();
            if (current.Length > best.Length)
            {
                best = current;
            }
        }

        return best;
    }

    private static Dictionary<string, string> ParseFormEncoded(string payload)
    {
        var pairs = new Dictionary<string, string>(StringComparer.Ordinal);

        if (string.IsNullOrWhiteSpace(payload))
        {
            return pairs;
        }

        foreach (var segment in payload.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separatorIndex = segment.IndexOf('=');
            if (separatorIndex < 0)
            {
                pairs[DecodeFormComponent(segment)] = string.Empty;
                continue;
            }

            var key = DecodeFormComponent(segment[..separatorIndex]);
            var value = DecodeFormComponent(segment[(separatorIndex + 1)..]);
            pairs[key] = value;
        }

        return pairs;
    }

    private static string BuildFormEncoded(Dictionary<string, string> pairs)
    {
        return string.Join("&", pairs.Select(pair =>
            $"{EncodeFormComponent(pair.Key)}={EncodeFormComponent(pair.Value)}"));
    }

    private static string DecodeFormComponent(string value)
    {
        return Uri.UnescapeDataString(value.Replace("+", " ", StringComparison.Ordinal));
    }

    private static string EncodeFormComponent(string value)
    {
        return Uri.EscapeDataString(value).Replace("%20", "+", StringComparison.Ordinal);
    }
}
