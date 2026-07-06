using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Backend.Models;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class WebsiteEmailExtractionService
{
    private static readonly Regex EmailRegex = new(
        @"(?<![A-Z0-9._%+-])[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}(?![A-Z0-9._%+-])",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex PhoneRegex = new(
        @"(?<!\w)(?:\+?\d[\d\s()./-]{6,}\d)(?!\w)",
        RegexOptions.Compiled);

    private static readonly Regex UrlRegex = new(
        @"https?://[^\s""'<>]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex DateLikePhoneRegex = new(
        @"^\d{1,2}[/-]\d{1,2}[/-]\d{2,4}$",
        RegexOptions.Compiled);

    private static readonly Regex ScriptRegex = new(
        @"<(script|style)[^>]*>[\s\S]*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex HrefRegex = new(
        "href\\s*=\\s*[\"'](?<url>[^\"'#]+)[\"']",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly string[] ContactHints =
    [
        "contact",
        "about",
        "support",
        "legal",
        "mentions",
        "impressum"
    ];

    private static readonly string[] BlockedEmailKeywords =
    [
        "developer",
        "noreply",
        "no-reply",
        "donotreply",
        "webmaster",
        "wordpress"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly GeminiProxyService _geminiProxyService;
    private readonly GooglePlacesOptions _options;

    public WebsiteEmailExtractionService(
        HttpClient httpClient,
        GeminiProxyService geminiProxyService,
        IOptions<GooglePlacesOptions> options)
    {
        _httpClient = httpClient;
        _geminiProxyService = geminiProxyService;
        _options = options.Value;
        _httpClient.Timeout = TimeSpan.FromSeconds(Math.Max(5, _options.WebsiteRequestTimeoutSeconds));
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LeadRadarSaas/1.0");
    }

    public async Task<(IReadOnlyList<string> Emails, string Source)> ExtractPublicEmailsAsync(
        string websiteUri,
        bool useGemini,
        CancellationToken cancellationToken = default)
    {
        var result = await ExtractPublicContactDetailsAsync(websiteUri, useGemini, cancellationToken);
        return (result.Emails, result.Source);
    }

    public async Task<WebsiteContactExtractionResult> ExtractPublicContactDetailsAsync(
        string websiteUri,
        bool useGemini,
        CancellationToken cancellationToken = default)
    {
        if (!Uri.TryCreate(websiteUri, UriKind.Absolute, out var rootUri))
        {
            return EmptyResult();
        }

        var snapshot = await DownloadCandidatePagesAsync(rootUri, cancellationToken);
        if (snapshot.Pages.Count == 0)
        {
            return EmptyResult();
        }

        var visiblePages = snapshot.Pages
            .Select(page => new PageTextSnapshot(page.Uri.AbsoluteUri, ToVisibleText(page.Html)))
            .Where(page => !string.IsNullOrWhiteSpace(page.Text))
            .ToList();

        var regexEmails = visiblePages
            .SelectMany(page => EmailRegex.Matches(page.Text).Cast<Match>().Select(match => match.Value.Trim()))
            .Where(IsLikelyBusinessEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var regexPhones = visiblePages
            .SelectMany(page => ExtractPhoneNumbers(page.Text))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        var contactPages = snapshot.ContactPageUris
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        if (!useGemini)
        {
            return new WebsiteContactExtractionResult(
                Emails: regexEmails,
                PhoneNumbers: regexPhones,
                ContactPageUris: contactPages,
                Source: HasContactData(regexEmails, regexPhones, contactPages) ? "regex" : "none");
        }

        try
        {
            var safeExcerpt = BuildGeminiExcerpt(visiblePages);
            if (string.IsNullOrWhiteSpace(safeExcerpt))
            {
                return new WebsiteContactExtractionResult(
                    Emails: regexEmails,
                    PhoneNumbers: regexPhones,
                    ContactPageUris: contactPages,
                    Source: HasContactData(regexEmails, regexPhones, contactPages) ? "regex" : "none");
            }

            var prompt = $@"
Extract only the public business contact details explicitly present in the website content below.
Return strict JSON only with this shape:
{{
  ""emails"": [""...""],
  ""phones"": [""...""],
  ""contactPages"": [""...""]
}}

Rules:
- Do not invent anything.
- Include only public business contact details visible in the content.
- Keep only real contact emails and business phone numbers.
- Ignore developer, vendor, tracking, hosting or clearly third-party service emails.
- Keep only URLs that look like contact pages or support pages.
- If nothing is found for a field, return an empty array.

Website content:
{safeExcerpt}
";

            var geminiResponse = await _geminiProxyService.AskAsync(prompt, cancellationToken);
            var geminiContacts = ParseGeminiContacts(geminiResponse);

            var mergedEmails = regexEmails
                .Concat(geminiContacts.Emails)
                .Where(IsLikelyBusinessEmail)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            var mergedPhones = regexPhones
                .Concat(geminiContacts.PhoneNumbers)
                .Select(NormalizePhoneNumber)
                .Where(IsLikelyPhoneNumber)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            var mergedContactPages = contactPages
                .Concat(geminiContacts.ContactPageUris)
                .Select(NormalizeUrl)
                .Where(IsLikelyContactPage)
                .Select(url => url!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .ToList();

            return new WebsiteContactExtractionResult(
                Emails: mergedEmails,
                PhoneNumbers: mergedPhones,
                ContactPageUris: mergedContactPages,
                Source: HasContactData(mergedEmails, mergedPhones, mergedContactPages) ? "gemini" : "none");
        }
        catch
        {
            return new WebsiteContactExtractionResult(
                Emails: regexEmails,
                PhoneNumbers: regexPhones,
                ContactPageUris: contactPages,
                Source: HasContactData(regexEmails, regexPhones, contactPages) ? "regex" : "none");
        }
    }

    private async Task<WebsitePageDownloadSnapshot> DownloadCandidatePagesAsync(
        Uri rootUri,
        CancellationToken cancellationToken)
    {
        var pages = new List<PageSnapshot>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        await TryAddPageAsync(rootUri, pages, visited, cancellationToken);

        if (pages.Count == 0)
        {
            return new WebsitePageDownloadSnapshot([], []);
        }

        var discoveredLinks = ExtractContactLinks(pages[0].Html, rootUri)
            .Take(Math.Max(0, _options.MaxWebsitePagesToScan - 1))
            .ToList();

        foreach (var link in discoveredLinks)
        {
            await TryAddPageAsync(link, pages, visited, cancellationToken);
        }

        return new WebsitePageDownloadSnapshot(
            Pages: pages,
            ContactPageUris: discoveredLinks.Select(link => link.AbsoluteUri).ToList());
    }

    private async Task TryAddPageAsync(
        Uri uri,
        List<PageSnapshot> pages,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (!visited.Add(uri.AbsoluteUri))
        {
            return;
        }

        try
        {
            using var response = await _httpClient.GetAsync(uri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (!mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var html = await response.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(html))
            {
                pages.Add(new PageSnapshot(uri, html));
            }
        }
        catch
        {
        }
    }

    private static IEnumerable<Uri> ExtractContactLinks(string html, Uri rootUri)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match match in HrefRegex.Matches(html))
        {
            var rawUrl = match.Groups["url"].Value.Trim();
            if (rawUrl.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ContactHints.Any(hint => rawUrl.Contains(hint, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (!Uri.TryCreate(rootUri, rawUrl, out var resolved))
            {
                continue;
            }

            if (!string.Equals(resolved.Host, rootUri.Host, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(resolved.AbsoluteUri))
            {
                continue;
            }

            yield return resolved;
        }
    }

    private static IEnumerable<string> ExtractPhoneNumbers(string text)
    {
        foreach (Match match in PhoneRegex.Matches(text))
        {
            var phone = NormalizePhoneNumber(match.Value);
            if (!IsLikelyPhoneNumber(phone))
            {
                continue;
            }

            yield return phone;
        }
    }

    private static GeminiContactResult ParseGeminiContacts(string responseText)
    {
        var jsonPayload = ExtractJsonObject(responseText);
        if (!string.IsNullOrWhiteSpace(jsonPayload))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<GeminiContactDto>(jsonPayload, JsonOptions);
                if (parsed is not null)
                {
                    return new GeminiContactResult(
                        Emails: NormalizeEmails(parsed.Emails),
                        PhoneNumbers: NormalizePhoneNumbers(parsed.Phones),
                        ContactPageUris: NormalizeUrls(parsed.ContactPages));
                }
            }
            catch
            {
            }
        }

        return new GeminiContactResult(
            Emails: NormalizeEmails(EmailRegex.Matches(responseText).Cast<Match>().Select(match => match.Value)),
            PhoneNumbers: NormalizePhoneNumbers(ExtractPhoneNumbers(responseText)),
            ContactPageUris: NormalizeUrls(
                UrlRegex.Matches(responseText)
                    .Cast<Match>()
                    .Select(match => match.Value)
                    .Where(IsLikelyContactPage)));
    }

    private static string ExtractJsonObject(string responseText)
    {
        if (string.IsNullOrWhiteSpace(responseText))
        {
            return string.Empty;
        }

        var fencedMatch = Regex.Match(
            responseText,
            "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```",
            RegexOptions.IgnoreCase);

        if (fencedMatch.Success)
        {
            return fencedMatch.Groups[1].Value.Trim();
        }

        var firstBrace = responseText.IndexOf('{');
        var lastBrace = responseText.LastIndexOf('}');

        return firstBrace >= 0 && lastBrace > firstBrace
            ? responseText[firstBrace..(lastBrace + 1)].Trim()
            : string.Empty;
    }

    private static string BuildGeminiExcerpt(IReadOnlyList<PageTextSnapshot> pages)
    {
        var parts = pages
            .Select(page => $"URL: {page.Url}\nTEXT: {page.Text}")
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .ToList();

        var combined = string.Join("\n\n---\n\n", parts);
        if (string.IsNullOrWhiteSpace(combined))
        {
            return string.Empty;
        }

        return combined.Length > 16000
            ? combined[..16000]
            : combined;
    }

    private static IReadOnlyList<string> NormalizeEmails(IEnumerable<string>? emails)
        => (emails ?? [])
            .Select(email => email.Trim().Trim('.', ',', ';', ':'))
            .Where(IsLikelyBusinessEmail)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string> NormalizePhoneNumbers(IEnumerable<string>? phones)
        => (phones ?? [])
            .Select(NormalizePhoneNumber)
            .Where(IsLikelyPhoneNumber)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static IReadOnlyList<string> NormalizeUrls(IEnumerable<string>? urls)
        => (urls ?? [])
            .Select(NormalizeUrl)
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => url!)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

    private static string? NormalizeUrl(string? url)
    {
        var trimmed = url?.Trim();
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return null;
        }

        if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Uri.TryCreate(trimmed, UriKind.Absolute, out var uri)
            ? uri.AbsoluteUri
            : null;
    }

    private static string NormalizePhoneNumber(string phone)
    {
        var trimmed = phone.Trim().Trim('.', ',', ';', ':');
        var hasPlusPrefix = trimmed.StartsWith("+", StringComparison.Ordinal);
        var digits = new string(trimmed.Where(char.IsDigit).ToArray());

        if (string.IsNullOrWhiteSpace(digits))
        {
            return string.Empty;
        }

        return hasPlusPrefix ? $"+{digits}" : digits;
    }

    private static string ToVisibleText(string html)
    {
        var withoutScripts = ScriptRegex.Replace(html, " ");
        var withoutTags = TagRegex.Replace(withoutScripts, " ");
        return Regex.Replace(withoutTags, "\\s+", " ").Trim();
    }

    private static bool HasContactData(
        IReadOnlyCollection<string> emails,
        IReadOnlyCollection<string> phones,
        IReadOnlyCollection<string> contactPages)
        => emails.Count > 0 || phones.Count > 0 || contactPages.Count > 0;

    private static bool IsLikelyBusinessEmail(string email)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return false;
        }

        var localPart = email.Split('@')[0];

        return !email.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            && !email.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            && !email.Contains("example.com", StringComparison.OrdinalIgnoreCase)
            && !email.Contains("domain.com", StringComparison.OrdinalIgnoreCase)
            && !BlockedEmailKeywords.Any(keyword => localPart.Contains(keyword, StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsLikelyPhoneNumber(string phone)
    {
        if (string.IsNullOrWhiteSpace(phone))
        {
            return false;
        }

        if (DateLikePhoneRegex.IsMatch(phone))
        {
            return false;
        }

        var digitCount = phone.Count(char.IsDigit);
        if (digitCount < 8 || digitCount > 15)
        {
            return false;
        }

        var digitsOnly = phone.All(char.IsDigit);
        if (digitsOnly && digitCount >= 13)
        {
            return false;
        }

        return true;
    }

    private static bool IsLikelyContactPage(string? url)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        return ContactHints.Any(hint => url.Contains(hint, StringComparison.OrdinalIgnoreCase));
    }

    private static WebsiteContactExtractionResult EmptyResult()
        => new([], [], [], "none");

    private sealed record PageSnapshot(Uri Uri, string Html);

    private sealed record PageTextSnapshot(string Url, string Text);

    private sealed record WebsitePageDownloadSnapshot(
        IReadOnlyList<PageSnapshot> Pages,
        IReadOnlyList<string> ContactPageUris);

    private sealed record GeminiContactResult(
        IReadOnlyList<string> Emails,
        IReadOnlyList<string> PhoneNumbers,
        IReadOnlyList<string> ContactPageUris);

    private sealed class GeminiContactDto
    {
        [JsonPropertyName("emails")]
        public List<string>? Emails { get; init; }

        [JsonPropertyName("phones")]
        public List<string>? Phones { get; init; }

        [JsonPropertyName("contactPages")]
        public List<string>? ContactPages { get; init; }
    }
}
