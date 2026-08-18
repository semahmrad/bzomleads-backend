using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class GeminiProxyService
{
    private const string ApiBaseUrl = "https://generativelanguage.googleapis.com/v1beta/models";
    private readonly HttpClient _httpClient;
    private readonly GeminiProxyOptions _options;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly SaasStoreService _saasStore;

    public GeminiProxyService(
        HttpClient httpClient,
        IOptions<GeminiProxyOptions> options,
        IHttpContextAccessor httpContextAccessor,
        SaasStoreService saasStore)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpContextAccessor = httpContextAccessor;
        _saasStore = saasStore;
    }

    public async Task<string> AskAsync(string prompt, CancellationToken cancellationToken = default)
    {
        var userId = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Aucun utilisateur connecte pour la requete IA.");
        var settings = await _saasStore.GetUserAiSettingsAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException(
                "Configure ta cle Google AI Studio dans Mon compte avant d utiliser l IA.");
        return await AskWithSettingsAsync(prompt, settings, cancellationToken);
    }

    public async Task<string> GetConfiguredModelAsync(CancellationToken cancellationToken = default)
    {
        var userId = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
            ?? throw new InvalidOperationException("Aucun utilisateur connecte pour la requete IA.");
        var settings = await _saasStore.GetUserAiSettingsAsync(userId, cancellationToken)
            ?? throw new InvalidOperationException("La configuration Google AI est manquante.");
        return settings.Model;
    }

    public async Task ValidateCredentialsAsync(
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = (apiKey ?? string.Empty).Trim();
        if (normalizedKey.Length is < 20 or > 256)
        {
            throw new ArgumentException("La cle Google AI Studio semble invalide.");
        }
        if (!GoogleAiModelCatalog.IsAllowed(model))
        {
            throw new ArgumentException("Le modele IA selectionne n est pas autorise.");
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{ApiBaseUrl}/{Uri.EscapeDataString(model)}");
        request.Headers.Add("x-goog-api-key", normalizedKey);
        using var response = await SendAsync(request, cancellationToken, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw BuildGoogleApiException(response.StatusCode);
        }
    }

    private async Task<string> AskWithSettingsAsync(
        string prompt,
        UserAiSettings settings,
        CancellationToken cancellationToken)
    {
        var normalizedPrompt = (prompt ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            throw new ArgumentException("Le prompt IA est obligatoire.", nameof(prompt));
        }

        var payload = JsonSerializer.Serialize(new
        {
            contents = new[]
            {
                new { role = "user", parts = new[] { new { text = normalizedPrompt } } }
            },
            generationConfig = new
            {
                temperature = 0.35,
                topP = 0.9,
                maxOutputTokens = 8192
            }
        });

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{ApiBaseUrl}/{Uri.EscapeDataString(settings.Model)}:generateContent")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json")
        };
        request.Headers.Add("x-goog-api-key", settings.ApiKey);
        using var response = await SendAsync(request, cancellationToken, timeoutCts.Token);
        if (!response.IsSuccessStatusCode)
        {
            throw BuildGoogleApiException(response.StatusCode);
        }

        var rawResponse = await response.Content.ReadAsStringAsync(timeoutCts.Token);
        var root = JsonNode.Parse(rawResponse);
        var parts = root?["candidates"]?[0]?["content"]?["parts"]?.AsArray();
        var text = parts is null
            ? string.Empty
            : string.Join(
                string.Empty,
                parts.Select(part => part?["text"]?.GetValue<string>() ?? string.Empty));
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new InvalidOperationException(
                "Google AI n a retourne aucun texte. Verifie le modele et les quotas de ta cle.");
        }
        return text.Trim();
    }

    private async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken callerToken,
        CancellationToken timeoutToken)
    {
        try
        {
            return await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutToken);
        }
        catch (OperationCanceledException) when (!callerToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                "Google AI ne repond pas dans le delai prevu. Reessaie dans quelques instants.");
        }
    }

    private static Exception BuildGoogleApiException(HttpStatusCode statusCode)
        => statusCode switch
        {
            HttpStatusCode.BadRequest => new InvalidOperationException(
                "Google AI a refuse la requete. Verifie le modele selectionne."),
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new InvalidOperationException(
                "La cle Google AI Studio est invalide ou n a pas acces a ce modele."),
            HttpStatusCode.NotFound => new InvalidOperationException(
                "Ce modele Google AI n est pas disponible pour cette cle. Choisis un autre modele."),
            HttpStatusCode.TooManyRequests => new InvalidOperationException(
                "Le quota gratuit Google AI est atteint. Reessaie plus tard ou consulte tes quotas AI Studio."),
            _ => new InvalidOperationException(
                $"Google AI est temporairement indisponible (code {(int)statusCode}).")
        };
}
