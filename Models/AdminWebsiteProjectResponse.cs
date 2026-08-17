namespace Backend.Models;

public sealed record AdminWebsiteProjectResponse(
    string ProjectId,
    string PlaceId,
    string BusinessName,
    string TemplateId,
    string TemplateName,
    string DesignConcept,
    string ModelUsed,
    string Status,
    string? DownloadUrl,
    string? RepositoryUrl,
    string? ProductionUrl,
    string? ChangeSummary,
    int UploadedImageCount,
    bool HasCustomLogo,
    bool HasBeenEdited,
    DateTimeOffset CreatedUtc,
    DateTimeOffset UpdatedUtc,
    string? CreatedByUserId,
    string? CreatedByUsername,
    string? CreatedByDisplayName,
    string? CommercialCountryCode,
    string? CommercialCountryName,
    bool? CommercialIsActive,
    bool ClientLinkSent,
    DateTimeOffset? ClientLinkSentUtc,
    string? ClientName,
    string? ClientContact,
    string? ClientDeliveryNotes,
    IReadOnlyList<CountryOptionResponse> CommercialCountries);

public sealed record UpdateClientDeliveryRequest(
    bool ClientLinkSent,
    string? ClientName,
    string? ClientContact,
    string? Notes);
