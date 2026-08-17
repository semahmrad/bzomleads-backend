namespace Backend.Models;

public sealed record LeadSearchRequest(
    string? Provider,
    string? LocationQuery,
    string? BusinessType,
    string? WebsiteFilter,
    bool ExtractEmailsFromSites,
    bool UseGeminiForEmailExtraction,
    int? MaxResults,
    string? CountryCode = null,
    string? CountryName = null,
    string? SearchSessionId = null);
