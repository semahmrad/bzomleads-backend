namespace Backend.Models;

public sealed record WebsiteProjectManifest(
    string ProjectId,
    string BusinessKey,
    string PlaceId,
    string BusinessName,
    string BusinessSlug,
    string TemplateId,
    string TemplateName,
    string DesignConcept,
    string ModelUsed,
    string StateJson,
    string DownloadFileName,
    string? RepositoryOwner,
    string? RepositoryName,
    string? RepositoryUrl,
    string? ProductionUrl,
    string? ChangeSummary,
    IReadOnlyList<string> UploadedImageFileNames,
    string? UploadedLogoFileName,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc);
