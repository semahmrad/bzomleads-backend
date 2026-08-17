using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class GooglePlaceWebsiteEnrichmentService
{
    private const string SearchUrl = "https://places.googleapis.com/v1/places:searchText";

    private readonly HttpClient _httpClient;
    private readonly GooglePlacesOptions _options;

    public GooglePlaceWebsiteEnrichmentService(
        HttpClient httpClient,
        IOptions<GooglePlacesOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<WebsiteGenerationEnrichment?> TryEnrichAsync(
        WebsiteGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            return null;
        }

        PlaceDetailsDto? details = null;

        if (!string.IsNullOrWhiteSpace(request.PlaceId))
        {
            try
            {
                details = await GetPlaceDetailsAsync(request.PlaceId.Trim(), cancellationToken);
            }
            catch
            {
                details = null;
            }
        }

        if (details is null)
        {
            var matchedPlaceId = await SearchMatchingPlaceIdAsync(request, cancellationToken);
            if (!string.IsNullOrWhiteSpace(matchedPlaceId))
            {
                try
                {
                    details = await GetPlaceDetailsAsync(matchedPlaceId, cancellationToken);
                }
                catch
                {
                    details = null;
                }
            }
        }

        if (details is null)
        {
            return null;
        }

        var openingHours = details.CurrentOpeningHours?.WeekdayDescriptions?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .ToList() ?? [];

        var photoUris = details.Photos?
            .Where(static photo => !string.IsNullOrWhiteSpace(photo.Name))
            .Take(4)
            .Select(photo => BuildPhotoMediaUri(photo.Name!))
            .ToList() ?? [];

        var reviewHighlights = details.Reviews?
            .Select(static review => new WebsiteReviewSnippet(
                AuthorName: review.AuthorAttribution?.DisplayName?.Trim() ?? "Client Google",
                Rating: review.Rating,
                RelativePublishTimeDescription: review.RelativePublishTimeDescription?.Trim(),
                Text: NormalizeReviewText(review.Text?.Text, review.OriginalText?.Text),
                GoogleMapsUri: review.GoogleMapsUri?.Trim()))
            .Where(static review =>
                review.Rating is >= 4 &&
                !string.IsNullOrWhiteSpace(review.Text))
            .Take(3)
            .ToList() ?? [];

        var features = BuildFeatureList(details);

        return new WebsiteGenerationEnrichment(
            GooglePlaceId: details.Id?.Trim(),
            Description: details.EditorialSummary?.Text?.Trim(),
            PhoneNumber: details.NationalPhoneNumber?.Trim() ?? details.InternationalPhoneNumber?.Trim(),
            WebsiteUri: details.WebsiteUri?.Trim(),
            GoogleMapsUri: details.GoogleMapsUri?.Trim(),
            Rating: details.Rating,
            ReviewCount: details.UserRatingCount,
            ReviewSummary: details.ReviewSummary?.Text?.Text?.Trim(),
            OpeningHours: openingHours,
            PhotoUris: photoUris,
            Features: features,
            ReviewHighlights: reviewHighlights,
            ReviewsUri: details.GoogleMapsLinks?.ReviewsUri?.Trim() ?? details.ReviewSummary?.ReviewsUri?.Trim(),
            WriteAReviewUri: details.GoogleMapsLinks?.WriteAReviewUri?.Trim(),
            PlaceUri: details.GoogleMapsLinks?.PlaceUri?.Trim(),
            Latitude: details.Location?.Latitude,
            Longitude: details.Location?.Longitude);
    }

    private async Task<string?> SearchMatchingPlaceIdAsync(
        WebsiteGenerationRequest request,
        CancellationToken cancellationToken)
    {
        var queryParts = new[]
        {
            request.BusinessName?.Trim(),
            request.Address?.Trim(),
            request.BusinessCategory?.Trim()
        }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        if (queryParts.Length == 0)
        {
            return null;
        }

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, SearchUrl)
        {
            Content = JsonContent.Create(new TextSearchRequest(
                TextQuery: string.Join(", ", queryParts),
                PageSize: 1,
                LanguageCode: _options.DefaultLanguageCode))
        };

        httpRequest.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        httpRequest.Headers.Add("X-Goog-FieldMask", "places.id");

        using var response = await _httpClient.SendAsync(httpRequest, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        var searchResponse = await response.Content.ReadFromJsonAsync<TextSearchResponse>(cancellationToken: cancellationToken);
        return searchResponse?.Places?.FirstOrDefault()?.Id?.Trim();
    }

    private async Task<PlaceDetailsDto> GetPlaceDetailsAsync(string placeId, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"https://places.googleapis.com/v1/places/{Uri.EscapeDataString(placeId)}");

        request.Headers.Add("X-Goog-Api-Key", _options.ApiKey);
        request.Headers.Add(
            "X-Goog-FieldMask",
            string.Join(",",
                "id",
                "displayName",
                "formattedAddress",
                "nationalPhoneNumber",
                "internationalPhoneNumber",
                "websiteUri",
                "googleMapsUri",
                "googleMapsLinks",
                "businessStatus",
                "location",
                "primaryType",
                "rating",
                "userRatingCount",
                "currentOpeningHours.weekdayDescriptions",
                "reviews",
                "editorialSummary",
                "photos",
                "delivery",
                "takeout",
                "dineIn",
                "reservable",
                "outdoorSeating",
                "liveMusic",
                "servesCoffee",
                "servesBreakfast",
                "servesLunch",
                "servesDinner",
                "servesBrunch",
                "servesBeer",
                "servesWine",
                "servesCocktails",
                "goodForChildren",
                "goodForGroups"));

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        await EnsureSuccessAsync(response, cancellationToken);

        return (await response.Content.ReadFromJsonAsync<PlaceDetailsDto>(cancellationToken: cancellationToken))
            ?? new PlaceDetailsDto();
    }

    private string BuildPhotoMediaUri(string photoName)
    {
        return $"https://places.googleapis.com/v1/{photoName}/media?maxWidthPx=1600&key={Uri.EscapeDataString(_options.ApiKey)}";
    }

    private static List<string> BuildFeatureList(PlaceDetailsDto details)
    {
        var features = new List<string>();

        AddFeatureIfTrue(features, details.Delivery, "Livraison");
        AddFeatureIfTrue(features, details.Takeout, "Vente a emporter");
        AddFeatureIfTrue(features, details.DineIn, "Service sur place");
        AddFeatureIfTrue(features, details.Reservable, "Reservation possible");
        AddFeatureIfTrue(features, details.OutdoorSeating, "Terrasse / exterieur");
        AddFeatureIfTrue(features, details.LiveMusic, "Ambiance musicale");
        AddFeatureIfTrue(features, details.ServesCoffee, "Cafe");
        AddFeatureIfTrue(features, details.ServesBreakfast, "Petit-dejeuner");
        AddFeatureIfTrue(features, details.ServesLunch, "Dejeuner");
        AddFeatureIfTrue(features, details.ServesDinner, "Diner");
        AddFeatureIfTrue(features, details.ServesBrunch, "Brunch");
        AddFeatureIfTrue(features, details.ServesBeer, "Biere");
        AddFeatureIfTrue(features, details.ServesWine, "Vin");
        AddFeatureIfTrue(features, details.ServesCocktails, "Cocktails");
        AddFeatureIfTrue(features, details.GoodForChildren, "Adapté aux enfants");
        AddFeatureIfTrue(features, details.GoodForGroups, "Convient aux groupes");

        return features
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void AddFeatureIfTrue(List<string> features, bool? value, string label)
    {
        if (value == true)
        {
            features.Add(label);
        }
    }

    private static string? NormalizeReviewText(string? localizedText, string? originalText)
    {
        var value = !string.IsNullOrWhiteSpace(localizedText)
            ? localizedText
            : originalText;

        return string.IsNullOrWhiteSpace(value)
            ? null
            : value.Trim();
    }

    private static async Task EnsureSuccessAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken);
        throw new InvalidOperationException(
            $"Google Places enrichment request failed with status {(int)response.StatusCode}: {content}");
    }

    private sealed record TextSearchRequest(
        [property: JsonPropertyName("textQuery")] string TextQuery,
        [property: JsonPropertyName("pageSize")] int PageSize,
        [property: JsonPropertyName("languageCode")] string LanguageCode);

    private sealed class TextSearchResponse
    {
        [JsonPropertyName("places")]
        public List<PlaceReferenceDto>? Places { get; init; }
    }

    private sealed class PlaceReferenceDto
    {
        [JsonPropertyName("id")]
        public string? Id { get; init; }
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

        [JsonPropertyName("internationalPhoneNumber")]
        public string? InternationalPhoneNumber { get; init; }

        [JsonPropertyName("websiteUri")]
        public string? WebsiteUri { get; init; }

        [JsonPropertyName("googleMapsUri")]
        public string? GoogleMapsUri { get; init; }

        [JsonPropertyName("googleMapsLinks")]
        public GoogleMapsLinksDto? GoogleMapsLinks { get; init; }

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

        [JsonPropertyName("currentOpeningHours")]
        public OpeningHoursDto? CurrentOpeningHours { get; init; }

        [JsonPropertyName("reviewSummary")]
        public ReviewSummaryDto? ReviewSummary { get; init; }

        [JsonPropertyName("reviews")]
        public List<ReviewDto>? Reviews { get; init; }

        [JsonPropertyName("editorialSummary")]
        public LocalizedTextDto? EditorialSummary { get; init; }

        [JsonPropertyName("photos")]
        public List<PhotoDto>? Photos { get; init; }

        [JsonPropertyName("delivery")]
        public bool? Delivery { get; init; }

        [JsonPropertyName("takeout")]
        public bool? Takeout { get; init; }

        [JsonPropertyName("dineIn")]
        public bool? DineIn { get; init; }

        [JsonPropertyName("reservable")]
        public bool? Reservable { get; init; }

        [JsonPropertyName("outdoorSeating")]
        public bool? OutdoorSeating { get; init; }

        [JsonPropertyName("liveMusic")]
        public bool? LiveMusic { get; init; }

        [JsonPropertyName("servesCoffee")]
        public bool? ServesCoffee { get; init; }

        [JsonPropertyName("servesBreakfast")]
        public bool? ServesBreakfast { get; init; }

        [JsonPropertyName("servesLunch")]
        public bool? ServesLunch { get; init; }

        [JsonPropertyName("servesDinner")]
        public bool? ServesDinner { get; init; }

        [JsonPropertyName("servesBrunch")]
        public bool? ServesBrunch { get; init; }

        [JsonPropertyName("servesBeer")]
        public bool? ServesBeer { get; init; }

        [JsonPropertyName("servesWine")]
        public bool? ServesWine { get; init; }

        [JsonPropertyName("servesCocktails")]
        public bool? ServesCocktails { get; init; }

        [JsonPropertyName("goodForChildren")]
        public bool? GoodForChildren { get; init; }

        [JsonPropertyName("goodForGroups")]
        public bool? GoodForGroups { get; init; }
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

    private sealed class OpeningHoursDto
    {
        [JsonPropertyName("weekdayDescriptions")]
        public List<string>? WeekdayDescriptions { get; init; }
    }

    private sealed class ReviewSummaryDto
    {
        [JsonPropertyName("text")]
        public LocalizedTextDto? Text { get; init; }

        [JsonPropertyName("reviewsUri")]
        public string? ReviewsUri { get; init; }
    }

    private sealed class ReviewDto
    {
        [JsonPropertyName("text")]
        public LocalizedTextDto? Text { get; init; }

        [JsonPropertyName("originalText")]
        public LocalizedTextDto? OriginalText { get; init; }

        [JsonPropertyName("rating")]
        public double? Rating { get; init; }

        [JsonPropertyName("relativePublishTimeDescription")]
        public string? RelativePublishTimeDescription { get; init; }

        [JsonPropertyName("googleMapsUri")]
        public string? GoogleMapsUri { get; init; }

        [JsonPropertyName("authorAttribution")]
        public AuthorAttributionDto? AuthorAttribution { get; init; }
    }

    private sealed class AuthorAttributionDto
    {
        [JsonPropertyName("displayName")]
        public string? DisplayName { get; init; }
    }

    private sealed class PhotoDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }
    }

    private sealed class GoogleMapsLinksDto
    {
        [JsonPropertyName("placeUri")]
        public string? PlaceUri { get; init; }

        [JsonPropertyName("reviewsUri")]
        public string? ReviewsUri { get; init; }

        [JsonPropertyName("writeAReviewUri")]
        public string? WriteAReviewUri { get; init; }
    }

    public sealed record WebsiteReviewSnippet(
        string AuthorName,
        double? Rating,
        string? RelativePublishTimeDescription,
        string? Text,
        string? GoogleMapsUri);

    public sealed record WebsiteGenerationEnrichment(
        string? GooglePlaceId,
        string? Description,
        string? PhoneNumber,
        string? WebsiteUri,
        string? GoogleMapsUri,
        double? Rating,
        int? ReviewCount,
        string? ReviewSummary,
        IReadOnlyList<string> OpeningHours,
        IReadOnlyList<string> PhotoUris,
        IReadOnlyList<string> Features,
        IReadOnlyList<WebsiteReviewSnippet> ReviewHighlights,
        string? ReviewsUri,
        string? WriteAReviewUri,
        string? PlaceUri,
        double? Latitude,
        double? Longitude);
}
