using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class GooglePlacesLeadService
{
    private const string SearchUrl = "https://places.googleapis.com/v1/places:searchText";
    private const int ProcessingConcurrency = 4;

    private readonly HttpClient _httpClient;
    private readonly GooglePlacesOptions _options;
    private readonly WebsiteEmailExtractionService _websiteEmailExtractionService;

    public GooglePlacesLeadService(
        HttpClient httpClient,
        IOptions<GooglePlacesOptions> options,
        WebsiteEmailExtractionService websiteEmailExtractionService)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _websiteEmailExtractionService = websiteEmailExtractionService;
    }

    public async Task<LeadSearchResponse> SearchAsync(
        LeadSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var locationQuery = (request.LocationQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            throw new ArgumentException("LocationQuery is required.", nameof(request));
        }

        var businessType = LeadSearchCatalog.NormalizeBusinessType(request.BusinessType);
        var websiteFilter = LeadSearchCatalog.NormalizeWebsiteFilter(request.WebsiteFilter);
        var maxResults = Math.Clamp(request.MaxResults ?? 10, 1, 20);
        var extractEmailsFromSites = request.ExtractEmailsFromSites && websiteFilter != "without_website";
        var useGeminiForEmailExtraction =
            extractEmailsFromSites && request.UseGeminiForEmailExtraction;

        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Google Places API key is not configured. Set GooglePlaces:ApiKey or GOOGLE_MAPS_API_KEY.");
        }

        var searchResponse = await SearchPlacesAsync(locationQuery, businessType, maxResults, cancellationToken);
        var seenPlaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var semaphore = new SemaphoreSlim(ProcessingConcurrency);
        var leadTasks = new List<Task<LeadSearchResultItem?>>();

        foreach (var place in searchResponse.Places ?? [])
        {
            if (string.IsNullOrWhiteSpace(place.Id) || !seenPlaceIds.Add(place.Id))
            {
                continue;
            }
            leadTasks.Add(ProcessPlaceAsync(
                place,
                businessType,
                websiteFilter,
                extractEmailsFromSites,
                useGeminiForEmailExtraction,
                semaphore,
                cancellationToken));
        }

        var leads = (await Task.WhenAll(leadTasks))
            .Where(item => item is not null)
            .Cast<LeadSearchResultItem>()
            .ToList();

        var withWebsiteCount = leads.Count(item => item.HasWebsite);
        var withoutWebsiteCount = leads.Count - withWebsiteCount;
        var emailCount = leads.Sum(item => item.EmailAddresses.Count);

        return new LeadSearchResponse(
            Provider: "google_places",
            Query: locationQuery,
            BusinessType: businessType,
            WebsiteFilter: websiteFilter,
            ExtractEmailsFromSites: extractEmailsFromSites,
            Total: leads.Count,
            ExistingResultsCount: 0,
            NewResultsCount: leads.Count,
            RequestedNewResults: maxResults,
            WithWebsiteCount: withWebsiteCount,
            WithoutWebsiteCount: withoutWebsiteCount,
            EmailCount: emailCount,
            Items: leads);
    }

    private async Task<LeadSearchResultItem?> ProcessPlaceAsync(
        PlaceDetailsDto place,
        string businessType,
        string websiteFilter,
        bool extractEmailsFromSites,
        bool useGeminiForEmailExtraction,
        SemaphoreSlim semaphore,
        CancellationToken cancellationToken)
    {
        await semaphore.WaitAsync(cancellationToken);
        try
        {
            var details = await GetPlaceDetailsAsync(place.Id!, cancellationToken);
            var websiteUri = details.WebsiteUri?.Trim();
            var hasWebsite = !string.IsNullOrWhiteSpace(websiteUri);

            if (websiteFilter == "with_website" && !hasWebsite)
            {
                return null;
            }

            if (websiteFilter == "without_website" && hasWebsite)
            {
                return null;
            }

            IReadOnlyList<string> emails = Array.Empty<string>();
            IReadOnlyList<string> contactPhones = Array.Empty<string>();
            IReadOnlyList<string> contactPageUris = Array.Empty<string>();
            var emailSource = "none";

            if (extractEmailsFromSites && hasWebsite)
            {
                var extraction = await _websiteEmailExtractionService.ExtractPublicContactDetailsAsync(
                    websiteUri!,
                    useGeminiForEmailExtraction,
                    cancellationToken);

                emails = extraction.Emails;
                contactPhones = extraction.PhoneNumbers;
                contactPageUris = extraction.ContactPageUris;
                emailSource = extraction.Source;
            }

            return new LeadSearchResultItem(
                PlaceId: place.Id!,
                Name: details.DisplayName?.Text?.Trim()
                    ?? place.DisplayName?.Text?.Trim()
                    ?? "Unknown place",
                BusinessLabel: LeadSearchCatalog.GetBusinessLabel(businessType),
                PrimaryType: details.PrimaryType ?? place.PrimaryType,
                FormattedAddress: details.FormattedAddress ?? place.FormattedAddress,
                PhoneNumber: details.NationalPhoneNumber,
                WebsiteUri: websiteUri,
                GoogleMapsUri: details.GoogleMapsUri ?? place.GoogleMapsUri,
                BusinessStatus: details.BusinessStatus ?? place.BusinessStatus,
                Rating: details.Rating ?? place.Rating,
                UserRatingCount: details.UserRatingCount ?? place.UserRatingCount,
                Latitude: details.Location?.Latitude ?? place.Location?.Latitude,
                Longitude: details.Location?.Longitude ?? place.Location?.Longitude,
                HasWebsite: hasWebsite,
                EmailExtractionSource: emailSource,
                EmailAddresses: emails,
                ContactPhoneNumbers: contactPhones,
                ContactPageUris: contactPageUris);
        }
        finally
        {
            semaphore.Release();
        }
    }

    private async Task<TextSearchResponse> SearchPlacesAsync(
        string locationQuery,
        string businessType,
        int maxResults,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, SearchUrl)
        {
            Content = JsonContent.Create(new TextSearchRequest(
                TextQuery: $"{LeadSearchCatalog.GetBusinessLabel(businessType)} in {locationQuery}",
                IncludedType: businessType,
                StrictTypeFiltering: true,
                PageSize: maxResults,
                LanguageCode: _options.DefaultLanguageCode))
        };

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "places.id,places.displayName,places.formattedAddress,places.googleMapsUri,places.businessStatus,places.location,places.primaryType,places.rating,places.userRatingCount");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<TextSearchResponse>(cancellationToken: cancellationToken))
            ?? new TextSearchResponse();
    }

    private async Task<PlaceDetailsDto> GetPlaceDetailsAsync(string placeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(placeId)}");

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            "id,displayName,formattedAddress,nationalPhoneNumber,websiteUri,googleMapsUri,businessStatus,location,primaryType,rating,userRatingCount");

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<PlaceDetailsDto>(cancellationToken: cancellationToken))
            ?? new PlaceDetailsDto();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Google Places request failed with status {(int)response.StatusCode}: {content}");
    }
    private sealed record TextSearchRequest(
        [property: JsonPropertyName("textQuery")] string TextQuery,
        [property: JsonPropertyName("includedType")] string IncludedType,
        [property: JsonPropertyName("strictTypeFiltering")] bool StrictTypeFiltering,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("languageCode")] string LanguageCode);

    private sealed class TextSearchResponse
    {
        [JsonPropertyName("places")]
        public List<PlaceDetailsDto>? Places { get; init; }
    }

    private sealed class PlaceDetailsDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }

        [JsonPropertyName("displayName")]
        public LocalizedTextDto? DisplayName { get; init; }

        [JsonPropertyName("formattedAddress")]
        public string? FormattedAddress { get; init; }

        [JsonPropertyName("nationalPhoneNumber")]
        public string? NationalPhoneNumber { get; init; }

        [JsonPropertyName("websiteUri")]
        public string? WebsiteUri { get; init; }

        [JsonPropertyName("googleMapsUri")]
        public string? GoogleMapsUri { get; init; }

        [JsonPropertyName("businessStatus")]
        public string? BusinessStatus { get; init; }

        [JsonPropertyName("location")]
        public LocationDto? Location { get; init; }

        [JsonPropertyName("primaryType")]
        public string? PrimaryType { get; init; }

        [JsonPropertyName("rating")]
        public double? Rating { get; init; }

        [JsonPropertyName("userRatingCount")]
        public int? UserRatingCount { get; init; }
    }

    private sealed class LocalizedTextDto
    {
        [JsonPropertyName("text")]
        public string? Text { get; init; }
    }

    private sealed class LocationDto
    {
        [JsonPropertyName("latitude")]
        public double? Latitude { get; init; }

        [JsonPropertyName("longitude")]
        public double? Longitude { get; init; }
    }
}
