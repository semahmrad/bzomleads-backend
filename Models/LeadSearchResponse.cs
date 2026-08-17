namespace Backend.Models;

public sealed record LeadSearchResponse(
    string Provider,
    string Query,
    string BusinessType,
    string WebsiteFilter,
    bool ExtractEmailsFromSites,
    int Total,
    int ExistingResultsCount,
    int NewResultsCount,
    int RequestedNewResults,
    int WithWebsiteCount,
    int WithoutWebsiteCount,
    int EmailCount,
    IReadOnlyList<LeadSearchResultItem> Items);

public sealed record LeadSearchResultItem(
    string PlaceId,
    string Name,
    string BusinessLabel,
    string? PrimaryType,
    string? FormattedAddress,
    string? PhoneNumber,
    string? WebsiteUri,
    string? GoogleMapsUri,
    string? BusinessStatus,
    double? Rating,
    int? UserRatingCount,
    double? Latitude,
    double? Longitude,
    bool HasWebsite,
    string EmailExtractionSource,
    IReadOnlyList<string> EmailAddresses,
    IReadOnlyList<string> ContactPhoneNumbers,
    IReadOnlyList<string> ContactPageUris);

public sealed record LeadStreamMessage(
    string Type,
    LeadSearchResponseSummary? Summary = null,
    LeadSearchResultItem? Lead = null,
    string? ErrorMessage = null,
    IReadOnlyList<LeadSearchResultItem>? Leads = null);

public sealed record LeadSearchResponseSummary(
    int Total,
    int ExistingResultsCount,
    int NewResultsCount,
    int RequestedNewResults,
    int WithWebsiteCount,
    int WithoutWebsiteCount,
    int EmailCount);
