namespace Backend.Models;

public sealed record WebsiteProjectResponse(
    string ProjectId,
    string BusinessName,
    string TemplateId,
    string TemplateName,
    string DesignConcept,
    string ModelUsed,
    string DownloadUrl,
    string RepositoryUrl,
    string ProductionUrl,
    string? ChangeSummary,
    IReadOnlyList<string> PrioritizedAssets,
    DateTimeOffset UpdatedUtc);
