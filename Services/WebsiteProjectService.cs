using System.Globalization;
using System.Text;
using Backend.Models;

namespace Backend.Services;

public sealed class WebsiteProjectService
{
    private readonly BusinessWebsiteGenerationService _businessWebsiteGenerationService;
    private readonly WebsiteProjectStoreService _websiteProjectStoreService;
    private readonly GitHubPagesDeploymentService _gitHubPagesDeploymentService;

    public WebsiteProjectService(
        BusinessWebsiteGenerationService businessWebsiteGenerationService,
        WebsiteProjectStoreService websiteProjectStoreService,
        GitHubPagesDeploymentService gitHubPagesDeploymentService)
    {
        _businessWebsiteGenerationService = businessWebsiteGenerationService;
        _websiteProjectStoreService = websiteProjectStoreService;
        _gitHubPagesDeploymentService = gitHubPagesDeploymentService;
    }

    public async Task<WebsiteProjectResponse> GenerateAsync(
        WebsiteGenerationRequest request,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo,
        string applicationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var projectId = Guid.NewGuid().ToString("n", CultureInfo.InvariantCulture);
        var businessSlug = Slugify(request.BusinessName);
        var repositoryPlan = await _gitHubPagesDeploymentService.PlanRepositoryAsync(
            businessSlug,
            projectId,
            cancellationToken);
        var businessKey = BuildBusinessKey(request.PlaceId, request.BusinessName, request.Address);
        var history = (await _websiteProjectStoreService.FindByBusinessKeyAsync(businessKey, cancellationToken))
            .Select(static manifest => new WebsiteGenerationHistoryEntry(
                manifest.ProjectId,
                manifest.TemplateId,
                manifest.DesignConcept,
                manifest.UpdatedUtc))
            .ToList();

        var build = await _businessWebsiteGenerationService.GenerateProjectAsync(
            request,
            uploadedImages,
            uploadedLogo,
            history,
            repositoryPlan.ProductionUrl,
            cancellationToken);

        var nowUtc = DateTimeOffset.UtcNow;

        var manifest = new WebsiteProjectManifest(
            ProjectId: projectId,
            BusinessKey: businessKey,
            PlaceId: request.PlaceId,
            BusinessName: build.BusinessName,
            BusinessSlug: build.BusinessSlug,
            TemplateId: build.TemplateId,
            TemplateName: build.TemplateName,
            DesignConcept: build.DesignConcept,
            ModelUsed: build.ModelUsed,
            StateJson: build.StateJson,
            DownloadFileName: build.FileName,
            RepositoryOwner: repositoryPlan.Owner,
            RepositoryName: repositoryPlan.RepositoryName,
            RepositoryUrl: repositoryPlan.RepositoryUrl,
            ProductionUrl: repositoryPlan.ProductionUrl,
            ChangeSummary: build.ChangeSummary,
            UploadedImageFileNames: build.UploadedImageFileNames,
            UploadedLogoFileName: build.UploadedLogoFileName,
            CreatedUtc: nowUtc,
            UpdatedUtc: nowUtc);

        await _websiteProjectStoreService.SaveNewProjectAsync(
            manifest,
            build.Files,
            build.ArchiveContent,
            cancellationToken);

        var deployment = await _gitHubPagesDeploymentService.CreateProjectAsync(
            manifest,
            repositoryPlan,
            _websiteProjectStoreService.GetSiteDirectory(projectId),
            cancellationToken);

        manifest = manifest with
        {
            RepositoryOwner = deployment.Owner,
            RepositoryName = deployment.RepositoryName,
            RepositoryUrl = deployment.RepositoryUrl,
            ProductionUrl = deployment.ProductionUrl,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await _websiteProjectStoreService.SaveManifestAsync(manifest, cancellationToken);
        return BuildResponse(manifest, build.PrioritizedAssets, applicationBaseUrl);
    }

    public async Task<WebsiteProjectResponse> EditAsync(
        string projectId,
        string prompt,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo,
        string applicationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _websiteProjectStoreService.GetManifestAsync(projectId, cancellationToken)
            ?? throw new FileNotFoundException("The generated website project was not found.", projectId);

        var build = await _businessWebsiteGenerationService.EditProjectAsync(
            manifest.StateJson,
            _websiteProjectStoreService.GetSiteDirectory(projectId),
            prompt,
            uploadedImages,
            uploadedLogo,
            cancellationToken);

        manifest = manifest with
        {
            BusinessName = build.BusinessName,
            BusinessSlug = build.BusinessSlug,
            TemplateId = build.TemplateId,
            TemplateName = build.TemplateName,
            DesignConcept = build.DesignConcept,
            ModelUsed = build.ModelUsed,
            StateJson = build.StateJson,
            DownloadFileName = build.FileName,
            ChangeSummary = build.ChangeSummary,
            UploadedImageFileNames = build.UploadedImageFileNames,
            UploadedLogoFileName = build.UploadedLogoFileName,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await _websiteProjectStoreService.UpdateProjectAsync(
            manifest,
            build.Files,
            build.ArchiveContent,
            cancellationToken);

        var deployment = await _gitHubPagesDeploymentService.UpdateProjectAsync(
            manifest,
            _websiteProjectStoreService.GetSiteDirectory(projectId),
            build.ChangeSummary,
            cancellationToken);

        manifest = manifest with
        {
            RepositoryOwner = deployment.Owner,
            RepositoryName = deployment.RepositoryName,
            RepositoryUrl = deployment.RepositoryUrl,
            ProductionUrl = deployment.ProductionUrl,
            UpdatedUtc = DateTimeOffset.UtcNow
        };

        await _websiteProjectStoreService.SaveManifestAsync(manifest, cancellationToken);
        return BuildResponse(manifest, build.PrioritizedAssets, applicationBaseUrl);
    }

    public async Task<(byte[] Content, string FileName)?> LoadArchiveAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _websiteProjectStoreService.GetManifestAsync(projectId, cancellationToken);
        if (manifest is null)
        {
            return null;
        }

        var content = await _websiteProjectStoreService.LoadArchiveAsync(projectId, cancellationToken);
        if (content is null)
        {
            return null;
        }

        return (content, manifest.DownloadFileName);
    }

    private static WebsiteProjectResponse BuildResponse(
        WebsiteProjectManifest manifest,
        IReadOnlyList<string> prioritizedAssets,
        string applicationBaseUrl)
    {
        if (string.IsNullOrWhiteSpace(manifest.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(manifest.ProductionUrl))
        {
            throw new InvalidOperationException("The website project has not been deployed yet.");
        }

        var normalizedBaseUrl = applicationBaseUrl.TrimEnd('/');
        return new WebsiteProjectResponse(
            ProjectId: manifest.ProjectId,
            BusinessName: manifest.BusinessName,
            TemplateId: manifest.TemplateId,
            TemplateName: manifest.TemplateName,
            DesignConcept: manifest.DesignConcept,
            ModelUsed: manifest.ModelUsed,
            DownloadUrl: $"{normalizedBaseUrl}/api/websites/projects/{manifest.ProjectId}/download",
            RepositoryUrl: manifest.RepositoryUrl,
            ProductionUrl: manifest.ProductionUrl,
            ChangeSummary: manifest.ChangeSummary,
            PrioritizedAssets: prioritizedAssets,
            UpdatedUtc: manifest.UpdatedUtc);
    }

    private static string BuildBusinessKey(
        string? placeId,
        string businessName,
        string? address)
    {
        if (!string.IsNullOrWhiteSpace(placeId))
        {
            return placeId.Trim().ToLowerInvariant();
        }

        var seed = $"{businessName}::{address ?? string.Empty}";
        return string.Join(
            '-',
            seed
                .Trim()
                .ToLowerInvariant()
                .Split([' ', ':', ',', '/', '\\'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static string Slugify(string value)
    {
        var slugBuilder = new StringBuilder();

        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (char.IsLetterOrDigit(character))
            {
                slugBuilder.Append(char.ToLowerInvariant(character));
                continue;
            }

            if (slugBuilder.Length == 0 || slugBuilder[^1] == '-')
            {
                continue;
            }

            slugBuilder.Append('-');
        }

        var slug = slugBuilder.ToString().Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "business" : slug;
    }
}
