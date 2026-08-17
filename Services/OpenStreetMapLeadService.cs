using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using System.Runtime.CompilerServices;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class OpenStreetMapLeadService
{
    private const string NominatimSearchUrl = "https://nominatim.openstreetmap.org/search";
    private static readonly IReadOnlyList<string> OverpassUrls =
    [
        "https://overpass-api.de/api/interpreter",
        "https://overpass.kumi.systems/api/interpreter",
        "https://overpass.private.coffee/api/interpreter"
    ];
    private const int ProcessingConcurrency = 4;
    private const int PublicMapsEnrichmentConcurrency = 2;
    private const int PublicMapsEnrichmentMaxItems = 6;

    private static readonly Regex EmailRegex = new(
        @"[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly HttpClient _httpClient;
    private readonly GooglePlacesOptions _options;
    private readonly GoogleMapsPublicLeadEnrichmentService _googleMapsPublicLeadEnrichmentService;
    private readonly WebsiteEmailExtractionService _websiteEmailExtractionService;

    public OpenStreetMapLeadService(
        HttpClient httpClient,
        IOptions<GooglePlacesOptions> options,
        GoogleMapsPublicLeadEnrichmentService googleMapsPublicLeadEnrichmentService,
        WebsiteEmailExtractionService websiteEmailExtractionService)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _googleMapsPublicLeadEnrichmentService = googleMapsPublicLeadEnrichmentService;
        _websiteEmailExtractionService = websiteEmailExtractionService;
        _httpClient.Timeout = TimeSpan.FromSeconds(120);

        if (!_httpClient.DefaultRequestHeaders.UserAgent.Any())
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LeadRadarSaas/1.0");
        }
    }

    public async Task<LeadSearchResponse> SearchAsync(
        LeadSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var leads = new List<LeadSearchResultItem>();
        await foreach (var lead in SearchStreamAsync(request, cancellationToken))
        {
            leads.Add(lead);
        }

        var locationQuery = (request.LocationQuery ?? string.Empty).Trim();
        var businessType = LeadSearchCatalog.NormalizeBusinessType(request.BusinessType);
        var websiteFilter = LeadSearchCatalog.NormalizeWebsiteFilter(request.WebsiteFilter);
        var maxResults = Math.Clamp(request.MaxResults ?? 10, 1, 5000);
        var extractEmailsFromSites = request.ExtractEmailsFromSites && websiteFilter != "without_website";

        var withWebsiteCount = leads.Count(item => item.HasWebsite);
        var withoutWebsiteCount = leads.Count - withWebsiteCount;
        var emailCount = leads.Sum(item => item.EmailAddresses.Count);

        return new LeadSearchResponse(
            Provider: "open_data",
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

    public async IAsyncEnumerable<LeadSearchResultItem> SearchStreamAsync(
        LeadSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var locationQuery = (request.LocationQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            throw new ArgumentException("LocationQuery is required.", nameof(request));
        }

        var businessType = LeadSearchCatalog.NormalizeBusinessType(request.BusinessType);
        var websiteFilter = LeadSearchCatalog.NormalizeWebsiteFilter(request.WebsiteFilter);
        var maxResults = Math.Clamp(request.MaxResults ?? 10, 1, 5000);
        var extractEmailsFromSites = request.ExtractEmailsFromSites && websiteFilter != "without_website";
        var useGeminiForEmailExtraction =
            extractEmailsFromSites && request.UseGeminiForEmailExtraction;

        var searchArea = await GeocodeAsync(locationQuery, request.CountryCode, cancellationToken);
        var searchLatitude = ParseDouble(searchArea.Lat);
        var searchLongitude = ParseDouble(searchArea.Lon);

        var rawItems = await SearchOverpassAsync(
            searchArea,
            businessType,
            CountryCatalog.NormalizeCode(request.CountryCode),
            Math.Max(5000, maxResults * 10),
            cancellationToken);

        // Pre-sort by distance to location center
        var sortedRawItems = rawItems
            .OrderBy(item => ComputeDistanceMeters(searchLatitude, searchLongitude, item.Center?.Lat ?? item.Lat, item.Center?.Lon ?? item.Lon))
            .ToList();

        var seenPlaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var itemsToProcess = new List<OverpassElement>();
        foreach (var item in sortedRawItems)
        {
            var placeId = $"{item.Type}:{item.Id}";
            if (seenPlaceIds.Add(placeId))
            {
                itemsToProcess.Add(item);
            }
        }

        var channel = Channel.CreateUnbounded<LeadSearchResultItem?>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        var processTask = Task.Run(async () =>
        {
            try
            {
                using var semaphore = new SemaphoreSlim(ProcessingConcurrency);
                var producedCount = 0;

                for (var offset = 0;
                     offset < itemsToProcess.Count && producedCount < maxResults;
                     offset += ProcessingConcurrency)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var batch = itemsToProcess
                        .Skip(offset)
                        .Take(ProcessingConcurrency)
                        .Select(async item =>
                        {
                            try
                            {
                                return await ProcessElementAsync(
                                    item,
                                    businessType,
                                    websiteFilter,
                                    extractEmailsFromSites,
                                    useGeminiForEmailExtraction,
                                    semaphore,
                                    cancellationToken);
                            }
                            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                            {
                                throw;
                            }
                            catch
                            {
                                return null;
                            }
                        });

                    var batchResults = await Task.WhenAll(batch);
                    foreach (var lead in batchResults)
                    {
                        if (lead is null || producedCount >= maxResults)
                        {
                            continue;
                        }

                        producedCount++;
                        await channel.Writer.WriteAsync(lead, cancellationToken);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // The user explicitly stopped the search.
            }
            catch
            {
                // Individual provider errors are surfaced by the outer search flow.
            }
            finally
            {
                channel.Writer.Complete();
            }
        }, cancellationToken);

        var yieldedCount = 0;
        var pendingEnrichmentCount = 0;

        while (await channel.Reader.WaitToReadAsync(cancellationToken))
        {
            while (channel.Reader.TryRead(out var item))
            {
                if (item is not null && yieldedCount < maxResults)
                {
                    yieldedCount++;
                    if (string.IsNullOrWhiteSpace(item.PhoneNumber) &&
                        item.ContactPhoneNumbers.Count == 0 &&
                        pendingEnrichmentCount < PublicMapsEnrichmentMaxItems)
                    {
                        pendingEnrichmentCount++;
                        var enrichedList = await EnrichFinalLeadsAsync(new List<LeadSearchResultItem> { item }, cancellationToken);
                        item = enrichedList.FirstOrDefault() ?? item;
                    }
                    yield return item;
                }
            }
        }

        await processTask;
    }

    private async Task<LeadSearchResultItem?> ProcessElementAsync(
        OverpassElement item,
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
            var websiteUri = NormalizeWebsite(
                FirstNotEmpty(item.Tags?.ContactWebsite, item.Tags?.Website));
            var hasWebsite = !string.IsNullOrWhiteSpace(websiteUri);

            if (websiteFilter == "with_website" && !hasWebsite)
            {
                return null;
            }

            if (websiteFilter == "without_website" && hasWebsite)
            {
                return null;
            }

            var directoryEmails = ParseEmails(
                FirstNotEmpty(item.Tags?.ContactEmail, item.Tags?.Email));

            var directoryPhone = FirstNotEmpty(item.Tags?.ContactPhone, item.Tags?.Phone);
            var contactPhones = ParsePhones(directoryPhone);
            IReadOnlyList<string> emails = directoryEmails;
            IReadOnlyList<string> contactPageUris = Array.Empty<string>();
            var emailSource = directoryEmails.Count > 0 || contactPhones.Count > 0 ? "directory" : "none";

            if (extractEmailsFromSites && hasWebsite)
            {
                var extraction = await _websiteEmailExtractionService.ExtractPublicContactDetailsAsync(
                    websiteUri!,
                    useGeminiForEmailExtraction,
                    cancellationToken);

                emails = emails
                    .Concat(extraction.Emails)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(10)
                    .ToList();

                contactPhones = NormalizePhones(contactPhones.Concat(extraction.PhoneNumbers));

                contactPageUris = extraction.ContactPageUris;
                emailSource = extraction.Source == "none" ? emailSource : extraction.Source;
            }

            var primaryPhoneNumber = FirstNotEmpty(directoryPhone, contactPhones.FirstOrDefault());

            var latitude = item.Center?.Lat ?? item.Lat;
            var longitude = item.Center?.Lon ?? item.Lon;

            return new LeadSearchResultItem(
                PlaceId: $"{item.Type}:{item.Id}",
                Name: item.Tags?.Name?.Trim() ?? $"{LeadSearchCatalog.GetBusinessLabel(businessType)} sans nom",
                BusinessLabel: LeadSearchCatalog.GetBusinessLabel(businessType),
                PrimaryType: item.Tags?.Amenity ?? item.Tags?.Shop,
                FormattedAddress: BuildAddress(item.Tags),
                PhoneNumber: primaryPhoneNumber,
                WebsiteUri: websiteUri,
                GoogleMapsUri: BuildGoogleMapsUri(item.Tags?.Name, latitude, longitude),
                BusinessStatus: null,
                Rating: null,
                UserRatingCount: null,
                Latitude: latitude,
                Longitude: longitude,
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

    private async Task<List<LeadSearchResultItem>> EnrichFinalLeadsAsync(
        List<LeadSearchResultItem> items,
        CancellationToken cancellationToken)
    {
        var candidates = items
            .Select((item, index) => new { Item = item, Index = index })
            .Where(entry =>
                (string.IsNullOrWhiteSpace(entry.Item.PhoneNumber) &&
                 entry.Item.ContactPhoneNumbers.Count == 0) ||
                entry.Item.Rating is null ||
                entry.Item.UserRatingCount is null)
            .Take(PublicMapsEnrichmentMaxItems)
            .ToList();

        if (candidates.Count == 0)
        {
            return items;
        }

        using var semaphore = new SemaphoreSlim(PublicMapsEnrichmentConcurrency);
        var tasks = candidates.Select(async candidate =>
        {
            await semaphore.WaitAsync(cancellationToken);
            try
            {
                var enrichment = await _googleMapsPublicLeadEnrichmentService.TryEnrichAsync(
                    candidate.Item.Name,
                    candidate.Item.GoogleMapsUri,
                    candidate.Item.Latitude,
                    candidate.Item.Longitude,
                    candidate.Item.FormattedAddress,
                    cancellationToken);

                return (candidate.Index, Enrichment: enrichment);
            }
            finally
            {
                semaphore.Release();
            }
        });

        var results = await Task.WhenAll(tasks);

        foreach (var result in results)
        {
            if (result.Enrichment is null)
            {
                continue;
            }

            var currentItem = items[result.Index];
            var mergedPhones = NormalizePhones(
                currentItem.ContactPhoneNumbers.Concat(ToPhoneSeed(result.Enrichment.PhoneNumber)));
            var primaryPhoneNumber = FirstNotEmpty(
                result.Enrichment.PhoneNumber,
                currentItem.PhoneNumber,
                mergedPhones.FirstOrDefault());

            items[result.Index] = currentItem with
            {
                PhoneNumber = primaryPhoneNumber,
                ContactPhoneNumbers = mergedPhones,
                GoogleMapsUri = FirstNotEmpty(result.Enrichment.GoogleMapsUri, currentItem.GoogleMapsUri),
                Rating = result.Enrichment.Rating ?? currentItem.Rating,
                UserRatingCount = result.Enrichment.ReviewCount ?? currentItem.UserRatingCount
            };
        }

        return items;
    }

    private async Task<NominatimResult> GeocodeAsync(
        string locationQuery,
        string? countryCode,
        CancellationToken cancellationToken)
    {
        var normalizedCountryCode = CountryCatalog.NormalizeCode(countryCode).ToLowerInvariant();
        var countryFilter = string.IsNullOrWhiteSpace(normalizedCountryCode)
            ? string.Empty
            : $"&countrycodes={Uri.EscapeDataString(normalizedCountryCode)}";
        var url = $"{NominatimSearchUrl}?format=jsonv2&limit=1{countryFilter}&q={Uri.EscapeDataString(locationQuery)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept-Language", _options.DefaultLanguageCode);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var results = await response.Content.ReadFromJsonAsync<List<NominatimResult>>(cancellationToken: cancellationToken)
            ?? [];

        return results.FirstOrDefault()
            ?? throw new InvalidOperationException($"No location found for '{locationQuery}'.");
    }

    private async Task<List<OverpassElement>> SearchOverpassAsync(
        NominatimResult searchArea,
        string businessType,
        string countryCode,
        int serverLimit,
        CancellationToken cancellationToken)
    {
        var latitude = ParseDouble(searchArea.Lat);
        var longitude = ParseDouble(searchArea.Lon);
        var radiusMeters = ComputeRadiusMeters(searchArea);
        var filter = LeadSearchCatalog.GetOverpassFilter(businessType);
        var lat = latitude.ToString("0.000000", CultureInfo.InvariantCulture);
        var lon = longitude.ToString("0.000000", CultureInfo.InvariantCulture);

        var query = $$"""
            [out:json][timeout:90];
            area["ISO3166-1"="{{countryCode}}"][admin_level=2]->.assignedCountry;
            (
              node{{filter}}(around:{{radiusMeters}},{{lat}},{{lon}})(area.assignedCountry);
              way{{filter}}(around:{{radiusMeters}},{{lat}},{{lon}})(area.assignedCountry);
              relation{{filter}}(around:{{radiusMeters}},{{lat}},{{lon}})(area.assignedCountry);
            );
            out center {{serverLimit}};
            """;

        Exception? lastError = null;

        foreach (var overpassUrl in OverpassUrls)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, overpassUrl)
            {
                Content = new StringContent(
                    $"data={Uri.EscapeDataString(query)}",
                    Encoding.UTF8,
                    "application/x-www-form-urlencoded")
            };

            try
            {
                using var response = await _httpClient.SendAsync(request, cancellationToken);
                response.EnsureSuccessStatusCode();

                var payload = await response.Content.ReadFromJsonAsync<OverpassResponse>(cancellationToken: cancellationToken);
                return payload?.Elements ?? [];
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        throw new InvalidOperationException(
            "All configured Overpass endpoints failed for the current search.",
            lastError);
    }

    private static int ComputeRadiusMeters(NominatimResult result)
    {
        if (result.BoundingBox is not { Count: 4 })
        {
            return 3500;
        }

        var south = ParseDouble(result.BoundingBox[0]);
        var north = ParseDouble(result.BoundingBox[1]);
        var west = ParseDouble(result.BoundingBox[2]);
        var east = ParseDouble(result.BoundingBox[3]);
        var centerLat = ParseDouble(result.Lat);

        var latMeters = Math.Abs(north - south) * 111_320d;
        var lonMeters = Math.Abs(east - west) * 111_320d * Math.Cos(centerLat * Math.PI / 180d);
        var halfSpan = Math.Max(latMeters, lonMeters) / 2d;

        return (int)Math.Clamp(Math.Ceiling(halfSpan), 1_200d, 20_000d);
    }

    private static string? BuildAddress(OverpassTags? tags)
    {
        if (tags is null)
        {
            return null;
        }

        var line1 = string.Join(" ", new[]
        {
            tags.HouseNumber,
            tags.Street
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var line2 = string.Join(" ", new[]
        {
            tags.Postcode,
            FirstNotEmpty(tags.City, tags.Town, tags.Village)
        }.Where(value => !string.IsNullOrWhiteSpace(value)));

        var address = string.Join(", ", new[] { line1, line2 }.Where(value => !string.IsNullOrWhiteSpace(value)));
        return string.IsNullOrWhiteSpace(address) ? null : address;
    }

    private static string? BuildGoogleMapsUri(string? name, double? latitude, double? longitude)
    {
        if (latitude is null || longitude is null)
        {
            return null;
        }

        var coordinates =
            $"{latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}";
        var query = !string.IsNullOrWhiteSpace(name)
            ? $"{name} {coordinates}"
            : coordinates;

        return $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}";
    }

    private static string? NormalizeWebsite(string? rawWebsite)
    {
        var trimmed = rawWebsite?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = $"https://{trimmed}";
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : null;
    }

    private static IReadOnlyList<string> ParseEmails(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<string>();
        }

        return EmailRegex.Matches(rawValue)
            .Cast<Match>()
            .Select(match => match.Value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();
    }

    private static IReadOnlyList<string> ParsePhones(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return Array.Empty<string>();
        }

        return NormalizePhones(
            rawValue.Split([';', ',', '|'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static IReadOnlyList<string> NormalizePhones(IEnumerable<string> values)
        => values
            .Select(NormalizePhone)
            .Where(value => value.Count(char.IsDigit) >= 8)
            .GroupBy(GetPhoneDedupKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(value => value.StartsWith('+')).First())
            .Take(10)
            .ToList();

    private static IEnumerable<string> ToPhoneSeed(string? value)
        => string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string NormalizePhone(string value)
    {
        var trimmed = value.Trim();
        var hasPlus = trimmed.StartsWith("+", StringComparison.Ordinal);
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());
        return hasPlus ? $"+{digits}" : digits;
    }

    private static string GetPhoneDedupKey(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        return digits.StartsWith("33", StringComparison.Ordinal) && digits.Length == 11
            ? $"0{digits[2..]}"
            : digits;
    }

    private static double ComputeDistanceMeters(
        double originLatitude,
        double originLongitude,
        double? targetLatitude,
        double? targetLongitude)
    {
        if (targetLatitude is null || targetLongitude is null)
        {
            return double.MaxValue;
        }

        const double EarthRadiusMeters = 6_371_000d;
        var originLatitudeRadians = originLatitude * Math.PI / 180d;
        var targetLatitudeRadians = targetLatitude.Value * Math.PI / 180d;
        var deltaLatitude = (targetLatitude.Value - originLatitude) * Math.PI / 180d;
        var deltaLongitude = (targetLongitude.Value - originLongitude) * Math.PI / 180d;

        var haversine =
            Math.Sin(deltaLatitude / 2d) * Math.Sin(deltaLatitude / 2d) +
            Math.Cos(originLatitudeRadians) * Math.Cos(targetLatitudeRadians) *
            Math.Sin(deltaLongitude / 2d) * Math.Sin(deltaLongitude / 2d);

        var centralAngle = 2d * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1d - haversine));
        return EarthRadiusMeters * centralAngle;
    }

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static double ParseDouble(string? rawValue)
        => double.Parse(rawValue ?? "0", CultureInfo.InvariantCulture);

    private sealed class OverpassResponse
    {
        [JsonPropertyName("elements")]
        public List<OverpassElement>? Elements { get; init; }
    }

    private sealed class OverpassElement
    {
        [JsonPropertyName("type")]
        public string Type { get; init; } = string.Empty;

        [JsonPropertyName("id")]
        public long Id { get; init; }

        [JsonPropertyName("lat")]
        public double? Lat { get; init; }

        [JsonPropertyName("lon")]
        public double? Lon { get; init; }

        [JsonPropertyName("center")]
        public OverpassCenter? Center { get; init; }

        [JsonPropertyName("tags")]
        public OverpassTags? Tags { get; init; }
    }

    private sealed class OverpassCenter
    {
        [JsonPropertyName("lat")]
        public double? Lat { get; init; }

        [JsonPropertyName("lon")]
        public double? Lon { get; init; }
    }

    private sealed class OverpassTags
    {
        [JsonPropertyName("name")]
        public string? Name { get; init; }

        [JsonPropertyName("amenity")]
        public string? Amenity { get; init; }

        [JsonPropertyName("shop")]
        public string? Shop { get; init; }

        [JsonPropertyName("website")]
        public string? Website { get; init; }

        [JsonPropertyName("contact:website")]
        public string? ContactWebsite { get; init; }

        [JsonPropertyName("email")]
        public string? Email { get; init; }

        [JsonPropertyName("contact:email")]
        public string? ContactEmail { get; init; }

        [JsonPropertyName("phone")]
        public string? Phone { get; init; }

        [JsonPropertyName("contact:phone")]
        public string? ContactPhone { get; init; }

        [JsonPropertyName("addr:housenumber")]
        public string? HouseNumber { get; init; }

        [JsonPropertyName("addr:street")]
        public string? Street { get; init; }

        [JsonPropertyName("addr:postcode")]
        public string? Postcode { get; init; }

        [JsonPropertyName("addr:city")]
        public string? City { get; init; }

        [JsonPropertyName("addr:town")]
        public string? Town { get; init; }

        [JsonPropertyName("addr:village")]
        public string? Village { get; init; }
    }

    private sealed class NominatimResult
    {
        [JsonPropertyName("lat")]
        public string? Lat { get; init; }

        [JsonPropertyName("lon")]
        public string? Lon { get; init; }

        [JsonPropertyName("boundingbox")]
        public List<string>? BoundingBox { get; init; }
    }
}
