namespace Backend.Models;

public sealed record AiModelOptionResponse(string Id, string Name, string Description);

public sealed record AiSettingsResponse(
    bool Configured,
    string? MaskedApiKey,
    string Model,
    IReadOnlyList<AiModelOptionResponse> AvailableModels);

public sealed record UpdateAiSettingsRequest(string? ApiKey, string? Model);

internal sealed record UserAiSettings(string ApiKey, string Model, DateTimeOffset UpdatedUtc);

public static class GoogleAiModelCatalog
{
    public const string DefaultModel = "gemma-3-27b-it";

    public static readonly IReadOnlyList<AiModelOptionResponse> Models =
    [
        new("gemma-3-27b-it", "Gemma 3 · 27B", "Modele ouvert Google, puissant pour la redaction et l analyse."),
        new("gemma-3-12b-it", "Gemma 3 · 12B", "Version plus legere et rapide pour les taches commerciales."),
        new("gemini-2.5-flash-lite", "Gemini 2.5 Flash Lite", "Modele Gemini rapide et economique, selon les quotas Google."),
        new("gemini-2.5-flash", "Gemini 2.5 Flash", "Bon equilibre entre qualite et rapidite, selon les quotas Google.")
    ];

    public static bool IsAllowed(string? model)
        => Models.Any(option => string.Equals(option.Id, model, StringComparison.OrdinalIgnoreCase));
}
