using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net;
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

    private readonly ILogger<GoogleMapsPublicLeadEnrichmentService> _logger;

    public GoogleMapsPublicLeadEnrichmentService(
        ILogger<GoogleMapsPublicLeadEnrichmentService> logger)
    {
        _logger = logger;
    }

    public async Task<PublicLeadEnrichment?> TryEnrichAsync(
        string? businessName,
        string? googleMapsUri,
        double? latitude,
        double? longitude,
        CancellationToken cancellationToken = default)
    {
        var browserExecutable = BrowserExecutable.Value;
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            return null;
        }

        var searchUrl = BuildSearchUrl(businessName, googleMapsUri, latitude, longitude);
        if (string.IsNullOrWhiteSpace(searchUrl))
        {
            return null;
        }

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

        var searchPhoneNumber = ExtractPhoneNumber(searchDom);
        var placeUrl = ExtractBestPlaceUrl(searchDom, businessName);
        var resolvedPlaceUrl = NormalizeGoogleMapsUrl(placeUrl);

        if (!string.IsNullOrWhiteSpace(searchPhoneNumber))
        {
            return new PublicLeadEnrichment(searchPhoneNumber, resolvedPlaceUrl);
        }

        if (string.IsNullOrWhiteSpace(placeUrl))
        {
            return null;
        }

        string? placeDom = null;
        try
        {
            placeDom = await DumpDomAsync(
                browserExecutable,
                placeUrl,
                PlaceVirtualTimeBudgetMs,
                cancellationToken);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
        {
            _logger.LogDebug(ex, "Public Google Maps place enrichment failed for {BusinessName}.", businessName);
        }

        var placePhoneNumber = string.IsNullOrWhiteSpace(placeDom)
            ? null
            : ExtractPhoneNumber(placeDom);

        var phoneNumber = FirstNotEmpty(placePhoneNumber, searchPhoneNumber);

        return phoneNumber is null && resolvedPlaceUrl is null
            ? null
            : new PublicLeadEnrichment(phoneNumber, resolvedPlaceUrl);
    }

    private static string? BuildSearchUrl(
        string? businessName,
        string? googleMapsUri,
        double? latitude,
        double? longitude)
    {
        if (!string.IsNullOrWhiteSpace(googleMapsUri))
        {
            return AppendLanguageHint(googleMapsUri.Trim());
        }

        var coordinates = latitude is not null && longitude is not null
            ? $"{latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}"
            : null;
        var query = !string.IsNullOrWhiteSpace(businessName) && !string.IsNullOrWhiteSpace(coordinates)
            ? $"{businessName.Trim()} {coordinates}"
            : !string.IsNullOrWhiteSpace(businessName)
                ? businessName.Trim()
                : coordinates;

        if (string.IsNullOrWhiteSpace(query))
        {
            return null;
        }

        return AppendLanguageHint(
            $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(query)}");
    }

    private static string AppendLanguageHint(string url)
    {
        if (url.Contains("hl=", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        return $"{url}{(url.Contains('?') ? '&' : '?')}hl=en";
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
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty).Trim();
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

        return Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", " ")
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
        string? GoogleMapsUri);
}
