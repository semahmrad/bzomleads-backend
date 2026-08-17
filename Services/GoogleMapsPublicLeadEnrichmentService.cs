using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Backend.Services;

public sealed class GoogleMapsPublicLeadEnrichmentService
{
    private const int SearchVirtualTimeBudgetMs = 6_000;
    private const int PlaceVirtualTimeBudgetMs = 8_000;
    private static readonly TimeSpan BrowserExecutionTimeout = TimeSpan.FromSeconds(25);
    private static readonly Lazy<string?> BrowserExecutable = new(FindBrowserExecutable);

    private static readonly Regex PhoneDataItemRegex = new(
        @"data-item-id=""phone:tel:(?<value>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TelHrefRegex = new(
        @"href=""tel:(?<value>[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex LabeledPhoneRegex = new(
        @"(?:Phone|Telephone|Call|Contact|Telephone number|Numero de telephone|Tel)\s*:?\s*(?<value>\+?\d[\d\s()./\-]{6,}\d)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex VisibleSearchCardPhoneRegex = new(
        @"class=""UsdlK"">(?<value>[^<]+)<",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceLinkRegex = new(
        @"href=""(?<url>(?:https://www\.google\.com)?/maps/place/[^""]+)""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex EmbeddedPlaceUrlRegex = new(
        @"(?<url>(?:https?:\\?/\\?/(?:www\.)?google\.[a-z.]+)?\\?/maps\\?/place\\?/[^""'<>\s]+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PlaceNameRegex = new(
        @"<h1\b[^>]*class=""[^""]*\bDUwDvf\b[^""]*""[^>]*>(?<value>.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex PlaceAddressRegex = new(
        @"aria-label=""(?:Adresse|Address)\s*:\s*(?<value>[^""]+)""[^>]*data-item-id=""address""",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex OverallRatingRegex = new(
        @"class=""F7nice[^""]*""[\s\S]{0,700}?aria-label=""(?<value>[0-5](?:[\.,][0-9]+)?)(?:&nbsp;|\s|\u00a0)+(?:étoile|étoiles|star|stars)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex AnyRatingRegex = new(
        @"aria-label=""(?<value>[0-5](?:[\.,][0-9]+)?)(?:&nbsp;|\s|\u00a0)+(?:étoile|étoiles|star|stars)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReviewCountRegex = new(
        @"(?<value>[0-9]{1,3}(?:[\s\u00a0\.,][0-9]{3})+|[0-9]+(?:[\.,][0-9]+)?k?)(?:&nbsp;|\s|\u00a0)+(?:avis|reviews?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReviewBlockRegex = new(
        @"<div\b[^>]*data-review-id=""[^""]+""[\s\S]*?(?=<div\b[^>]*data-review-id=""|$)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ReviewAuthorRegex = new(
        @"class=""d4r55[^""]*""[^>]*>(?<value>.*?)</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReviewTimeRegex = new(
        @"class=""rsqaWe[^""]*""[^>]*>(?<value>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReviewTextRegex = new(
        @"class=""wiI7pd[^""]*""[^>]*>(?<value>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly Regex ReviewRatingRegex = new(
        @"class=""kvMYJc[^""]*""[^>]*aria-label=""(?<value>[0-5](?:[\.,][0-9]+)?)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly HashSet<string> GenericBusinessNameTokens = new(StringComparer.Ordinal)
    {
        "bar", "cafe", "coffee", "hotel", "restaurant", "shop", "store"
    };
    private readonly ILogger<GoogleMapsPublicLeadEnrichmentService> _logger;
    private readonly GeminiProxyService _geminiProxyService;

    public GoogleMapsPublicLeadEnrichmentService(
        ILogger<GoogleMapsPublicLeadEnrichmentService> logger,
        GeminiProxyService geminiProxyService)
    {
        _logger = logger;
        _geminiProxyService = geminiProxyService;
    }

    public async Task<PublicLeadEnrichment?> TryEnrichAsync(
        string? businessName,
        string? googleMapsUri,
        double? latitude,
        double? longitude,
        string? formattedAddress = null,
        CancellationToken cancellationToken = default,
        bool includeReviews = false)
    {
        var browserExecutable = BrowserExecutable.Value;
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            return null;
        }

        PublicLeadEnrichment? googleEnrichment = null;
        foreach (var searchUrl in BuildSearchUrls(businessName, googleMapsUri, latitude, longitude, formattedAddress))
        {
            googleEnrichment = await TryEnrichFromGoogleMapsAsync(
                browserExecutable,
                searchUrl,
                businessName,
                formattedAddress,
                cancellationToken,
                includeReviews);

            if (googleEnrichment is not null)
            {
                break;
            }
        }

        var phoneNumber = googleEnrichment?.PhoneNumber;

        // Fallback: If no phone number was found by browser automation, call Gemini proxy to extract it using web search
        if (phoneNumber is null && !string.IsNullOrWhiteSpace(businessName))
        {
            try
            {
                var promptBuilder = new System.Text.StringBuilder();
                promptBuilder.AppendLine("En tant qu'assistant de recherche de prospection et de leads, trouve le numéro de téléphone de cet établissement sur le Web :");
                promptBuilder.AppendLine($"- Nom : {businessName}");
                if (!string.IsNullOrWhiteSpace(formattedAddress))
                {
                    promptBuilder.AppendLine($"- Adresse : {formattedAddress}");
                }
                if (latitude is not null && longitude is not null)
                {
                    promptBuilder.AppendLine($"- Coordonnées GPS : {latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}, {longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
                }
                promptBuilder.AppendLine();
                promptBuilder.AppendLine("Instructions cruciales :");
                promptBuilder.AppendLine("1. Recherche sur le Web pour trouver le vrai numéro de téléphone de cet établissement.");
                promptBuilder.AppendLine("2. Renvoie UNIQUEMENT le numéro de téléphone trouvé, de préférence au format national français propre (comme 04 92 21 04 04 ou international +33...).");
                promptBuilder.AppendLine("3. S'il y a plusieurs numéros différents, renvoie le numéro de téléphone le plus pertinent pour ce lieu précis.");
                promptBuilder.AppendLine("4. Si l'établissement n'a pas de téléphone ou s'il est impossible de le trouver de manière sûre, réponds uniquement \"NON_TROUVE\".");
                promptBuilder.AppendLine("5. Ne renvoie AUCUNE autre explication, AUCUN mot superflu, AUCUNE politesse. Juste le numéro de téléphone brut ou \"NON_TROUVE\".");

                _logger.LogInformation("Attempting Gemini fallback phone extraction for {BusinessName}...", businessName);
                var geminiResponse = await _geminiProxyService.AskAsync(promptBuilder.ToString(), cancellationToken);
                var cleanedGeminiPhone = CleanPhoneCandidate(geminiResponse);
                if (!string.IsNullOrWhiteSpace(cleanedGeminiPhone) && !geminiResponse.Contains("NON_TROUVE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation("Gemini successfully extracted phone for {BusinessName}: {CleanedPhone}", businessName, cleanedGeminiPhone);
                    phoneNumber = cleanedGeminiPhone;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Gemini fallback phone enrichment failed for {BusinessName}.", businessName);
            }
        }

        if (googleEnrichment is not null)
        {
            return googleEnrichment with { PhoneNumber = FirstNotEmpty(phoneNumber, googleEnrichment.PhoneNumber) };
        }

        return phoneNumber is null
            ? null
            : new PublicLeadEnrichment(phoneNumber, null, null, null, [], null);
    }

    private async Task<PublicLeadEnrichment?> TryEnrichFromGoogleMapsAsync(
        string browserExecutable,
        string searchUrl,
        string? businessName,
        string? formattedAddress,
        CancellationToken cancellationToken,
        bool includeReviews)
    {
        string? searchDom;
        try
        {
            searchDom = await DumpDomAsync(
                browserExecutable,
                searchUrl,
                SearchVirtualTimeBudgetMs,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
        {
            _logger.LogDebug(ex, "Public Google Maps search enrichment failed for {BusinessName}.", businessName);
            return null;
        }

        if (string.IsNullOrWhiteSpace(searchDom))
        {
            return null;
        }

        var placeUrl = NormalizeGoogleMapsUrl(ExtractBestPlaceUrl(searchDom, businessName));
        if (string.IsNullOrWhiteSpace(placeUrl) && IsMatchingBusiness(searchDom, businessName, formattedAddress))
        {
            placeUrl = NormalizeGoogleMapsUrl(searchUrl);
        }

        if (string.IsNullOrWhiteSpace(placeUrl))
        {
            return null;
        }

        string placeDom;
        if (IsMatchingBusiness(searchDom, businessName, formattedAddress))
        {
            placeDom = searchDom;
        }
        else
        {
            try
            {
                var loadedPlaceDom = await DumpDomAsync(
                    browserExecutable,
                    placeUrl,
                    PlaceVirtualTimeBudgetMs,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(loadedPlaceDom))
                {
                    return null;
                }

                placeDom = loadedPlaceDom;
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
            {
                _logger.LogDebug(ex, "Public Google Maps place enrichment failed for {BusinessName}.", businessName);
                return null;
            }
        }

        if (!IsMatchingBusiness(placeDom, businessName, formattedAddress))
        {
            _logger.LogWarning(
                "Rejected a Google Maps match for {BusinessName}: the public place identity did not match.",
                businessName);
            return null;
        }

        var rating = ExtractRating(placeDom);
        var reviewCount = ExtractReviewCount(placeDom);
        var reviewsUri = BuildReviewsUri(placeUrl);
        var reviewHighlights = ExtractReviewHighlights(placeDom, reviewsUri);

        if (includeReviews && reviewHighlights.Count < 3 && !string.IsNullOrWhiteSpace(reviewsUri))
        {
            try
            {
                var reviewsDom = await DumpReviewsDomAsync(
                    browserExecutable,
                    placeUrl,
                    cancellationToken);
                if (string.IsNullOrWhiteSpace(reviewsDom))
                {
                    return new PublicLeadEnrichment(
                        ExtractPhoneNumber(placeDom),
                        placeUrl,
                        rating,
                        reviewCount,
                        reviewHighlights,
                        reviewsUri);
                }

                rating ??= ExtractRating(reviewsDom);
                reviewCount ??= ExtractReviewCount(reviewsDom);
                reviewHighlights = ExtractReviewHighlights(reviewsDom, reviewsUri);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
            {
                _logger.LogDebug(ex, "Public Google Maps review enrichment failed for {BusinessName}.", businessName);
            }
        }

        return new PublicLeadEnrichment(
            ExtractPhoneNumber(placeDom),
            placeUrl,
            rating,
            reviewCount,
            reviewHighlights,
            reviewsUri);
    }
    private static IReadOnlyList<string> BuildSearchUrls(
        string? businessName,
        string? googleMapsUri,
        double? latitude,
        double? longitude,
        string? formattedAddress)
    {
        var urls = new List<string>();
        if (!string.IsNullOrWhiteSpace(googleMapsUri))
        {
            urls.Add(AppendLanguageHint(googleMapsUri.Trim()));
        }

        var coordinates = latitude is not null && longitude is not null
            ? $"{latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}"
            : null;
        var query = string.Join(
            " ",
            new[] { businessName, formattedAddress, coordinates }
                .Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value!.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase));

        if (!string.IsNullOrWhiteSpace(query))
        {
            urls.Add(AppendLanguageHint(
                $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}"));
        }

        return urls.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string AppendLanguageHint(string url)
    {
        if (url.Contains("hl=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"{url}{(url.Contains('?') ? '&' : '?')}hl=fr";
    }

    private static bool IsMatchingBusiness(string html, string? businessName, string? formattedAddress)
    {
        if (string.IsNullOrWhiteSpace(businessName))
        {
            return true;
        }

        var actualName = ExtractHtmlText(PlaceNameRegex.Match(html).Groups["value"].Value);
        if (string.IsNullOrWhiteSpace(actualName) || !NamesLikelyMatch(businessName, actualName))
        {
            return false;
        }

        var expectedPostalCode = ExtractPostalCode(formattedAddress);
        var actualAddress = ExtractHtmlText(PlaceAddressRegex.Match(html).Groups["value"].Value);
        var actualPostalCode = ExtractPostalCode(actualAddress);

        return string.IsNullOrWhiteSpace(expectedPostalCode) ||
               string.IsNullOrWhiteSpace(actualPostalCode) ||
               string.Equals(expectedPostalCode, actualPostalCode, StringComparison.OrdinalIgnoreCase);
    }

    private static bool NamesLikelyMatch(string expectedName, string actualName)
    {
        var expected = NormalizeForComparison(expectedName);
        var actual = NormalizeForComparison(actualName);
        if (string.IsNullOrWhiteSpace(expected) || string.IsNullOrWhiteSpace(actual))
        {
            return false;
        }

        if (string.Equals(expected, actual, StringComparison.Ordinal) ||
            expected.Contains(actual, StringComparison.Ordinal) ||
            actual.Contains(expected, StringComparison.Ordinal))
        {
            return true;
        }

        var expectedTokens = expected
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(static token => token.Length >= 3 && !GenericBusinessNameTokens.Contains(token))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        var actualTokens = actual
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(static token => token.Length >= 3 && !GenericBusinessNameTokens.Contains(token))
            .ToHashSet(StringComparer.Ordinal);

        if (expectedTokens.Count == 0)
        {
            expectedTokens = expected
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(static token => token.Length >= 2)
                .Distinct(StringComparer.Ordinal)
                .ToList();
        }

        var matchingTokenCount = expectedTokens.Count(actualTokens.Contains);
        var requiredTokenCount = expectedTokens.Count <= 1
            ? 1
            : (int)Math.Ceiling(expectedTokens.Count * 0.6);

        return matchingTokenCount >= requiredTokenCount;
    }

    private static string? ExtractPostalCode(string? value)
    {
        var match = Regex.Match(value ?? string.Empty, @"\b[0-9]{5}\b");
        return match.Success ? match.Value : null;
    }

    private static double? ExtractRating(string html)
    {
        var match = OverallRatingRegex.Match(html);
        if (!match.Success)
        {
            match = AnyRatingRegex.Match(html);
        }

        var normalized = match.Groups["value"].Value.Replace(',', '.');
        return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var rating) &&
               rating is >= 0 and <= 5
            ? rating
            : null;
    }

    private static int? ExtractReviewCount(string html)
    {
        var values = ReviewCountRegex.Matches(html)
            .Cast<Match>()
            .Select(match => ParseReviewCount(match.Groups["value"].Value))
            .Where(static value => value is > 0)
            .Select(static value => value!.Value)
            .ToList();

        return values.Count == 0 ? null : values.Max();
    }

    private static int? ParseReviewCount(string? value)
    {
        var normalized = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace("\u00a0", string.Empty, StringComparison.Ordinal)
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Trim()
            .ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (normalized.EndsWith('k'))
        {
            normalized = normalized[..^1].Replace(',', '.');
            return double.TryParse(normalized, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var thousands)
                ? (int)Math.Round(thousands * 1000, MidpointRounding.AwayFromZero)
                : null;
        }

        var digits = new string(normalized.Where(char.IsDigit).ToArray());
        return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var count)
            ? count
            : null;
    }

    private static List<PublicReview> ExtractReviewHighlights(string html, string? reviewsUri)
    {
        return ReviewBlockRegex.Matches(html)
            .Cast<Match>()
            .Select(match =>
            {
                var block = match.Value;
                var author = ExtractHtmlText(ReviewAuthorRegex.Match(block).Groups["value"].Value);
                var relativeTime = ExtractHtmlText(ReviewTimeRegex.Match(block).Groups["value"].Value);
                var text = ExtractHtmlText(ReviewTextRegex.Match(block).Groups["value"].Value);
                var ratingText = ReviewRatingRegex.Match(block).Groups["value"].Value.Replace(',', '.');
                var rating = double.TryParse(
                    ratingText,
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var parsedRating)
                    ? parsedRating
                    : (double?)null;

                return new PublicReview(
                    string.IsNullOrWhiteSpace(author) ? "Client Google" : author,
                    rating,
                    relativeTime,
                    text,
                    reviewsUri);
            })
            .Where(static review => !string.IsNullOrWhiteSpace(review.Text))
            .GroupBy(static review => $"{review.AuthorName}\n{review.Text}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .OrderByDescending(static review => review.Rating)
            .Take(3)
            .ToList();
    }

    private static string? BuildReviewsUri(string? placeUrl)
    {
        var normalized = NormalizeGoogleMapsUrl(placeUrl);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return null;
        }

        if (!normalized.Contains("/maps/place/", StringComparison.OrdinalIgnoreCase))
        {
            return AppendLanguageHint(normalized);
        }

        var reviewUrl = normalized
            .Replace("!4m7!3m6", "!4m8!3m7", StringComparison.Ordinal)
            .Replace("!4m5!3m4", "!4m8!3m7", StringComparison.Ordinal);

        if (!reviewUrl.Contains("!9m1!1b1", StringComparison.Ordinal))
        {
            var queryIndex = reviewUrl.IndexOf('?');
            var path = queryIndex >= 0 ? reviewUrl[..queryIndex] : reviewUrl;
            var query = queryIndex >= 0 ? reviewUrl[queryIndex..] : string.Empty;
            var markerIndex = path.IndexOf("!16s", StringComparison.Ordinal);
            path = markerIndex >= 0
                ? path.Insert(markerIndex, "!9m1!1b1")
                : $"{path}!9m1!1b1";
            reviewUrl = $"{path}{query}";
        }

        return AppendLanguageHint(reviewUrl);
    }

    private static string? ExtractHtmlText(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        var withoutTags = Regex.Replace(decoded, "<[^>]+>", " ");
        var normalizedWhitespace = Regex.Replace(withoutTags, @"\s+", " ").Trim();
        return string.IsNullOrWhiteSpace(normalizedWhitespace) ? null : normalizedWhitespace;
    }

    private async Task<string?> DumpReviewsDomAsync(
        string browserExecutable,
        string placeUrl,
        CancellationToken cancellationToken)
    {
        var userDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "lead-radar-reviews-browser",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(userDataDirectory);

        Process? process = null;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(BrowserExecutionTimeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = browserExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in new[]
                     {
                         "--headless=new",
                         "--disable-gpu",
                         "--no-sandbox",
                         "--disable-dev-shm-usage",
                         "--no-first-run",
                         "--no-default-browser-check",
                         "--window-size=1440,2200",
                         "--lang=fr-FR",
                         "--remote-debugging-port=0",
                         $"--user-data-dir={userDataDirectory}",
                         AppendLanguageHint(placeUrl)
                     })
            {
                startInfo.ArgumentList.Add(argument);
            }

            process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                return null;
            }

            var outputDrainTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorDrainTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);
            var devToolsPortFile = Path.Combine(userDataDirectory, "DevToolsActivePort");
            while (!File.Exists(devToolsPortFile))
            {
                if (process.HasExited)
                {
                    return null;
                }

                await Task.Delay(100, timeoutSource.Token);
            }

            var portLines = await File.ReadAllLinesAsync(devToolsPortFile, timeoutSource.Token);
            if (portLines.Length == 0 || !int.TryParse(portLines[0], out var devToolsPort))
            {
                return null;
            }

            using var discoveryClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            string? webSocketDebuggerUrl = null;
            for (var attempt = 0; attempt < 30 && string.IsNullOrWhiteSpace(webSocketDebuggerUrl); attempt++)
            {
                try
                {
                    using var pagesJson = JsonDocument.Parse(await discoveryClient.GetStringAsync(
                        $"http://127.0.0.1:{devToolsPort}/json/list",
                        timeoutSource.Token));
                    webSocketDebuggerUrl = pagesJson.RootElement
                        .EnumerateArray()
                        .FirstOrDefault(page =>
                            page.TryGetProperty("type", out var type) &&
                            string.Equals(type.GetString(), "page", StringComparison.OrdinalIgnoreCase))
                        .GetProperty("webSocketDebuggerUrl")
                        .GetString();
                }
                catch (Exception ex) when (ex is HttpRequestException or InvalidOperationException or KeyNotFoundException)
                {
                    await Task.Delay(100, timeoutSource.Token);
                }
            }

            if (string.IsNullOrWhiteSpace(webSocketDebuggerUrl))
            {
                return null;
            }

            using var webSocket = new ClientWebSocket();
            await webSocket.ConnectAsync(new Uri(webSocketDebuggerUrl), timeoutSource.Token);
            using (await SendDevToolsCommandAsync(webSocket, 1, "Page.enable", new { }, timeoutSource.Token))
            {
            }

            using (await SendDevToolsCommandAsync(webSocket, 2, "Runtime.enable", new { }, timeoutSource.Token))
            {
            }

            await Task.Delay(2_500, timeoutSource.Token);
            const string clickReviewsExpression = """
                (() => {
                  const nodes = [...document.querySelectorAll('button, [role="tab"]')];
                  const target = nodes.find(node => {
                    const label = `${node.getAttribute('aria-label') || ''} ${node.textContent || ''}`.trim();
                    return /(?:\bavis\b|reviews?)/i.test(label) &&
                           !/(?:rédiger|écrire|write|ajouter|add)/i.test(label);
                  });
                  if (!target) {
                    return {
                      clicked: false,
                      candidates: nodes
                        .map(node => `${node.getAttribute('aria-label') || ''} ${node.textContent || ''}`.trim())
                        .filter(label => /(?:avis|reviews?|étoile|stars?)/i.test(label))
                        .slice(0, 20)
                    };
                  }
                  target.scrollIntoView({ block: 'center' });
                  target.click();
                  return { clicked: true, candidates: [] };
                })()
                """;

            using var clickResponse = await SendDevToolsCommandAsync(
                webSocket,
                3,
                "Runtime.evaluate",
                new { expression = clickReviewsExpression, returnByValue = true },
                timeoutSource.Token);
            _logger.LogDebug(
                "Google Maps review panel interaction result: {Result}",
                clickResponse.RootElement.GetProperty("result").GetProperty("result").GetRawText());

            await Task.Delay(4_000, timeoutSource.Token);
            using var domResponse = await SendDevToolsCommandAsync(
                webSocket,
                4,
                "Runtime.evaluate",
                new { expression = "document.documentElement.outerHTML", returnByValue = true },
                timeoutSource.Token);

            var html = domResponse.RootElement
                .GetProperty("result")
                .GetProperty("result")
                .GetProperty("value")
                .GetString();

            TryKillProcess(process);
            _ = await outputDrainTask;
            _ = await errorDrainTask;
            return string.IsNullOrWhiteSpace(html) ? null : html;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or
                                      Win32Exception or WebSocketException or OperationCanceledException or
                                      JsonException or HttpRequestException)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            _logger.LogDebug(ex, "Google Maps review panel automation failed for {PlaceUrl}.", placeUrl);
            return null;
        }
        finally
        {
            if (process is not null)
            {
                TryKillProcess(process);
                process.Dispose();
            }

            TryDeleteDirectory(userDataDirectory);
        }
    }

    private static async Task<JsonDocument> SendDevToolsCommandAsync(
        ClientWebSocket webSocket,
        int id,
        string method,
        object parameters,
        CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
        await webSocket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);

        var buffer = new byte[64 * 1024];
        while (true)
        {
            using var message = new MemoryStream();
            WebSocketReceiveResult receiveResult;
            do
            {
                receiveResult = await webSocket.ReceiveAsync(buffer, cancellationToken);
                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    throw new WebSocketException("The Chrome DevTools connection closed unexpectedly.");
                }

                await message.WriteAsync(buffer.AsMemory(0, receiveResult.Count), cancellationToken);
            }
            while (!receiveResult.EndOfMessage);

            var response = JsonDocument.Parse(message.ToArray());
            if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id)
            {
                return response;
            }

            response.Dispose();
        }
    }

    private async Task<string?> DumpDomAsync(
        string browserExecutable,
        string url,
        int virtualTimeBudgetMs,
        CancellationToken cancellationToken)
    {
        var userDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "lead-radar-browser",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(userDataDirectory);

        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(BrowserExecutionTimeout);

            var startInfo = new ProcessStartInfo
            {
                FileName = browserExecutable,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            foreach (var argument in BuildBrowserArguments(userDataDirectory, virtualTimeBudgetMs, url))
            {
                startInfo.ArgumentList.Add(argument);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
            {
                throw new InvalidOperationException($"Unable to start browser process {browserExecutable}.");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var errorTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                throw new TimeoutException("The public Google Maps browser fallback took too long.");
            }

            var output = await outputTask;
            var error = await errorTask;

            if (string.IsNullOrWhiteSpace(output))
            {
                if (!string.IsNullOrWhiteSpace(error))
                {
                    _logger.LogDebug(
                        "Browser DOM dump returned no output for {Url}. Exit code: {ExitCode}. Error: {Error}",
                        url,
                        process.ExitCode,
                        TrimForLog(error));
                }

                return null;
            }

            if (process.ExitCode != 0 && !output.Contains("<html", StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogDebug(
                    "Browser DOM dump failed for {Url}. Exit code: {ExitCode}. Error: {Error}",
                    url,
                    process.ExitCode,
                    TrimForLog(error));
                return null;
            }

            return output;
        }
        finally
        {
            TryDeleteDirectory(userDataDirectory);
        }
    }

    private static IReadOnlyList<string> BuildBrowserArguments(
        string userDataDirectory,
        int virtualTimeBudgetMs,
        string url)
    {
        return
        [
            "--headless=new",
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--blink-settings=imagesEnabled=false",
            "--window-size=1440,1400",
            "--lang=en-US",
            "--no-first-run",
            "--no-default-browser-check",
            $"--user-data-dir={userDataDirectory}",
            $"--virtual-time-budget={virtualTimeBudgetMs}",
            "--dump-dom",
            url
        ];
    }

    private static void TryKillProcess(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string? ExtractPhoneNumber(string html)
    {
        var candidates = PhoneDataItemRegex.Matches(html)
            .Cast<Match>()
            .Select(match => match.Groups["value"].Value)
            .Concat(TelHrefRegex.Matches(html).Cast<Match>().Select(match => match.Groups["value"].Value))
            .Concat(LabeledPhoneRegex.Matches(WebUtility.HtmlDecode(html)).Cast<Match>().Select(match => match.Groups["value"].Value))
            .Concat(VisibleSearchCardPhoneRegex.Matches(html).Cast<Match>().Select(match => match.Groups["value"].Value))
            .Select(CleanPhoneCandidate)
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(static value => value!.StartsWith('+'))
            .ThenByDescending(static value => value!.Count(char.IsDigit))
            .ToList();

        return candidates.FirstOrDefault();
    }

    private static string? ExtractBestPlaceUrl(string html, string? businessName)
    {
        var normalizedBusinessName = NormalizeForComparison(businessName);
        var candidates = PlaceLinkRegex.Matches(html)
            .Cast<Match>()
            .Select(match => NormalizeGoogleMapsUrl(match.Groups["url"].Value))
            .Concat(EmbeddedPlaceUrlRegex.Matches(html)
                .Cast<Match>()
                .Select(match => NormalizeGoogleMapsUrl(match.Groups["url"].Value)))
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (candidates.Count == 0)
        {
            return null;
        }

        return candidates
            .OrderByDescending(url => ComputePlaceUrlScore(url!, normalizedBusinessName))
            .ThenBy(static url => url)
            .FirstOrDefault();
    }

    private static int ComputePlaceUrlScore(string url, string normalizedBusinessName)
    {
        var normalizedUrl = NormalizeForComparison(Uri.UnescapeDataString(url));
        var score = 0;

        if (!string.IsNullOrWhiteSpace(normalizedBusinessName) &&
            normalizedUrl.Contains(normalizedBusinessName, StringComparison.Ordinal))
        {
            score += 100;
        }

        foreach (var token in normalizedBusinessName.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length >= 3 && normalizedUrl.Contains(token, StringComparison.Ordinal))
            {
                score += 10;
            }
        }

        if (normalizedUrl.Contains("/maps/place/", StringComparison.Ordinal))
        {
            score += 5;
        }

        return score;
    }

    private static string? CleanPhoneCandidate(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        decoded = Uri.UnescapeDataString(decoded);
        var hasPlus = decoded.StartsWith("+", StringComparison.Ordinal);
        var digits = new string(decoded.Where(char.IsDigit).ToArray());
        if (digits.Length < 8 || digits.Length > 15)
        {
            return null;
        }

        return hasPlus ? $"+{digits}" : digits;
    }

    private static string? NormalizeGoogleMapsUrl(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Trim();
        if (string.IsNullOrWhiteSpace(decoded))
        {
            return null;
        }

        if (decoded.StartsWith("//", StringComparison.Ordinal))
        {
            decoded = $"https:{decoded}";
        }
        else if (decoded.StartsWith("/", StringComparison.Ordinal))
        {
            decoded = $"https://www.google.com{decoded}";
        }

        return Uri.TryCreate(decoded, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : null;
    }

    private static string NormalizeForComparison(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var decomposed = value.Trim().ToLowerInvariant().Normalize(NormalizationForm.FormD);
        var withoutDiacritics = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) != UnicodeCategory.NonSpacingMark)
            {
                withoutDiacritics.Append(character);
            }
        }

        return Regex.Replace(withoutDiacritics.ToString(), @"[^a-z0-9]+", " ")
            .Trim();
    }

    private static string? FindBrowserExecutable()
    {
        var environmentOverride = Environment.GetEnvironmentVariable("LEAD_RADAR_BROWSER_PATH");
        if (!string.IsNullOrWhiteSpace(environmentOverride) && File.Exists(environmentOverride))
        {
            return environmentOverride;
        }

        var candidates = new List<string>();

        if (OperatingSystem.IsWindows())
        {
            var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
            var programFilesX86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

            candidates.Add(Path.Combine(programFilesX86, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Microsoft", "Edge", "Application", "msedge.exe"));
            candidates.Add(Path.Combine(programFiles, "Google", "Chrome", "Application", "chrome.exe"));
            candidates.Add(Path.Combine(localAppData, "Google", "Chrome", "Application", "chrome.exe"));
        }
        else if (OperatingSystem.IsMacOS())
        {
            candidates.Add("/Applications/Microsoft Edge.app/Contents/MacOS/Microsoft Edge");
            candidates.Add("/Applications/Google Chrome.app/Contents/MacOS/Google Chrome");
        }
        else
        {
            candidates.Add("/usr/bin/microsoft-edge");
            candidates.Add("/usr/bin/microsoft-edge-stable");
            candidates.Add("/usr/bin/google-chrome");
            candidates.Add("/usr/bin/google-chrome-stable");
            candidates.Add("/usr/bin/chromium");
            candidates.Add("/usr/bin/chromium-browser");
        }

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? FirstNotEmpty(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string TrimForLog(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        return trimmed.Length <= 320 ? trimmed : $"{trimmed[..320]}...";
    }

    public sealed record PublicLeadEnrichment(
        string? PhoneNumber,
        string? GoogleMapsUri,
        double? Rating,
        int? ReviewCount,
        IReadOnlyList<PublicReview> ReviewHighlights,
        string? ReviewsUri);

    public sealed record PublicReview(
        string AuthorName,
        double? Rating,
        string? RelativePublishTimeDescription,
        string? Text,
        string? GoogleMapsUri);
}
