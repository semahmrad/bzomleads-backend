using System.Runtime.CompilerServices;
using Backend.Models;

namespace Backend.Services;

public sealed class LeadSearchService
{
    private readonly GooglePlacesLeadService _googlePlacesLeadService;
    private readonly LeadSearchStoreService _leadSearchStoreService;
    private readonly OpenStreetMapLeadService _openStreetMapLeadService;

    public LeadSearchService(
        GooglePlacesLeadService googlePlacesLeadService,
        OpenStreetMapLeadService openStreetMapLeadService,
        LeadSearchStoreService leadSearchStoreService)
    {
        _googlePlacesLeadService = googlePlacesLeadService;
        _openStreetMapLeadService = openStreetMapLeadService;
        _leadSearchStoreService = leadSearchStoreService;
    }

    public async Task<LeadSearchResponse> SearchAsync(
        LeadSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var provider = LeadSearchCatalog.NormalizeProvider(request.Provider);
        var locationQuery = (request.LocationQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            throw new ArgumentException("LocationQuery is required.", nameof(request));
        }

        var businessType = LeadSearchCatalog.NormalizeBusinessType(request.BusinessType);
        var websiteFilter = LeadSearchCatalog.NormalizeWebsiteFilter(request.WebsiteFilter);
        var countryCode = CountryCatalog.NormalizeCode(request.CountryCode);
        if (CountryCatalog.Find(countryCode) is null)
        {
            throw new ArgumentException("A valid account country is required.", nameof(request));
        }
        var requestedNewResults = NormalizeRequestedNewResults(provider, request.MaxResults);
        var normalizedRequest = request with
        {
            Provider = provider,
            LocationQuery = locationQuery,
            BusinessType = businessType,
            WebsiteFilter = websiteFilter,
            MaxResults = requestedNewResults
        };

        var storedResults = await _leadSearchStoreService.GetStoredResultsAsync(
            provider,
            locationQuery,
            businessType,
            countryCode,
            cancellationToken);

        var existingResults = ApplyWebsiteFilter(storedResults, websiteFilter);
        var existingByPlaceId = storedResults.ToDictionary(item => item.PlaceId, StringComparer.OrdinalIgnoreCase);
        var fetchBatchSize = ComputeFetchBatchSize(provider, requestedNewResults, storedResults.Count);

        var freshResponse = await SearchProviderAsync(
            normalizedRequest with { MaxResults = fetchBatchSize },
            cancellationToken);

        var selectedNewResults = new List<LeadSearchResultItem>();
        var itemsToPersist = new List<LeadSearchResultItem>();
        var persistedPlaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var item in freshResponse.Items)
        {
            var mergedItem = existingByPlaceId.TryGetValue(item.PlaceId, out var existingItem)
                ? MergeLeadResult(existingItem, item)
                : item;

            if (!persistedPlaceIds.Add(mergedItem.PlaceId))
            {
                continue;
            }

            if (existingByPlaceId.ContainsKey(mergedItem.PlaceId))
            {
                itemsToPersist.Add(mergedItem);
                continue;
            }

            if (selectedNewResults.Count >= requestedNewResults)
            {
                continue;
            }

            selectedNewResults.Add(mergedItem);
            itemsToPersist.Add(mergedItem);
        }

        await _leadSearchStoreService.UpsertResultsAsync(
            provider,
            locationQuery,
            businessType,
            countryCode,
            itemsToPersist,
            cancellationToken);

        var allStoredResults = await _leadSearchStoreService.GetStoredResultsAsync(
            provider,
            locationQuery,
            businessType,
            countryCode,
            cancellationToken);
        var returnedItems = ApplyWebsiteFilter(allStoredResults, websiteFilter);

        var withWebsiteCount = returnedItems.Count(item => item.HasWebsite);
        var withoutWebsiteCount = returnedItems.Count - withWebsiteCount;
        var emailCount = returnedItems.Sum(item => item.EmailAddresses.Count);

        return new LeadSearchResponse(
            Provider: provider,
            Query: locationQuery,
            BusinessType: businessType,
            WebsiteFilter: websiteFilter,
            ExtractEmailsFromSites: normalizedRequest.ExtractEmailsFromSites && websiteFilter != "without_website",
            Total: returnedItems.Count,
            ExistingResultsCount: existingResults.Count,
            NewResultsCount: selectedNewResults.Count,
            RequestedNewResults: requestedNewResults,
            WithWebsiteCount: withWebsiteCount,
            WithoutWebsiteCount: withoutWebsiteCount,
            EmailCount: emailCount,
            Items: returnedItems);
    }

    public async IAsyncEnumerable<LeadStreamMessage> SearchStreamAsync(
        LeadSearchRequest request,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var provider = LeadSearchCatalog.NormalizeProvider(request.Provider);
        var locationQuery = (request.LocationQuery ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(locationQuery))
        {
            yield return new LeadStreamMessage("error", ErrorMessage: "LocationQuery is required.");
            yield break;
        }

        var businessType = LeadSearchCatalog.NormalizeBusinessType(request.BusinessType);
        var websiteFilter = LeadSearchCatalog.NormalizeWebsiteFilter(request.WebsiteFilter);
        var countryCode = CountryCatalog.NormalizeCode(request.CountryCode);
        if (CountryCatalog.Find(countryCode) is null)
        {
            yield return new LeadStreamMessage("error", ErrorMessage: "A valid account country is required.");
            yield break;
        }
        var requestedNewResults = NormalizeRequestedNewResults(provider, request.MaxResults);
        var normalizedRequest = request with
        {
            Provider = provider,
            LocationQuery = locationQuery,
            BusinessType = businessType,
            WebsiteFilter = websiteFilter,
            MaxResults = requestedNewResults
        };

        var storedResults = await _leadSearchStoreService.GetStoredResultsAsync(
            provider,
            locationQuery,
            businessType,
            countryCode,
            cancellationToken);

        var existingResults = ApplyWebsiteFilter(storedResults, websiteFilter);
        var existingByPlaceId = storedResults.ToDictionary(item => item.PlaceId, StringComparer.OrdinalIgnoreCase);

        var counts = new LeadSearchResponseSummary(
            Total: existingResults.Count,
            ExistingResultsCount: existingResults.Count,
            NewResultsCount: 0,
            RequestedNewResults: requestedNewResults,
            WithWebsiteCount: existingResults.Count(item => item.HasWebsite),
            WithoutWebsiteCount: existingResults.Count(item => !item.HasWebsite),
            EmailCount: existingResults.Sum(item => item.EmailAddresses.Count));

        yield return new LeadStreamMessage("summary", Summary: counts);

        foreach (var item in existingResults)
        {
            yield return new LeadStreamMessage("lead", Lead: item);
        }

        var fetchBatchSize = ComputeFetchBatchSize(provider, requestedNewResults, storedResults.Count);
        var providerRequest = normalizedRequest with { MaxResults = fetchBatchSize };

        IAsyncEnumerable<LeadSearchResultItem> freshStream = provider switch
        {
            "google_places" => _googlePlacesLeadService.SearchStreamAsync(providerRequest, cancellationToken),
            _ => _openStreetMapLeadService.SearchStreamAsync(providerRequest, cancellationToken)
        };

        var selectedNewResults = new List<LeadSearchResultItem>();
        var persistedPlaceIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await foreach (var item in freshStream.WithCancellation(cancellationToken))
        {
            var mergedItem = existingByPlaceId.TryGetValue(item.PlaceId, out var existingItem)
                ? MergeLeadResult(existingItem, item)
                : item;

            if (!persistedPlaceIds.Add(mergedItem.PlaceId))
            {
                continue;
            }

            var isExisting = existingByPlaceId.ContainsKey(mergedItem.PlaceId);

            await _leadSearchStoreService.UpsertResultsAsync(
                provider,
                locationQuery,
                businessType,
                countryCode,
                new[] { mergedItem },
                cancellationToken);

            if (isExisting)
            {
                yield return new LeadStreamMessage("lead", Lead: mergedItem);
            }
            else
            {
                if (selectedNewResults.Count < requestedNewResults)
                {
                    selectedNewResults.Add(mergedItem);
                    yield return new LeadStreamMessage("lead", Lead: mergedItem);
                }
            }
        }

        var allStoredResults = await _leadSearchStoreService.GetStoredResultsAsync(
            provider,
            locationQuery,
            businessType,
            countryCode,
            cancellationToken);
        var returnedItems = ApplyWebsiteFilter(allStoredResults, websiteFilter);

        var finalWithWebsiteCount = returnedItems.Count(item => item.HasWebsite);
        var finalWithoutWebsiteCount = returnedItems.Count - finalWithWebsiteCount;
        var finalEmailCount = returnedItems.Sum(item => item.EmailAddresses.Count);

        var finalSummary = new LeadSearchResponseSummary(
            Total: returnedItems.Count,
            ExistingResultsCount: existingResults.Count,
            NewResultsCount: selectedNewResults.Count,
            RequestedNewResults: requestedNewResults,
            WithWebsiteCount: finalWithWebsiteCount,
            WithoutWebsiteCount: finalWithoutWebsiteCount,
            EmailCount: finalEmailCount);

        yield return new LeadStreamMessage("done", Summary: finalSummary, Leads: returnedItems);
    }

    private Task<LeadSearchResponse> SearchProviderAsync(
        LeadSearchRequest request,
        CancellationToken cancellationToken)
    {
        return request.Provider switch
        {
            "google_places" => _googlePlacesLeadService.SearchAsync(request, cancellationToken),
            _ => _openStreetMapLeadService.SearchAsync(request, cancellationToken)
        };
    }

    private static int NormalizeRequestedNewResults(string provider, int? requestedValue)
    {
        var maxAllowed = provider == "google_places" ? 20 : 5000;
        return Math.Clamp(requestedValue ?? 10, 1, maxAllowed);
    }

    private static int ComputeFetchBatchSize(string provider, int requestedNewResults, int storedCount)
    {
        if (provider == "google_places")
        {
            return Math.Clamp(Math.Max(requestedNewResults + 5, 20), 1, 20);
        }

        var duplicatePadding = Math.Min(storedCount / 2, 100);
        return Math.Clamp(requestedNewResults + duplicatePadding + 20, requestedNewResults, 5000);
    }

    private static List<LeadSearchResultItem> ApplyWebsiteFilter(
        IReadOnlyList<LeadSearchResultItem> items,
        string websiteFilter)
    {
        return websiteFilter switch
        {
            "with_website" => items.Where(item => item.HasWebsite).ToList(),
            "without_website" => items.Where(item => !item.HasWebsite).ToList(),
            _ => items.ToList()
        };
    }

    private static LeadSearchResultItem MergeLeadResult(
        LeadSearchResultItem existingItem,
        LeadSearchResultItem incomingItem)
    {
        var mergedContactPhones = MergeValues(
            existingItem.ContactPhoneNumbers,
            incomingItem.ContactPhoneNumbers,
            20);
        var mergedPhoneNumber = PreferNullableText(incomingItem.PhoneNumber, existingItem.PhoneNumber);
        mergedPhoneNumber = PreferNullableText(mergedPhoneNumber, mergedContactPhones.FirstOrDefault());

        return new LeadSearchResultItem(
            PlaceId: incomingItem.PlaceId,
            Name: PreferText(incomingItem.Name, existingItem.Name),
            BusinessLabel: PreferText(incomingItem.BusinessLabel, existingItem.BusinessLabel),
            PrimaryType: PreferNullableText(incomingItem.PrimaryType, existingItem.PrimaryType),
            FormattedAddress: PreferNullableText(incomingItem.FormattedAddress, existingItem.FormattedAddress),
            PhoneNumber: mergedPhoneNumber,
            WebsiteUri: PreferNullableText(incomingItem.WebsiteUri, existingItem.WebsiteUri),
            GoogleMapsUri: PreferNullableText(incomingItem.GoogleMapsUri, existingItem.GoogleMapsUri),
            BusinessStatus: PreferNullableText(incomingItem.BusinessStatus, existingItem.BusinessStatus),
            Rating: incomingItem.Rating ?? existingItem.Rating,
            UserRatingCount: incomingItem.UserRatingCount ?? existingItem.UserRatingCount,
            Latitude: incomingItem.Latitude ?? existingItem.Latitude,
            Longitude: incomingItem.Longitude ?? existingItem.Longitude,
            HasWebsite: incomingItem.HasWebsite || existingItem.HasWebsite,
            EmailExtractionSource: PreferExtractionSource(
                incomingItem.EmailExtractionSource,
                existingItem.EmailExtractionSource),
            EmailAddresses: MergeValues(existingItem.EmailAddresses, incomingItem.EmailAddresses, 20),
            ContactPhoneNumbers: mergedContactPhones,
            ContactPageUris: MergeValues(existingItem.ContactPageUris, incomingItem.ContactPageUris, 20));
    }

    private static string PreferText(string preferredValue, string fallbackValue)
        => string.IsNullOrWhiteSpace(preferredValue) ? fallbackValue : preferredValue.Trim();

    private static string? PreferNullableText(string? preferredValue, string? fallbackValue)
        => string.IsNullOrWhiteSpace(preferredValue) ? fallbackValue : preferredValue.Trim();

    private static string PreferExtractionSource(string preferredValue, string fallbackValue)
    {
        if (!string.IsNullOrWhiteSpace(preferredValue) &&
            !string.Equals(preferredValue, "none", StringComparison.OrdinalIgnoreCase))
        {
            return preferredValue.Trim();
        }

        return string.IsNullOrWhiteSpace(fallbackValue) ? "none" : fallbackValue.Trim();
    }

    private static IReadOnlyList<string> MergeValues(
        IReadOnlyList<string> existingValues,
        IReadOnlyList<string> incomingValues,
        int maxCount)
    {
        return existingValues
            .Concat(incomingValues)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(maxCount)
            .ToList();
    }
}
