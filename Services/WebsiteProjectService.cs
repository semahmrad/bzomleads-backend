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
        UserActor actor,
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
            UpdatedUtc: nowUtc,
            CreatedByUserId: actor.UserId,
            CreatedByUsername: actor.Username,
            CreatedByDisplayName: actor.DisplayName);

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
        UserActor actor,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _websiteProjectStoreService.GetManifestAsync(projectId, cancellationToken)
            ?? throw new FileNotFoundException("The generated website project was not found.", projectId);
        EnsureProjectAccess(manifest, actor);

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
        UserActor actor,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _websiteProjectStoreService.GetManifestAsync(projectId, cancellationToken);
        if (manifest is null)
        {
            return null;
        }

        EnsureProjectAccess(manifest, actor);

        var content = await _websiteProjectStoreService.LoadArchiveAsync(projectId, cancellationToken);
        if (content is null)
        {
            return null;
        }

        return (content, manifest.DownloadFileName);
    }

    public async Task<IReadOnlyList<WebsiteProjectResponse>> ListOwnedProjectsAsync(
        UserActor actor,
        string applicationBaseUrl,
        CancellationToken cancellationToken = default)
    {
        var manifests = await _websiteProjectStoreService.GetAllManifestsAsync(cancellationToken);
        return manifests
            .Where(manifest => actor.IsAdmin || string.Equals(
                    manifest.CreatedByUserId,
                    actor.UserId,
                    StringComparison.OrdinalIgnoreCase))
            .Where(manifest => !string.IsNullOrWhiteSpace(manifest.RepositoryUrl) &&
                               !string.IsNullOrWhiteSpace(manifest.ProductionUrl))
            .Select(manifest => BuildResponse(manifest, [], applicationBaseUrl))
            .ToList();
    }

    public async Task<IReadOnlyList<AdminWebsiteProjectResponse>> ListAdminProjectsAsync(
        string applicationBaseUrl,
        IReadOnlyList<AdminUserResponse> users,
        CancellationToken cancellationToken = default)
    {
        var userLookup = users.ToDictionary(
            static user => user.Id,
            StringComparer.OrdinalIgnoreCase);
        var normalizedBaseUrl = applicationBaseUrl.TrimEnd('/');
        var manifests = await _websiteProjectStoreService.GetAllManifestsAsync(cancellationToken);

        return manifests.Select(manifest =>
        {
            userLookup.TryGetValue(manifest.CreatedByUserId ?? string.Empty, out var commercial);
            return BuildAdminResponse(manifest, commercial, normalizedBaseUrl);
        }).ToList();
    }

    public async Task UpdateClientDeliveryAsync(
        string projectId,
        UpdateClientDeliveryRequest request,
        CancellationToken cancellationToken = default)
    {
        var manifest = await _websiteProjectStoreService.GetManifestAsync(projectId, cancellationToken)
            ?? throw new FileNotFoundException("Le projet de site est introuvable.", projectId);
        var clientName = NormalizeOptionalField(request.ClientName, 120, "Le nom du client");
        var clientContact = NormalizeOptionalField(request.ClientContact, 180, "Le contact client");
        var notes = NormalizeOptionalField(request.Notes, 1000, "La note de suivi");

        manifest = manifest with
        {
            ClientLinkSent = request.ClientLinkSent,
            ClientLinkSentUtc = request.ClientLinkSent
                ? manifest.ClientLinkSentUtc ?? DateTimeOffset.UtcNow
                : null,
            ClientName = clientName,
            ClientContact = clientContact,
            ClientDeliveryNotes = notes
        };
        await _websiteProjectStoreService.SaveManifestAsync(manifest, cancellationToken);
    }

    private AdminWebsiteProjectResponse BuildAdminResponse(
        WebsiteProjectManifest manifest,
        AdminUserResponse? commercial,
        string normalizedBaseUrl)
    {
        var hasArchive = _websiteProjectStoreService.ArchiveExists(manifest.ProjectId);
        var status = !string.IsNullOrWhiteSpace(manifest.ProductionUrl)
            ? "Published"
            : !string.IsNullOrWhiteSpace(manifest.RepositoryUrl)
                ? "RepositoryReady"
                : "Generated";

        return new AdminWebsiteProjectResponse(
                ProjectId: manifest.ProjectId,
                PlaceId: manifest.PlaceId,
                BusinessName: manifest.BusinessName,
                TemplateId: manifest.TemplateId,
                TemplateName: manifest.TemplateName,
                DesignConcept: manifest.DesignConcept,
                ModelUsed: manifest.ModelUsed,
                Status: status,
                DownloadUrl: hasArchive
                    ? $"{normalizedBaseUrl}/api/websites/projects/{manifest.ProjectId}/download"
                    : null,
                RepositoryUrl: manifest.RepositoryUrl,
                ProductionUrl: manifest.ProductionUrl,
                ChangeSummary: manifest.ChangeSummary,
                UploadedImageCount: manifest.UploadedImageFileNames.Count,
                HasCustomLogo: !string.IsNullOrWhiteSpace(manifest.UploadedLogoFileName),
                HasBeenEdited: !string.IsNullOrWhiteSpace(manifest.ChangeSummary),
                CreatedUtc: manifest.CreatedUtc,
                UpdatedUtc: manifest.UpdatedUtc,
                CreatedByUserId: manifest.CreatedByUserId,
                CreatedByUsername: manifest.CreatedByUsername ?? commercial?.Username,
                CreatedByDisplayName: manifest.CreatedByDisplayName ?? commercial?.DisplayName,
                CommercialCountryCode: commercial?.CountryCode,
                CommercialCountryName: commercial?.CountryName,
                CommercialIsActive: commercial?.IsActive,
                ClientLinkSent: manifest.ClientLinkSent,
                ClientLinkSentUtc: manifest.ClientLinkSentUtc,
                ClientName: manifest.ClientName,
                ClientContact: manifest.ClientContact,
                ClientDeliveryNotes: manifest.ClientDeliveryNotes,
                CommercialCountries: commercial?.AllowedCountries ?? []);
    }

    private static string? NormalizeOptionalField(string? value, int maxLength, string label)
    {
        var normalized = value?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }
        if (normalized.Length > maxLength)
        {
            throw new ArgumentException($"{label} ne peut pas depasser {maxLength} caracteres.");
        }
        return normalized;
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
            UpdatedUtc: manifest.UpdatedUtc,
            PlaceId: manifest.PlaceId,
            CreatedByUserId: manifest.CreatedByUserId,
            CreatedByDisplayName: manifest.CreatedByDisplayName);
    }

    private static void EnsureProjectAccess(WebsiteProjectManifest manifest, UserActor actor)
    {
        if (actor.IsAdmin)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(manifest.CreatedByUserId) ||
            !string.Equals(manifest.CreatedByUserId, actor.UserId, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnauthorizedAccessException("You do not have access to this website project.");
        }
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
