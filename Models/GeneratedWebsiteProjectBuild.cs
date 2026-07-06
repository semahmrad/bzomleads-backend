namespace Backend.Models;

public sealed record WebsiteGenerationHistoryEntry(
    string ProjectId,
    string TemplateId,
    string DesignConcept,
    DateTimeOffset UpdatedUtc);

public sealed record GeneratedWebsiteProjectBuild(
    string StateJson,
    string FileName,
    byte[] ArchiveContent,
    IReadOnlyDictionary<string, byte[]> Files,
    string BusinessName,
    string BusinessSlug,
    string TemplateId,
    string TemplateName,
    string DesignConcept,
    string ModelUsed,
    string? ChangeSummary,
    IReadOnlyList<string> PrioritizedAssets,
    IReadOnlyList<string> UploadedImageFileNames,
    string? UploadedLogoFileName);
