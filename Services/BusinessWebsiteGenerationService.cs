using System.Globalization;
using System.IO.Compression;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using Backend.Generation;
using Backend.Models;

namespace Backend.Services;

public sealed class BusinessWebsiteGenerationService
{
    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/138.0.0.0 Safari/537.36";

    private const string BrowserAcceptLanguage = "fr-FR,fr;q=0.9,en-US;q=0.8,en;q=0.7";
    private const int RenderedDomVirtualTimeBudgetMs = 12_000;
    private static readonly TimeSpan RenderedDomTimeout = TimeSpan.FromSeconds(24);
    private static readonly Lazy<string?> BrowserExecutable = new(FindBrowserExecutable);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly Regex JsonFenceRegex = new(
        "```(?:json)?\\s*(\\{[\\s\\S]*\\})\\s*```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CollapseWhitespaceRegex = new(
        "\\s+",
        RegexOptions.Compiled);

    private static readonly Regex MetaImageRegex = new(
        "<meta[^>]+(?:property|name)\\s*=\\s*[\"'](?:og:image|og:image:url|twitter:image|twitter:image:src)[\"'][^>]+content\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>|<meta[^>]+content\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]+(?:property|name)\\s*=\\s*[\"'](?:og:image|og:image:url|twitter:image|twitter:image:src)[\"'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex LinkIconRegex = new(
        "<link[^>]+rel\\s*=\\s*[\"'][^\"']*(?:icon|apple-touch-icon)[^\"']*[\"'][^>]+href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>|<link[^>]+href\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]+rel\\s*=\\s*[\"'][^\"']*(?:icon|apple-touch-icon)[^\"']*[\"'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex ImageSourceRegex = new(
        "<(?:img|source|image)[^>]+(?:src|data-src|srcset)\\s*=\\s*[\"'](?<url>[^\"']+)[\"'][^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex BackgroundImageUrlRegex = new(
        "background-image\\s*:\\s*url\\((?<url>[^)]+)\\)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex EmbeddedRemoteImageUrlRegex = new(
        @"https?:\/\/(?:lh\d+\.googleusercontent\.com|[^""'\s<>]+)\S*?(?:\.avif|\.gif|\.jpe?g|\.png|\.webp|=w\d+-h\d+[^""'\s<>]*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DimensionHintRegex = new(
        @"(?:=|/)(?:w|s)(?<width>\d+)-h(?<height>\d+)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.Compiled);

    private static readonly Regex DuckDuckGoResultTitleRegex = new(
        "<a[^>]+class\\s*=\\s*[\"'][^\"']*result__a[^\"']*[\"'][^>]+href\\s*=\\s*[\"'](?<href>[^\"']+)[\"'][^>]*>(?<title>[\\s\\S]*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex DuckDuckGoResultSnippetRegex = new(
        "<a[^>]+class\\s*=\\s*[\"'][^\"']*result__snippet[^\"']*[\"'][^>]*>(?<snippet>[\\s\\S]*?)</a>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Base64LikePayloadRegex = new(
        "^[A-Za-z0-9+/=_-]{64,}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly IReadOnlyList<TemplateDefinition> TemplateDefinitions =
    [
        new TemplateDefinition("restaurant-signature", "Restaurant Signature", false),
        new TemplateDefinition("coffee-shop-signature", "Coffee Shop Signature", false),
        new TemplateDefinition("luxury", "Elegant Luxury", true),
        new TemplateDefinition("minimal", "Minimal Modern", false),
        new TemplateDefinition("creative", "Creative Bold", true),
        new TemplateDefinition("corporate", "Corporate Professional", false),
        new TemplateDefinition("premium", "Premium Landing", true)
    ];

    private static readonly IReadOnlyList<FontPair> FontPairs =
    [
        new FontPair(
            "Playfair Display",
            "'Playfair Display', 'Segoe UI', sans-serif",
            "Plus Jakarta Sans",
            "'Plus Jakarta Sans', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Playfair+Display:wght@700;800&family=Plus+Jakarta+Sans:wght@400;500;600;700;800&display=swap"),
        new FontPair(
            "Playfair Display",
            "'Playfair Display', 'Segoe UI', sans-serif",
            "Poppins",
            "'Poppins', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Playfair+Display:wght@600;700;800&family=Poppins:wght@300;400;500;600;700&display=swap"),
        new FontPair(
            "Space Grotesk",
            "'Space Grotesk', 'Segoe UI', sans-serif",
            "Manrope",
            "'Manrope', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Space+Grotesk:wght@500;700&family=Manrope:wght@400;500;600;700;800&display=swap"),
        new FontPair(
            "Fraunces",
            "'Fraunces', 'Georgia', serif",
            "DM Sans",
            "'DM Sans', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Fraunces:opsz,wght@9..144,600;9..144,700&family=DM+Sans:wght@400;500;700;800&display=swap"),
        new FontPair(
            "Sora",
            "'Sora', 'Segoe UI', sans-serif",
            "Public Sans",
            "'Public Sans', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Sora:wght@500;600;700;800&family=Public+Sans:wght@400;500;600;700;800&display=swap"),
        new FontPair(
            "Syne",
            "'Syne', 'Segoe UI', sans-serif",
            "Manrope",
            "'Manrope', 'Segoe UI', sans-serif",
            "https://fonts.googleapis.com/css2?family=Syne:wght@600;700;800&family=Manrope:wght@400;500;600;700;800&display=swap")
    ];

    private static readonly IReadOnlyList<string> AllowedSectionIds =
    [
        "about",
        "highlights",
        "services",
        "gallery",
        "reviews",
        "contact",
        "faq"
    ];

    private static readonly IReadOnlyList<PaletteDefinition> PaletteDefinitions =
    [
        new PaletteDefinition(
            "midnight-gold",
            true,
            "#c79a49",
            "#8c6a2d",
            "#f2d091",
            "#09070b",
            "#151016",
            "#201922",
            "#f8f3ea",
            "rgba(235, 223, 203, 0.76)",
            "rgba(206, 175, 120, 0.18)",
            "#1a1306",
            ["luxury-gold", "restaurant", "bar", "premium", "luxury"]),
        new PaletteDefinition(
            "emerald-champagne",
            true,
            "#51a88c",
            "#2d6d58",
            "#ead8a4",
            "#071311",
            "#10201c",
            "#17312b",
            "#f4fbf8",
            "rgba(214, 233, 226, 0.76)",
            "rgba(122, 177, 157, 0.2)",
            "#0b1612",
            ["botanical-green", "beauty", "premium", "grocery-store", "organic"]),
        new PaletteDefinition(
            "cobalt-cinema",
            true,
            "#4b74ff",
            "#1e3e96",
            "#86d8ff",
            "#08111f",
            "#101b2d",
            "#17263b",
            "#f7fbff",
            "rgba(219, 231, 255, 0.78)",
            "rgba(120, 150, 214, 0.18)",
            "#07111f",
            ["premium-cobalt", "corporate", "hotel", "cafe", "coastal-blue"]),
        new PaletteDefinition(
            "neon-plum",
            true,
            "#d560ff",
            "#7a2ca8",
            "#53f0ff",
            "#0d0717",
            "#171022",
            "#241737",
            "#fbf7ff",
            "rgba(230, 219, 249, 0.78)",
            "rgba(183, 112, 225, 0.18)",
            "#13081d",
            ["nightlife-neon", "creative", "bar", "bold"]),
        new PaletteDefinition(
            "ink-crimson",
            true,
            "#d86e5b",
            "#7f2d28",
            "#f4c89e",
            "#120909",
            "#1c1010",
            "#2b1716",
            "#fff8f5",
            "rgba(241, 225, 219, 0.76)",
            "rgba(215, 123, 108, 0.18)",
            "#180907",
            ["warm-terra", "restaurant", "bakery", "editorial"]),
        new PaletteDefinition(
            "sand-terracotta",
            false,
            "#8f3f36",
            "#5f302b",
            "#c89b5b",
            "#f8f4ed",
            "#fffefb",
            "#eee5da",
            "#241a17",
            "rgba(66, 48, 42, 0.74)",
            "rgba(143, 63, 54, 0.16)",
            "#fffaf5",
            ["warm-terra", "restaurant", "bakery", "cafe"]),
        new PaletteDefinition(
            "coastal-indigo",
            false,
            "#3455c5",
            "#24418f",
            "#63b1d9",
            "#f1f6fb",
            "#ffffff",
            "#e6edf8",
            "#10213f",
            "rgba(39, 59, 99, 0.75)",
            "rgba(111, 145, 207, 0.18)",
            "#f7fbff",
            ["coastal-blue", "corporate", "cafe", "premium"]),
        new PaletteDefinition(
            "olive-cream",
            false,
            "#5f7d37",
            "#3f5f21",
            "#d9b86d",
            "#f5f4ea",
            "#fffef9",
            "#ebeadb",
            "#1f2b16",
            "rgba(61, 74, 45, 0.76)",
            "rgba(125, 148, 102, 0.18)",
            "#fffef8",
            ["botanical-green", "grocery-store", "organic", "beauty"]),
        new PaletteDefinition(
            "rose-stone",
            false,
            "#bc6b7a",
            "#8a4956",
            "#f0c7b8",
            "#faf4f3",
            "#ffffff",
            "#f2e4e2",
            "#351b24",
            "rgba(90, 52, 62, 0.74)",
            "rgba(197, 141, 152, 0.18)",
            "#fff9fb",
            ["rose-boutique", "beauty", "clothing-store", "luxury"]),
        new PaletteDefinition(
            "ink-porcelain",
            false,
            "#1f2c44",
            "#33435f",
            "#c8a86a",
            "#f3f5f8",
            "#ffffff",
            "#e9edf3",
            "#111827",
            "rgba(55, 65, 81, 0.76)",
            "rgba(120, 138, 166, 0.18)",
            "#f8fbff",
            ["monochrome-ink", "corporate", "professional", "premium"])
    ];

    private readonly HttpClient _httpClient;
    private readonly GeminiProxyService _geminiProxyService;
    private readonly GooglePlaceWebsiteEnrichmentService _googlePlaceWebsiteEnrichmentService;
    private readonly GoogleMapsPublicLeadEnrichmentService _googleMapsPublicLeadEnrichmentService;
    private readonly IHostEnvironment _environment;

    public BusinessWebsiteGenerationService(
        HttpClient httpClient,
        GeminiProxyService geminiProxyService,
        GooglePlaceWebsiteEnrichmentService googlePlaceWebsiteEnrichmentService,
        GoogleMapsPublicLeadEnrichmentService googleMapsPublicLeadEnrichmentService,
        IHostEnvironment environment)
    {
        _httpClient = httpClient;
        _geminiProxyService = geminiProxyService;
        _googlePlaceWebsiteEnrichmentService = googlePlaceWebsiteEnrichmentService;
        _googleMapsPublicLeadEnrichmentService = googleMapsPublicLeadEnrichmentService;
        _environment = environment;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
    }

    public async Task<GeneratedWebsiteProjectBuild> GenerateProjectAsync(
        WebsiteGenerationRequest request,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo,
        IReadOnlyList<WebsiteGenerationHistoryEntry> history,
        string siteUrl,
        CancellationToken cancellationToken = default)
    {
        var enrichment = await _googlePlaceWebsiteEnrichmentService.TryEnrichAsync(request, cancellationToken);
        GoogleMapsPublicLeadEnrichmentService.PublicLeadEnrichment? publicGoogleEnrichment = null;
        if (enrichment?.Rating is null || enrichment.ReviewHighlights.Count == 0)
        {
            publicGoogleEnrichment = await _googleMapsPublicLeadEnrichmentService.TryEnrichAsync(
                request.BusinessName,
                enrichment?.GoogleMapsUri ?? request.GoogleMapsUri,
                enrichment?.Latitude ?? request.Latitude,
                enrichment?.Longitude ?? request.Longitude,
                request.Address,
                cancellationToken,
                includeReviews: true);
        }

        var business = await EnrichBusinessVisualsAsync(
            NormalizeRequest(request, enrichment, publicGoogleEnrichment),
            cancellationToken);
        var template = SelectTemplateDefinition(history, business, preferredTemplateId: null);
        var designConcept = BuildDesignConcept(template, business);
        var theme = BuildTheme(template, business, designConcept.ColorMood, designConcept.FontDirection);
        var contentBundle = await BuildContentBundleAsync(
            business,
            template,
            theme,
            designConcept.DefaultSectionOrder,
            designConcept.MotionStyle,
            cancellationToken);
        var mediaAssets = await PrepareImageAssetsAsync(
            business,
            contentBundle.FallbackFrench.GalleryCaptions,
            theme,
            uploadedImages,
            cancellationToken);
        var logoAsset = await PrepareLogoAssetAsync(
            business,
            theme,
            uploadedLogo,
            cancellationToken);

        var state = new WebsiteProjectState(
            StateVersion: "1",
            Business: business,
            TemplateId: template.Id,
            TemplateName: template.DisplayName,
            DesignConcept: designConcept.Id,
            ColorMood: designConcept.ColorMood,
            FontDirection: designConcept.FontDirection,
            MotionStyle: designConcept.MotionStyle,
            SiteUrl: siteUrl,
            ModelUsed: contentBundle.ModelUsed,
            Theme: theme,
            Translations: contentBundle.Translations,
            Seo: contentBundle.Seo,
            SectionOrder: designConcept.DefaultSectionOrder.ToList(),
            HiddenSections: [],
            MediaAssets: mediaAssets
                .Select(static asset => new StoredMediaAsset(
                    asset.ArchivePath,
                    asset.WebPath,
                    asset.Caption,
                    asset.CssClass,
                    asset.Width,
                    asset.Height))
                .ToList(),
            LogoAsset: new StoredLogoAsset(
                logoAsset.WebPath,
                logoAsset.SvgMarkup,
                logoAsset.ArchivePath,
                logoAsset.IsUploaded),
            PrioritizedAssets: BuildPrioritizedAssetLabels(uploadedImages, mediaAssets),
            UploadedImageFileNames: uploadedImages
                .Where(static asset => !string.IsNullOrWhiteSpace(asset.FileName))
                .Select(static asset => asset.FileName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList(),
            UploadedLogoFileName: string.IsNullOrWhiteSpace(uploadedLogo?.FileName)
                ? null
                : uploadedLogo.FileName.Trim());

        return BuildProjectBuild(state, mediaAssets, logoAsset, changeSummary: null);
    }

    public async Task<GeneratedWebsiteProjectBuild> EditProjectAsync(
        string stateJson,
        string currentSiteDirectory,
        string prompt,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo,
        CancellationToken cancellationToken = default)
    {
        var normalizedPrompt = CleanText(prompt);
        if (string.IsNullOrWhiteSpace(normalizedPrompt))
        {
            throw new ArgumentException("Prompt is required.", nameof(prompt));
        }

        var state = JsonSerializer.Deserialize<WebsiteProjectState>(stateJson, JsonOptions)
            ?? throw new InvalidOperationException("Stored website project state is invalid.");

        var business = await EnrichBusinessVisualsAsync(state.Business, cancellationToken);
        state = state with { Business = business };

        var aiPayload = await TryGenerateAiEditPayloadAsync(
                state,
                normalizedPrompt,
                uploadedImages,
                uploadedLogo,
                cancellationToken)
            ?? BuildFallbackEditPayload(normalizedPrompt);

        var template = SelectTemplateDefinition(
            [],
            state.Business,
            preferredTemplateId: aiPayload.Payload.Design?.TemplateId ?? state.TemplateId);
        var colorMood = PreferNullable(aiPayload.Payload.Design?.ColorMood, state.ColorMood) ?? state.ColorMood;
        var fontDirection = PreferNullable(aiPayload.Payload.Design?.FontDirection, state.FontDirection) ?? state.FontDirection;
        var motionStyle = PreferNullable(aiPayload.Payload.Design?.MotionStyle, state.MotionStyle) ?? state.MotionStyle;
        var theme = BuildTheme(template, state.Business, colorMood, fontDirection);
        var translations = PolishTranslationsForPresentation(MergeTranslations(
            state.Translations.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            new AiPayloadResult(aiPayload.ModelName, new AiWebsitePayload
            {
                Translations = aiPayload.Payload.Translations,
                Seo = aiPayload.Payload.Seo
            })),
            state.Business);
        var seo = MergeSeo(state.Seo, aiPayload.Payload.Seo, translations["fr"]);
        var sectionOrder = NormalizeSectionOrder(aiPayload.Payload.Design?.SectionOrder, state.SectionOrder, template.Id);
        var hiddenSections = NormalizeHiddenSections(aiPayload.Payload.Design?.HiddenSections, state.HiddenSections);
        var mediaAssets = await LoadGeneratedMediaAssetsAsync(state, currentSiteDirectory, cancellationToken);

        if (uploadedImages.Count > 0)
        {
            var uploadedMediaAssets = CreateMediaAssetsFromUploads(uploadedImages, translations["fr"].GalleryCaptions);
            mediaAssets = MergeEditedMediaAssets(mediaAssets, uploadedMediaAssets, normalizedPrompt);
        }
        else if (ShouldRefreshMediaLibrary(normalizedPrompt))
        {
            var refreshedAssets = await PrepareImageAssetsAsync(
                state.Business,
                translations["fr"].GalleryCaptions,
                theme,
                [],
                cancellationToken);

            if (HasMeaningfulRealImages(refreshedAssets))
            {
                mediaAssets = MergeEditedMediaAssets(mediaAssets, refreshedAssets, normalizedPrompt);
            }
        }

        if (ShouldRotateMedia(normalizedPrompt) && mediaAssets.Count > 1)
        {
            mediaAssets = [.. mediaAssets.Skip(1), mediaAssets[0]];
        }

        mediaAssets = SyncMediaCaptions(mediaAssets, translations["fr"].GalleryCaptions);
        var logoAsset = uploadedLogo is null
            ? await LoadGeneratedLogoAssetAsync(state, currentSiteDirectory, theme, cancellationToken)
            : await PrepareLogoAssetAsync(state.Business, theme, uploadedLogo, cancellationToken);

        var refreshedPrioritizedAssets = uploadedImages.Count > 0
            ? BuildPrioritizedAssetLabels(uploadedImages, mediaAssets)
            : state.PrioritizedAssets;
        var refreshedUploadedImageFileNames = uploadedImages.Count > 0
            ? uploadedImages
                .Where(static asset => !string.IsNullOrWhiteSpace(asset.FileName))
                .Select(static asset => asset.FileName.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(6)
                .ToList()
            : state.UploadedImageFileNames;
        var refreshedUploadedLogoFileName = string.IsNullOrWhiteSpace(uploadedLogo?.FileName)
            ? state.UploadedLogoFileName
            : uploadedLogo.FileName.Trim();

        var nextState = state with
        {
            Business = state.Business,
            TemplateId = template.Id,
            TemplateName = template.DisplayName,
            DesignConcept = BuildDesignConceptId(template.Id, colorMood, motionStyle),
            ColorMood = colorMood,
            FontDirection = fontDirection,
            MotionStyle = motionStyle,
            ModelUsed = aiPayload.ModelName,
            Theme = theme,
            Translations = translations,
            Seo = seo,
            SectionOrder = sectionOrder,
            HiddenSections = hiddenSections,
            MediaAssets = mediaAssets
                .Select(static asset => new StoredMediaAsset(
                    asset.ArchivePath,
                    asset.WebPath,
                    asset.Caption,
                    asset.CssClass,
                    asset.Width,
                    asset.Height))
                .ToList(),
            LogoAsset = new StoredLogoAsset(
                logoAsset.WebPath,
                logoAsset.SvgMarkup,
                logoAsset.ArchivePath,
                logoAsset.IsUploaded),
            PrioritizedAssets = refreshedPrioritizedAssets,
            UploadedImageFileNames = refreshedUploadedImageFileNames,
            UploadedLogoFileName = refreshedUploadedLogoFileName
        };

        return BuildProjectBuild(nextState, mediaAssets, logoAsset, aiPayload.Payload.ChangeSummary);
    }

    public async Task<GeneratedWebsiteArchive> GenerateAsync(
        WebsiteGenerationRequest request,
        CancellationToken cancellationToken = default)
    {
        var build = await GenerateProjectAsync(
            request,
            uploadedImages: [],
            uploadedLogo: null,
            history: [],
            siteUrl: $"https://replace-with-your-domain.example/{Slugify(request.BusinessName)}/",
            cancellationToken);

        return new GeneratedWebsiteArchive(
            build.FileName,
            "application/zip",
            build.ArchiveContent,
            build.TemplateName,
            build.ModelUsed);
    }

    private GeneratedWebsiteProjectBuild BuildProjectBuild(
        WebsiteProjectState state,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        GeneratedLogoAsset logoAsset,
        string? changeSummary)
    {
        var template = ResolveTemplateDefinition(state.TemplateId);
        var contentBundle = new LocalizedContentBundle(
            state.Translations.ToDictionary(static entry => entry.Key, static entry => entry.Value, StringComparer.OrdinalIgnoreCase),
            state.Seo,
            state.ModelUsed,
            state.Translations["fr"]);
        var siteConfig = BuildClientConfig(state.Business, state.SiteUrl, logoAsset, mediaAssets);
        var defaultContent = contentBundle.Translations["fr"];
        var files = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);

        AddTextFile(files, "index.html", BuildIndexHtml(
            state.Business,
            template,
            state.Theme,
            state.MotionStyle,
            state.SectionOrder,
            state.HiddenSections,
            siteConfig,
            defaultContent,
            state.Seo,
            mediaAssets,
            logoAsset,
            state.SiteUrl,
            BuildStructuredDataJson(state.Business, defaultContent, mediaAssets, state.SiteUrl)));
        AddTextFile(files, "assets/css/styles.css", BuildStylesheet(template, state.Theme));
        AddTextFile(files, "assets/js/app.js", BuildClientScript());
        AddTextFile(files, "assets/translations/i18n.json", BuildTranslationsJson(contentBundle));
        AddTextFile(files, "manifest.json", BuildManifestJson(state.Business, state.Theme, logoAsset));
        AddTextFile(files, "robots.txt", BuildRobotsTxt(state.SiteUrl));
        AddTextFile(files, "sitemap.xml", BuildSitemapXml(state.SiteUrl));
        AddTextFile(files, "README.md", BuildGeneratedReadme(state.Business, template, state.ModelUsed, state.SiteUrl));
        AddTextFile(files, ".nojekyll", string.Empty);

        if (logoAsset.IsUploaded && !string.IsNullOrWhiteSpace(logoAsset.ArchivePath) && logoAsset.Content is not null)
        {
            files[logoAsset.ArchivePath] = logoAsset.Content;
        }
        else
        {
            AddTextFile(files, "assets/icons/logo-mark.svg", logoAsset.SvgMarkup);
        }

        AddTextFile(files, "assets/icons/favicon.svg", logoAsset.SvgMarkup);

        foreach (var mediaAsset in mediaAssets)
        {
            files[mediaAsset.ArchivePath] = mediaAsset.Content;
        }

        var archiveBytes = BuildZip(files);
        var fileName = $"ai-website-{state.Business.Slug}-{DateTime.UtcNow:yyyyMMdd-HHmm}.zip";
        var stateJson = JsonSerializer.Serialize(state, JsonOptions);

        return new GeneratedWebsiteProjectBuild(
            StateJson: stateJson,
            FileName: fileName,
            ArchiveContent: archiveBytes,
            Files: files,
            BusinessName: state.Business.Name,
            BusinessSlug: state.Business.Slug,
            TemplateId: state.TemplateId,
            TemplateName: state.TemplateName,
            DesignConcept: state.DesignConcept,
            ModelUsed: state.ModelUsed,
            ChangeSummary: changeSummary,
            PrioritizedAssets: state.PrioritizedAssets,
            UploadedImageFileNames: state.UploadedImageFileNames,
            UploadedLogoFileName: state.UploadedLogoFileName);
    }

    private TemplateDefinition ResolveTemplateDefinition(string templateId)
    {
        return TemplateDefinitions.FirstOrDefault(
                   template => string.Equals(template.Id, templateId, StringComparison.OrdinalIgnoreCase))
               ?? TemplateDefinitions[0];
    }

    private TemplateDefinition SelectTemplateDefinition(
        IReadOnlyList<WebsiteGenerationHistoryEntry> history,
        NormalizedBusiness business,
        string? preferredTemplateId)
    {
        if (!string.IsNullOrWhiteSpace(preferredTemplateId))
        {
            return ResolveTemplateDefinition(preferredTemplateId);
        }

        var recommendedTemplateIds = GetRecommendedTemplateIds(business);
        var recentlyUsedTemplates = history
            .Select(static entry => entry.TemplateId)
            .Where(static templateId => !string.IsNullOrWhiteSpace(templateId))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var availableTemplates = TemplateDefinitions
            .Where(template => recommendedTemplateIds.Contains(template.Id))
            .Where(template => !recentlyUsedTemplates.Contains(template.Id))
            .ToList();

        if (availableTemplates.Count == 0 && history.Count > 0)
        {
            var lastTemplateId = history[0].TemplateId;
            availableTemplates = TemplateDefinitions
                .Where(template => recommendedTemplateIds.Contains(template.Id))
                .Where(template => !string.Equals(template.Id, lastTemplateId, StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        if (availableTemplates.Count == 0)
        {
            availableTemplates = TemplateDefinitions
                .Where(template => recommendedTemplateIds.Contains(template.Id))
                .ToList();
        }

        if (availableTemplates.Count == 0)
        {
            availableTemplates = TemplateDefinitions.ToList();
        }

        return availableTemplates[Random.Shared.Next(availableTemplates.Count)];
    }

    private static string BuildBusinessCategoryFingerprint(NormalizedBusiness business)
        => NormalizeSearchFingerprint($"{business.Category} {business.PrimaryType}");

    private static bool IsCoffeeShopBusiness(NormalizedBusiness business)
    {
        var categoryFingerprint = BuildBusinessCategoryFingerprint(business);
        return categoryFingerprint.Contains("cafe", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("café", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("coffee", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("espresso", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("tea house", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("salon de the", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("salon de thé", StringComparison.Ordinal);
    }

    private static bool IsHospitalityBusiness(NormalizedBusiness business)
    {
        var categoryFingerprint = BuildBusinessCategoryFingerprint(business);
        return categoryFingerprint.Contains("restaurant", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("restauration", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("cafe", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("café", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("coffee", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("bar", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("hotel", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("bakery", StringComparison.Ordinal) ||
               categoryFingerprint.Contains("boulanger", StringComparison.Ordinal);
    }

    private static HashSet<string> GetRecommendedTemplateIds(NormalizedBusiness business)
    {
        var categoryFingerprint = BuildBusinessCategoryFingerprint(business);

        if (IsCoffeeShopBusiness(business))
        {
            return ["coffee-shop-signature"];
        }

        if (IsHospitalityBusiness(business))
        {
            return ["restaurant-signature"];
        }

        if (categoryFingerprint.Contains("beauty", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("salon", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("spa", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("fashion", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("boutique", StringComparison.Ordinal))
        {
            return ["luxury", "creative", "premium"];
        }

        if (categoryFingerprint.Contains("consult", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("law", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("agency", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("clinic", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("medical", StringComparison.Ordinal) ||
            categoryFingerprint.Contains("office", StringComparison.Ordinal))
        {
            return ["corporate", "premium", "minimal"];
        }

        return ["premium", "luxury", "creative", "corporate", "minimal"];
    }

    private DesignConceptChoice BuildDesignConcept(
        TemplateDefinition template,
        NormalizedBusiness business)
    {
        var colorMood = ResolveDefaultColorMood(template.Id, business);
        var fontDirection = ResolveDefaultFontDirection(template.Id, business);
        var motionStyle = ResolveDefaultMotionStyle(template.Id);
        return new DesignConceptChoice(
            Id: BuildDesignConceptId(template.Id, colorMood, motionStyle),
            TemplateId: template.Id,
            ColorMood: colorMood,
            FontDirection: fontDirection,
            MotionStyle: motionStyle,
            DefaultSectionOrder: GetDefaultSectionOrder(template.Id));
    }

    private static string BuildDesignConceptId(
        string templateId,
        string colorMood,
        string motionStyle)
        => $"{templateId}:{colorMood}:{motionStyle}";

    private static IReadOnlyList<string> BuildPrioritizedAssetLabels(
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        var uploadedLabels = uploadedImages
            .Where(static asset => !string.IsNullOrWhiteSpace(asset.FileName))
            .Select(static asset => asset.FileName.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(6)
            .ToList();

        if (uploadedLabels.Count > 0)
        {
            return uploadedLabels;
        }

        return mediaAssets
            .Select(static asset => asset.Caption)
            .Where(static caption => !string.IsNullOrWhiteSpace(caption))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(4)
            .ToList();
    }

    private static List<GeneratedMediaAsset> CreateMediaAssetsFromUploads(
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        IReadOnlyList<string> captions)
    {
        var mediaAssets = new List<GeneratedMediaAsset>();

        foreach (var uploadedImage in uploadedImages.Take(6))
        {
            if (!TryResolveUploadedFileExtension(uploadedImage.FileName, uploadedImage.ContentType, out var extension))
            {
                continue;
            }

            var assetIndex = mediaAssets.Count;
            var caption = captions[Math.Min(assetIndex, captions.Count - 1)];
            var archivePath = $"assets/images/uploaded-edit-{assetIndex + 1}.{extension}";
            mediaAssets.Add(new GeneratedMediaAsset(
                archivePath,
                archivePath.Replace('\\', '/'),
                caption,
                ResolveGalleryCssClass(assetIndex),
                uploadedImage.Content,
                1600,
                1000));
        }

        return mediaAssets;
    }

    private static List<GeneratedMediaAsset> MergeEditedMediaAssets(
        IReadOnlyList<GeneratedMediaAsset> currentAssets,
        IReadOnlyList<GeneratedMediaAsset> newAssets,
        string prompt)
    {
        if (newAssets.Count == 0)
        {
            return currentAssets.ToList();
        }

        var maxAssets = Math.Min(6, Math.Max(currentAssets.Count, 4));
        var replaceAll = ShouldReplaceEntireMediaCollection(prompt);
        var replaceHeroOnly = !replaceAll && ShouldReplaceHeroMedia(prompt);
        var appendOnly = !replaceAll && !replaceHeroOnly && ShouldAppendMedia(prompt);

        var merged = new List<GeneratedMediaAsset>();

        if (replaceHeroOnly)
        {
            merged.Add(newAssets[0]);
            merged.AddRange(currentAssets.Skip(1));
            if (newAssets.Count > 1)
            {
                merged.AddRange(newAssets.Skip(1));
            }
        }
        else if (appendOnly)
        {
            merged.AddRange(currentAssets);
            merged.AddRange(newAssets);
        }
        else if (replaceAll)
        {
            merged.AddRange(newAssets);
            merged.AddRange(currentAssets.Where(asset => !IsPlaceholderMediaAsset(asset)));
        }
        else
        {
            merged.AddRange(newAssets);
            merged.AddRange(currentAssets);
        }

        return merged
            .Take(maxAssets)
            .Select((asset, index) => asset with { CssClass = ResolveGalleryCssClass(index) })
            .ToList();
    }

    private static bool HasMeaningfulRealImages(IReadOnlyList<GeneratedMediaAsset> mediaAssets)
        => mediaAssets.Any(asset => !IsPlaceholderMediaAsset(asset));

    private static bool IsPlaceholderMediaAsset(GeneratedMediaAsset asset)
        => asset.ArchivePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) &&
           asset.ArchivePath.Contains("visual-", StringComparison.OrdinalIgnoreCase);

    private async Task<List<GeneratedMediaAsset>> LoadGeneratedMediaAssetsAsync(
        WebsiteProjectState state,
        string currentSiteDirectory,
        CancellationToken cancellationToken)
    {
        var mediaAssets = new List<GeneratedMediaAsset>();

        foreach (var asset in state.MediaAssets)
        {
            var assetPath = Path.Combine(currentSiteDirectory, asset.ArchivePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(assetPath))
            {
                continue;
            }

            var content = await File.ReadAllBytesAsync(assetPath, cancellationToken);
            mediaAssets.Add(new GeneratedMediaAsset(
                asset.ArchivePath,
                asset.WebPath,
                asset.Caption,
                asset.CssClass,
                content,
                asset.Width,
                asset.Height));
        }

        return mediaAssets;
    }

    private static List<GeneratedMediaAsset> SyncMediaCaptions(
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        IReadOnlyList<string> captions)
    {
        if (mediaAssets.Count == 0)
        {
            return [];
        }

        return mediaAssets
            .Select((asset, index) => asset with
            {
                Caption = captions[Math.Min(index, captions.Count - 1)],
                CssClass = ResolveGalleryCssClass(index)
            })
            .ToList();
    }

    private async Task<GeneratedLogoAsset> LoadGeneratedLogoAssetAsync(
        WebsiteProjectState state,
        string currentSiteDirectory,
        ThemeChoice theme,
        CancellationToken cancellationToken)
    {
        if (state.LogoAsset.IsUploaded &&
            !string.IsNullOrWhiteSpace(state.LogoAsset.ArchivePath))
        {
            var logoPath = Path.Combine(
                currentSiteDirectory,
                state.LogoAsset.ArchivePath.Replace('/', Path.DirectorySeparatorChar));

            if (File.Exists(logoPath))
            {
                return new GeneratedLogoAsset(
                    state.LogoAsset.WebPath,
                    state.LogoAsset.SvgMarkup,
                    state.LogoAsset.ArchivePath,
                    await File.ReadAllBytesAsync(logoPath, cancellationToken),
                    true);
            }
        }

        return new GeneratedLogoAsset(
            "assets/icons/logo-mark.svg",
            BuildFallbackLogoSvg(state.Business, theme),
            "assets/icons/logo-mark.svg",
            null,
            false);
    }

    private async Task<AiEditPayloadResult?> TryGenerateAiEditPayloadAsync(
        WebsiteProjectState state,
        string prompt,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo,
        CancellationToken cancellationToken)
    {
        var editPrompt = BuildEditPrompt(state, prompt, uploadedImages, uploadedLogo);

        try
        {
            var responseText = await _geminiProxyService.AskAsync(editPrompt, cancellationToken);
            var payload = TryParseAiEditPayload(responseText);
            if (payload is not null)
            {
                var model = await _geminiProxyService.GetConfiguredModelAsync(cancellationToken);
                return new AiEditPayloadResult(model, payload);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep the deterministic editor available if the account model is unavailable.
        }

        return null;
    }

    private string BuildEditPrompt(
        WebsiteProjectState state,
        string prompt,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        WebsiteUploadedAsset? uploadedLogo)
    {
        var creativeBrief = WebsiteGenerationCreativeDirection.BuildBrief(
            state.Business.Category,
            state.Business.PrimaryType,
            state.TemplateId,
            state.TemplateName,
            state.ColorMood,
            state.FontDirection,
            state.MotionStyle,
            state.Theme.FontPair.DisplayName,
            state.Theme.FontPair.BodyName,
            state.Theme.PrimaryColor,
            state.Theme.SecondaryColor,
            state.Theme.AccentColor,
            state.Theme.Background,
            state.Theme.Surface,
            state.Theme.TextColor,
            state.SectionOrder,
            state.Business.Services.Count > 0
                ? state.Business.Services
                : state.Translations["fr"].Services.Select(static card => card.Title).ToList(),
            state.Business.Features.Count > 0
                ? state.Business.Features
                : state.Translations["fr"].Highlights.Select(static card => card.Title).ToList(),
            state.Business.Description);

        var promptPayload = JsonSerializer.Serialize(new
        {
            currentDesign = new
            {
                state.TemplateId,
                state.DesignConcept,
                state.ColorMood,
                state.FontDirection,
                state.MotionStyle,
                state.SectionOrder,
                state.HiddenSections
            },
            currentBusiness = new
            {
                state.Business.Name,
                state.Business.Category,
                state.Business.Address,
                state.Business.PhoneNumber,
                state.Business.Rating,
                state.Business.ReviewCount
            },
            currentFrench = new
            {
                heroTitle = state.Translations["fr"].HeroTitle,
                heroSubtitle = state.Translations["fr"].HeroSubtitle,
                aboutTitle = state.Translations["fr"].AboutTitle,
                aboutBody = state.Translations["fr"].AboutBody,
                servicesTitle = state.Translations["fr"].ServicesTitle,
                galleryTitle = state.Translations["fr"].GalleryTitle,
                galleryCaptions = state.Translations["fr"].GalleryCaptions,
                reviewsTitle = state.Translations["fr"].ReviewsTitle,
                contactTitle = state.Translations["fr"].ContactTitle,
                faqTitle = state.Translations["fr"].FaqTitle
            },
            currentAssets = new
            {
                currentPrioritizedAssets = state.PrioritizedAssets,
                currentUploadedImages = state.UploadedImageFileNames,
                currentUploadedLogo = state.UploadedLogoFileName
            },
            pendingUploads = new
            {
                uploadedImages = uploadedImages
                    .Where(static asset => !string.IsNullOrWhiteSpace(asset.FileName))
                    .Select(static asset => asset.FileName.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(6)
                    .ToList(),
                uploadedLogo = string.IsNullOrWhiteSpace(uploadedLogo?.FileName)
                    ? null
                    : uploadedLogo.FileName.Trim()
            },
            userInstruction = prompt
        }, JsonOptions);

        return $$"""
        You are editing an existing generated static website.
        Return ONLY strict JSON. No markdown. No commentary.
        Respect the current creative direction unless the user explicitly asks to change it.

        Current creative direction:
        {{creativeBrief}}

        Rules:
        - Change only the parts requested by the user.
        - Omit untouched fields instead of rewriting everything.
        - Keep content professional, polished, and conversion-oriented.
        - Preserve the business facts unless the user explicitly asks to change wording.
        - Allowed templateId values: restaurant-signature, coffee-shop-signature, luxury, minimal, creative, corporate, premium.
        - Allowed colorMood values: luxury-gold, botanical-green, premium-cobalt, nightlife-neon, warm-terra, coastal-blue, rose-boutique, monochrome-ink.
        - Allowed fontDirection values: editorial-serif, geometric-modern, bold-display, professional-clean, premium-classic.
        - Allowed motionStyle values: gentle, minimal, energetic, dramatic.
        - Allowed section ids: about, highlights, services, gallery, reviews, contact, faq.
        - If the user asks to remove a section, place it in hiddenSections.
        - If the user asks to rearrange the layout, return a full sectionOrder array.
        - If the user asks for text changes, provide only the changed translation fields.
        - If pending uploaded images or a pending uploaded logo are present, assume they are available for the next website revision.
        - When the request mentions replacing, improving, or adding images, you may refresh galleryTitle, galleryIntro, and galleryCaptions to match the new visual direction.

        JSON schema:
        {
          "changeSummary": "string",
          "design": {
            "templateId": "string",
            "colorMood": "string",
            "fontDirection": "string",
            "motionStyle": "string",
            "sectionOrder": ["about", "services"],
            "hiddenSections": ["faq"]
          },
          "translations": {
            "fr": {
              "heroTitle": "string",
              "heroSubtitle": "string",
              "heroDescription": "string",
              "aboutTitle": "string",
              "aboutBody": "string",
              "servicesTitle": "string",
              "servicesIntro": "string",
              "serviceItems": [{ "title": "string", "description": "string" }],
              "highlightsTitle": "string",
              "highlightItems": [{ "title": "string", "description": "string" }],
              "galleryTitle": "string",
              "galleryIntro": "string",
              "galleryCaptions": ["string"],
              "reviewTitle": "string",
              "reviewSummary": "string",
              "contactTitle": "string",
              "contactIntro": "string",
              "formTitle": "string",
              "formIntro": "string",
              "faqTitle": "string",
              "faqItems": [{ "question": "string", "answer": "string" }],
              "footerTagline": "string"
            },
            "en": { "same optional structure as fr": "same" },
            "ar": { "same optional structure as fr": "same" }
          },
          "seo": {
            "title": "string",
            "description": "string",
            "keywords": ["string"]
          }
        }

        Current state JSON:
        {{promptPayload}}
        """;
    }

    private AiWebsiteEditPayload? TryParseAiEditPayload(string rawResponse)
    {
        foreach (var candidate in ExtractJsonCandidates(rawResponse))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AiWebsiteEditPayload>(candidate, JsonOptions);
                if (payload is not null)
                {
                    return payload;
                }
            }
            catch
            {
                // Ignore parse attempts and continue.
            }
        }

        return null;
    }

    private AiEditPayloadResult BuildFallbackEditPayload(string prompt)
    {
        var normalizedPrompt = NormalizeSearchFingerprint(prompt);
        var design = new AiDesignEdit();
        var hiddenSections = new List<string>();

        if (normalizedPrompt.Contains("coffee", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("coffee shop", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("cafeteria", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("salon de the", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("salon de thé", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("café", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("cafe", StringComparison.Ordinal))
        {
            design.TemplateId = "coffee-shop-signature";
            design.ColorMood = "warm-terra";
            design.FontDirection = "premium-classic";
            design.MotionStyle = "gentle";
        }
        else if (normalizedPrompt.Contains("restaurant", StringComparison.Ordinal) ||
                 normalizedPrompt.Contains("restaur", StringComparison.Ordinal) ||
                 normalizedPrompt.Contains("bistro", StringComparison.Ordinal))
        {
            design.TemplateId = "restaurant-signature";
            design.ColorMood = "warm-terra";
            design.FontDirection = "premium-classic";
            design.MotionStyle = "gentle";
        }
        else if (normalizedPrompt.Contains("luxur", StringComparison.Ordinal))
        {
            design.TemplateId = "luxury";
            design.ColorMood = "luxury-gold";
            design.FontDirection = "editorial-serif";
            design.MotionStyle = "dramatic";
        }
        else if (normalizedPrompt.Contains("modern", StringComparison.Ordinal))
        {
            design.TemplateId = "premium";
            design.ColorMood = "coastal-blue";
            design.FontDirection = "premium-classic";
            design.MotionStyle = "gentle";
        }
        else if (normalizedPrompt.Contains("creative", StringComparison.Ordinal) ||
                 normalizedPrompt.Contains("bold", StringComparison.Ordinal))
        {
            design.TemplateId = "creative";
            design.ColorMood = "nightlife-neon";
            design.FontDirection = "bold-display";
            design.MotionStyle = "energetic";
        }
        else if (normalizedPrompt.Contains("corporate", StringComparison.Ordinal) ||
                 normalizedPrompt.Contains("profession", StringComparison.Ordinal))
        {
            design.TemplateId = "premium";
            design.ColorMood = "monochrome-ink";
            design.FontDirection = "professional-clean";
            design.MotionStyle = "gentle";
        }

        if (normalizedPrompt.Contains("remove faq", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("supprimer faq", StringComparison.Ordinal))
        {
            hiddenSections.Add("faq");
        }

        if (normalizedPrompt.Contains("remove review", StringComparison.Ordinal) ||
            normalizedPrompt.Contains("supprimer avis", StringComparison.Ordinal))
        {
            hiddenSections.Add("reviews");
        }

        if (hiddenSections.Count > 0)
        {
            design.HiddenSections = hiddenSections;
        }

        return new AiEditPayloadResult(
            "Deterministic fallback",
            new AiWebsiteEditPayload
            {
                ChangeSummary = "Mise a jour appliquee via le fallback de design local.",
                Design = design
            });
    }

    private static IReadOnlyList<string> NormalizeSectionOrder(
        IReadOnlyList<string>? requestedOrder,
        IReadOnlyList<string> currentOrder,
        string templateId)
    {
        var normalized = (requestedOrder ?? [])
            .Where(static sectionId => !string.IsNullOrWhiteSpace(sectionId))
            .Select(static sectionId => sectionId.Trim().ToLowerInvariant())
            .Where(sectionId => AllowedSectionIds.Contains(sectionId, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (normalized.Count == 0)
        {
            normalized = currentOrder.Count > 0
                ? currentOrder.ToList()
                : GetDefaultSectionOrder(templateId).ToList();
        }

        foreach (var sectionId in AllowedSectionIds)
        {
            if (!normalized.Contains(sectionId, StringComparer.OrdinalIgnoreCase))
            {
                normalized.Add(sectionId);
            }
        }

        return normalized;
    }

    private static IReadOnlyList<string> NormalizeHiddenSections(
        IReadOnlyList<string>? requestedHiddenSections,
        IReadOnlyList<string> currentHiddenSections)
    {
        var normalized = (requestedHiddenSections ?? currentHiddenSections)
            .Where(static sectionId => !string.IsNullOrWhiteSpace(sectionId))
            .Select(static sectionId => sectionId.Trim().ToLowerInvariant())
            .Where(sectionId => AllowedSectionIds.Contains(sectionId, StringComparer.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return normalized;
    }

    private static bool ShouldRotateMedia(string prompt)
    {
        return prompt.Contains("hero", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("gallery", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("visuel", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReplaceEntireMediaCollection(string prompt)
    {
        return prompt.Contains("replace images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("replace all images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("remplace les images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("remplacer les images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("change the images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("change les images", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldReplaceHeroMedia(string prompt)
    {
        return prompt.Contains("hero image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("image hero", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("image principale", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("remplace le hero", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("replace the hero", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldAppendMedia(string prompt)
    {
        return prompt.Contains("add image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("add images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("ajoute une image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("ajoute des images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("ajouter des images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("add to the gallery", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldRefreshMediaLibrary(string prompt)
    {
        return prompt.Contains("better image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("better images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("find images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("cherche des images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("cherche une image", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("trouve des images", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("replace placeholder", StringComparison.OrdinalIgnoreCase) ||
               prompt.Contains("remplace les placeholders", StringComparison.OrdinalIgnoreCase);
    }

    private static string? PreferNullable(string? preferred, string? fallback)
        => string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();

    private static string ResolveHeadlineLocation(string? address, string fallback)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return fallback;
        }

        var segments = address
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static segment => !string.IsNullOrWhiteSpace(segment))
            .ToArray();

        if (segments.Length > 0)
        {
            var lastSegment = segments[^1];
            if (!string.IsNullOrWhiteSpace(lastSegment) && lastSegment.Length <= 24)
            {
                return lastSegment;
            }
        }

        return address.Length <= 32
            ? address.Trim()
            : address[..32].TrimEnd();
    }

    private async Task<LocalizedContentBundle> BuildContentBundleAsync(
        NormalizedBusiness business,
        TemplateDefinition template,
        ThemeChoice theme,
        IReadOnlyList<string> sectionOrder,
        string motionStyle,
        CancellationToken cancellationToken)
    {
        var fallbackTranslations = PolishTranslationsForPresentation(BuildFallbackTranslations(business), business);
        var fallbackFrench = fallbackTranslations["fr"];
        var fallbackSeo = BuildFallbackSeo(business, fallbackFrench);
        var creativeBrief = BuildCreativeDirectionBrief(
            business,
            template,
            theme,
            sectionOrder,
            motionStyle,
            fallbackFrench);

        var aiPayload = await TryGenerateAiPayloadAsync(
            business,
            fallbackFrench,
            creativeBrief,
            cancellationToken);
        if (aiPayload is null)
        {
            return new LocalizedContentBundle(
                fallbackTranslations,
                fallbackSeo,
                "Local deterministic fallback",
                fallbackFrench);
        }

        var mergedTranslations = PolishTranslationsForPresentation(MergeTranslations(fallbackTranslations, aiPayload), business);
        var mergedSeo = MergeSeo(fallbackSeo, aiPayload.Payload.Seo, mergedTranslations["fr"]);

        return new LocalizedContentBundle(
            mergedTranslations,
            mergedSeo,
            aiPayload.ModelName,
            fallbackFrench);
    }

    private async Task<AiPayloadResult?> TryGenerateAiPayloadAsync(
        NormalizedBusiness business,
        LocalizedWebsiteContent fallbackFrench,
        string creativeBrief,
        CancellationToken cancellationToken)
    {
        var prompt = BuildAiPrompt(business, fallbackFrench, creativeBrief);

        try
        {
            var responseText = await _geminiProxyService.AskAsync(prompt, cancellationToken);
            if (!string.IsNullOrWhiteSpace(responseText))
            {
                var aiPayload = TryParseAiPayload(responseText);
                if (aiPayload is not null)
                {
                    var model = await _geminiProxyService.GetConfiguredModelAsync(cancellationToken);
                    return new AiPayloadResult(model, aiPayload);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // Keep deterministic content generation available if Google AI is unavailable.
        }

        return null;
    }

    private AiWebsitePayload? TryParseAiPayload(string rawResponse)
    {
        foreach (var candidate in ExtractJsonCandidates(rawResponse))
        {
            try
            {
                var payload = JsonSerializer.Deserialize<AiWebsitePayload>(candidate, JsonOptions);
                if (payload?.Translations?.Count > 0)
                {
                    return payload;
                }
            }
            catch
            {
                // Ignore parse attempts and try the next JSON candidate.
            }
        }

        return null;
    }

    private IEnumerable<string> ExtractJsonCandidates(string rawResponse)
    {
        var trimmed = rawResponse.Trim();
        if (trimmed.StartsWith('{') && trimmed.EndsWith('}'))
        {
            yield return trimmed;
        }

        var fenceMatch = JsonFenceRegex.Match(rawResponse);
        if (fenceMatch.Success)
        {
            yield return fenceMatch.Groups[1].Value.Trim();
        }

        var firstBrace = rawResponse.IndexOf('{');
        var lastBrace = rawResponse.LastIndexOf('}');
        if (firstBrace >= 0 && lastBrace > firstBrace)
        {
            yield return rawResponse[firstBrace..(lastBrace + 1)].Trim();
        }
    }

    private Dictionary<string, LocalizedWebsiteContent> MergeTranslations(
        Dictionary<string, LocalizedWebsiteContent> fallbackTranslations,
        AiPayloadResult aiPayload)
    {
        var merged = new Dictionary<string, LocalizedWebsiteContent>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in fallbackTranslations)
        {
            AiLocalizedContent? aiTranslation = null;
            aiPayload.Payload.Translations?.TryGetValue(entry.Key, out aiTranslation);
            merged[entry.Key] = MergeLocalizedContent(entry.Value, aiTranslation);
        }

        return merged;
    }

    private Dictionary<string, LocalizedWebsiteContent> PolishTranslationsForPresentation(
        Dictionary<string, LocalizedWebsiteContent> translations,
        NormalizedBusiness business)
    {
        var fallbackTranslations = BuildFallbackTranslations(business);
        var polished = new Dictionary<string, LocalizedWebsiteContent>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in translations)
        {
            var languageCode = entry.Key;
            var fallback = fallbackTranslations.TryGetValue(languageCode, out var fallbackContent)
                ? fallbackContent
                : fallbackTranslations["fr"];
            polished[languageCode] = PolishLocalizedContent(entry.Value, fallback, business);
        }

        return polished;
    }

    private static LocalizedWebsiteContent PolishLocalizedContent(
        LocalizedWebsiteContent content,
        LocalizedWebsiteContent fallback,
        NormalizedBusiness business)
    {
        return content with
        {
            HeroTitle = PolishHeroTitle(content.HeroTitle, fallback.HeroTitle, business),
            HeroSubtitle = PolishHeroSubtitle(content.HeroSubtitle, fallback.HeroSubtitle, business),
            HeroDescription = TrimToLengthSafe(
                PreferNullable(content.HeroDescription, fallback.HeroDescription) ?? fallback.HeroDescription,
                220)
        };
    }

    private static string PolishHeroTitle(
        string candidate,
        string fallback,
        NormalizedBusiness business)
    {
        var cleaned = CleanText(candidate);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        if (ContainsFullAddress(cleaned, business.Address) ||
            CountWords(cleaned) > 8 ||
            cleaned.Length > 68)
        {
            return fallback;
        }

        return cleaned;
    }

    private static string PolishHeroSubtitle(
        string candidate,
        string fallback,
        NormalizedBusiness business)
    {
        var cleaned = CleanText(candidate);
        if (string.IsNullOrWhiteSpace(cleaned))
        {
            return fallback;
        }

        if (ContainsFullAddress(cleaned, business.Address) || cleaned.Length > 140)
        {
            return fallback;
        }

        return cleaned;
    }

    private static bool ContainsFullAddress(string text, string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return false;
        }

        return text.Contains(address.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    private static int CountWords(string value)
    {
        return value
            .Split([' ', '\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Length;
    }

    private static LocalizedWebsiteContent MergeLocalizedContent(
        LocalizedWebsiteContent fallback,
        AiLocalizedContent? ai)
    {
        if (ai is null)
        {
            return fallback;
        }

        return fallback with
        {
            HeroEyebrow = Prefer(ai.HeroEyebrow, fallback.HeroEyebrow),
            HeroTitle = Prefer(ai.HeroTitle, fallback.HeroTitle),
            HeroSubtitle = Prefer(ai.HeroSubtitle, fallback.HeroSubtitle),
            HeroDescription = Prefer(ai.HeroDescription, fallback.HeroDescription),
            AboutTitle = Prefer(ai.AboutTitle, fallback.AboutTitle),
            AboutBody = Prefer(ai.AboutBody, fallback.AboutBody),
            ServicesTitle = Prefer(ai.ServicesTitle, fallback.ServicesTitle),
            ServicesIntro = Prefer(ai.ServicesIntro, fallback.ServicesIntro),
            Services = MergeCards(fallback.Services, ai.ServiceItems),
            HighlightsTitle = Prefer(ai.HighlightsTitle, fallback.HighlightsTitle),
            Highlights = MergeCards(fallback.Highlights, ai.HighlightItems),
            GalleryTitle = Prefer(ai.GalleryTitle, fallback.GalleryTitle),
            GalleryIntro = Prefer(ai.GalleryIntro, fallback.GalleryIntro),
            GalleryCaptions = MergeCaptions(fallback.GalleryCaptions, ai.GalleryCaptions),
            ReviewsTitle = Prefer(ai.ReviewTitle, fallback.ReviewsTitle),
            ReviewsSummary = Prefer(ai.ReviewSummary, fallback.ReviewsSummary),
            ContactTitle = Prefer(ai.ContactTitle, fallback.ContactTitle),
            ContactIntro = Prefer(ai.ContactIntro, fallback.ContactIntro),
            FormTitle = Prefer(ai.FormTitle, fallback.FormTitle),
            FormIntro = Prefer(ai.FormIntro, fallback.FormIntro),
            FaqTitle = Prefer(ai.FaqTitle, fallback.FaqTitle),
            Faq = MergeFaq(fallback.Faq, ai.FaqItems),
            FooterTagline = Prefer(ai.FooterTagline, fallback.FooterTagline)
        };
    }

    private static SeoContent MergeSeo(
        SeoContent fallbackSeo,
        AiSeoContent? aiSeo,
        LocalizedWebsiteContent frenchContent)
    {
        if (aiSeo is null)
        {
            return fallbackSeo;
        }

        var title = Prefer(aiSeo.Title, fallbackSeo.Title);
        var description = Prefer(aiSeo.Description, fallbackSeo.Description);
        var keywords = aiSeo.Keywords?
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Select(static keyword => keyword.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(14)
            .ToList();

        if (keywords is null || keywords.Count == 0)
        {
            keywords = fallbackSeo.Keywords.ToList();
        }

        return new SeoContent(title, description, keywords, frenchContent.HeroTitle);
    }

    private string BuildCreativeDirectionBrief(
        NormalizedBusiness business,
        TemplateDefinition template,
        ThemeChoice theme,
        IReadOnlyList<string> sectionOrder,
        string motionStyle,
        LocalizedWebsiteContent fallbackFrench)
    {
        return WebsiteGenerationCreativeDirection.BuildBrief(
            business.Category,
            business.PrimaryType,
            template.Id,
            template.DisplayName,
            ResolveDefaultColorMood(template.Id, business),
            ResolveDefaultFontDirection(template.Id, business),
            motionStyle,
            theme.FontPair.DisplayName,
            theme.FontPair.BodyName,
            theme.PrimaryColor,
            theme.SecondaryColor,
            theme.AccentColor,
            theme.Background,
            theme.Surface,
            theme.TextColor,
            sectionOrder,
            business.Services.Count > 0
                ? business.Services
                : fallbackFrench.Services.Select(static card => card.Title).ToList(),
            business.Features.Count > 0
                ? business.Features
                : fallbackFrench.Highlights.Select(static card => card.Title).ToList(),
            business.Description);
    }

    private string BuildAiPrompt(
        NormalizedBusiness business,
        LocalizedWebsiteContent fallbackFrench,
        string creativeBrief)
    {
        var promptPayload = JsonSerializer.Serialize(new
        {
            business = new
            {
                business.Name,
                business.Category,
                business.PrimaryType,
                business.Description,
                business.Address,
                business.PhoneNumber,
                business.WhatsappNumber,
                business.PrimaryEmail,
                business.GoogleMapsUri,
                business.Rating,
                business.ReviewCount,
                business.ReviewsSummary,
                reviewHighlights = business.ReviewHighlights.Select(review => new
                {
                    review.AuthorName,
                    review.Rating,
                    review.RelativePublishTimeDescription,
                    review.Text
                }),
                business.OpeningHours,
                business.Services,
                business.Features
            },
            fallbackFrench = new
            {
                fallbackFrench.HeroTitle,
                fallbackFrench.HeroSubtitle,
                fallbackFrench.AboutBody,
                services = fallbackFrench.Services,
                highlights = fallbackFrench.Highlights,
                faq = fallbackFrench.Faq
            }
        }, JsonOptions);

        return $$"""
        You are creating premium marketing copy for a static website generator.
        Return ONLY strict JSON. No markdown fences. No explanation.
        You are not writing from a blank slate: the generator already selected a deliberate creative direction for this website.
        The copy, captions, FAQ wording, and SEO choices must feel consistent with that direction rather than generic.

        Creative direction:
        {{creativeBrief}}

        Requirements:
        - Languages: fr, en, ar.
        - Tone: modern, polished, credible, conversion-oriented, local SEO friendly.
        - Do not invent prices, fake guarantees, fake awards, fake years, or unverified claims.
        - If data is missing, infer carefully from the business category and local context.
        - heroTitle: 2 to 6 words when possible, brand-first, elegant, and never the full street address.
        - heroSubtitle: concise and premium, can mention the city or neighborhood, but never the full street address.
        - heroDescription: max 2 short sentences, customer-facing, natural, and not technical.
        - serviceItems: exactly 4 items.
        - highlightItems: exactly 3 items.
        - faqItems: exactly 4 items.
        - galleryCaptions: exactly 4 short captions.
        - Keep paragraphs concise and natural for a landing page.

        JSON schema:
        {
          "translations": {
            "fr": {
              "heroEyebrow": "string",
              "heroTitle": "string",
              "heroSubtitle": "string",
              "heroDescription": "string",
              "aboutTitle": "string",
              "aboutBody": "string",
              "servicesTitle": "string",
              "servicesIntro": "string",
              "serviceItems": [{ "title": "string", "description": "string" }],
              "highlightsTitle": "string",
              "highlightItems": [{ "title": "string", "description": "string" }],
              "galleryTitle": "string",
              "galleryIntro": "string",
              "galleryCaptions": ["string", "string", "string", "string"],
              "reviewTitle": "string",
              "reviewSummary": "string",
              "contactTitle": "string",
              "contactIntro": "string",
              "formTitle": "string",
              "formIntro": "string",
              "faqTitle": "string",
              "faqItems": [{ "question": "string", "answer": "string" }],
              "footerTagline": "string"
            },
            "en": { "same fields as fr": "same structure" },
            "ar": { "same fields as fr": "same structure" }
          },
          "seo": {
            "title": "string",
            "description": "string",
            "keywords": ["string", "string", "string"]
          }
        }

        Business context JSON:
        {{promptPayload}}
        """;
    }

    private Dictionary<string, LocalizedWebsiteContent> BuildFallbackTranslations(NormalizedBusiness business)
    {
        var translations = new Dictionary<string, LocalizedWebsiteContent>(StringComparer.OrdinalIgnoreCase)
        {
            ["fr"] = BuildFrenchFallbackContent(business),
            ["en"] = BuildEnglishFallbackContent(business),
            ["ar"] = BuildArabicFallbackContent(business)
        };

        return translations;
    }

    private LocalizedWebsiteContent BuildFrenchFallbackContent(NormalizedBusiness business)
    {
        var locationText = business.Address ?? "votre zone";
        var headlineLocation = ResolveHeadlineLocation(business.Address, "votre zone");
        var services = BuildFrenchServiceCards(business);
        var highlights = BuildFrenchHighlightCards(business);
        var faq = BuildFrenchFaqItems(business);
        var reviewSummary = BuildFrenchReviewSummary(business);
        var descriptionLead = !string.IsNullOrWhiteSpace(business.Description)
            ? business.Description
            : $"{business.Name} vous accueille avec une presentation soignee, une ambiance rassurante et les informations utiles pour preparer une visite, une commande ou une prise de contact.";
        var aboutLead = !string.IsNullOrWhiteSpace(business.Description)
            ? $"{business.Description} Cette page presente ensuite l essentiel avec une composition plus elegante, plus claire et plus agreable a parcourir."
            : $"{business.Name} accueille sa clientele depuis {locationText} avec une attention particuliere portee au service, a l atmosphere et a la clarte des informations utiles. Cette page permet de retrouver facilement l adresse, les horaires, les services et les moyens de contact.";

        return new LocalizedWebsiteContent(
            LanguageCode: "fr",
            LanguageLabel: "Francais",
            HeroEyebrow: business.Rating is not null
                ? $"{business.Category} note {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5"
                : $"{business.Category} a {headlineLocation}",
            HeroTitle: business.Name,
            HeroSubtitle: $"Une adresse locale a {headlineLocation}, presentee avec plus d elegance, de chaleur et de clarte.",
            HeroDescription: descriptionLead,
            PrimaryCta: "Contacter sur WhatsApp",
            SecondaryCta: "Voir les services",
            AboutEyebrow: "A propos",
            AboutTitle: $"L esprit {business.Name}",
            AboutBody: aboutLead,
            ServicesEyebrow: "Services",
            ServicesTitle: "Services et experience",
            ServicesIntro: "Retrouvez les prestations, les formats et les attentions qui rendent l experience plus simple, plus fluide et plus agreable.",
            Services: services,
            HighlightsEyebrow: "Points forts",
            HighlightsTitle: "Ce qui fait la difference",
            Highlights: highlights,
            GalleryEyebrow: "Galerie",
            GalleryTitle: $"L univers de {business.Name}",
            GalleryIntro: "Une galerie bien composee permet de retrouver l ambiance, les details du lieu et la qualite de presentation en un coup d oeil.",
            GalleryCaptions: ["Facade et identite", "Ambiance et accueil", "Produits et savoir-faire", "Contact et localisation"],
            ReviewsEyebrow: "Reputation",
            ReviewsTitle: "L avis des clients",
            ReviewsSummary: reviewSummary,
            HoursEyebrow: "Horaires",
            HoursTitle: "Horaires et disponibilites",
            ContactEyebrow: "Contact",
            ContactTitle: "Nous contacter",
            ContactIntro: "Adresse, telephone, carte, horaires et WhatsApp sont reunis au meme endroit pour rendre chaque prise de contact plus simple.",
            FormTitle: "Envoyer un message",
            FormIntro: "Le formulaire ouvre directement WhatsApp pour contacter l etablissement rapidement depuis mobile comme depuis desktop.",
            FaqEyebrow: "FAQ",
            FaqTitle: "Questions frequentes",
            Faq: faq,
            FooterTagline: $"{business.Name} - {business.Category} a {locationText}",
            Ui: new UiLabels(
                NavAbout: "A propos",
                NavServices: "Services",
                NavGallery: "Galerie",
                NavReviews: "Avis",
                NavContact: "Contact",
                LanguageLabel: "Langue",
                AddressLabel: "Adresse",
                PhoneLabel: "Telephone",
                EmailLabel: "Email",
                HoursLabel: "Horaires",
                RatingLabel: "Note",
                OpenMap: "Ouvrir la carte",
                WhatsAppLabel: "WhatsApp",
                FormNameLabel: "Nom",
                FormPhoneLabel: "Telephone",
                FormMessageLabel: "Message",
                FormSubmitLabel: "Envoyer sur WhatsApp",
                FormNamePlaceholder: "Votre nom",
                FormPhonePlaceholder: "Votre numero",
                FormMessagePlaceholder: "Bonjour, je souhaite obtenir plus d informations sur vos services.",
                NoHours: "Horaires communiques sur demande",
                GalleryBadge: "Photos",
                FeatureBadge: "Atouts",
                ReviewBadge: "Confiance",
                ContactBadge: "Disponible",
                FaqBadge: "Questions",
                ViewOnMaps: "Voir sur Google Maps",
                ViewReviews: "Voir les avis Google",
                WriteReview: "Laisser un avis",
                CallNow: "Appeler maintenant",
                SendOnWhatsapp: "Demarrer sur WhatsApp"));
    }

    private LocalizedWebsiteContent BuildEnglishFallbackContent(NormalizedBusiness business)
    {
        var locationText = business.Address ?? "your area";
        var headlineLocation = ResolveHeadlineLocation(business.Address, "your area");
        var services = BuildEnglishServiceCards(business);
        var highlights = BuildEnglishHighlightCards(business);
        var faq = BuildEnglishFaqItems(business);
        var reviewSummary = BuildEnglishReviewSummary(business);

        return new LocalizedWebsiteContent(
            LanguageCode: "en",
            LanguageLabel: "English",
            HeroEyebrow: business.Rating is not null
                ? $"{LocalizeCategory(business.Category, "en")} rated {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5"
                : $"{LocalizeCategory(business.Category, "en")} in {headlineLocation}",
            HeroTitle: business.Name,
            HeroSubtitle: $"A more elegant local presentation in {headlineLocation}, designed to feel warm, premium, and easy to trust.",
            HeroDescription: $"{business.Name} brings together the essentials in one polished experience: atmosphere, services, location, and the fastest way to get in touch.",
            PrimaryCta: "Contact on WhatsApp",
            SecondaryCta: "View services",
            AboutEyebrow: "About",
            AboutTitle: $"The spirit of {business.Name}",
            AboutBody: $"{business.Name} welcomes customers in {locationText} with a clear offer, a stronger sense of atmosphere, and a presentation that feels more refined on every screen.",
            ServicesEyebrow: "Services",
            ServicesTitle: "Services and experience",
            ServicesIntro: "Everything here is arranged to make the offer easier to understand, easier to enjoy, and easier to contact.",
            Services: services,
            HighlightsEyebrow: "Highlights",
            HighlightsTitle: "What makes it stand out",
            Highlights: highlights,
            GalleryEyebrow: "Gallery",
            GalleryTitle: $"Inside {business.Name}",
            GalleryIntro: "A stronger gallery helps visitors feel the atmosphere, discover the visual identity, and understand the experience before they arrive.",
            GalleryCaptions: ["Brand frontage", "Welcome and atmosphere", "Products and expertise", "Location and contact"],
            ReviewsEyebrow: "Reputation",
            ReviewsTitle: "Guest impressions",
            ReviewsSummary: reviewSummary,
            HoursEyebrow: "Hours",
            HoursTitle: "Opening hours and availability",
            ContactEyebrow: "Contact",
            ContactTitle: "Get in touch",
            ContactIntro: "Phone, map, hours, and WhatsApp are grouped together so every visitor can reach the business with less friction.",
            FormTitle: "Send a message",
            FormIntro: "The form opens WhatsApp directly for a fast, practical conversation from any device.",
            FaqEyebrow: "FAQ",
            FaqTitle: "Frequently asked questions",
            Faq: faq,
            FooterTagline: $"{business.Name} - {LocalizeCategory(business.Category, "en")} in {locationText}",
            Ui: new UiLabels(
                NavAbout: "About",
                NavServices: "Services",
                NavGallery: "Gallery",
                NavReviews: "Reviews",
                NavContact: "Contact",
                LanguageLabel: "Language",
                AddressLabel: "Address",
                PhoneLabel: "Phone",
                EmailLabel: "Email",
                HoursLabel: "Hours",
                RatingLabel: "Rating",
                OpenMap: "Open map",
                WhatsAppLabel: "WhatsApp",
                FormNameLabel: "Name",
                FormPhoneLabel: "Phone",
                FormMessageLabel: "Message",
                FormSubmitLabel: "Send on WhatsApp",
                FormNamePlaceholder: "Your name",
                FormPhonePlaceholder: "Your phone number",
                FormMessagePlaceholder: "Hello, I would like more information about your services.",
                NoHours: "Hours available on request",
                GalleryBadge: "Photos",
                FeatureBadge: "Highlights",
                ReviewBadge: "Trust",
                ContactBadge: "Available",
                FaqBadge: "FAQ",
                ViewOnMaps: "View on Google Maps",
                ViewReviews: "Read Google reviews",
                WriteReview: "Leave a review",
                CallNow: "Call now",
                SendOnWhatsapp: "Start on WhatsApp"));
    }

    private LocalizedWebsiteContent BuildArabicFallbackContent(NormalizedBusiness business)
    {
        var locationText = business.Address ?? "منطقتك";
        var services = BuildArabicServiceCards(business);
        var highlights = BuildArabicHighlightCards(business);
        var faq = BuildArabicFaqItems(business);
        var reviewSummary = BuildArabicReviewSummary(business);

        return new LocalizedWebsiteContent(
            LanguageCode: "ar",
            LanguageLabel: "العربية",
            HeroEyebrow: business.Rating is not null
                ? $"نشاط محلي بتقييم {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5"
                : "حضور محلي وتواصل سريع",
            HeroTitle: $"{business.Name}، خيار موثوق في {locationText}.",
            HeroSubtitle: $"موقع ثابت عصري يعرّف بخدمات {business.Name} ويحوّل الزيارات المحلية إلى طلبات حقيقية.",
            HeroDescription: $"تعرض هذه الصفحة المعلومات المهمة عن {business.Name} بشكل واضح: ما الذي يقدمه النشاط، أين يوجد، وكيف يمكن التواصل بسرعة من الهاتف أو واتساب.",
            PrimaryCta: "تواصل عبر واتساب",
            SecondaryCta: "عرض الخدمات",
            AboutEyebrow: "من نحن",
            AboutTitle: $"لماذا يختار العملاء {business.Name}؟",
            AboutBody: $"يخدم {business.Name} العملاء في {locationText} بعرض واضح وتجربة تواصل سهلة تساعد الزائر على فهم النشاط واتخاذ القرار بسرعة.",
            ServicesEyebrow: "الخدمات",
            ServicesTitle: "خدمات مناسبة للبحث المحلي",
            ServicesIntro: "يمكن تعديل هذه الأقسام بسهولة لتناسب الخدمات الفعلية أو المنتجات أو العروض الخاصة بالنشاط.",
            Services: services,
            HighlightsEyebrow: "نقاط القوة",
            HighlightsTitle: "أسباب تدفع العميل للتواصل",
            Highlights: highlights,
            GalleryEyebrow: "المعرض",
            GalleryTitle: "صور واضحة تبني الثقة",
            GalleryIntro: "عرض بصري جيد يساعد العميل على فهم الأجواء والخدمات وسهولة الوصول إلى النشاط.",
            GalleryCaptions: ["الواجهة والهوية", "الاستقبال والأجواء", "الخدمات والمنتجات", "الموقع ووسائل التواصل"],
            ReviewsEyebrow: "السمعة",
            ReviewsTitle: "ما الذي تعكسه البطاقة العامة",
            ReviewsSummary: reviewSummary,
            HoursEyebrow: "الأوقات",
            HoursTitle: "ساعات العمل والتوفر",
            ContactEyebrow: "تواصل",
            ContactTitle: "اجعل التواصل أسهل",
            ContactIntro: "الهاتف والعنوان والخريطة وواتساب في مكان واحد لتسهيل التواصل السريع مع النشاط.",
            FormTitle: "أرسل طلباً سريعاً",
            FormIntro: "النموذج يفتح واتساب مباشرة من دون أي باك إند ليسهّل إرسال الرسائل من الهاتف.",
            FaqEyebrow: "الأسئلة الشائعة",
            FaqTitle: "أسئلة متكررة",
            Faq: faq,
            FooterTagline: $"{business.Name} - {LocalizeCategory(business.Category, "ar")} في {locationText}",
            Ui: new UiLabels(
                NavAbout: "من نحن",
                NavServices: "الخدمات",
                NavGallery: "المعرض",
                NavReviews: "التقييم",
                NavContact: "تواصل",
                LanguageLabel: "اللغة",
                AddressLabel: "العنوان",
                PhoneLabel: "الهاتف",
                EmailLabel: "البريد",
                HoursLabel: "الأوقات",
                RatingLabel: "التقييم",
                OpenMap: "فتح الخريطة",
                WhatsAppLabel: "واتساب",
                FormNameLabel: "الاسم",
                FormPhoneLabel: "الهاتف",
                FormMessageLabel: "الرسالة",
                FormSubmitLabel: "إرسال عبر واتساب",
                FormNamePlaceholder: "اسمك",
                FormPhonePlaceholder: "رقم الهاتف",
                FormMessagePlaceholder: "مرحباً، أريد معرفة المزيد عن خدماتكم.",
                NoHours: "ساعات العمل متاحة عند الطلب",
                GalleryBadge: "صور",
                FeatureBadge: "المزايا",
                ReviewBadge: "ثقة",
                ContactBadge: "متاح",
                FaqBadge: "الأسئلة",
                ViewOnMaps: "عرض على خرائط Google",
                ViewReviews: "عرض تقييمات Google",
                WriteReview: "إضافة تقييم",
                CallNow: "اتصل الآن",
                SendOnWhatsapp: "ابدأ عبر واتساب"));
    }

    private static SeoContent BuildFallbackSeo(NormalizedBusiness business, LocalizedWebsiteContent frenchContent)
    {
        var locationLabel = string.IsNullOrWhiteSpace(business.Address)
            ? business.Category
            : business.Address;

        var title = $"{business.Name} | {business.Category} a {locationLabel}";
        var description = TrimToLengthSafe(
            $"{business.Name} presente ses services, ses horaires, son adresse et son contact direct a {locationLabel}. Site vitrine statique moderne optimise SEO local.",
            155);
        var keywords = new[]
        {
            business.Name,
            business.Category,
            $"{business.Category} {locationLabel}",
            $"contact {business.Name}",
            $"WhatsApp {business.Name}",
            $"adresse {business.Name}",
            "site vitrine",
            "SEO local"
        }
            .Where(static keyword => !string.IsNullOrWhiteSpace(keyword))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new SeoContent(title, description, keywords, frenchContent.HeroTitle);
    }

    private ClientSiteConfig BuildClientConfig(
        NormalizedBusiness business,
        string siteUrl,
        GeneratedLogoAsset logoAsset,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return new ClientSiteConfig(
            SiteUrl: siteUrl,
            DefaultLanguage: "fr",
            AvailableLanguages: ["fr", "en", "ar"],
            BusinessName: business.Name,
            BusinessCategory: business.Category,
            Address: business.Address,
            PhoneNumber: business.PhoneNumber,
            WhatsappNumber: business.WhatsappNumber,
            PrimaryEmail: business.PrimaryEmail,
            GoogleMapsUri: business.GoogleMapsUri,
            MapEmbedUri: business.MapEmbedUri,
            Rating: business.Rating,
            ReviewCount: business.ReviewCount,
            ReviewsUri: business.ReviewsUri,
            WriteAReviewUri: business.WriteAReviewUri,
            OpeningHours: business.OpeningHours,
            SocialLinks: business.SocialLinks,
            LogoPath: logoAsset.WebPath,
            Gallery: mediaAssets.Select(mediaAsset => new ClientGalleryItem(
                mediaAsset.WebPath,
                mediaAsset.CssClass,
                mediaAsset.Width,
                mediaAsset.Height)).ToList());
    }

    private Task<GeneratedLogoAsset> PrepareLogoAssetAsync(
        NormalizedBusiness business,
        ThemeChoice theme,
        WebsiteUploadedAsset? uploadedLogo,
        CancellationToken cancellationToken)
    {
        if (uploadedLogo is not null &&
            TryResolveUploadedFileExtension(uploadedLogo.FileName, uploadedLogo.ContentType, out var extension))
        {
            return Task.FromResult(new GeneratedLogoAsset(
                WebPath: $"assets/images/uploaded-logo.{extension}",
                SvgMarkup: BuildFallbackLogoSvg(business, theme),
                ArchivePath: $"assets/images/uploaded-logo.{extension}",
                Content: uploadedLogo.Content,
                IsUploaded: true));
        }

        return PrepareResolvedLogoAssetAsync(business, theme, cancellationToken);
    }

    private async Task<GeneratedLogoAsset> PrepareResolvedLogoAssetAsync(
        NormalizedBusiness business,
        ThemeChoice theme,
        CancellationToken cancellationToken)
    {
        if (TryGetAbsoluteHttpUri(business.LogoUri, out var logoUri))
        {
            try
            {
                var downloaded = await DownloadBinaryAssetAsync(
                    logoUri,
                    "assets/images/discovered-logo",
                    cancellationToken);

                if (downloaded is not null)
                {
                    return new GeneratedLogoAsset(
                        downloaded.WebPath,
                        BuildFallbackLogoSvg(business, theme),
                        downloaded.ArchivePath,
                        downloaded.Content,
                        true);
                }
            }
            catch
            {
                // Fall back to the generated SVG logo.
            }
        }

        return new GeneratedLogoAsset(
            WebPath: "assets/icons/logo-mark.svg",
            SvgMarkup: BuildFallbackLogoSvg(business, theme),
            ArchivePath: "assets/icons/logo-mark.svg",
            Content: null,
            IsUploaded: false);
    }

    private async Task<IReadOnlyList<GeneratedMediaAsset>> PrepareImageAssetsAsync(
        NormalizedBusiness business,
        IReadOnlyList<string> fallbackCaptions,
        ThemeChoice theme,
        IReadOnlyList<WebsiteUploadedAsset> uploadedImages,
        CancellationToken cancellationToken)
    {
        var mediaAssets = new List<GeneratedMediaAsset>();
        var captionIndex = 0;

        foreach (var uploadedImage in uploadedImages.Take(6))
        {
            if (!TryResolveUploadedFileExtension(uploadedImage.FileName, uploadedImage.ContentType, out var extension))
            {
                continue;
            }

            var archivePath = $"assets/images/uploaded-{mediaAssets.Count + 1}.{extension}";
            mediaAssets.Add(new GeneratedMediaAsset(
                archivePath,
                archivePath.Replace('\\', '/'),
                fallbackCaptions[Math.Min(captionIndex, fallbackCaptions.Count - 1)],
                ResolveGalleryCssClass(mediaAssets.Count),
                uploadedImage.Content,
                1600,
                1000));
            captionIndex++;
        }

        var uniqueUris = business.PhotoUris
            .Where(IsAcceptableBusinessPhotoUri)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(18)
            .ToList();

        foreach (var rawUri in uniqueUris)
        {
            if (mediaAssets.Count >= 6)
            {
                break;
            }

            if (!TryGetAbsoluteHttpUri(rawUri, out var photoUri))
            {
                continue;
            }

            try
            {
                var downloaded = await DownloadBinaryAssetAsync(
                    photoUri,
                    $"assets/images/gallery-{mediaAssets.Count + 1}",
                    cancellationToken);

                if (downloaded is null)
                {
                    continue;
                }

                if (!IsSuitableGalleryBinaryAsset(rawUri, downloaded))
                {
                    continue;
                }

                mediaAssets.Add(new GeneratedMediaAsset(
                    downloaded.ArchivePath,
                    downloaded.WebPath,
                    fallbackCaptions[Math.Min(captionIndex, fallbackCaptions.Count - 1)],
                    ResolveGalleryCssClass(mediaAssets.Count),
                    downloaded.Content,
                    downloaded.Width,
                    downloaded.Height));
                captionIndex++;
            }
            catch
            {
                // Fall back to generated placeholders.
            }
        }

        if (mediaAssets.Count < 4 && IsHospitalityBusiness(business))
        {
            var fallbackUris = IsCoffeeShopBusiness(business)
                ? GetCoffeeTemplateFallbackImageUris()
                : GetRestaurantTemplateFallbackImageUris();

            foreach (var fallbackUri in fallbackUris)
            {
                if (mediaAssets.Count >= 6)
                {
                    break;
                }

                try
                {
                    var downloaded = await DownloadBinaryAssetAsync(
                        new Uri(fallbackUri),
                        $"assets/images/gallery-{mediaAssets.Count + 1}",
                        cancellationToken);

                    if (downloaded is null || !IsSuitableGalleryBinaryAsset(fallbackUri, downloaded))
                    {
                        continue;
                    }

                    mediaAssets.Add(new GeneratedMediaAsset(
                        downloaded.ArchivePath,
                        downloaded.WebPath,
                        fallbackCaptions[Math.Min(captionIndex, fallbackCaptions.Count - 1)],
                        ResolveGalleryCssClass(mediaAssets.Count),
                        downloaded.Content,
                        downloaded.Width,
                        downloaded.Height));
                    captionIndex++;
                }
                catch
                {
                    // Ignore fallback download failures and keep generating the local placeholders.
                }
            }
        }

        while (mediaAssets.Count < 4)
        {
            var caption = fallbackCaptions[Math.Min(mediaAssets.Count, fallbackCaptions.Count - 1)];
            var archivePath = $"assets/images/visual-{mediaAssets.Count + 1}.svg";
            var svgMarkup = BuildPlaceholderImageSvg(
                business,
                theme,
                mediaAssets.Count + 1,
                caption);
            mediaAssets.Add(new GeneratedMediaAsset(
                archivePath,
                archivePath.Replace('\\', '/'),
                caption,
                ResolveGalleryCssClass(mediaAssets.Count),
                Encoding.UTF8.GetBytes(svgMarkup),
                1600,
                1000));
        }

        return mediaAssets;
    }

    private static bool IsSuitableGalleryBinaryAsset(
        string sourceUrl,
        DownloadedBinaryAsset asset)
    {
        if (asset.Content.Length < 10_000)
        {
            return false;
        }

        if (asset.Width < 480 || asset.Height < 320)
        {
            return false;
        }

        return IsAcceptableBusinessPhotoUri(sourceUrl) &&
               !IsLikelyDecorativeImageUrl(sourceUrl);
    }

    private async Task<DownloadedBinaryAsset?> DownloadBinaryAssetAsync(
        Uri assetUri,
        string archivePathWithoutExtension,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, assetUri);
        ApplyBrowserLikeHeaders(request);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        var extension = ResolveFileExtension(mediaType);
        if (string.IsNullOrWhiteSpace(extension))
        {
            extension = ResolveFileExtensionFromPath(assetUri.AbsolutePath);
        }

        if (string.IsNullOrWhiteSpace(extension))
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is > 6_000_000)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var memoryStream = new MemoryStream();
        await stream.CopyToAsync(memoryStream, cancellationToken);

        if (memoryStream.Length == 0 || memoryStream.Length > 6_000_000)
        {
            return null;
        }

        var archivePath = $"{archivePathWithoutExtension}.{extension}";
        return new DownloadedBinaryAsset(
            archivePath,
            archivePath.Replace('\\', '/'),
            memoryStream.ToArray(),
            1600,
            1000);
    }

    private string BuildIndexHtml(
        NormalizedBusiness business,
        TemplateDefinition template,
        ThemeChoice theme,
        string motionStyle,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        ClientSiteConfig siteConfig,
        LocalizedWebsiteContent content,
        SeoContent seo,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        GeneratedLogoAsset logoAsset,
        string siteUrl,
        string structuredDataJson)
    {
        var galleryHtml = string.Join(Environment.NewLine, mediaAssets.Select((asset, index) =>
            $$"""
            <figure class="{{asset.CssClass}}" data-gallery-index="{{index}}">
              <img
                src="{{asset.WebPath}}"
                alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[Math.Min(index, content.GalleryCaptions.Count - 1)]}")}}"
                width="{{asset.Width}}"
                height="{{asset.Height}}"
                {{(index == 0 ? "fetchpriority=\"high\"" : "loading=\"lazy\"")}}
              />
              <figcaption>{{EscapeHtml(content.GalleryCaptions[Math.Min(index, content.GalleryCaptions.Count - 1)])}}</figcaption>
            </figure>
            """));

        var openingHoursHtml = business.OpeningHours.Count > 0
            ? string.Join(Environment.NewLine, business.OpeningHours.Select(hours =>
                $$"""<li>{{EscapeHtml(hours)}}</li>"""))
            : $$"""<li>{{EscapeHtml(content.Ui.NoHours)}}</li>""";

        var serviceCardsHtml = BuildServiceCardsHtml(content.Services);
        var highlightCardsHtml = BuildHighlightCardsHtml(content.Highlights);
        var faqHtml = BuildFaqHtml(content.Faq);
        var heroShowcaseHtml = BuildHeroShowcaseHtml(business, content, mediaAssets, template.Id);
        var socialLinksHtml = business.SocialLinks.Count > 0
            ? string.Join(Environment.NewLine, business.SocialLinks.Select(link =>
                $$"""<a href="{{EscapeHtmlAttribute(link.Value)}}" target="_blank" rel="noreferrer noopener">{{EscapeHtml(link.Key)}}</a>"""))
            : string.Empty;

        var heroVisualCss = mediaAssets.Count > 0
            ? $"url('{mediaAssets[0].WebPath}')"
            : "none";
        var themeCss = $$"""
        :root {
          --color-bg: {{theme.Background}};
          --color-surface: {{theme.Surface}};
          --color-surface-2: {{theme.SurfaceAlt}};
          --color-text: {{theme.TextColor}};
          --color-muted: {{theme.MutedText}};
          --color-border: {{theme.BorderColor}};
          --color-primary: {{theme.PrimaryColor}};
          --color-secondary: {{theme.SecondaryColor}};
          --color-accent: {{theme.AccentColor}};
          --color-button-text: {{theme.ButtonTextColor}};
          --radius-xl: {{theme.RadiusLarge}};
          --radius-lg: {{theme.RadiusMedium}};
          --radius-md: {{theme.RadiusSmall}};
          --section-gap: {{theme.SectionSpacing}};
          --hero-gradient: {{theme.HeroGradient}};
          --shadow-soft: {{theme.ShadowStyle}};
          --glow-color: {{theme.GlowColor}};
          --hero-visual: {{heroVisualCss}};
        }
        """;

        var siteConfigJson = JsonSerializer.Serialize(siteConfig, JsonOptions);
        var metadata = BuildMetaTags(content, seo, business, siteUrl, mediaAssets, logoAsset);

        if (string.Equals(template.Id, "restaurant-signature", StringComparison.OrdinalIgnoreCase))
        {
            return BuildRestaurantIndexHtml(
                business,
                template,
                theme,
                motionStyle,
                sectionOrder,
                hiddenSections,
                siteConfigJson,
                content,
                mediaAssets,
                logoAsset,
                metadata,
                structuredDataJson);
        }

        if (string.Equals(template.Id, "coffee-shop-signature", StringComparison.OrdinalIgnoreCase))
        {
            return BuildCoffeeIndexHtml(
                business,
                template,
                theme,
                motionStyle,
                sectionOrder,
                hiddenSections,
                siteConfigJson,
                content,
                mediaAssets,
                logoAsset,
                metadata,
                structuredDataJson);
        }

        var templateBody = BuildTemplateBody(
            template,
            business,
            content,
            sectionOrder,
            hiddenSections,
            heroShowcaseHtml,
            galleryHtml,
            serviceCardsHtml,
            highlightCardsHtml,
            faqHtml,
            openingHoursHtml,
            socialLinksHtml);
        var splashImage = mediaAssets[0].WebPath;

        return $$"""
        <!doctype html>
        <html lang="fr">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="theme-color" content="{{theme.PrimaryColor}}" />
          <title>{{EscapeHtml(contentBundleSafeMetaTitle(content, business))}}</title>
          {{metadata}}
          <link rel="preconnect" href="https://fonts.googleapis.com" />
          <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
          <link rel="stylesheet" href="{{theme.FontPair.StylesheetUri}}" />
          <link rel="icon" type="image/svg+xml" href="assets/icons/favicon.svg" />
          <link rel="manifest" href="manifest.json" />
          <link rel="stylesheet" href="assets/css/styles.css" />
          <style>{{themeCss}}</style>
          <script type="application/ld+json">{{structuredDataJson}}</script>
        </head>
        <body data-template="{{template.Id}}" data-motion="{{motionStyle}}">
          <section class="welcome-splash" id="welcomeSplash" aria-labelledby="welcomeSplashTitle">
            <div class="welcome-splash__visual" style="background-image:url('{{splashImage}}');"></div>
            <div class="welcome-splash__overlay"></div>
            <div class="welcome-splash__content">
              <span class="welcome-splash__eyebrow">{{EscapeHtml(content.HeroEyebrow)}}</span>
              <h2 id="welcomeSplashTitle">{{EscapeHtml(content.HeroTitle)}}</h2>
              <p>{{EscapeHtml(content.HeroSubtitle)}}</p>
              <div class="welcome-splash__actions">
                <button type="button" class="button button--primary" id="welcomeContinueButton">Continuer</button>
                <button type="button" class="button button--secondary" id="welcomeCloseButton">Fermer</button>
              </div>
            </div>
          </section>

          <div class="site-shell">
            <header class="site-header">
              <nav class="site-nav">
                <a class="brand" href="#top" aria-label="{{EscapeHtml(business.Name)}}">
                  <img class="brand__logo" src="{{logoAsset.WebPath}}" alt="{{EscapeHtmlAttribute(business.Name)}}" width="64" height="64" />
                  <span class="brand__text">
                    <strong>{{EscapeHtml(business.Name)}}</strong>
                    <small>{{EscapeHtml(business.Category)}}</small>
                  </span>
                </a>

                <button class="nav-toggle" id="navToggle" type="button" aria-expanded="false" aria-controls="navLinks">
                  <span></span>
                  <span></span>
                </button>

                <div class="nav-links" id="navLinks">
                  <a id="navAbout" href="#about">{{EscapeHtml(content.Ui.NavAbout)}}</a>
                  <a id="navServices" href="#services">{{EscapeHtml(content.Ui.NavServices)}}</a>
                  <a id="navGallery" href="#gallery">{{EscapeHtml(content.Ui.NavGallery)}}</a>
                  <a id="navReviews" href="#reviews">{{EscapeHtml(content.Ui.NavReviews)}}</a>
                  <a id="navContact" href="#contact">{{EscapeHtml(content.Ui.NavContact)}}</a>
                  <label class="language-switcher">
                    <span id="languageLabel">{{EscapeHtml(content.Ui.LanguageLabel)}}</span>
                    <select id="languageSwitcher" aria-label="{{EscapeHtmlAttribute(content.Ui.LanguageLabel)}}">
                      <option value="fr">Francais</option>
                      <option value="en">English</option>
                      <option value="ar">&#1575;&#1604;&#1593;&#1585;&#1576;&#1610;&#1577;</option>
                    </select>
                  </label>
                </div>
              </nav>
            </header>

            {{templateBody}}

            <footer class="site-footer">
              <div>
                <strong>{{EscapeHtml(business.Name)}}</strong>
                <p id="footerTagline">{{EscapeHtml(content.FooterTagline)}}</p>
              </div>

              <div class="footer-links">
                {{socialLinksHtml}}
              </div>
            </footer>
          </div>

          <div class="lightbox" id="lightbox" hidden>
            <button class="lightbox__close" type="button" id="lightboxClose" aria-label="Fermer la galerie">Fermer</button>
            <img id="lightboxImage" src="{{mediaAssets[0].WebPath}}" alt="" />
            <p id="lightboxCaption">{{EscapeHtml(content.GalleryCaptions[0])}}</p>
          </div>

          <script id="site-config" type="application/json">{{siteConfigJson}}</script>
          <script src="assets/js/app.js" defer></script>
        </body>
        </html>
        """;
    }

    private string BuildRestaurantIndexHtml(
        NormalizedBusiness business,
        TemplateDefinition template,
        ThemeChoice theme,
        string motionStyle,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string siteConfigJson,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        GeneratedLogoAsset logoAsset,
        string metadata,
        string structuredDataJson)
    {
        var heroImage = mediaAssets[0].WebPath;
        var restaurantThemeCss = BuildRestaurantThemeCss(theme);
        var sectionStackHtml = BuildRestaurantSectionStack(business, content, sectionOrder, hiddenSections, mediaAssets);
        var socialLinksHtml = BuildRestaurantSocialLinksHtml(business);
        var footerHoursHtml = business.OpeningHours.Count > 0
            ? string.Join("<br />", business.OpeningHours.Take(3).Select(EscapeHtml))
            : EscapeHtml(content.Ui.NoHours);
        var restaurantVariant = theme.FontPair.BodyName.Contains("Poppins", StringComparison.OrdinalIgnoreCase)
            ? "editorial"
            : "classic";

        return $$"""
        <!doctype html>
        <html lang="fr">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="theme-color" content="{{theme.PrimaryColor}}" />
          <title>{{EscapeHtml(contentBundleSafeMetaTitle(content, business))}}</title>
          {{metadata}}
          <link rel="preconnect" href="https://fonts.googleapis.com" />
          <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
          <link rel="stylesheet" href="{{theme.FontPair.StylesheetUri}}" />
          <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css" />
          <link rel="icon" type="image/svg+xml" href="assets/icons/favicon.svg" />
          <link rel="manifest" href="manifest.json" />
          <link rel="stylesheet" href="assets/css/styles.css" />
          <style>{{restaurantThemeCss}}</style>
          <script type="application/ld+json">{{structuredDataJson}}</script>
        </head>
        <body data-template="{{template.Id}}" data-motion="{{motionStyle}}" data-restaurant-variant="{{restaurantVariant}}">
          <section class="welcome-splash" id="welcomeSplash" aria-labelledby="welcomeSplashTitle">
            <div class="welcome-splash__visual" style="background-image:url('{{heroImage}}');"></div>
            <div class="welcome-splash__overlay"></div>
            <div class="welcome-splash__content">
              <span class="welcome-splash__eyebrow">{{EscapeHtml(content.HeroEyebrow)}}</span>
              <h2 id="welcomeSplashTitle">{{EscapeHtml(content.HeroTitle)}}</h2>
              <p>{{EscapeHtml(content.HeroSubtitle)}}</p>
              <div class="welcome-splash__actions">
                <button type="button" class="btn btn-gold" id="welcomeContinueButton">Continuer</button>
                <button type="button" class="btn btn-outline" id="welcomeCloseButton">Fermer</button>
              </div>
            </div>
          </section>

          <header id="navbar" class="navbar">
            <div class="container navbar-inner">
              <a href="#home" class="brand brand--restaurant" aria-label="{{EscapeHtmlAttribute(business.Name)}}">
                <img class="brand__logo" src="{{logoAsset.WebPath}}" alt="{{EscapeHtmlAttribute(business.Name)}}" width="64" height="64" />
                <span class="brand__text">
                  <strong>{{EscapeHtml(business.Name)}}</strong>
                  <small>{{EscapeHtml(business.Category)}}</small>
                </span>
              </a>

              <button id="navToggle" class="nav-toggle" type="button" aria-label="Toggle navigation menu" aria-expanded="false">
                <span></span><span></span><span></span>
              </button>

              <nav id="navMenu" class="nav-menu">
                <a id="navAbout" class="nav-link" href="#about">{{EscapeHtml(content.Ui.NavAbout)}}</a>
                <a id="navServices" class="nav-link" href="#services">{{EscapeHtml(content.Ui.NavServices)}}</a>
                <a id="navGallery" class="nav-link" href="#gallery">{{EscapeHtml(content.Ui.NavGallery)}}</a>
                <a id="navReviews" class="nav-link" href="#reviews">{{EscapeHtml(content.Ui.NavReviews)}}</a>
                <a id="navContact" class="nav-link" href="#contact">{{EscapeHtml(content.Ui.NavContact)}}</a>
                <label class="lang-pill">
                  <span id="languageLabel">{{EscapeHtml(content.Ui.LanguageLabel)}}</span>
                  <select id="languageSwitcher" aria-label="{{EscapeHtmlAttribute(content.Ui.LanguageLabel)}}">
                    <option value="fr">Francais</option>
                    <option value="en">English</option>
                    <option value="ar">&#1575;&#1604;&#1593;&#1585;&#1576;&#1610;&#1577;</option>
                  </select>
                </label>
              </nav>
            </div>
          </header>

          <main class="restaurant-site-main" id="top">
            <section id="home" class="hero">
              <div class="hero-bg hero-visual" style="background-image:url('{{heroImage}}');"></div>
              <div class="hero-overlay"></div>
              <div class="container hero-layout">
                <div class="hero-content">
                  <p class="hero-eyebrow" id="heroEyebrow">{{EscapeHtml(content.HeroEyebrow)}}</p>
                  <h1 class="hero-title" id="heroTitle">{{EscapeHtml(content.HeroTitle)}}</h1>
                  <p class="hero-subtitle" id="heroSubtitle">{{EscapeHtml(content.HeroSubtitle)}}</p>
                  <p class="hero-description" id="heroDescription">{{EscapeHtml(content.HeroDescription)}}</p>
                  <div class="hero-buttons">
                    <a href="#contact" class="btn btn-gold btn-pulse" id="heroPrimaryCta">{{EscapeHtml(content.PrimaryCta)}}</a>
                    <a href="#services" class="btn btn-outline" id="heroSecondaryCta">{{EscapeHtml(content.SecondaryCta)}}</a>
                    <a href="{{EscapeHtmlAttribute(business.GoogleMapsUri)}}" class="btn btn-outline" target="_blank" rel="noreferrer noopener">{{EscapeHtml(content.Ui.ViewOnMaps)}}</a>
                  </div>
                </div>

                <aside class="hero-aside">
                  <article class="hero-stat">
                    <span id="heroRatingLabel">{{EscapeHtml(content.Ui.RatingLabel)}}</span>
                    <strong>{{EscapeHtml(FormatRating(business))}}</strong>
                    <p>{{EscapeHtml(FormatReviewCount(business))}}</p>
                  </article>
                  <article class="hero-stat">
                    <span id="heroContactLabel">{{EscapeHtml(content.Ui.ContactBadge)}}</span>
                    <strong>{{EscapeHtml(PreferNullable(PreferNullable(business.PhoneNumber, business.PrimaryEmail), content.Ui.WhatsAppLabel) ?? content.Ui.WhatsAppLabel)}}</strong>
                    <p>{{EscapeHtml(PreferNullable(business.OpeningHours.FirstOrDefault(), content.ContactIntro) ?? content.ContactIntro)}}</p>
                  </article>
                  <article class="hero-stat">
                    <span id="heroAddressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</span>
                    <strong>{{EscapeHtml(ResolveHeadlineLocation(business.Address, business.Name))}}</strong>
                    <p>{{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</p>
                  </article>
                </aside>
              </div>
              <a href="#about" class="scroll-indicator" aria-label="Scroll down">
                <span></span>
              </a>
            </section>

            {{sectionStackHtml}}
          </main>

          <footer class="footer">
            <div class="container footer-grid">
              <div class="footer-col">
                <a href="#home" class="logo footer-logo">{{EscapeHtml(business.Name)}}</a>
                <p id="footerTagline">{{EscapeHtml(content.FooterTagline)}}</p>
                <div class="social-icons">
                  {{socialLinksHtml}}
                </div>
              </div>
              <div class="footer-col">
                <h4>Navigation</h4>
                <a href="#about">{{EscapeHtml(content.Ui.NavAbout)}}</a>
                <a href="#services">{{EscapeHtml(content.Ui.NavServices)}}</a>
                <a href="#gallery">{{EscapeHtml(content.Ui.NavGallery)}}</a>
                <a href="#reviews">{{EscapeHtml(content.Ui.NavReviews)}}</a>
                <a href="#contact">{{EscapeHtml(content.Ui.NavContact)}}</a>
              </div>
              <div class="footer-col">
                <h4>Contact</h4>
                <p><i class="fa-solid fa-location-dot"></i> {{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</p>
                {{(string.IsNullOrWhiteSpace(business.PhoneNumber) ? string.Empty : $$"""<p><i class="fa-solid fa-phone"></i> {{EscapeHtml(business.PhoneNumber)}}</p>""")}}
                {{(string.IsNullOrWhiteSpace(business.PrimaryEmail) ? string.Empty : $$"""<p><i class="fa-solid fa-envelope"></i> {{EscapeHtml(business.PrimaryEmail)}}</p>""")}}
              </div>
              <div class="footer-col">
                <h4>Horaires</h4>
                <p>{{footerHoursHtml}}</p>
                <p>{{EscapeHtml(FormatReviewCount(business))}}</p>
              </div>
            </div>
            <div class="footer-bottom">
              <p>&copy; <span id="year">{{DateTime.UtcNow.Year}}</span> {{EscapeHtml(business.Name)}}. Tous droits reserves.</p>
            </div>
          </footer>

          <button id="backToTop" aria-label="Back to top"><i class="fa-solid fa-arrow-up"></i></button>

          <div class="lightbox" id="lightbox" hidden>
            <button class="lightbox__close" type="button" id="lightboxClose" aria-label="Fermer la galerie">Fermer</button>
            <img id="lightboxImage" src="{{heroImage}}" alt="" />
            <p id="lightboxCaption">{{EscapeHtml(content.GalleryCaptions[0])}}</p>
          </div>

          <script id="site-config" type="application/json">{{siteConfigJson}}</script>
          <script src="assets/js/app.js" defer></script>
        </body>
        </html>
        """;
    }

    private string BuildRestaurantThemeCss(ThemeChoice theme)
    {
        var darkBackground = BlendHexColors(theme.SecondaryColor, "#060506", 0.78);
        var darkBackgroundAlt = BlendHexColors(theme.PrimaryColor, "#13100f", 0.72);
        var charcoal = BlendHexColors(theme.TextColor, "#1d1814", 0.4);
        var accentLight = BlendHexColors(theme.AccentColor, "#FFFFFF", 0.18);
        var strongShadow = $"0 24px 68px {ToRgba(BlendHexColors(theme.SecondaryColor, "#000000", 0.55), 0.34)}";

        return $$"""
        :root {
          --color-bg: {{darkBackground}};
          --color-bg-alt: {{darkBackgroundAlt}};
          --color-cream: {{theme.Surface}};
          --color-cream-alt: {{theme.SurfaceAlt}};
          --color-charcoal: {{charcoal}};
          --color-text-dark: {{theme.TextColor}};
          --color-text-muted: {{theme.MutedText}};
          --color-gold: {{theme.AccentColor}};
          --color-gold-light: {{accentLight}};
          --color-burgundy: {{theme.PrimaryColor}};
          --color-green: {{theme.SecondaryColor}};
          --color-border: {{theme.BorderColor}};
          --color-button-text: {{theme.ButtonTextColor}};
          --radius-xl: {{theme.RadiusLarge}};
          --radius-lg: {{theme.RadiusMedium}};
          --radius-md: {{theme.RadiusSmall}};
          --shadow-soft: {{theme.ShadowStyle}};
          --shadow-strong: {{strongShadow}};
          --glow-color: {{theme.GlowColor}};
          --hero-gradient: {{theme.HeroGradient}};
          --section-gap: {{theme.SectionSpacing}};
          --hero-overlay: linear-gradient(180deg, rgba(8, 7, 7, 0.46) 0%, rgba(8, 7, 7, 0.7) 55%, rgba(8, 7, 7, 0.92) 100%);
        }
        """;
    }

    private string BuildRestaurantSectionStack(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        var hidden = hiddenSections
            .Where(static sectionId => !string.IsNullOrWhiteSpace(sectionId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["about"] = BuildRestaurantAboutSection(business, content, mediaAssets),
            ["services"] = BuildRestaurantServicesSection(business, content, mediaAssets),
            ["gallery"] = BuildRestaurantGallerySection(business, content, mediaAssets),
            ["highlights"] = BuildRestaurantHighlightsSection(content),
            ["reviews"] = BuildRestaurantReviewsSection(business, content),
            ["contact"] = BuildRestaurantContactSection(business, content),
            ["faq"] = BuildRestaurantFaqSection(content)
        };

        var orderedSectionIds = NormalizeSectionOrder(sectionOrder, [], "restaurant-signature");

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedSectionIds
                .Where(sectionId => !hidden.Contains(sectionId))
                .Where(sections.ContainsKey)
                .Select(sectionId => sections[sectionId]));
    }

    private string BuildRestaurantAboutSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        var mainImage = mediaAssets.Count > 1 ? mediaAssets[1] : mediaAssets[0];
        var sideImage = mediaAssets.Count > 2 ? mediaAssets[2] : mediaAssets[0];
        var badgeValue = business.Rating is not null
            ? business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)
            : (business.ReviewCount is > 0 ? business.ReviewCount.Value.ToString(CultureInfo.InvariantCulture) : business.Category);
        var badgeLabel = business.Rating is not null
            ? "Google rating"
            : (business.ReviewCount is > 0 ? "Avis publies" : business.Category);
        var locationLine = ResolveHeadlineLocation(business.Address, business.Name);

        return $$"""
        <section id="about" class="about section">
          <div class="container about-grid">
            <div class="about-images">
              <img class="about-img-main" src="{{mainImage.WebPath}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[Math.Min(1, content.GalleryCaptions.Count - 1)]}")}}" width="{{mainImage.Width}}" height="{{mainImage.Height}}" loading="lazy" />
              <img class="about-img-small" src="{{sideImage.WebPath}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[Math.Min(2, content.GalleryCaptions.Count - 1)]}")}}" width="{{sideImage.Width}}" height="{{sideImage.Height}}" loading="lazy" />
              <div class="about-badge">
                <span>{{EscapeHtml(badgeValue)}}</span>
                <small>{{EscapeHtml(badgeLabel)}}</small>
              </div>
            </div>
            <div class="about-content">
              <p class="section-eyebrow" id="aboutEyebrow">{{EscapeHtml(content.AboutEyebrow)}}</p>
              <h2 class="section-title" id="aboutTitle">{{EscapeHtml(content.AboutTitle)}}</h2>
              <p class="section-text section-text--lead" id="aboutBody">{{EscapeHtml(content.AboutBody)}}</p>
              <div class="about-values">
                <div class="value-item">
                  <i class="fa-solid fa-location-dot"></i>
                  <div>
                    <h4>{{EscapeHtml(locationLine)}}</h4>
                    <p>{{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</p>
                  </div>
                </div>
                <div class="value-item">
                  <i class="fa-solid fa-star"></i>
                  <div>
                    <h4>{{EscapeHtml(FormatRating(business))}}</h4>
                    <p>{{EscapeHtml(FormatReviewCount(business))}}</p>
                  </div>
                </div>
                <div class="value-item">
                  <i class="fa-solid fa-phone-volume"></i>
                  <div>
                    <h4>{{EscapeHtml(PreferNullable(PreferNullable(business.PhoneNumber, business.PrimaryEmail), content.Ui.WhatsAppLabel) ?? content.Ui.WhatsAppLabel)}}</h4>
                    <p>{{EscapeHtml(PreferNullable(business.OpeningHours.FirstOrDefault(), content.ContactIntro) ?? content.ContactIntro)}}</p>
                  </div>
                </div>
              </div>
              <a href="#contact" class="btn btn-dark">{{EscapeHtml(content.ContactTitle)}} <i class="fa-solid fa-arrow-right"></i></a>
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantServicesSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return $$"""
        <section id="services" class="menu section">
          <div class="container">
            <div class="section-header center">
              <p class="section-eyebrow" id="servicesEyebrow">{{EscapeHtml(content.ServicesEyebrow)}}</p>
              <h2 class="section-title" id="servicesTitle">{{EscapeHtml(content.ServicesTitle)}}</h2>
              <p class="section-text" id="servicesIntro">{{EscapeHtml(content.ServicesIntro)}}</p>
            </div>
            <div class="menu-showcase-grid" id="servicesGrid" data-render-style="restaurant-menu">
              {{BuildRestaurantServiceCardsHtml(content.Services, mediaAssets, business.Name)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantGallerySection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return $$"""
        <section id="gallery" class="gallery section section-dark">
          <div class="container">
            <div class="section-header center">
              <p class="section-eyebrow" id="galleryEyebrow">{{EscapeHtml(content.GalleryEyebrow)}}</p>
              <h2 class="section-title light" id="galleryTitle">{{EscapeHtml(content.GalleryTitle)}}</h2>
              <p class="section-text light" id="galleryIntro">{{EscapeHtml(content.GalleryIntro)}}</p>
            </div>
            <div class="gallery-grid" id="galleryGrid">
              {{BuildRestaurantGalleryHtml(business, content, mediaAssets)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantHighlightsSection(LocalizedWebsiteContent content)
    {
        return $$"""
        <section id="highlights" class="features section">
          <div class="container">
            <div class="section-header center">
              <p class="section-eyebrow" id="highlightsEyebrow">{{EscapeHtml(content.HighlightsEyebrow)}}</p>
              <h2 class="section-title" id="highlightsTitle">{{EscapeHtml(content.HighlightsTitle)}}</h2>
            </div>
            <div class="features-grid" id="highlightsGrid">
              {{BuildHighlightCardsHtml(content.Highlights)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantReviewsSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        var viewReviewsHref = PreferNullable(business.ReviewsUri, business.GoogleMapsUri) ?? "#reviews";
        var writeReviewHref = PreferNullable(business.WriteAReviewUri, business.GoogleMapsUri) ?? "#reviews";

        return $$"""
        <section id="reviews" class="testimonials section section-dark">
          <div class="container">
            <div class="section-header center">
              <p class="section-eyebrow" id="reviewsEyebrow">{{EscapeHtml(content.ReviewsEyebrow)}}</p>
              <h2 class="section-title light" id="reviewsTitle">{{EscapeHtml(content.ReviewsTitle)}}</h2>
              <p class="section-text light" id="reviewsSummary">{{EscapeHtml(content.ReviewsSummary)}}</p>
            </div>

            <div class="reviews-overview">
              <div class="reviews-overview__score">
                <span id="reviewsBadge">{{EscapeHtml(content.Ui.ReviewBadge)}}</span>
                <strong>{{EscapeHtml(FormatRating(business))}}</strong>
                <div class="rating-stars" aria-hidden="true">
                  {{BuildRatingStars(business.Rating)}}
                </div>
                <small>{{EscapeHtml(FormatReviewCount(business))}}</small>
              </div>
              <div class="reviews-overview__actions">
                <a class="btn btn-outline" id="viewReviewsButton" href="{{EscapeHtmlAttribute(viewReviewsHref)}}" target="_blank" rel="noreferrer noopener">{{EscapeHtml(content.Ui.ViewReviews)}}</a>
                <a class="btn btn-gold" id="writeReviewButton" href="{{EscapeHtmlAttribute(writeReviewHref)}}" target="_blank" rel="noreferrer noopener">{{EscapeHtml(content.Ui.WriteReview)}}</a>
              </div>
            </div>

            <div class="testimonials-grid">
              {{BuildRestaurantReviewCardsHtml(business, content)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantContactSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        var phoneHref = string.IsNullOrWhiteSpace(business.PhoneNumber)
            ? null
            : $"tel:{EscapeHtmlAttribute(business.PhoneNumber)}";
        var whatsappDigits = new string((business.WhatsappNumber ?? business.PhoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var whatsappHref = string.IsNullOrWhiteSpace(whatsappDigits)
            ? null
            : $"https://wa.me/{whatsappDigits}";
        var mapHref = business.GoogleMapsUri;

        return $$"""
        <section id="contact" class="contact section">
          <div class="container">
            <div class="section-header center">
              <p class="section-eyebrow" id="contactEyebrow">{{EscapeHtml(content.ContactEyebrow)}}</p>
              <h2 class="section-title" id="contactTitle">{{EscapeHtml(content.ContactTitle)}}</h2>
              <p class="section-text" id="contactIntro">{{EscapeHtml(content.ContactIntro)}}</p>
            </div>

            <div class="contact-info-grid">
              {{BuildRestaurantContactCardsHtml(business, content, whatsappHref)}}
            </div>

            <div class="contact-cta">
              {{(phoneHref is null ? string.Empty : $$"""<a href="{{phoneHref}}" class="btn btn-dark" id="callNowLink"><i class="fa-solid fa-phone"></i> {{EscapeHtml(content.Ui.CallNow)}}</a>""")}}
              {{(whatsappHref is null ? string.Empty : $$"""<a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="_blank" rel="noopener" class="btn btn-whatsapp"><i class="fa-brands fa-whatsapp"></i> {{EscapeHtml(content.Ui.SendOnWhatsapp)}}</a>""")}}
              <a href="{{EscapeHtmlAttribute(mapHref)}}" class="btn btn-outline" target="_blank" rel="noopener">{{EscapeHtml(content.Ui.ViewOnMaps)}}</a>
            </div>

            <div class="contact-grid">
              <div class="contact-form-wrap">
                <span class="section-eyebrow section-eyebrow--compact" id="contactBadge">{{EscapeHtml(content.Ui.ContactBadge)}}</span>
                <h3 id="formTitle">{{EscapeHtml(content.FormTitle)}}</h3>
                <p class="form-subtext" id="formIntro">{{EscapeHtml(content.FormIntro)}}</p>
                <form id="whatsappForm">
                  <div class="form-row">
                    <div class="form-group">
                      <label for="contactName" id="formNameLabel">{{EscapeHtml(content.Ui.FormNameLabel)}}</label>
                      <input id="contactName" name="name" type="text" placeholder="{{EscapeHtmlAttribute(content.Ui.FormNamePlaceholder)}}" />
                    </div>
                    <div class="form-group">
                      <label for="contactPhone" id="formPhoneLabel">{{EscapeHtml(content.Ui.FormPhoneLabel)}}</label>
                      <input id="contactPhone" name="phone" type="tel" placeholder="{{EscapeHtmlAttribute(content.Ui.FormPhonePlaceholder)}}" />
                    </div>
                  </div>
                  <div class="form-group">
                    <label for="contactMessage" id="formMessageLabel">{{EscapeHtml(content.Ui.FormMessageLabel)}}</label>
                    <textarea id="contactMessage" name="message" rows="5" placeholder="{{EscapeHtmlAttribute(content.Ui.FormMessagePlaceholder)}}"></textarea>
                  </div>
                  <button type="submit" class="btn btn-gold btn-block" id="formSubmitButton">{{EscapeHtml(content.Ui.FormSubmitLabel)}}</button>
                </form>
              </div>

              <div class="contact-map-wrap">
                <div class="map-embed">
                  <iframe
                    src="{{EscapeHtmlAttribute(business.MapEmbedUri)}}"
                    width="100%"
                    height="100%"
                    style="border:0;"
                    allowfullscreen=""
                    loading="lazy"
                    referrerpolicy="no-referrer-when-downgrade"
                    title="{{EscapeHtmlAttribute($"{business.Name} location on Google Maps")}}">
                  </iframe>
                </div>
                <a href="{{EscapeHtmlAttribute(mapHref)}}" target="_blank" rel="noopener" class="btn btn-dark btn-block map-btn" id="openMapLink">
                  <i class="fa-solid fa-diamond-turn-right"></i> {{EscapeHtml(content.Ui.ViewOnMaps)}}
                </a>
              </div>
            </div>
          </div>
        </section>
        """;
    }

    private string BuildRestaurantFaqSection(LocalizedWebsiteContent content)
    {
        return $$"""
        <section id="faq" class="faq section">
          <div class="container faq-shell">
            <div class="section-header center">
              <p class="section-eyebrow" id="faqEyebrow">{{EscapeHtml(content.FaqEyebrow)}}</p>
              <h2 class="section-title" id="faqTitle">{{EscapeHtml(content.FaqTitle)}}</h2>
            </div>
            <div class="faq-list" id="faqList">
              {{BuildFaqHtml(content.Faq)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeIndexHtml(
        NormalizedBusiness business,
        TemplateDefinition template,
        ThemeChoice theme,
        string motionStyle,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string siteConfigJson,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        GeneratedLogoAsset logoAsset,
        string metadata,
        string structuredDataJson)
    {
        var heroImage = mediaAssets[0].WebPath;
        var coffeeThemeCss = BuildCoffeeThemeCss(theme);
        var sectionStackHtml = BuildCoffeeSectionStack(business, content, sectionOrder, hiddenSections, mediaAssets);
        var socialLinksHtml = BuildRestaurantSocialLinksHtml(business);
        var footerHoursHtml = business.OpeningHours.Count > 0
            ? string.Join("<br />", business.OpeningHours.Take(3).Select(EscapeHtml))
            : EscapeHtml(content.Ui.NoHours);
        var whatsappDigits = new string((business.WhatsappNumber ?? business.PhoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var whatsappHref = string.IsNullOrWhiteSpace(whatsappDigits)
            ? "#contact"
            : $"https://wa.me/{whatsappDigits}";
        var heroLocation = ResolveHeadlineLocation(business.Address, business.Name);
        var heroContactValue = PreferNullable(business.PhoneNumber, business.PrimaryEmail) ?? content.Ui.WhatsAppLabel;
        var coffeeVariant = theme.FontPair.DisplayName.Contains("Fraunces", StringComparison.OrdinalIgnoreCase)
            ? "artisan"
            : "modern";

        return $$"""
        <!doctype html>
        <html lang="fr">
        <head>
          <meta charset="utf-8" />
          <meta name="viewport" content="width=device-width, initial-scale=1" />
          <meta name="theme-color" content="{{theme.PrimaryColor}}" />
          <title>{{EscapeHtml(contentBundleSafeMetaTitle(content, business))}}</title>
          {{metadata}}
          <link rel="preconnect" href="https://fonts.googleapis.com" />
          <link rel="preconnect" href="https://fonts.gstatic.com" crossorigin />
          <link rel="stylesheet" href="{{theme.FontPair.StylesheetUri}}" />
          <link rel="stylesheet" href="https://cdnjs.cloudflare.com/ajax/libs/font-awesome/6.5.1/css/all.min.css" />
          <link rel="icon" type="image/svg+xml" href="assets/icons/favicon.svg" />
          <link rel="manifest" href="manifest.json" />
          <link rel="stylesheet" href="assets/css/styles.css" />
          <style>{{coffeeThemeCss}}</style>
          <script type="application/ld+json">{{structuredDataJson}}</script>
        </head>
        <body data-template="{{template.Id}}" data-motion="{{motionStyle}}" data-coffee-variant="{{coffeeVariant}}">
          <section class="welcome-splash welcome-splash--coffee" id="welcomeSplash" aria-labelledby="welcomeSplashTitle">
            <div class="welcome-splash__visual" style="background-image:url('{{heroImage}}');"></div>
            <div class="welcome-splash__overlay"></div>
            <div class="welcome-splash__content">
              <span class="welcome-splash__eyebrow">{{EscapeHtml(content.HeroEyebrow)}}</span>
              <h2 id="welcomeSplashTitle">{{EscapeHtml(content.HeroTitle)}}</h2>
              <p>{{EscapeHtml(content.HeroSubtitle)}}</p>
              <div class="welcome-splash__actions">
                <button type="button" class="btn btn-caramel" id="welcomeContinueButton">Continuer</button>
                <button type="button" class="btn btn-outline-brown" id="welcomeCloseButton">Fermer</button>
              </div>
            </div>
          </section>

          <div class="bean-field" aria-hidden="true"></div>

          <header id="navbar" class="navbar">
            <div class="navbar-pill">
              <a href="#home" class="logo brand--coffee" aria-label="{{EscapeHtmlAttribute(business.Name)}}">
                <img class="brand-mark" src="{{logoAsset.WebPath}}" alt="{{EscapeHtmlAttribute(business.Name)}}" width="52" height="52" />
                <span class="brand-copy">
                  <strong>{{EscapeHtml(business.Name)}}</strong>
                  <small>{{EscapeHtml(business.Category)}}</small>
                </span>
              </a>

              <nav id="navMenu" class="nav-menu">
                <a id="navAbout" class="nav-link" href="#about">{{EscapeHtml(content.Ui.NavAbout)}}</a>
                <a id="navServices" class="nav-link" href="#menu">{{EscapeHtml(content.Ui.NavServices)}}</a>
                <a id="navGallery" class="nav-link" href="#gallery">{{EscapeHtml(content.Ui.NavGallery)}}</a>
                <a id="navReviews" class="nav-link" href="#reviews">{{EscapeHtml(content.Ui.NavReviews)}}</a>
                <a id="navContact" class="nav-link" href="#contact">{{EscapeHtml(content.Ui.NavContact)}}</a>
                <label class="lang-switch">
                  <span id="languageLabel">{{EscapeHtml(content.Ui.LanguageLabel)}}</span>
                  <select id="languageSwitcher" aria-label="{{EscapeHtmlAttribute(content.Ui.LanguageLabel)}}">
                    <option value="fr">Francais</option>
                    <option value="en">English</option>
                    <option value="ar">&#1575;&#1604;&#1593;&#1585;&#1576;&#1610;&#1577;</option>
                  </select>
                </label>
              </nav>

              <a href="#contact" class="btn btn-caramel btn-small nav-cta">{{EscapeHtml(content.ContactTitle)}}</a>
              <button id="navToggle" class="nav-toggle" aria-label="Toggle navigation menu" aria-expanded="false" type="button">
                <span></span><span></span><span></span>
              </button>
            </div>
          </header>

          <main id="top">
            <section id="home" class="hero">
              <div class="hero-blob hero-blob-1" aria-hidden="true"></div>
              <div class="hero-blob hero-blob-2" aria-hidden="true"></div>
              <div class="hero-inner container">
                <div class="hero-text" data-reveal="left">
                  <p class="hero-eyebrow" id="heroEyebrow"><span class="dot"></span> {{EscapeHtml(content.HeroEyebrow)}}</p>
                  <h1 class="hero-title" id="heroTitle">{{EscapeHtml(content.HeroTitle)}}</h1>
                  <p class="hero-subtitle" id="heroSubtitle">{{EscapeHtml(content.HeroSubtitle)}}</p>
                  <p class="hero-desc" id="heroDescription">{{EscapeHtml(content.HeroDescription)}}</p>
                  <div class="hero-buttons">
                    <a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="{{(whatsappHref.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ? "_blank" : "_self")}}" rel="noopener noreferrer" class="btn btn-espresso" id="heroPrimaryCta"><i class="fa-brands fa-whatsapp"></i> {{EscapeHtml(content.PrimaryCta)}}</a>
                    <a href="#menu" class="btn btn-outline-brown" id="heroSecondaryCta"><i class="fa-solid fa-mug-saucer"></i> {{EscapeHtml(content.SecondaryCta)}}</a>
                  </div>
                  <div class="hero-stats">
                    <div class="hero-stat">
                      <strong>{{EscapeHtml(FormatRating(business))}}</strong>
                      <span id="heroRatingLabel">{{EscapeHtml(content.Ui.RatingLabel)}}</span>
                      <small class="hero-stat-meta">{{EscapeHtml(FormatReviewCount(business))}}</small>
                    </div>
                    <div class="hero-stat">
                      <strong>{{EscapeHtml(heroLocation)}}</strong>
                      <span id="heroAddressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</span>
                    </div>
                    <div class="hero-stat">
                      <strong>{{EscapeHtml(heroContactValue)}}</strong>
                      <span id="heroContactLabel">{{EscapeHtml(content.Ui.ContactBadge)}}</span>
                    </div>
                  </div>
                </div>

                <div class="hero-visual" data-reveal="right">
                  <div class="hero-image-frame">
                    <img src="{{heroImage}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[0]}")}}" width="{{mediaAssets[0].Width}}" height="{{mediaAssets[0].Height}}" fetchpriority="high" />
                    <span class="steam s1"></span>
                    <span class="steam s2"></span>
                    <span class="steam s3"></span>
                  </div>
                  <div class="hero-badge">
                    <svg viewBox="0 0 100 100" class="badge-ring">
                      <path id="badgeCircle" d="M 50,50 m -38,0 a 38,38 0 1,1 76,0 a 38,38 0 1,1 -76,0" fill="none"></path>
                      <text font-size="8" letter-spacing="2">
                        <textPath href="#badgeCircle">{{EscapeHtml(business.Name.ToUpperInvariant())}} • {{EscapeHtml(business.Category.ToUpperInvariant())}} • {{EscapeHtml(ResolveHeadlineLocation(business.Address, business.Category).ToUpperInvariant())}} • </textPath>
                      </text>
                    </svg>
                    <span class="badge-center"><i class="fa-solid fa-mug-hot"></i></span>
                  </div>
                </div>
              </div>
              <a href="#about" class="scroll-cue" aria-label="Scroll down"><span></span> Scroll</a>
            </section>

            {{sectionStackHtml}}
          </main>

          <footer class="footer">
            <div class="footer-wave" aria-hidden="true">
              <svg viewBox="0 0 1200 60" preserveAspectRatio="none"><path d="M0,30 C300,60 900,0 1200,30 L1200,60 L0,60 Z"></path></svg>
            </div>
            <div class="container footer-grid">
              <div class="footer-col">
                <a href="#home" class="logo footer-logo">
                  <img class="brand-mark brand-mark--small" src="{{logoAsset.WebPath}}" alt="{{EscapeHtmlAttribute(business.Name)}}" width="40" height="40" />
                  <span class="brand-copy">
                    <strong>{{EscapeHtml(business.Name)}}</strong>
                    <small>{{EscapeHtml(business.Category)}}</small>
                  </span>
                </a>
                <p id="footerTagline">{{EscapeHtml(content.FooterTagline)}}</p>
                <div class="social-icons">
                  {{socialLinksHtml}}
                </div>
              </div>
              <div class="footer-col">
                <h4>Navigation</h4>
                <a href="#about">{{EscapeHtml(content.Ui.NavAbout)}}</a>
                <a href="#menu">{{EscapeHtml(content.Ui.NavServices)}}</a>
                <a href="#gallery">{{EscapeHtml(content.Ui.NavGallery)}}</a>
                <a href="#reviews">{{EscapeHtml(content.Ui.NavReviews)}}</a>
                <a href="#contact">{{EscapeHtml(content.Ui.NavContact)}}</a>
              </div>
              <div class="footer-col">
                <h4>Contact</h4>
                <p><i class="fa-solid fa-location-dot"></i> {{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</p>
                {{(string.IsNullOrWhiteSpace(business.PhoneNumber) ? string.Empty : $$"""<p><i class="fa-solid fa-phone"></i> {{EscapeHtml(business.PhoneNumber)}}</p>""")}}
                {{(string.IsNullOrWhiteSpace(business.PrimaryEmail) ? string.Empty : $$"""<p><i class="fa-solid fa-envelope"></i> {{EscapeHtml(business.PrimaryEmail)}}</p>""")}}
              </div>
              <div class="footer-col">
                <h4>Horaires</h4>
                <p>{{footerHoursHtml}}</p>
              </div>
            </div>
            <div class="footer-bottom">
              <p>&copy; <span id="year">{{DateTime.UtcNow.Year}}</span> {{EscapeHtml(business.Name)}}. Tous droits reserves.</p>
            </div>
          </footer>

          {{(whatsappHref == "#contact" ? string.Empty : $$"""
          <a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="_blank" rel="noopener" id="whatsappFab" class="whatsapp-fab" aria-label="Chat on WhatsApp">
            <i class="fa-brands fa-whatsapp"></i>
          </a>
          """)}}

          <button id="backToTop" aria-label="Back to top"><i class="fa-solid fa-arrow-up"></i></button>

          <div class="lightbox" id="lightbox" hidden>
            <button class="lightbox__close" type="button" id="lightboxClose" aria-label="Fermer la galerie">Fermer</button>
            <img id="lightboxImage" src="{{heroImage}}" alt="" />
            <p id="lightboxCaption">{{EscapeHtml(content.GalleryCaptions[0])}}</p>
          </div>

          <script id="site-config" type="application/json">{{siteConfigJson}}</script>
          <script src="assets/js/app.js" defer></script>
        </body>
        </html>
        """;
    }

    private string BuildCoffeeThemeCss(ThemeChoice theme)
    {
        var espresso = BlendHexColors(theme.PrimaryColor, "#3c2415", 0.62);
        var darkChocolate = BlendHexColors(theme.SecondaryColor, "#241209", 0.74);
        var caramel = theme.PrimaryColor;
        var caramelLight = BlendHexColors(theme.AccentColor, "#f2ca90", 0.34);
        var cream = theme.Surface;
        var beige = theme.SurfaceAlt;
        var gold = theme.AccentColor;
        var textDark = theme.TextColor;
        var shadowSoft = $"0 12px 34px {ToRgba(BlendHexColors(theme.SecondaryColor, "#1e120b", 0.6), 0.14)}";
        var shadowStrong = $"0 24px 60px {ToRgba(BlendHexColors(theme.SecondaryColor, "#120804", 0.72), 0.28)}";

        return $$"""
        :root {
          --espresso: {{espresso}};
          --dark-chocolate: {{darkChocolate}};
          --caramel: {{caramel}};
          --caramel-light: {{caramelLight}};
          --cream: {{cream}};
          --beige: {{beige}};
          --gold: {{gold}};
          --text-dark: {{textDark}};
          --text-muted: {{theme.MutedText}};
          --shadow-soft: {{shadowSoft}};
          --shadow-strong: {{shadowStrong}};
          --border-color: {{theme.BorderColor}};
          --glow-color: {{theme.GlowColor}};
          --hero-gradient: {{theme.HeroGradient}};
          --section-gap: {{theme.SectionSpacing}};
          --radius-xl: {{theme.RadiusLarge}};
          --radius-lg: {{theme.RadiusMedium}};
          --radius-md: {{theme.RadiusSmall}};
        }
        """;
    }

    private string BuildCoffeeSectionStack(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        var hidden = hiddenSections
            .Where(static sectionId => !string.IsNullOrWhiteSpace(sectionId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["about"] = BuildCoffeeAboutSection(business, content, mediaAssets),
            ["highlights"] = BuildCoffeeProcessSection(content),
            ["services"] = BuildCoffeeMenuSection(business, content, mediaAssets),
            ["gallery"] = BuildCoffeeGallerySection(business, content, mediaAssets),
            ["reviews"] = BuildCoffeeReviewsSection(business, content),
            ["contact"] = BuildCoffeeContactSection(business, content),
            ["faq"] = BuildCoffeeFaqSection(content)
        };

        var orderedSectionIds = NormalizeSectionOrder(sectionOrder, [], "coffee-shop-signature");

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedSectionIds
                .Where(sectionId => !hidden.Contains(sectionId))
                .Where(sections.ContainsKey)
                .Select(sectionId => sections[sectionId]));
    }

    private string BuildCoffeeAboutSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        var firstImage = mediaAssets.Count > 1 ? mediaAssets[1] : mediaAssets[0];
        var secondImage = mediaAssets.Count > 2 ? mediaAssets[2] : mediaAssets[0];
        var secondaryStory = BuildCoffeeSecondaryStoryText(business, content);

        return $$"""
        <section id="about" class="about section">
          <div class="container">
            <div class="section-header" data-reveal="up">
              <p class="section-eyebrow" id="aboutEyebrow">{{EscapeHtml(content.AboutEyebrow)}}</p>
              <h2 class="section-title" id="aboutTitle">{{EscapeHtml(content.AboutTitle)}}</h2>
            </div>

            <div class="zigzag-row" data-reveal="up">
              <div class="zigzag-img">
                <img src="{{firstImage.WebPath}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[Math.Min(1, content.GalleryCaptions.Count - 1)]}")}}" width="{{firstImage.Width}}" height="{{firstImage.Height}}" loading="lazy" />
              </div>
              <div class="zigzag-text">
                <span class="zigzag-number">01</span>
                <h3>{{EscapeHtml(content.AboutTitle)}}</h3>
                <p id="aboutBody">{{EscapeHtml(content.AboutBody)}}</p>
              </div>
            </div>

            <div class="zigzag-row reverse" data-reveal="up">
              <div class="zigzag-img">
                <img src="{{secondImage.WebPath}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {content.GalleryCaptions[Math.Min(2, content.GalleryCaptions.Count - 1)]}")}}" width="{{secondImage.Width}}" height="{{secondImage.Height}}" loading="lazy" />
              </div>
              <div class="zigzag-text">
                <span class="zigzag-number">02</span>
                <h3>{{EscapeHtml(content.HighlightsTitle)}}</h3>
                <p>{{EscapeHtml(secondaryStory)}}</p>
              </div>
            </div>

            <div class="values-strip" data-reveal="up">
              {{BuildCoffeeValuePillsHtml(business)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeProcessSection(LocalizedWebsiteContent content)
    {
        return $$"""
        <section id="process" class="process section section-dark">
          <div class="container">
            <div class="section-header center" data-reveal="up">
              <p class="section-eyebrow" id="highlightsEyebrow">{{EscapeHtml(content.HighlightsEyebrow)}}</p>
              <h2 class="section-title light" id="highlightsTitle">{{EscapeHtml(content.HighlightsTitle)}}</h2>
            </div>
            <div class="process-track" id="highlightsGrid" data-render-style="coffee-process">
              {{BuildCoffeeProcessStepsHtml(content.Highlights)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeMenuSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return $$"""
        <section id="menu" class="menu section">
          <div class="container">
            <div class="section-header center" data-reveal="up">
              <p class="section-eyebrow" id="servicesEyebrow">{{EscapeHtml(content.ServicesEyebrow)}}</p>
              <h2 class="section-title" id="servicesTitle">{{EscapeHtml(content.ServicesTitle)}}</h2>
              <p class="section-text" id="servicesIntro">{{EscapeHtml(content.ServicesIntro)}}</p>
            </div>

            <div class="menu-filters" id="menuFilters" data-reveal="up">
              {{BuildCoffeeFilterChipsHtml()}}
            </div>

            <div class="menu-grid" id="servicesGrid" data-render-style="coffee-menu">
              {{BuildCoffeeMenuCardsHtml(content.Services, mediaAssets, business.Name)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeGallerySection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return $$"""
        <section id="gallery" class="gallery section section-beige">
          <div class="container">
            <div class="section-header center" data-reveal="up">
              <p class="section-eyebrow" id="galleryEyebrow">{{EscapeHtml(content.GalleryEyebrow)}}</p>
              <h2 class="section-title" id="galleryTitle">{{EscapeHtml(content.GalleryTitle)}}</h2>
              <p class="section-text" id="galleryIntro">{{EscapeHtml(content.GalleryIntro)}}</p>
            </div>
          </div>
          <div class="drag-gallery" id="dragGallery">
            <div class="drag-track" id="galleryGrid">
              {{BuildCoffeeGalleryHtml(business, content, mediaAssets)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeReviewsSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        var viewReviewsHref = PreferNullable(business.ReviewsUri, business.GoogleMapsUri) ?? "#reviews";
        var writeReviewHref = PreferNullable(business.WriteAReviewUri, business.GoogleMapsUri) ?? "#reviews";

        return $$"""
        <section id="reviews" class="reviews section">
          <div class="container">
            <div class="section-header center" data-reveal="up">
              <p class="section-eyebrow" id="reviewsEyebrow">{{EscapeHtml(content.ReviewsEyebrow)}}</p>
              <h2 class="section-title" id="reviewsTitle">{{EscapeHtml(content.ReviewsTitle)}}</h2>
              <p class="section-text" id="reviewsSummary">{{EscapeHtml(content.ReviewsSummary)}}</p>
            </div>

            <div class="reviews-overview-coffee" data-reveal="up">
              <div class="reviews-overview-coffee__score">
                <span id="reviewsBadge">{{EscapeHtml(content.Ui.ReviewBadge)}}</span>
                <strong>{{EscapeHtml(FormatRating(business))}}</strong>
                <small>{{EscapeHtml(FormatReviewCount(business))}}</small>
              </div>
              <div class="reviews-overview-coffee__actions">
                <a class="btn btn-outline-brown" id="viewReviewsButton" href="{{EscapeHtmlAttribute(viewReviewsHref)}}" target="_blank" rel="noreferrer noopener">{{EscapeHtml(content.Ui.ViewReviews)}}</a>
                <a class="btn btn-caramel" id="writeReviewButton" href="{{EscapeHtmlAttribute(writeReviewHref)}}" target="_blank" rel="noreferrer noopener">{{EscapeHtml(content.Ui.WriteReview)}}</a>
              </div>
            </div>

            <div class="quote-carousel" data-reveal="up">
              <i class="fa-solid fa-quote-left quote-mark"></i>
              <div class="quote-track" id="quoteTrack">
                {{BuildCoffeeQuoteSlidesHtml(business, content)}}
              </div>
              <div class="quote-controls">
                <button id="quotePrev" aria-label="Previous review" type="button"><i class="fa-solid fa-arrow-left"></i></button>
                <div class="quote-dots" id="quoteDots"></div>
                <button id="quoteNext" aria-label="Next review" type="button"><i class="fa-solid fa-arrow-right"></i></button>
              </div>
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeContactSection(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        var phoneHref = string.IsNullOrWhiteSpace(business.PhoneNumber)
            ? null
            : $"tel:{EscapeHtmlAttribute(business.PhoneNumber)}";
        var whatsappDigits = new string((business.WhatsappNumber ?? business.PhoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        var whatsappHref = string.IsNullOrWhiteSpace(whatsappDigits)
            ? null
            : $"https://wa.me/{whatsappDigits}";
        var mapHref = business.GoogleMapsUri;

        return $$"""
        <section id="contact" class="contact">
          <div class="contact-split">
            <div class="contact-panel contact-panel-dark" data-reveal="left">
              <p class="section-eyebrow" id="contactEyebrow">{{EscapeHtml(content.ContactEyebrow)}}</p>
              <h2 class="section-title light" id="contactTitle">{{EscapeHtml(content.ContactTitle)}}</h2>
              <p class="section-text light" id="contactIntro">{{EscapeHtml(content.ContactIntro)}}</p>

              <ul class="contact-info-list">
                {{BuildCoffeeContactInfoItemsHtml(business, content, whatsappHref)}}
              </ul>

              <div class="contact-cta">
                {{(phoneHref is null ? string.Empty : $$"""<a href="{{phoneHref}}" class="btn btn-caramel" id="callNowLink"><i class="fa-solid fa-phone"></i> {{EscapeHtml(content.Ui.CallNow)}}</a>""")}}
                {{(whatsappHref is null ? string.Empty : $$"""<a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="_blank" rel="noopener" class="btn btn-whatsapp-dark"><i class="fa-brands fa-whatsapp"></i> {{EscapeHtml(content.Ui.SendOnWhatsapp)}}</a>""")}}
              </div>

              <div class="map-embed">
                <iframe
                  src="{{EscapeHtmlAttribute(business.MapEmbedUri)}}"
                  width="100%"
                  height="100%"
                  style="border:0;"
                  allowfullscreen=""
                  loading="lazy"
                  referrerpolicy="no-referrer-when-downgrade"
                  title="{{EscapeHtmlAttribute($"{business.Name} location on Google Maps")}}">
                </iframe>
              </div>
              <a href="{{EscapeHtmlAttribute(mapHref)}}" target="_blank" rel="noopener" class="btn btn-outline-cream btn-block" id="openMapLink">
                <i class="fa-solid fa-diamond-turn-right"></i> {{EscapeHtml(content.Ui.ViewOnMaps)}}
              </a>
            </div>

            <div class="contact-panel contact-panel-cream" data-reveal="right">
              <span class="section-eyebrow section-eyebrow--compact" id="contactBadge">{{EscapeHtml(content.Ui.ContactBadge)}}</span>
              <h3 id="formTitle">{{EscapeHtml(content.FormTitle)}}</h3>
              <p class="form-subtext" id="formIntro">{{EscapeHtml(content.FormIntro)}}</p>
              <form id="whatsappForm">
                <div class="form-group">
                  <label for="contactName" id="formNameLabel">{{EscapeHtml(content.Ui.FormNameLabel)}}</label>
                  <input id="contactName" name="name" type="text" placeholder="{{EscapeHtmlAttribute(content.Ui.FormNamePlaceholder)}}" />
                </div>
                <div class="form-group">
                  <label for="contactPhone" id="formPhoneLabel">{{EscapeHtml(content.Ui.FormPhoneLabel)}}</label>
                  <input id="contactPhone" name="phone" type="tel" placeholder="{{EscapeHtmlAttribute(content.Ui.FormPhonePlaceholder)}}" />
                </div>
                <div class="form-group">
                  <label for="contactMessage" id="formMessageLabel">{{EscapeHtml(content.Ui.FormMessageLabel)}}</label>
                  <textarea id="contactMessage" name="message" rows="5" placeholder="{{EscapeHtmlAttribute(content.Ui.FormMessagePlaceholder)}}"></textarea>
                </div>
                <button type="submit" class="btn btn-espresso btn-block" id="formSubmitButton">{{EscapeHtml(content.Ui.FormSubmitLabel)}} <i class="fa-solid fa-paper-plane"></i></button>
              </form>
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeFaqSection(LocalizedWebsiteContent content)
    {
        return $$"""
        <section id="faq" class="faq section">
          <div class="container faq-shell">
            <div class="section-header center" data-reveal="up">
              <p class="section-eyebrow" id="faqEyebrow">{{EscapeHtml(content.FaqEyebrow)}}</p>
              <h2 class="section-title" id="faqTitle">{{EscapeHtml(content.FaqTitle)}}</h2>
            </div>
            <div class="faq-list" id="faqList">
              {{BuildFaqHtml(content.Faq)}}
            </div>
          </div>
        </section>
        """;
    }

    private string BuildCoffeeMenuCardsHtml(
        IReadOnlyList<WebsiteCard> cards,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        string businessName)
    {
        if (cards.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, cards.Take(8).Select((card, index) =>
        {
            var image = mediaAssets[(index + 1) % mediaAssets.Count];
            var category = ResolveCoffeeMenuCategory(card, index);
            return $$"""
            <article class="menu-card" data-category="{{category}}" data-reveal="up" data-reveal-delay="{{index * 90}}">
              <div class="menu-card-img">
                <img src="{{image.WebPath}}" alt="{{EscapeHtmlAttribute($"{businessName} - {card.Title}")}}" width="{{image.Width}}" height="{{image.Height}}" loading="lazy" />
              </div>
              <span class="menu-price">{{(index + 1).ToString("00", CultureInfo.InvariantCulture)}}</span>
              <h4>{{EscapeHtml(card.Title)}}</h4>
              <p>{{EscapeHtml(card.Description)}}</p>
            </article>
            """;
        }));
    }

    private string BuildCoffeeProcessStepsHtml(IReadOnlyList<WebsiteCard> cards)
    {
        var steps = cards.Count > 0
            ? cards.Take(4).ToList()
            : new List<WebsiteCard>
            {
                new("Selection", "Une approche attentive pour garantir une offre claire, qualitative et coherente."),
                new("Preparation", "Chaque commande est pensee pour etre fluide, agreable et facile a savourer."),
                new("Service", "Une experience accueillante, rapide et bien presentee a chaque visite."),
                new("Fidelite", "Un lieu que l'on recommande volontiers pour son ambiance et sa regularite.")
            };

        return string.Join(Environment.NewLine, steps.Select((card, index) => $$"""
        <article class="process-step" data-reveal="up" data-reveal-delay="{{index * 90}}">
          <div class="process-icon"><i class="{{ResolveCoffeeProcessIconClass(card, index)}}"></i></div>
          <h4>{{(index + 1).ToString("00", CultureInfo.InvariantCulture)}}. {{EscapeHtml(card.Title)}}</h4>
          <p>{{EscapeHtml(card.Description)}}</p>
        </article>
        """));
    }

    private string BuildCoffeeGalleryHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return string.Join(Environment.NewLine, mediaAssets.Select((asset, index) =>
        {
            var caption = content.GalleryCaptions[Math.Min(index, content.GalleryCaptions.Count - 1)];
            var tiltClass = index % 2 == 0 ? "tilt-1" : "tilt-2";
            return $$"""
            <figure class="polaroid {{tiltClass}}" data-gallery-index="{{index}}">
              <img src="{{asset.WebPath}}" alt="{{EscapeHtmlAttribute($"{business.Name} - {caption}")}}" width="{{asset.Width}}" height="{{asset.Height}}" {{(index == 0 ? "fetchpriority=\"high\"" : "loading=\"lazy\"")}} />
              <figcaption>{{EscapeHtml(caption)}}</figcaption>
            </figure>
            """;
        }));
    }

    private string BuildCoffeeQuoteSlidesHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        if (business.ReviewHighlights.Count == 0)
        {
            return $$"""
            <div class="quote-slide active">
              <p>Consultez les avis authentiques publiés par les clients directement sur la fiche Google Maps.</p>
              <div class="quote-author">
                <span class="quote-avatar">G</span>
                <div><h5>Avis vérifiés</h5><span>Source Google Maps</span></div>
              </div>
            </div>
            """;
        }

        return string.Join(Environment.NewLine, business.ReviewHighlights.Take(3).Select((review, index) =>
        {
            var publishTime = PreferNullable(review.RelativePublishTimeDescription, "Google review") ?? "Google review";
            return $$"""
            <div class="quote-slide {{(index == 0 ? "active" : string.Empty)}}">
              <p>{{EscapeHtml(TrimToLengthSafe(review.Text, 240))}}</p>
              <div class="quote-author">
                <span class="quote-avatar">{{EscapeHtml(BuildInitials(review.AuthorName))}}</span>
                <div><h5>{{EscapeHtml(review.AuthorName)}}</h5><span>{{EscapeHtml(publishTime)}}</span></div>
              </div>
            </div>
            """;
        }));
    }

    private string BuildCoffeeValuePillsHtml(NormalizedBusiness business)
    {
        var values = business.Features.Count > 0
            ? business.Features.Take(4).ToList()
            : business.Services.Take(4).ToList();

        if (values.Count == 0)
        {
            values = ["Service soigne", "Ambiance locale", "Pause gourmande", "Adresse de quartier"];
        }

        return string.Join(Environment.NewLine, values.Select((value, index) => $$"""
        <div class="value-pill">
          <i class="{{ResolveCoffeeValueIconClass(value, index)}}"></i> {{EscapeHtml(value)}}
        </div>
        """));
    }

    private string BuildCoffeeFilterChipsHtml()
    {
        return """
        <button class="filter-chip active" id="menuFilterAll" data-filter="all" type="button">Tout</button>
        <button class="filter-chip" id="menuFilterCoffee" data-filter="coffee" type="button">Boissons</button>
        <button class="filter-chip" id="menuFilterPastry" data-filter="pastry" type="button">Gourmand</button>
        <button class="filter-chip" id="menuFilterExperience" data-filter="experience" type="button">Experience</button>
        """;
    }

    private string BuildCoffeeContactInfoItemsHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        string? whatsappHref)
    {
        var items = new List<string>();

        if (!string.IsNullOrWhiteSpace(business.PhoneNumber))
        {
            items.Add($$"""
            <li>
              <i class="fa-solid fa-phone"></i>
              <div><span id="phoneLabel">{{EscapeHtml(content.Ui.PhoneLabel)}}</span><a href="tel:{{EscapeHtmlAttribute(business.PhoneNumber)}}">{{EscapeHtml(business.PhoneNumber)}}</a></div>
            </li>
            """);
        }

        if (!string.IsNullOrWhiteSpace(whatsappHref))
        {
            var whatsappText = PreferNullable(business.PhoneNumber, content.Ui.WhatsAppLabel) ?? content.Ui.WhatsAppLabel;
            items.Add($$"""
            <li>
              <i class="fa-brands fa-whatsapp"></i>
              <div><span>{{EscapeHtml(content.Ui.WhatsAppLabel)}}</span><a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="_blank" rel="noopener">{{EscapeHtml(whatsappText)}}</a></div>
            </li>
            """);
        }

        if (!string.IsNullOrWhiteSpace(business.PrimaryEmail))
        {
            items.Add($$"""
            <li>
              <i class="fa-solid fa-envelope"></i>
              <div><span id="emailLabel">{{EscapeHtml(content.Ui.EmailLabel)}}</span><a href="mailto:{{EscapeHtmlAttribute(business.PrimaryEmail)}}">{{EscapeHtml(business.PrimaryEmail)}}</a></div>
            </li>
            """);
        }

        items.Add($$"""
        <li>
          <i class="fa-solid fa-clock"></i>
          <div><span id="hoursLabel">{{EscapeHtml(content.Ui.HoursLabel)}}</span><span>{{string.Join("<br>", business.OpeningHours.Take(3).Select(EscapeHtml).DefaultIfEmpty(EscapeHtml(content.Ui.NoHours)))}}</span></div>
        </li>
        """);

        items.Add($$"""
        <li>
          <i class="fa-solid fa-location-dot"></i>
          <div><span id="addressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</span><span>{{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</span></div>
        </li>
        """);

        return string.Join(Environment.NewLine, items);
    }

    private string BuildCoffeeSecondaryStoryText(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        var serviceLine = business.Services.Count > 0
            ? string.Join(", ", business.Services.Take(3))
            : content.ServicesIntro;
        var featureLine = business.Features.Count > 0
            ? string.Join(", ", business.Features.Take(3))
            : content.ReviewsSummary;
        return $"{serviceLine}. {featureLine}";
    }

    private static string ResolveCoffeeMenuCategory(WebsiteCard card, int index)
    {
        var value = $"{card.Title} {card.Description}".ToLowerInvariant();
        if (value.Contains("croissant", StringComparison.Ordinal) ||
            value.Contains("pastry", StringComparison.Ordinal) ||
            value.Contains("dessert", StringComparison.Ordinal) ||
            value.Contains("cookie", StringComparison.Ordinal) ||
            value.Contains("viennois", StringComparison.Ordinal) ||
            value.Contains("gourmand", StringComparison.Ordinal))
        {
            return "pastry";
        }

        if (value.Contains("space", StringComparison.Ordinal) ||
            value.Contains("cowork", StringComparison.Ordinal) ||
            value.Contains("event", StringComparison.Ordinal) ||
            value.Contains("group", StringComparison.Ordinal) ||
            value.Contains("ambiance", StringComparison.Ordinal) ||
            value.Contains("experience", StringComparison.Ordinal))
        {
            return "experience";
        }

        return index % 3 == 2
            ? "experience"
            : "coffee";
    }

    private static string ResolveCoffeeProcessIconClass(WebsiteCard card, int index)
    {
        var value = $"{card.Title} {card.Description}".ToLowerInvariant();
        if (value.Contains("source", StringComparison.Ordinal) ||
            value.Contains("origine", StringComparison.Ordinal) ||
            value.Contains("selection", StringComparison.Ordinal))
        {
            return "fa-solid fa-seedling";
        }

        if (value.Contains("roast", StringComparison.Ordinal) ||
            value.Contains("torr", StringComparison.Ordinal) ||
            value.Contains("hot", StringComparison.Ordinal))
        {
            return "fa-solid fa-fire-flame-curved";
        }

        if (value.Contains("service", StringComparison.Ordinal) ||
            value.Contains("team", StringComparison.Ordinal) ||
            value.Contains("contact", StringComparison.Ordinal))
        {
            return "fa-solid fa-hand-holding-heart";
        }

        return index switch
        {
            0 => "fa-solid fa-seedling",
            1 => "fa-solid fa-fire-flame-curved",
            2 => "fa-solid fa-mug-hot",
            _ => "fa-solid fa-star"
        };
    }

    private static string ResolveCoffeeValueIconClass(string value, int index)
    {
        var normalized = value.ToLowerInvariant();
        if (normalized.Contains("bio", StringComparison.Ordinal) ||
            normalized.Contains("organic", StringComparison.Ordinal) ||
            normalized.Contains("ethic", StringComparison.Ordinal))
        {
            return "fa-solid fa-seedling";
        }

        if (normalized.Contains("rapid", StringComparison.Ordinal) ||
            normalized.Contains("service", StringComparison.Ordinal) ||
            normalized.Contains("takeaway", StringComparison.Ordinal))
        {
            return "fa-solid fa-bolt";
        }

        if (normalized.Contains("local", StringComparison.Ordinal) ||
            normalized.Contains("community", StringComparison.Ordinal) ||
            normalized.Contains("quartier", StringComparison.Ordinal))
        {
            return "fa-solid fa-people-group";
        }

        return index switch
        {
            0 => "fa-solid fa-mug-hot",
            1 => "fa-solid fa-cookie-bite",
            2 => "fa-solid fa-heart",
            _ => "fa-solid fa-star"
        };
    }

    private string BuildRestaurantServiceCardsHtml(
        IReadOnlyList<WebsiteCard> cards,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        string businessName)
    {
        if (cards.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, cards.Select((card, index) =>
        {
            var image = mediaAssets[(index + 1) % mediaAssets.Count];
            return $$"""
            <article class="menu-entry">
              <img src="{{image.WebPath}}" alt="{{EscapeHtmlAttribute($"{businessName} - {card.Title}")}}" width="{{image.Width}}" height="{{image.Height}}" loading="lazy" />
              <div class="menu-entry__body">
                <div class="menu-entry__top">
                  <span class="menu-entry__index">{{(index + 1).ToString("00", CultureInfo.InvariantCulture)}}</span>
                  <h3>{{EscapeHtml(card.Title)}}</h3>
                </div>
                <p>{{EscapeHtml(card.Description)}}</p>
              </div>
            </article>
            """;
        }));
    }

    private string BuildRestaurantGalleryHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets)
    {
        return string.Join(Environment.NewLine, mediaAssets.Select((asset, index) =>
        {
            var caption = content.GalleryCaptions[Math.Min(index, content.GalleryCaptions.Count - 1)];
            var spanClass = index is 0 or 4 ? "gallery-item span-2" : "gallery-item";
            return $$"""
            <figure class="{{spanClass}}" data-gallery-index="{{index}}">
              <img
                src="{{asset.WebPath}}"
                alt="{{EscapeHtmlAttribute($"{business.Name} - {caption}")}}"
                width="{{asset.Width}}"
                height="{{asset.Height}}"
                {{(index == 0 ? "fetchpriority=\"high\"" : "loading=\"lazy\"")}}
              />
              <figcaption><span>{{EscapeHtml(caption)}}</span></figcaption>
            </figure>
            """;
        }));
    }

    private string BuildRestaurantReviewCardsHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content)
    {
        if (business.ReviewHighlights.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, business.ReviewHighlights
            .Take(4)
            .Select(review =>
            {
                var reviewText = TrimToLengthSafe(review.Text, 210);
                var publishTime = PreferNullable(review.RelativePublishTimeDescription, "Google review") ?? "Google review";
                var reviewLinkHtml = string.IsNullOrWhiteSpace(review.GoogleMapsUri)
                    ? string.Empty
                    : $$"""<a class="testimonial-link" href="{{EscapeHtmlAttribute(review.GoogleMapsUri)}}" target="_blank" rel="noreferrer noopener">Google Maps</a>""";

                return $$"""
                <article class="testimonial-card">
                  <div class="stars">{{BuildRatingStars(review.Rating)}}</div>
                  <p>{{EscapeHtml(reviewText)}}</p>
                  <div class="testimonial-author">
                    <span class="testimonial-avatar">{{EscapeHtml(BuildInitials(review.AuthorName))}}</span>
                    <div>
                      <h5>{{EscapeHtml(review.AuthorName)}}</h5>
                      <span>{{EscapeHtml(publishTime)}}</span>
                    </div>
                  </div>
                  {{reviewLinkHtml}}
                </article>
                """;
            }));
    }

    private string BuildRestaurantContactCardsHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        string? whatsappHref)
    {
        var cards = new List<string>();

        if (!string.IsNullOrWhiteSpace(business.PhoneNumber))
        {
            cards.Add($$"""
            <div class="contact-info-card">
              <i class="fa-solid fa-phone"></i>
              <h4 id="phoneLabel">{{EscapeHtml(content.Ui.PhoneLabel)}}</h4>
              <p><a href="tel:{{EscapeHtmlAttribute(business.PhoneNumber)}}">{{EscapeHtml(business.PhoneNumber)}}</a></p>
            </div>
            """);
        }

        if (!string.IsNullOrWhiteSpace(whatsappHref))
        {
            var whatsappText = PreferNullable(business.PhoneNumber, content.Ui.WhatsAppLabel) ?? content.Ui.WhatsAppLabel;
            cards.Add($$"""
            <div class="contact-info-card">
              <i class="fa-brands fa-whatsapp"></i>
              <h4>{{EscapeHtml(content.Ui.WhatsAppLabel)}}</h4>
              <p><a href="{{EscapeHtmlAttribute(whatsappHref)}}" target="_blank" rel="noopener">{{EscapeHtml(whatsappText)}}</a></p>
            </div>
            """);
        }

        if (!string.IsNullOrWhiteSpace(business.PrimaryEmail))
        {
            cards.Add($$"""
            <div class="contact-info-card">
              <i class="fa-solid fa-envelope"></i>
              <h4 id="emailLabel">{{EscapeHtml(content.Ui.EmailLabel)}}</h4>
              <p><a href="mailto:{{EscapeHtmlAttribute(business.PrimaryEmail)}}">{{EscapeHtml(business.PrimaryEmail)}}</a></p>
            </div>
            """);
        }

        cards.Add($$"""
        <div class="contact-info-card">
          <i class="fa-solid fa-clock"></i>
          <h4 id="hoursLabel">{{EscapeHtml(content.Ui.HoursLabel)}}</h4>
          <p>{{string.Join("<br>", business.OpeningHours.Take(3).Select(EscapeHtml).DefaultIfEmpty(EscapeHtml(content.Ui.NoHours)))}}</p>
        </div>
        """);

        cards.Add($$"""
        <div class="contact-info-card">
          <i class="fa-solid fa-location-dot"></i>
          <h4 id="addressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</h4>
          <p>{{EscapeHtml(PreferNullable(business.Address, business.Category) ?? business.Name)}}</p>
        </div>
        """);

        if (cards.Count < 4)
        {
            cards.Add($$"""
            <div class="contact-info-card">
              <i class="fa-solid fa-star"></i>
              <h4>{{EscapeHtml(content.Ui.RatingLabel)}}</h4>
              <p>{{EscapeHtml(FormatRating(business))}}<br>{{EscapeHtml(FormatReviewCount(business))}}</p>
            </div>
            """);
        }

        return string.Join(Environment.NewLine, cards);
    }

    private string BuildRestaurantSocialLinksHtml(NormalizedBusiness business)
    {
        var links = new List<string>();

        foreach (var link in business.SocialLinks)
        {
            if (!TryGetAbsoluteHttpUri(link.Value, out _))
            {
                continue;
            }

            var label = ResolveSocialLabel(link.Key, link.Value);
            links.Add($$"""
            <a href="{{EscapeHtmlAttribute(link.Value)}}" target="_blank" rel="noopener" aria-label="{{EscapeHtmlAttribute(label)}}">
              <i class="{{ResolveSocialIconClass(link.Key, link.Value)}}"></i>
            </a>
            """);
        }

        var whatsappDigits = new string((business.WhatsappNumber ?? business.PhoneNumber ?? string.Empty).Where(char.IsDigit).ToArray());
        if (!string.IsNullOrWhiteSpace(whatsappDigits))
        {
            links.Add($$"""
            <a href="https://wa.me/{{whatsappDigits}}" target="_blank" rel="noopener" aria-label="WhatsApp">
              <i class="fa-brands fa-whatsapp"></i>
            </a>
            """);
        }

        return links.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, links);
    }

    private static string ResolveSocialLabel(string key, string url)
    {
        var value = $"{key} {url}".ToLowerInvariant();
        if (value.Contains("instagram", StringComparison.Ordinal))
        {
            return "Instagram";
        }

        if (value.Contains("facebook", StringComparison.Ordinal))
        {
            return "Facebook";
        }

        if (value.Contains("tiktok", StringComparison.Ordinal))
        {
            return "TikTok";
        }

        if (value.Contains("linkedin", StringComparison.Ordinal))
        {
            return "LinkedIn";
        }

        if (value.Contains("youtube", StringComparison.Ordinal))
        {
            return "YouTube";
        }

        if (value.Contains("twitter", StringComparison.Ordinal) || value.Contains("x.com", StringComparison.Ordinal))
        {
            return "X";
        }

        return CleanText(key) ?? "Website";
    }

    private static string ResolveSocialIconClass(string key, string url)
    {
        var value = $"{key} {url}".ToLowerInvariant();
        if (value.Contains("instagram", StringComparison.Ordinal))
        {
            return "fa-brands fa-instagram";
        }

        if (value.Contains("facebook", StringComparison.Ordinal))
        {
            return "fa-brands fa-facebook-f";
        }

        if (value.Contains("tiktok", StringComparison.Ordinal))
        {
            return "fa-brands fa-tiktok";
        }

        if (value.Contains("linkedin", StringComparison.Ordinal))
        {
            return "fa-brands fa-linkedin-in";
        }

        if (value.Contains("youtube", StringComparison.Ordinal))
        {
            return "fa-brands fa-youtube";
        }

        if (value.Contains("twitter", StringComparison.Ordinal) || value.Contains("x.com", StringComparison.Ordinal))
        {
            return "fa-brands fa-x-twitter";
        }

        return "fa-solid fa-link";
    }

    private static string BuildInitials(string value)
    {
        var tokens = (CleanText(value) ?? string.Empty)
            .Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(token => char.ToUpperInvariant(token[0]))
            .ToArray();

        return tokens.Length == 0
            ? "LR"
            : new string(tokens);
    }

    private string BuildTemplateBody(
        TemplateDefinition template,
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return template.Id switch
        {
            "luxury" => BuildLuxuryBody(business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml),
            "minimal" => BuildMinimalBody(business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml),
            "creative" => BuildCreativeBody(business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml),
            "corporate" => BuildCorporateBody(business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml),
            _ => BuildPremiumBody(business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml)
        };
    }

    private string BuildLuxuryBody(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return BuildSignatureBody("luxury", business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml);
    }

    private string BuildMinimalBody(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return BuildSignatureBody("minimal", business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml);
    }

    private string BuildCreativeBody(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return BuildSignatureBody("creative", business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml);
    }

    private string BuildCorporateBody(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return BuildSignatureBody("corporate", business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml);
    }

    private string BuildPremiumBody(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        return BuildSignatureBody("premium", business, content, sectionOrder, hiddenSections, heroShowcaseHtml, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml);
    }

    private string BuildSignatureBody(
        string templateId,
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string heroShowcaseHtml,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        var heroFactsHtml = BuildHeroFactStrip(business, content);

        return $$"""
        <main class="site-main site-main--{{templateId}}" id="top">
          <section class="hero hero--signature hero--signature-{{templateId}}">
            <div class="hero-copy hero-copy--signature">
              <span class="eyebrow" id="heroEyebrow">{{EscapeHtml(content.HeroEyebrow)}}</span>
              <h1 id="heroTitle">{{EscapeHtml(content.HeroTitle)}}</h1>
              <p class="hero-subtitle" id="heroSubtitle">{{EscapeHtml(content.HeroSubtitle)}}</p>
              <p class="hero-description" id="heroDescription">{{EscapeHtml(content.HeroDescription)}}</p>
              <div class="hero-actions">
                <a class="button button--primary" id="heroPrimaryCta" href="#contact">{{EscapeHtml(content.PrimaryCta)}}</a>
                <a class="button button--secondary" id="heroSecondaryCta" href="#services">{{EscapeHtml(content.SecondaryCta)}}</a>
              </div>
            </div>

            <div class="hero-stage">
              {{heroShowcaseHtml}}
            </div>

            {{heroFactsHtml}}
          </section>

          <div class="section-flow section-flow--{{templateId}}">
            {{BuildSectionStack(templateId, business, content, sectionOrder, hiddenSections, galleryHtml, serviceCardsHtml, highlightCardsHtml, faqHtml, openingHoursHtml, socialLinksHtml)}}
          </div>
        </main>
        """;
    }

    private string BuildSectionStack(
        string templateId,
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> hiddenSections,
        string galleryHtml,
        string serviceCardsHtml,
        string highlightCardsHtml,
        string faqHtml,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        var hidden = hiddenSections
            .Where(static sectionId => !string.IsNullOrWhiteSpace(sectionId))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var sections = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["about"] = BuildAboutSection(business, content),
            ["highlights"] = BuildHighlightsSection(content, highlightCardsHtml),
            ["services"] = BuildServicesSection(content, serviceCardsHtml),
            ["gallery"] = BuildGallerySection(content, galleryHtml),
            ["reviews"] = BuildReviewsSection(content, business),
            ["contact"] = BuildContactSection(content, business, openingHoursHtml, socialLinksHtml),
            ["faq"] = BuildFaqSection(content, faqHtml)
        };

        var orderedSectionIds = NormalizeSectionOrder(sectionOrder, [], templateId);

        return string.Join(
            Environment.NewLine + Environment.NewLine,
            orderedSectionIds
                .Where(sectionId => !hidden.Contains(sectionId))
                .Where(sections.ContainsKey)
                .Select(sectionId => sections[sectionId]));
    }

    private string BuildSectionHead(
        string eyebrowId,
        string titleId,
        string eyebrow,
        string title,
        string? description = null,
        string? descriptionId = null,
        string? modifierClass = null)
    {
        var classes = string.IsNullOrWhiteSpace(modifierClass)
            ? "section-head section-head--split"
            : $"section-head section-head--split {modifierClass}";
        var descriptionAttributes = string.IsNullOrWhiteSpace(descriptionId)
            ? string.Empty
            : $" id=\"{descriptionId}\"";
        var descriptionHtml = string.IsNullOrWhiteSpace(description)
            ? string.Empty
            : $$"""
              <div class="section-head__copy">
                <p{{descriptionAttributes}}>{{EscapeHtml(description)}}</p>
              </div>
              """;

        return $$"""
        <div class="{{classes}}">
          <div class="section-head__intro">
            <span class="eyebrow" id="{{eyebrowId}}">{{EscapeHtml(eyebrow)}}</span>
            <h2 id="{{titleId}}">{{EscapeHtml(title)}}</h2>
          </div>
          {{descriptionHtml}}
        </div>
        """;
    }

    private string BuildAboutSection(NormalizedBusiness business, LocalizedWebsiteContent content)
    {
        var identityLine = ResolveHeadlineLocation(
            business.Address,
            PreferNullable(business.Category, business.Name) ?? business.Name);
        var actionLabel = content.Ui.WhatsAppLabel;
        var actionValue = PreferNullable(business.PhoneNumber, business.PrimaryEmail) ?? content.PrimaryCta;
        var supportingLine = PreferNullable(business.OpeningHours.FirstOrDefault(), business.PrimaryEmail)
            ?? PreferNullable(business.Address, business.GoogleMapsUri)
            ?? business.Name;
        var reviewSnippet = TrimToLengthSafe(content.ReviewsSummary, 120);

        return $$"""
        <section class="content-section content-section--story" id="about">
          {{BuildSectionHead("aboutEyebrow", "aboutTitle", content.AboutEyebrow, content.AboutTitle, identityLine, modifierClass: "section-head--story")}}
          <div class="story-layout">
            <article class="about-panel">
              <p id="aboutBody">{{EscapeHtml(content.AboutBody)}}</p>
            </article>
            <aside class="story-rail">
              <article class="story-note">
                <span class="story-note__eyebrow">{{EscapeHtml(business.Category)}}</span>
                <strong>{{EscapeHtml(business.Name)}}</strong>
                <p>{{EscapeHtml(identityLine)}}</p>
              </article>
              <article class="story-fact">
                <span>{{EscapeHtml(FormatRating(business))}}</span>
                <strong>{{EscapeHtml(FormatReviewCount(business))}}</strong>
                <p>{{EscapeHtml(reviewSnippet)}}</p>
              </article>
              <article class="story-fact">
                <span>{{EscapeHtml(actionLabel)}}</span>
                <strong>{{EscapeHtml(actionValue)}}</strong>
                <p>{{EscapeHtml(supportingLine)}}</p>
              </article>
            </aside>
          </div>
        </section>
        """;
    }

    private string BuildHighlightsSection(LocalizedWebsiteContent content, string highlightCardsHtml)
    {
        return $$"""
        <section class="content-section content-section--highlights" id="highlights">
          {{BuildSectionHead("highlightsEyebrow", "highlightsTitle", content.HighlightsEyebrow, content.HighlightsTitle)}}
          <div class="card-grid card-grid--highlights" id="highlightsGrid">
            {{highlightCardsHtml}}
          </div>
        </section>
        """;
    }

    private string BuildServicesSection(LocalizedWebsiteContent content, string serviceCardsHtml)
    {
        return $$"""
        <section class="content-section content-section--services" id="services">
          {{BuildSectionHead("servicesEyebrow", "servicesTitle", content.ServicesEyebrow, content.ServicesTitle, content.ServicesIntro, "servicesIntro")}}
          <div class="card-grid" id="servicesGrid">
            {{serviceCardsHtml}}
          </div>
        </section>
        """;
    }

    private string BuildGallerySection(LocalizedWebsiteContent content, string galleryHtml)
    {
        return $$"""
        <section class="content-section content-section--gallery" id="gallery">
          {{BuildSectionHead("galleryEyebrow", "galleryTitle", content.GalleryEyebrow, content.GalleryTitle, content.GalleryIntro, "galleryIntro")}}
          <div class="gallery-grid" id="galleryGrid">
            {{galleryHtml}}
          </div>
        </section>
        """;
    }

    private string BuildReviewsSection(LocalizedWebsiteContent content, NormalizedBusiness business)
    {
        var reviewActionsHtml = BuildReviewActionsHtml(content, business);
        var reviewHighlightsHtml = BuildReviewHighlightsHtml(business);
        var reviewIntro = business.ReviewCount is > 0
            ? $"{FormatReviewCount(business)} visibles sur Google Maps."
            : "Retours, confiance locale et informations utiles reunis au meme endroit.";
        var reviewHighlightsBlock = string.IsNullOrWhiteSpace(reviewHighlightsHtml)
            ? string.Empty
            : $$"""
              <div class="review-highlights">
                {{reviewHighlightsHtml}}
              </div>
              """;

        return $$"""
        <section class="content-section content-section--reviews" id="reviews">
          {{BuildSectionHead("reviewsEyebrow", "reviewsTitle", content.ReviewsEyebrow, content.ReviewsTitle, reviewIntro)}}
          <div class="review-panel">
            <div class="review-panel__score">
              <span class="review-panel__source">Google Maps</span>
              <strong>{{FormatRating(business)}}</strong>
              <div class="rating-stars" aria-hidden="true">
                {{BuildRatingStars(business.Rating)}}
              </div>
              <small>{{FormatReviewCount(business)}}</small>
            </div>
            <div class="review-panel__body">
              <p id="reviewsSummary">{{EscapeHtml(content.ReviewsSummary)}}</p>
              {{reviewActionsHtml}}
            </div>
          </div>
          {{reviewHighlightsBlock}}
        </section>
        """;
    }

    private string BuildContactSection(
        LocalizedWebsiteContent content,
        NormalizedBusiness business,
        string openingHoursHtml,
        string socialLinksHtml)
    {
        var emailHtml = string.IsNullOrWhiteSpace(business.PrimaryEmail)
            ? string.Empty
            : $$"""
              <div class="contact-detail">
                <span id="emailLabel">{{EscapeHtml(content.Ui.EmailLabel)}}</span>
                <a href="mailto:{{EscapeHtmlAttribute(business.PrimaryEmail)}}">{{EscapeHtml(business.PrimaryEmail)}}</a>
              </div>
              """;
        var socialHtml = string.IsNullOrWhiteSpace(socialLinksHtml)
            ? string.Empty
            : $$"""
              <div class="contact-socials">{{socialLinksHtml}}</div>
              """;
        var callActionHtml = string.IsNullOrWhiteSpace(business.PhoneNumber)
            ? string.Empty
            : $$"""
              <a class="button button--ghost" id="callNowLink" href="tel:{{EscapeHtmlAttribute(business.PhoneNumber)}}">
                {{EscapeHtml(content.Ui.CallNow)}}
              </a>
              """;

        return $$"""
        <section class="content-section content-section--contact" id="contact">
          {{BuildSectionHead("contactEyebrow", "contactTitle", content.ContactEyebrow, content.ContactTitle, content.ContactIntro, "contactIntro")}}

          <div class="contact-layout">
            <article class="contact-card">
              <div class="contact-detail">
                <span id="addressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</span>
                <strong>{{EscapeHtml(business.Address ?? business.Category)}}</strong>
              </div>

              <div class="contact-detail">
                <span id="phoneLabel">{{EscapeHtml(content.Ui.PhoneLabel)}}</span>
                <a href="tel:{{EscapeHtmlAttribute(business.PhoneNumber ?? string.Empty)}}">{{EscapeHtml(business.PhoneNumber ?? "N/A")}}</a>
              </div>

              {{emailHtml}}

              <div class="contact-detail">
                <span id="hoursLabel">{{EscapeHtml(content.Ui.HoursLabel)}}</span>
                <ul class="hours-list" id="hoursList">
                  {{openingHoursHtml}}
                </ul>
              </div>

              <div class="contact-actions">
                <a class="button button--secondary" id="openMapLink" href="{{EscapeHtmlAttribute(business.GoogleMapsUri)}}" target="_blank" rel="noreferrer noopener">
                  {{EscapeHtml(content.Ui.ViewOnMaps)}}
                </a>
                {{callActionHtml}}
              </div>

              {{socialHtml}}
            </article>

            <article class="contact-card contact-card--form">
              <div>
                <span class="eyebrow eyebrow--small">{{EscapeHtml(content.Ui.WhatsAppLabel)}}</span>
                <h3 id="formTitle">{{EscapeHtml(content.FormTitle)}}</h3>
                <p id="formIntro">{{EscapeHtml(content.FormIntro)}}</p>
              </div>

              <form id="whatsappForm" class="whatsapp-form">
                <label for="contactName" id="formNameLabel">{{EscapeHtml(content.Ui.FormNameLabel)}}</label>
                <input id="contactName" name="name" type="text" placeholder="{{EscapeHtmlAttribute(content.Ui.FormNamePlaceholder)}}" />

                <label for="contactPhone" id="formPhoneLabel">{{EscapeHtml(content.Ui.FormPhoneLabel)}}</label>
                <input id="contactPhone" name="phone" type="tel" placeholder="{{EscapeHtmlAttribute(content.Ui.FormPhonePlaceholder)}}" />

                <label for="contactMessage" id="formMessageLabel">{{EscapeHtml(content.Ui.FormMessageLabel)}}</label>
                <textarea id="contactMessage" name="message" rows="5" placeholder="{{EscapeHtmlAttribute(content.Ui.FormMessagePlaceholder)}}"></textarea>

                <button class="button button--primary" id="formSubmitButton" type="submit">
                  {{EscapeHtml(content.Ui.FormSubmitLabel)}}
                </button>
              </form>
            </article>

            <div class="map-card">
              <iframe
                title="{{EscapeHtmlAttribute($"{business.Name} - map")}}"
                src="{{EscapeHtmlAttribute(business.MapEmbedUri)}}"
                loading="lazy"
                referrerpolicy="no-referrer-when-downgrade"
              ></iframe>
            </div>
          </div>
        </section>
        """;
    }

    private string BuildFaqSection(LocalizedWebsiteContent content, string faqHtml)
    {
        return $$"""
        <section class="content-section content-section--faq" id="faq">
          {{BuildSectionHead("faqEyebrow", "faqTitle", content.FaqEyebrow, content.FaqTitle)}}
          <div class="faq-list" id="faqList">
            {{faqHtml}}
          </div>
        </section>
        """;
    }

    private string BuildServiceCardsHtml(IReadOnlyList<WebsiteCard> cards)
    {
        return string.Join(Environment.NewLine, cards.Select((card, index) =>
            $$"""
            <article class="info-card">
              <span class="card-kicker">{{(index + 1).ToString("00", CultureInfo.InvariantCulture)}}</span>
              <h3>{{EscapeHtml(card.Title)}}</h3>
              <p>{{EscapeHtml(card.Description)}}</p>
            </article>
            """));
    }

    private string BuildHighlightCardsHtml(IReadOnlyList<WebsiteCard> cards)
    {
        return string.Join(Environment.NewLine, cards.Select((card, index) =>
            $$"""
            <article class="highlight-card">
              <span class="card-kicker">{{(index + 1).ToString("00", CultureInfo.InvariantCulture)}}</span>
              <h3>{{EscapeHtml(card.Title)}}</h3>
              <p>{{EscapeHtml(card.Description)}}</p>
            </article>
            """));
    }

    private string BuildHeroFactStrip(NormalizedBusiness business, LocalizedWebsiteContent content)
    {
        var experienceValue = PreferNullable(business.Category, business.Name) ?? business.Name;
        var experienceSupporting = PreferNullable(business.OpeningHours.FirstOrDefault(), business.PhoneNumber)
            ?? PreferNullable(business.PrimaryEmail, business.Name)
            ?? business.Name;
        var locationValue = ResolveHeadlineLocation(business.Address, business.Name);
        var fullAddress = PreferNullable(business.Address, business.Category) ?? business.Name;
        var locationSupporting = string.Equals(locationValue, fullAddress, StringComparison.OrdinalIgnoreCase)
            ? PreferNullable(business.Category, business.OpeningHours.FirstOrDefault()) ?? business.Name
            : fullAddress;

        return $$"""
        <div class="hero-facts">
          <article class="hero-fact hero-fact--rating">
            <span id="heroRatingLabel">{{EscapeHtml(content.Ui.RatingLabel)}}</span>
            <strong>{{EscapeHtml(FormatRating(business))}}</strong>
            <div class="rating-stars rating-stars--compact" aria-hidden="true">
              {{BuildRatingStars(business.Rating)}}
            </div>
          </article>
          <article class="hero-fact hero-fact--experience">
            <span id="heroFeatureLabel">{{EscapeHtml(content.Ui.FeatureBadge)}}</span>
            <strong>{{EscapeHtml(experienceValue)}}</strong>
            <p>{{EscapeHtml(experienceSupporting)}}</p>
          </article>
          <article class="hero-fact hero-fact--location">
            <span id="heroAddressLabel">{{EscapeHtml(content.Ui.AddressLabel)}}</span>
            <strong>{{EscapeHtml(locationValue)}}</strong>
            <p>{{EscapeHtml(locationSupporting)}}</p>
          </article>
        </div>
        """;
    }

    private string BuildHeroShowcaseHtml(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        string templateId)
    {
        if (mediaAssets.Count == 0)
        {
            return string.Empty;
        }

        var primaryAsset = mediaAssets[0];
        var secondaryAsset = mediaAssets.Count > 1 ? mediaAssets[1] : mediaAssets[0];
        var editorialMode = IsHospitalityBusiness(business) && (templateId == "luxury" || templateId == "premium");
        var headlineLocation = ResolveHeadlineLocation(
            business.Address,
            PreferNullable(business.Category, business.Name) ?? business.Name);
        var locationLine = editorialMode
            ? headlineLocation
            : PreferNullable(business.Address, business.Category) ?? business.Name;
        var contactPrimary = PreferNullable(business.PhoneNumber, business.PrimaryEmail)
            ?? PreferNullable(business.Address, business.Name)
            ?? business.Name;
        var contactSecondary = PreferNullable(business.Address, business.OpeningHours.FirstOrDefault())
            ?? PreferNullable(business.PrimaryEmail, business.Category)
            ?? business.Category;
        var atmosphereLine = PreferNullable(
                secondaryAsset.Caption,
                PreferNullable(content.GalleryIntro, content.HeroDescription))
            ?? business.Category;
        var hospitalityLine = PreferNullable(business.OpeningHours.FirstOrDefault(), business.Category) ?? business.Name;
        var reviewSnippet = TrimToLengthSafe(
            PreferNullable(content.ReviewsSummary, content.AboutBody)
                ?? PreferNullable(content.HeroDescription, business.Name)
                ?? business.Name,
            132);
        var variantClass = editorialMode
            ? "hero-showcase hero-showcase--editorial"
            : templateId is "minimal" or "premium"
            ? "hero-showcase hero-showcase--wide"
            : templateId == "creative"
                ? "hero-showcase hero-showcase--creative"
                : "hero-showcase hero-showcase--split";
        var thumbClass = editorialMode ? "hero-thumb hero-thumb--editorial" : "hero-thumb";

        var thumbHtml = $$"""
        <figure class="{{thumbClass}}">
          <img
            src="{{secondaryAsset.WebPath}}"
            alt="{{EscapeHtmlAttribute($"{business.Name} - {secondaryAsset.Caption}")}}"
            width="{{secondaryAsset.Width}}"
            height="{{secondaryAsset.Height}}"
            loading="lazy"
          />
          <figcaption>{{EscapeHtml(secondaryAsset.Caption)}}</figcaption>
        </figure>
        """;

        var metricCardHtml = $$"""
        <article class="hero-card hero-card--metric">
          <span id="reviewsBadge">{{EscapeHtml(content.Ui.ReviewBadge)}}</span>
          <strong>{{FormatRating(business)}}</strong>
          <small>{{EscapeHtml(FormatReviewCount(business))}}</small>
        </article>
        """;

        var contactCardHtml = $$"""
        <article class="hero-card hero-card--contact">
          <span id="contactBadge">{{EscapeHtml(content.Ui.ContactBadge)}}</span>
          <strong>{{EscapeHtml(contactPrimary)}}</strong>
          <small>{{EscapeHtml(contactSecondary)}}</small>
        </article>
        """;

        var storyCardHtml = $$"""
        <article class="hero-card hero-card--story">
          <span>{{EscapeHtml(content.Ui.FeatureBadge)}}</span>
          <strong>{{EscapeHtml(content.HighlightsTitle)}}</strong>
          <small>{{EscapeHtml(reviewSnippet)}}</small>
        </article>
        """;

        var editorialStoryCardHtml = $$"""
        <article class="hero-card hero-card--story hero-card--editorial">
          <span>{{EscapeHtml(content.Ui.FeatureBadge)}}</span>
          <strong>{{EscapeHtml(headlineLocation)}}</strong>
          <small>{{EscapeHtml($"{atmosphereLine} · {hospitalityLine}")}}</small>
        </article>
        """;

        var railHtml = editorialMode
            ? mediaAssets.Count > 1
                ? string.Join(Environment.NewLine, [metricCardHtml, thumbHtml])
                : string.Join(Environment.NewLine, [metricCardHtml, editorialStoryCardHtml])
            : templateId switch
        {
            "minimal" or "premium" => string.Join(Environment.NewLine, [thumbHtml, storyCardHtml]),
            "creative" => string.Join(Environment.NewLine, [thumbHtml, storyCardHtml, metricCardHtml]),
            _ => string.Join(Environment.NewLine, [metricCardHtml, thumbHtml, contactCardHtml])
        };
        var railClass = editorialMode
            ? "hero-showcase__rail hero-showcase__rail--editorial"
            : "hero-showcase__rail";

        return $$"""
        <div class="{{variantClass}}">
          <figure class="hero-visual">
            <img
              src="{{primaryAsset.WebPath}}"
              alt="{{EscapeHtmlAttribute($"{business.Name} - {primaryAsset.Caption}")}}"
              width="{{primaryAsset.Width}}"
              height="{{primaryAsset.Height}}"
              fetchpriority="high"
            />
            <figcaption class="hero-visual__caption">
              <span>{{EscapeHtml(business.Category)}}</span>
              <strong>{{EscapeHtml(business.Name)}}</strong>
              <p>{{EscapeHtml(locationLine)}}</p>
            </figcaption>
          </figure>

          <div class="{{railClass}}">
            {{railHtml}}
          </div>
        </div>
        """;
    }

    private static string ResolveGalleryCssClass(int index)
    {
        return index switch
        {
            0 => "media-card media-card--hero media-card--wide",
            1 => "media-card media-card--portrait",
            2 => "media-card media-card--square",
            3 => "media-card media-card--square media-card--offset",
            4 => "media-card media-card--wide media-card--wide-secondary",
            _ => "media-card media-card--portrait media-card--portrait-secondary"
        };
    }

    private string BuildFaqHtml(IReadOnlyList<FaqItem> items)
    {
        return string.Join(Environment.NewLine, items.Select((item, index) =>
            $$"""
            <details class="faq-item" {{(index == 0 ? "open" : string.Empty)}}>
              <summary>{{EscapeHtml(item.Question)}}</summary>
            <p>{{EscapeHtml(item.Answer)}}</p>
            </details>
            """));
    }

    private string BuildReviewActionsHtml(LocalizedWebsiteContent content, NormalizedBusiness business)
    {
        var actions = new List<string>();

        if (!string.IsNullOrWhiteSpace(business.ReviewsUri))
        {
            actions.Add($$"""
            <a class="button button--secondary" id="viewReviewsButton" href="{{EscapeHtmlAttribute(business.ReviewsUri)}}" target="_blank" rel="noreferrer noopener">
              {{EscapeHtml(content.Ui.ViewReviews)}}
            </a>
            """);
        }

        if (!string.IsNullOrWhiteSpace(business.WriteAReviewUri))
        {
            actions.Add($$"""
            <a class="button button--ghost" id="writeReviewButton" href="{{EscapeHtmlAttribute(business.WriteAReviewUri)}}" target="_blank" rel="noreferrer noopener">
              {{EscapeHtml(content.Ui.WriteReview)}}
            </a>
            """);
        }

        if (actions.Count == 0)
        {
            return string.Empty;
        }

        return $$"""
        <div class="review-actions">
          {{string.Join(Environment.NewLine, actions)}}
        </div>
        """;
    }

    private string BuildReviewHighlightsHtml(NormalizedBusiness business)
    {
        if (business.ReviewHighlights.Count == 0)
        {
            return string.Empty;
        }

        return string.Join(Environment.NewLine, business.ReviewHighlights
            .Take(3)
            .Select(review =>
            {
                var reviewText = TrimToLengthSafe(review.Text, 220);
                var publishTime = CleanText(review.RelativePublishTimeDescription);
                var reviewLinkHtml = string.IsNullOrWhiteSpace(review.GoogleMapsUri)
                    ? string.Empty
                    : $$"""
                      <a class="review-quote__link" href="{{EscapeHtmlAttribute(review.GoogleMapsUri)}}" target="_blank" rel="noreferrer noopener">Google Maps</a>
                      """;

                return $$"""
                <article class="review-quote">
                  <div class="review-quote__head">
                    <div>
                      <strong>{{EscapeHtml(review.AuthorName)}}</strong>
                      <span>{{EscapeHtml(publishTime ?? "Google review")}}</span>
                    </div>
                    <div class="rating-stars rating-stars--compact" aria-hidden="true">
                      {{BuildRatingStars(review.Rating)}}
                    </div>
                  </div>
                  <p>{{EscapeHtml(reviewText)}}</p>
                  {{reviewLinkHtml}}
                </article>
                """;
            }));
    }

    private static string BuildRatingStars(double? rating)
    {
        if (rating is null || rating <= 0)
        {
            return string.Concat(Enumerable.Repeat("""<span class="rating-stars__star rating-stars__star--muted">&#9733;</span>""", 5));
        }

        var filledStars = Math.Clamp((int)Math.Round(rating.Value, MidpointRounding.AwayFromZero), 0, 5);
        var builder = new StringBuilder();

        for (var index = 1; index <= 5; index++)
        {
            var cssClass = index <= filledStars
                ? "rating-stars__star rating-stars__star--active"
                : "rating-stars__star rating-stars__star--muted";
            builder.Append($"""<span class="{cssClass}">&#9733;</span>""");
        }

        return builder.ToString();
    }

    private string BuildTranslationsJson(LocalizedContentBundle contentBundle)
    {
        var payload = new
        {
            defaultLanguage = "fr",
            languages = new[] { "fr", "en", "ar" },
            seo = new
            {
                title = contentBundle.Seo.Title,
                description = contentBundle.Seo.Description,
                keywords = contentBundle.Seo.Keywords
            },
            content = contentBundle.Translations.ToDictionary(
                entry => entry.Key,
                entry => new
                {
                    metaTitle = BuildMetaTitle(entry.Value, contentBundle.Seo),
                    metaDescription = contentBundle.Seo.Description,
                    nav = new
                    {
                        about = entry.Value.Ui.NavAbout,
                        services = entry.Value.Ui.NavServices,
                        gallery = entry.Value.Ui.NavGallery,
                        reviews = entry.Value.Ui.NavReviews,
                        contact = entry.Value.Ui.NavContact
                    },
                    hero = new
                    {
                        eyebrow = entry.Value.HeroEyebrow,
                        title = entry.Value.HeroTitle,
                        subtitle = entry.Value.HeroSubtitle,
                        description = entry.Value.HeroDescription,
                        primaryCta = entry.Value.PrimaryCta,
                        secondaryCta = entry.Value.SecondaryCta
                    },
                    about = new
                    {
                        eyebrow = entry.Value.AboutEyebrow,
                        title = entry.Value.AboutTitle,
                        body = entry.Value.AboutBody
                    },
                    services = new
                    {
                        eyebrow = entry.Value.ServicesEyebrow,
                        title = entry.Value.ServicesTitle,
                        intro = entry.Value.ServicesIntro,
                        items = entry.Value.Services
                    },
                    highlights = new
                    {
                        eyebrow = entry.Value.HighlightsEyebrow,
                        title = entry.Value.HighlightsTitle,
                        items = entry.Value.Highlights
                    },
                    gallery = new
                    {
                        eyebrow = entry.Value.GalleryEyebrow,
                        title = entry.Value.GalleryTitle,
                        intro = entry.Value.GalleryIntro,
                        captions = entry.Value.GalleryCaptions
                    },
                    reviews = new
                    {
                        eyebrow = entry.Value.ReviewsEyebrow,
                        title = entry.Value.ReviewsTitle,
                        summary = entry.Value.ReviewsSummary
                    },
                    contact = new
                    {
                        eyebrow = entry.Value.ContactEyebrow,
                        title = entry.Value.ContactTitle,
                        intro = entry.Value.ContactIntro
                    },
                    form = new
                    {
                        title = entry.Value.FormTitle,
                        intro = entry.Value.FormIntro
                    },
                    faq = new
                    {
                        eyebrow = entry.Value.FaqEyebrow,
                        title = entry.Value.FaqTitle,
                        items = entry.Value.Faq
                    },
                    footer = new
                    {
                        tagline = entry.Value.FooterTagline
                    },
                    ui = entry.Value.Ui
                })
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private string BuildManifestJson(
        NormalizedBusiness business,
        ThemeChoice theme,
        GeneratedLogoAsset logoAsset)
    {
        var payload = new
        {
            name = business.Name,
            short_name = business.Name.Length > 18 ? business.Name[..18] : business.Name,
            description = $"{business.Name} - {business.Category}",
            start_url = "./",
            scope = "./",
            display = "standalone",
            background_color = theme.Background,
            theme_color = theme.PrimaryColor,
            icons = new[]
            {
                new
                {
                    src = logoAsset.WebPath,
                    type = "image/svg+xml",
                    sizes = "any"
                }
            }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    private string BuildRobotsTxt(string siteUrl)
    {
        return $$"""
        User-agent: *
        Allow: /

        Sitemap: {{siteUrl}}sitemap.xml
        """;
    }

    private string BuildSitemapXml(string siteUrl)
    {
        var escapedUrl = SecurityElementEscape(siteUrl);
        return $$"""
        <?xml version="1.0" encoding="UTF-8"?>
        <urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">
          <url>
            <loc>{{escapedUrl}}</loc>
            <changefreq>weekly</changefreq>
            <priority>0.9</priority>
          </url>
        </urlset>
        """;
    }

    private string BuildGeneratedReadme(
        NormalizedBusiness business,
        TemplateDefinition template,
        string modelUsed,
        string siteUrl)
    {
        return $$"""
        # {{business.Name}}

        Static website generated for **{{business.Name}}**.

        ## Included

        - Responsive landing page
        - Multilingual JSON translations (`fr`, `en`, `ar`)
        - WhatsApp contact form without backend
        - SEO meta tags and JSON-LD
        - Web manifest, robots.txt, sitemap.xml

        ## Generation metadata

        - Template: {{template.DisplayName}}
        - AI model used: {{modelUsed}}
        - Public site URL: {{siteUrl}}

        ## Deploy quickly

        - GitHub Pages: this project is structured for direct static deployment
        - Netlify: deploy the directory as a static site if you want another hosting target
        - Cloudflare Pages: deploy the directory as a static site
        - Vercel: import the folder as a static project
        """;
    }

    private string BuildMetaTags(
        LocalizedWebsiteContent content,
        SeoContent seo,
        NormalizedBusiness business,
        string siteUrl,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        GeneratedLogoAsset logoAsset)
    {
        var metaTitle = SecurityElementEscape(BuildMetaTitle(content, seo));
        var metaDescription = SecurityElementEscape(TrimToLengthSafe(seo.Description, 155));
        var keywords = SecurityElementEscape(string.Join(", ", seo.Keywords));
        var ogImage = SecurityElementEscape($"{siteUrl}{mediaAssets[0].WebPath}");
        var canonicalUrl = SecurityElementEscape(siteUrl);
        var businessName = SecurityElementEscape(business.Name);
        var logoUrl = SecurityElementEscape($"{siteUrl}{logoAsset.WebPath}");

        return $$"""
        <meta name="description" content="{{metaDescription}}" />
        <meta name="keywords" content="{{keywords}}" />
        <link rel="canonical" href="{{canonicalUrl}}" />
        <meta property="og:type" content="website" />
        <meta property="og:title" content="{{metaTitle}}" />
        <meta property="og:description" content="{{metaDescription}}" />
        <meta property="og:url" content="{{canonicalUrl}}" />
        <meta property="og:image" content="{{ogImage}}" />
        <meta property="og:site_name" content="{{businessName}}" />
        <meta name="twitter:card" content="summary_large_image" />
        <meta name="twitter:title" content="{{metaTitle}}" />
        <meta name="twitter:description" content="{{metaDescription}}" />
        <meta name="twitter:image" content="{{ogImage}}" />
        <meta itemprop="image" content="{{logoUrl}}" />
        """;
    }

    private string BuildStructuredDataJson(
        NormalizedBusiness business,
        LocalizedWebsiteContent content,
        IReadOnlyList<GeneratedMediaAsset> mediaAssets,
        string siteUrl)
    {
        var schema = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org",
            ["@type"] = ResolveSchemaType(business.Category),
            ["name"] = business.Name,
            ["description"] = content.HeroDescription,
            ["url"] = siteUrl,
            ["image"] = mediaAssets.Select(asset => $"{siteUrl}{asset.WebPath}").ToList(),
            ["telephone"] = business.PhoneNumber,
            ["email"] = business.PrimaryEmail,
            ["address"] = business.Address,
            ["sameAs"] = business.SocialLinks.Values.ToList(),
            ["openingHours"] = business.OpeningHours.ToList()
        };

        if (business.Latitude is not null && business.Longitude is not null)
        {
            schema["geo"] = new
            {
                @type = "GeoCoordinates",
                latitude = business.Latitude.Value.ToString(CultureInfo.InvariantCulture),
                longitude = business.Longitude.Value.ToString(CultureInfo.InvariantCulture)
            };
        }

        if (business.Rating is not null && business.ReviewCount is > 0)
        {
            schema["aggregateRating"] = new
            {
                @type = "AggregateRating",
                ratingValue = business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture),
                reviewCount = business.ReviewCount.Value
            };
        }

        return JsonSerializer.Serialize(schema, JsonOptions);
    }

    private string BuildStylesheet(TemplateDefinition template, ThemeChoice theme)
    {
        var stylesheetFileName = template.Id switch
        {
            "restaurant-signature" => "restaurant-signature.css",
            "coffee-shop-signature" => "coffee-shop-signature.css",
            _ => "styles.css"
        };
        var templatePath = Path.Combine(_environment.ContentRootPath, "Templates", "generated-site", stylesheetFileName);
        var stylesheet = File.ReadAllText(templatePath);

        return stylesheet
            .Replace("{{COLOR_SCHEME}}", template.IsDark ? "dark" : "light", StringComparison.Ordinal)
            .Replace("{{BODY_FONT}}", theme.FontPair.BodyFamily, StringComparison.Ordinal)
            .Replace("{{DISPLAY_FONT}}", theme.FontPair.DisplayFamily, StringComparison.Ordinal);
    }

    private string BuildClientScript()
    {
        return """
        const siteConfigElement = document.getElementById('site-config');
        const siteConfig = siteConfigElement ? JSON.parse(siteConfigElement.textContent || '{}') : {};
        const state = {
          translations: null,
          language: localStorage.getItem('lead-radar-language') || siteConfig.defaultLanguage || 'fr',
        };

        const refs = {
          title: document.querySelector('title'),
          metaDescription: document.querySelector('meta[name="description"]'),
          navbar: document.getElementById('navbar'),
          navAbout: document.getElementById('navAbout'),
          navServices: document.getElementById('navServices'),
          navGallery: document.getElementById('navGallery'),
          navReviews: document.getElementById('navReviews'),
          navContact: document.getElementById('navContact'),
          languageLabel: document.getElementById('languageLabel'),
          heroSection: document.querySelector('.hero'),
          heroVisual: document.querySelector('.hero-visual, .hero-bg'),
          heroEyebrow: document.getElementById('heroEyebrow'),
          heroTitle: document.getElementById('heroTitle'),
          heroSubtitle: document.getElementById('heroSubtitle'),
          heroDescription: document.getElementById('heroDescription'),
          heroPrimaryCta: document.getElementById('heroPrimaryCta'),
          heroSecondaryCta: document.getElementById('heroSecondaryCta'),
          heroRatingLabel: document.getElementById('heroRatingLabel'),
          heroContactLabel: document.getElementById('heroContactLabel'),
          heroAddressLabel: document.getElementById('heroAddressLabel'),
          aboutEyebrow: document.getElementById('aboutEyebrow'),
          aboutTitle: document.getElementById('aboutTitle'),
          aboutBody: document.getElementById('aboutBody'),
          servicesEyebrow: document.getElementById('servicesEyebrow'),
          servicesTitle: document.getElementById('servicesTitle'),
          servicesIntro: document.getElementById('servicesIntro'),
          servicesGrid: document.getElementById('servicesGrid'),
          highlightsEyebrow: document.getElementById('highlightsEyebrow'),
          highlightsTitle: document.getElementById('highlightsTitle'),
          highlightsGrid: document.getElementById('highlightsGrid'),
          galleryEyebrow: document.getElementById('galleryEyebrow'),
          galleryTitle: document.getElementById('galleryTitle'),
          galleryIntro: document.getElementById('galleryIntro'),
          reviewsEyebrow: document.getElementById('reviewsEyebrow'),
          reviewsTitle: document.getElementById('reviewsTitle'),
          reviewsSummary: document.getElementById('reviewsSummary'),
          contactEyebrow: document.getElementById('contactEyebrow'),
          contactTitle: document.getElementById('contactTitle'),
          contactIntro: document.getElementById('contactIntro'),
          faqEyebrow: document.getElementById('faqEyebrow'),
          faqTitle: document.getElementById('faqTitle'),
          faqList: document.getElementById('faqList'),
          footerTagline: document.getElementById('footerTagline'),
          addressLabel: document.getElementById('addressLabel'),
          phoneLabel: document.getElementById('phoneLabel'),
          emailLabel: document.getElementById('emailLabel'),
          hoursLabel: document.getElementById('hoursLabel'),
          formTitle: document.getElementById('formTitle'),
          formIntro: document.getElementById('formIntro'),
          formNameLabel: document.getElementById('formNameLabel'),
          formPhoneLabel: document.getElementById('formPhoneLabel'),
          formMessageLabel: document.getElementById('formMessageLabel'),
          formSubmitButton: document.getElementById('formSubmitButton'),
          contactName: document.getElementById('contactName'),
          contactPhone: document.getElementById('contactPhone'),
          contactMessage: document.getElementById('contactMessage'),
          openMapLink: document.getElementById('openMapLink'),
          callNowLink: document.getElementById('callNowLink'),
          viewReviewsButton: document.getElementById('viewReviewsButton'),
          writeReviewButton: document.getElementById('writeReviewButton'),
          reviewsBadge: document.getElementById('reviewsBadge'),
          contactBadge: document.getElementById('contactBadge'),
          languageSwitcher: document.getElementById('languageSwitcher'),
          navToggle: document.getElementById('navToggle'),
          whatsappForm: document.getElementById('whatsappForm'),
          welcomeSplash: document.getElementById('welcomeSplash'),
          welcomeContinueButton: document.getElementById('welcomeContinueButton'),
          welcomeCloseButton: document.getElementById('welcomeCloseButton'),
          lightbox: document.getElementById('lightbox'),
          lightboxImage: document.getElementById('lightboxImage'),
          lightboxCaption: document.getElementById('lightboxCaption'),
          lightboxClose: document.getElementById('lightboxClose'),
          galleryGrid: document.getElementById('galleryGrid'),
          dragGallery: document.getElementById('dragGallery'),
          quoteTrack: document.getElementById('quoteTrack'),
          quoteDots: document.getElementById('quoteDots'),
          quotePrev: document.getElementById('quotePrev'),
          quoteNext: document.getElementById('quoteNext'),
          menuFilters: document.getElementById('menuFilters'),
          menuFilterAll: document.getElementById('menuFilterAll'),
          menuFilterCoffee: document.getElementById('menuFilterCoffee'),
          menuFilterPastry: document.getElementById('menuFilterPastry'),
          menuFilterExperience: document.getElementById('menuFilterExperience'),
          whatsappFab: document.getElementById('whatsappFab'),
          backToTop: document.getElementById('backToTop'),
          year: document.getElementById('year'),
        };

        const setText = (element, value) => {
          if (element && typeof value === 'string') {
            element.textContent = value;
          }
        };

        const escapeAttribute = (value) => escapeHtml(value);

        const getGalleryItem = (index) => {
          if (!Array.isArray(siteConfig.gallery) || siteConfig.gallery.length === 0) {
            return null;
          }

          return siteConfig.gallery[index % siteConfig.gallery.length] || null;
        };

        const resolveCoffeeCardCategory = (item, index) => {
          const value = `${item?.title || ''} ${item?.description || ''}`.toLowerCase();
          if (value.includes('croissant') || value.includes('pastry') || value.includes('dessert') || value.includes('cookie') || value.includes('viennois') || value.includes('gourmand')) {
            return 'pastry';
          }

          if (value.includes('space') || value.includes('cowork') || value.includes('event') || value.includes('group') || value.includes('ambiance') || value.includes('experience')) {
            return 'experience';
          }

          return index % 3 === 2 ? 'experience' : 'coffee';
        };

        const renderCards = (container, items, className) => {
          if (!container || !Array.isArray(items)) {
            return;
          }

          const renderStyle = container.dataset.renderStyle || 'default';

          if (renderStyle === 'restaurant-menu') {
            container.innerHTML = items
              .map((item, index) => {
                const galleryItem = getGalleryItem(index + 1) || getGalleryItem(index);
                const imageHtml = galleryItem?.src
                  ? `
                    <img
                      src="${escapeAttribute(galleryItem.src)}"
                      alt="${escapeAttribute(`${siteConfig.businessName || 'Business'} - ${item.title || ''}`)}"
                      loading="lazy"
                    />
                  `
                  : '';

                return `
                  <article class="menu-entry">
                    ${imageHtml}
                    <div class="menu-entry__body">
                      <div class="menu-entry__top">
                        <span class="menu-entry__index">${String(index + 1).padStart(2, '0')}</span>
                        <h3>${escapeHtml(item.title || '')}</h3>
                      </div>
                      <p>${escapeHtml(item.description || '')}</p>
                    </div>
                  </article>
                `;
              })
              .join('');
            return;
          }

          if (renderStyle === 'coffee-process') {
            const icons = [
              'fa-solid fa-seedling',
              'fa-solid fa-fire-flame-curved',
              'fa-solid fa-mug-hot',
              'fa-solid fa-star',
            ];

            container.innerHTML = items
              .slice(0, 4)
              .map((item, index) => `
                <article class="process-step">
                  <div class="process-icon"><i class="${icons[index % icons.length]}"></i></div>
                  <h4>${String(index + 1).padStart(2, '0')}. ${escapeHtml(item.title || '')}</h4>
                  <p>${escapeHtml(item.description || '')}</p>
                </article>
              `)
              .join('');
            return;
          }

          if (renderStyle === 'coffee-menu') {
            container.innerHTML = items
              .map((item, index) => {
                const galleryItem = getGalleryItem(index + 1) || getGalleryItem(index);
                const category = resolveCoffeeCardCategory(item, index);
                const imageHtml = galleryItem?.src
                  ? `
                    <div class="menu-card-img">
                      <img
                        src="${escapeAttribute(galleryItem.src)}"
                        alt="${escapeAttribute(`${siteConfig.businessName || 'Business'} - ${item.title || ''}`)}"
                        loading="lazy"
                      />
                    </div>
                  `
                  : '<div class="menu-card-img"></div>';

                return `
                  <article class="menu-card" data-category="${category}">
                    ${imageHtml}
                    <span class="menu-price">${String(index + 1).padStart(2, '0')}</span>
                    <h4>${escapeHtml(item.title || '')}</h4>
                    <p>${escapeHtml(item.description || '')}</p>
                  </article>
                `;
              })
              .join('');
            return;
          }

          container.innerHTML = items
            .map(
              (item, index) => `
                <article class="${className}">
                  <span class="card-kicker">${String(index + 1).padStart(2, '0')}</span>
                  <h3>${escapeHtml(item.title || '')}</h3>
                  <p>${escapeHtml(item.description || '')}</p>
                </article>
              `,
            )
            .join('');
        };

        const renderFaq = (container, items) => {
          if (!container || !Array.isArray(items)) {
            return;
          }

          container.innerHTML = items
            .map(
              (item, index) => `
                <details class="faq-item" ${index === 0 ? 'open' : ''}>
                  <summary>${escapeHtml(item.question || '')}</summary>
                  <p>${escapeHtml(item.answer || '')}</p>
                </details>
              `,
            )
            .join('');
        };

        const updateGalleryCaptions = (captions) => {
          if (!refs.galleryGrid || !Array.isArray(captions)) {
            return;
          }

          refs.galleryGrid.querySelectorAll('figcaption').forEach((captionElement, index) => {
            captionElement.textContent = captions[index] || captions[captions.length - 1] || '';
          });
        };

        const applyCoffeeFilterLabels = (language) => {
          const labels = {
            fr: { all: 'Tout', coffee: 'Boissons', pastry: 'Gourmand', experience: 'Experience' },
            en: { all: 'All', coffee: 'Drinks', pastry: 'Pastries', experience: 'Experience' },
            ar: { all: 'الكل', coffee: 'المشروبات', pastry: 'الحلويات', experience: 'التجربة' },
          };

          const current = labels[language] || labels.fr;
          setText(refs.menuFilterAll, current.all);
          setText(refs.menuFilterCoffee, current.coffee);
          setText(refs.menuFilterPastry, current.pastry);
          setText(refs.menuFilterExperience, current.experience);
        };

        const applyLanguage = (language) => {
          if (!state.translations?.content?.[language]) {
            return;
          }

          const translation = state.translations.content[language];
          state.language = language;
          localStorage.setItem('lead-radar-language', language);

          document.documentElement.lang = language;
          const isRtl = language === 'ar';
          document.documentElement.dir = isRtl ? 'rtl' : 'ltr';
          document.body.dataset.dir = isRtl ? 'rtl' : 'ltr';

          setText(refs.title, translation.metaTitle);
          if (refs.metaDescription && translation.metaDescription) {
            refs.metaDescription.setAttribute('content', translation.metaDescription);
          }

          setText(refs.navAbout, translation.nav.about);
          setText(refs.navServices, translation.nav.services);
          setText(refs.navGallery, translation.nav.gallery);
          setText(refs.navReviews, translation.nav.reviews);
          setText(refs.navContact, translation.nav.contact);
          setText(refs.languageLabel, translation.ui.languageLabel);

          setText(refs.heroEyebrow, translation.hero.eyebrow);
          setText(refs.heroTitle, translation.hero.title);
          setText(refs.heroSubtitle, translation.hero.subtitle);
          setText(refs.heroDescription, translation.hero.description);
          setText(refs.heroPrimaryCta, translation.hero.primaryCta);
          setText(refs.heroSecondaryCta, translation.hero.secondaryCta);
          setText(refs.heroRatingLabel, translation.ui.ratingLabel);
          setText(refs.heroContactLabel, translation.ui.contactBadge);
          setText(refs.heroAddressLabel, translation.ui.addressLabel);

          setText(refs.aboutEyebrow, translation.about.eyebrow);
          setText(refs.aboutTitle, translation.about.title);
          setText(refs.aboutBody, translation.about.body);

          setText(refs.servicesEyebrow, translation.services.eyebrow);
          setText(refs.servicesTitle, translation.services.title);
          setText(refs.servicesIntro, translation.services.intro);

          setText(refs.highlightsEyebrow, translation.highlights.eyebrow);
          setText(refs.highlightsTitle, translation.highlights.title);

          setText(refs.galleryEyebrow, translation.gallery.eyebrow);
          setText(refs.galleryTitle, translation.gallery.title);
          setText(refs.galleryIntro, translation.gallery.intro);

          setText(refs.reviewsEyebrow, translation.reviews.eyebrow);
          setText(refs.reviewsTitle, translation.reviews.title);
          setText(refs.reviewsSummary, translation.reviews.summary);

          setText(refs.contactEyebrow, translation.contact.eyebrow);
          setText(refs.contactTitle, translation.contact.title);
          setText(refs.contactIntro, translation.contact.intro);

          setText(refs.faqEyebrow, translation.faq.eyebrow);
          setText(refs.faqTitle, translation.faq.title);

          setText(refs.footerTagline, translation.footer.tagline);
          setText(refs.addressLabel, translation.ui.addressLabel);
          setText(refs.phoneLabel, translation.ui.phoneLabel);
          setText(refs.emailLabel, translation.ui.emailLabel);
          setText(refs.hoursLabel, translation.ui.hoursLabel);
          setText(refs.formTitle, translation.form.title);
          setText(refs.formIntro, translation.form.intro);
          setText(refs.formNameLabel, translation.ui.formNameLabel);
          setText(refs.formPhoneLabel, translation.ui.formPhoneLabel);
          setText(refs.formMessageLabel, translation.ui.formMessageLabel);
          setText(refs.formSubmitButton, translation.ui.formSubmitLabel);
          setText(refs.openMapLink, translation.ui.viewOnMaps);
          setText(refs.callNowLink, translation.ui.callNow);
          setText(refs.viewReviewsButton, translation.ui.viewReviews);
          setText(refs.writeReviewButton, translation.ui.writeReview);
          setText(refs.reviewsBadge, translation.ui.reviewBadge);
          setText(refs.contactBadge, translation.ui.contactBadge);

          if (refs.contactName) {
            refs.contactName.placeholder = translation.ui.formNamePlaceholder || '';
          }

          if (refs.contactPhone) {
            refs.contactPhone.placeholder = translation.ui.formPhonePlaceholder || '';
          }

          if (refs.contactMessage) {
            refs.contactMessage.placeholder = translation.ui.formMessagePlaceholder || '';
          }

          renderCards(refs.servicesGrid, translation.services.items, 'info-card');
          renderCards(refs.highlightsGrid, translation.highlights.items, 'highlight-card');
          renderFaq(refs.faqList, translation.faq.items);
          updateGalleryCaptions(translation.gallery.captions);
          applyCoffeeFilterLabels(language);

          if (refs.languageSwitcher) {
            refs.languageSwitcher.value = language;
          }
        };

        const handleWhatsAppForm = () => {
          if (!refs.whatsappForm) {
            return;
          }

          refs.whatsappForm.addEventListener('submit', (event) => {
            event.preventDefault();

            const name = refs.contactName?.value?.trim() || '';
            const phone = refs.contactPhone?.value?.trim() || '';
            const message = refs.contactMessage?.value?.trim() || '';
            const whatsappNumber = String(siteConfig.whatsappNumber || siteConfig.phoneNumber || '')
              .replace(/[^\d]/g, '');

            if (!whatsappNumber) {
              window.alert('No WhatsApp number is available for this business.');
              return;
            }

            const payload = [
              name ? `Name: ${name}` : '',
              phone ? `Phone: ${phone}` : '',
              message || refs.contactMessage?.placeholder || '',
            ]
              .filter(Boolean)
              .join('\n');

            const whatsappUrl = `https://wa.me/${whatsappNumber}?text=${encodeURIComponent(payload)}`;
            window.open(whatsappUrl, '_blank', 'noopener,noreferrer');
          });
        };

        const handleLightbox = () => {
          if (!refs.galleryGrid || !refs.lightbox || !refs.lightboxImage || !refs.lightboxCaption) {
            return;
          }

          refs.galleryGrid.addEventListener('click', (event) => {
            const figure = event.target instanceof HTMLElement
              ? event.target.closest('figure')
              : null;

            if (!figure) {
              return;
            }

            const image = figure.querySelector('img');
            const caption = figure.querySelector('figcaption');

            if (!(image instanceof HTMLImageElement)) {
              return;
            }

            refs.lightboxImage.src = image.src;
            refs.lightboxImage.alt = image.alt;
            refs.lightboxCaption.textContent = caption?.textContent || '';
            refs.lightbox.hidden = false;
          });

          refs.lightboxClose?.addEventListener('click', () => {
            refs.lightbox.hidden = true;
          });

          refs.lightbox.addEventListener('click', (event) => {
            if (event.target === refs.lightbox) {
              refs.lightbox.hidden = true;
            }
          });
        };

        const handleCoffeeMenuFilters = () => {
          if (!refs.menuFilters) {
            return;
          }

          refs.menuFilters.addEventListener('click', (event) => {
            const button = event.target instanceof HTMLElement
              ? event.target.closest('.filter-chip')
              : null;

            if (!(button instanceof HTMLElement)) {
              return;
            }

            const filter = button.dataset.filter || 'all';
            refs.menuFilters.querySelectorAll('.filter-chip').forEach((chip) => chip.classList.remove('active'));
            button.classList.add('active');

            document.querySelectorAll('.menu-card').forEach((card) => {
              const category = card instanceof HTMLElement ? card.dataset.category : '';
              const visible = filter === 'all' || category === filter;
              card.classList.toggle('hide', !visible);
            });
          });
        };

        const handleCoffeeGalleryDrag = () => {
          if (!refs.dragGallery) {
            return;
          }

          let isDown = false;
          let startX = 0;
          let scrollLeft = 0;

          refs.dragGallery.addEventListener('mousedown', (event) => {
            isDown = true;
            refs.dragGallery.classList.add('dragging');
            startX = event.pageX - refs.dragGallery.offsetLeft;
            scrollLeft = refs.dragGallery.scrollLeft;
          });

          ['mouseleave', 'mouseup'].forEach((eventName) => {
            refs.dragGallery.addEventListener(eventName, () => {
              isDown = false;
              refs.dragGallery.classList.remove('dragging');
            });
          });

          refs.dragGallery.addEventListener('mousemove', (event) => {
            if (!isDown) {
              return;
            }

            event.preventDefault();
            const x = event.pageX - refs.dragGallery.offsetLeft;
            const walk = (x - startX) * 1.4;
            refs.dragGallery.scrollLeft = scrollLeft - walk;
          });
        };

        const handleQuoteCarousel = () => {
          if (!refs.quoteTrack || !refs.quoteDots || !refs.quotePrev || !refs.quoteNext) {
            return;
          }

          const slides = Array.from(refs.quoteTrack.querySelectorAll('.quote-slide'));
          if (slides.length === 0) {
            return;
          }

          let currentIndex = 0;
          let intervalId = null;

          const syncDots = () => {
            refs.quoteDots.innerHTML = '';
            slides.forEach((_, index) => {
              const dot = document.createElement('span');
              dot.classList.toggle('active', index === currentIndex);
              dot.addEventListener('click', () => goToSlide(index));
              refs.quoteDots.appendChild(dot);
            });
          };

          const goToSlide = (index) => {
            slides[currentIndex]?.classList.remove('active');
            currentIndex = (index + slides.length) % slides.length;
            slides[currentIndex]?.classList.add('active');
            Array.from(refs.quoteDots.children).forEach((dot, dotIndex) => {
              dot.classList.toggle('active', dotIndex === currentIndex);
            });
          };

          refs.quotePrev.addEventListener('click', () => goToSlide(currentIndex - 1));
          refs.quoteNext.addEventListener('click', () => goToSlide(currentIndex + 1));

          syncDots();

          if (slides.length > 1) {
            intervalId = window.setInterval(() => goToSlide(currentIndex + 1), 6000);
            const carousel = refs.quoteTrack.closest('.quote-carousel');
            carousel?.addEventListener('mouseenter', () => {
              if (intervalId) {
                window.clearInterval(intervalId);
                intervalId = null;
              }
            });
            carousel?.addEventListener('mouseleave', () => {
              if (!intervalId) {
                intervalId = window.setInterval(() => goToSlide(currentIndex + 1), 6000);
              }
            });
          }
        };

        const handleNavigationToggle = () => {
          if (!refs.navToggle) {
            return;
          }

          const navPanel = document.getElementById('navLinks') || document.getElementById('navMenu');

          const closeNavigation = () => {
            document.body.dataset.navOpen = 'false';
            refs.navToggle.setAttribute('aria-expanded', 'false');
            refs.navToggle.classList.remove('active');
            navPanel?.classList.remove('active');
          };

          refs.navToggle.addEventListener('click', () => {
            const nextState = document.body.dataset.navOpen === 'true' ? 'false' : 'true';
            document.body.dataset.navOpen = nextState;
            refs.navToggle.setAttribute('aria-expanded', String(nextState === 'true'));
            refs.navToggle.classList.toggle('active', nextState === 'true');
            navPanel?.classList.toggle('active', nextState === 'true');
          });

          document.querySelectorAll('#navLinks a, #navMenu a').forEach((link) => {
            link.addEventListener('click', closeNavigation);
          });
        };

        const handleWelcomeSplash = () => {
          const params = new URLSearchParams(window.location.search);
          const shouldSkipSplash = params.get('preview') === '1' || params.get('preview') === 'true';

          if (shouldSkipSplash) {
            document.body.dataset.splash = 'closed';
            return;
          }

          document.body.dataset.splash = 'open';

          const closeSplash = () => {
            document.body.dataset.splash = 'closed';
          };

          refs.welcomeContinueButton?.addEventListener('click', closeSplash);
          refs.welcomeCloseButton?.addEventListener('click', closeSplash);
        };

        const handleHeaderState = () => {
          const syncHeaderState = () => {
            const isScrolled = window.scrollY > 18;
            document.body.dataset.scrolled = isScrolled ? 'true' : 'false';
            refs.navbar?.classList.toggle('scrolled', isScrolled);
          };

          syncHeaderState();
          window.addEventListener('scroll', syncHeaderState, { passive: true });
        };

        const handleBackToTop = () => {
          if (!refs.backToTop) {
            return;
          }

          const syncBackToTop = () => {
            refs.backToTop.classList.toggle('visible', window.scrollY > 520);
          };

          refs.backToTop.addEventListener('click', () => {
            window.scrollTo({ top: 0, behavior: 'smooth' });
          });

          syncBackToTop();
          window.addEventListener('scroll', syncBackToTop, { passive: true });
        };

        const handleScrollReveal = () => {
          const targets = [
            ...document.querySelectorAll('[data-reveal], .content-section, .site-footer, .hero-card, .hero-fact, .story-note, .story-fact, .info-card, .highlight-card, .review-quote, .contact-card, .map-card, .faq-item, .media-card, .about-images, .about-content, .menu-entry, .gallery-item, .testimonial-card, .contact-info-card, .contact-form-wrap, .contact-map-wrap, .footer-col, .zigzag-row, .value-pill, .process-step, .menu-card, .quote-carousel, .contact-panel'),
          ];

          if (targets.length === 0) {
            return;
          }

          if (!('IntersectionObserver' in window) || window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            targets.forEach((target) => {
              if (target instanceof HTMLElement && target.dataset.reveal) {
                target.classList.add('revealed');
              } else {
                target.classList.add('is-visible');
              }
            });
            return;
          }

          document.body.dataset.motionReady = 'true';

          const observer = new IntersectionObserver(
            (entries) => {
              entries.forEach((entry) => {
                if (!entry.isIntersecting) {
                  return;
                }

                if (entry.target instanceof HTMLElement && entry.target.dataset.reveal) {
                  const delay = Number(entry.target.dataset.revealDelay || 0);
                  window.setTimeout(() => entry.target.classList.add('revealed'), delay);
                } else {
                  entry.target.classList.add('is-visible');
                }
                observer.unobserve(entry.target);
              });
            },
            {
              rootMargin: '0px 0px -10% 0px',
              threshold: 0.16,
            },
          );

          targets.forEach((target, index) => {
            if (!(target instanceof HTMLElement) || target.dataset.reveal) {
              observer.observe(target);
              return;
            }

            target.classList.add('reveal-target');
            target.style.setProperty('--reveal-delay', `${Math.min(index * 36, 260)}ms`);
            observer.observe(target);
          });
        };

        const handleHeroMotion = () => {
          if (!(refs.heroSection instanceof HTMLElement) || !(refs.heroVisual instanceof HTMLElement)) {
            return;
          }

          if (window.matchMedia('(prefers-reduced-motion: reduce)').matches) {
            return;
          }

          const resetVisual = () => {
            refs.heroVisual.style.transform = '';
          };

          refs.heroSection.addEventListener('pointermove', (event) => {
            const bounds = refs.heroSection.getBoundingClientRect();
            const offsetX = ((event.clientX - bounds.left) / bounds.width - 0.5) * 18;
            const offsetY = ((event.clientY - bounds.top) / bounds.height - 0.5) * 14;
            refs.heroVisual.style.transform = `translate3d(${offsetX}px, ${offsetY}px, 0) scale(1.01)`;
          });

          refs.heroSection.addEventListener('pointerleave', resetVisual);
        };

        const escapeHtml = (value) =>
          String(value)
            .replaceAll('&', '&amp;')
            .replaceAll('<', '&lt;')
            .replaceAll('>', '&gt;')
            .replaceAll('"', '&quot;')
            .replaceAll("'", '&#39;');

        const init = async () => {
          try {
            const response = await fetch('assets/translations/i18n.json', { cache: 'no-store' });
            state.translations = await response.json();
            applyLanguage(state.language);
          } catch {
            // The default language is already present in the HTML.
          }

          refs.languageSwitcher?.addEventListener('change', (event) => {
            applyLanguage(event.target.value);
          });

          handleWelcomeSplash();
          handleHeaderState();
          handleNavigationToggle();
          handleBackToTop();
          handleWhatsAppForm();
          handleLightbox();
          handleCoffeeMenuFilters();
          handleCoffeeGalleryDrag();
          handleQuoteCarousel();
          handleScrollReveal();
          handleHeroMotion();
          if (refs.year) {
            refs.year.textContent = String(new Date().getFullYear());
          }
        };

        init();
        """;
    }

    private ThemeChoice BuildTheme(
        TemplateDefinition template,
        NormalizedBusiness business,
        string colorMood,
        string fontDirection)
    {
        var fontPair = SelectFontPair(fontDirection, template.Id);
        var palette = SelectPalette(template, business, colorMood);

        return new ThemeChoice(
            FontPair: fontPair,
            PrimaryColor: palette.PrimaryColor,
            SecondaryColor: palette.SecondaryColor,
            AccentColor: palette.AccentColor,
            Background: palette.Background,
            Surface: palette.Surface,
            SurfaceAlt: palette.SurfaceAlt,
            TextColor: palette.TextColor,
            MutedText: palette.MutedText,
            BorderColor: palette.BorderColor,
            ButtonTextColor: palette.ButtonTextColor,
            RadiusLarge: template.IsDark ? "32px" : "28px",
            RadiusMedium: template.IsDark ? "22px" : "18px",
            RadiusSmall: template.IsDark ? "18px" : "14px",
            SectionSpacing: template.Id switch
            {
                "coffee-shop-signature" => "22px",
                "restaurant-signature" => "22px",
                "creative" => "24px",
                "premium" => "22px",
                "corporate" => "18px",
                _ => "20px"
            },
            HeroGradient: template.IsDark
                ? $"radial-gradient(circle at top right, {ToRgba(palette.AccentColor, 0.24)}, transparent 36%), linear-gradient(135deg, {ToRgba(palette.PrimaryColor, 0.14)}, {ToRgba(palette.SecondaryColor, 0.12)})"
                : $"radial-gradient(circle at top right, {ToRgba(palette.AccentColor, 0.18)}, transparent 34%), linear-gradient(135deg, {ToRgba(palette.PrimaryColor, 0.08)}, {ToRgba(palette.SecondaryColor, 0.05)})",
            ShadowStyle: template.IsDark
                ? $"0 30px 70px {ToRgba(palette.Background, 0.42)}"
                : "0 26px 64px rgba(15, 23, 42, 0.12)",
            GlowColor: ToRgba(palette.AccentColor, template.IsDark ? 0.16 : 0.12));
    }

    private PaletteDefinition SelectPalette(
        TemplateDefinition template,
        NormalizedBusiness business,
        string colorMood)
    {
        var categoryTag = Slugify(business.Category);
        var matchingPalettes = PaletteDefinitions
            .Where(palette => palette.IsDark == template.IsDark)
            .Where(palette =>
                palette.Tags.Contains(colorMood, StringComparer.OrdinalIgnoreCase) ||
                palette.Tags.Contains(categoryTag, StringComparer.OrdinalIgnoreCase) ||
                palette.Tags.Contains(template.Id, StringComparer.OrdinalIgnoreCase))
            .ToList();

        if (matchingPalettes.Count == 0)
        {
            matchingPalettes = PaletteDefinitions
                .Where(palette => palette.IsDark == template.IsDark)
                .ToList();
        }

        return matchingPalettes[Random.Shared.Next(matchingPalettes.Count)];
    }

    private FontPair SelectFontPair(string fontDirection, string templateId)
    {
        if (templateId is "restaurant-signature" or "coffee-shop-signature")
        {
            return FontPairs.First(fontPair => fontPair.DisplayName == "Fraunces");
        }

        var matchingPairs = FontPairs
            .Where(fontPair => fontDirection switch
            {
                "editorial-serif" => fontPair.DisplayName is "Playfair Display" or "Fraunces",
                "geometric-modern" => fontPair.DisplayName is "Space Grotesk" or "Sora",
                "bold-display" => fontPair.DisplayName is "Syne" or "Space Grotesk",
                "professional-clean" => fontPair.DisplayName is "Sora" or "Plus Jakarta Sans",
                "premium-classic" => fontPair.DisplayName is "Fraunces" or "Playfair Display",
                _ => templateId switch
                {
                    "coffee-shop-signature" => fontPair.DisplayName is "Fraunces" or "Playfair Display",
                    "restaurant-signature" => fontPair.DisplayName is "Playfair Display" or "Fraunces",
                    "luxury" => fontPair.DisplayName is "Playfair Display" or "Fraunces",
                    "creative" => fontPair.DisplayName is "Syne" or "Space Grotesk",
                    "corporate" => fontPair.DisplayName is "Sora" or "Plus Jakarta Sans",
                    _ => true
                }
            })
            .ToList();

        if (matchingPairs.Count == 0)
        {
            matchingPairs = FontPairs.ToList();
        }

        return matchingPairs[Random.Shared.Next(matchingPairs.Count)];
    }

    private string ResolveDefaultColorMood(
        string templateId,
        NormalizedBusiness business)
    {
        var categoryTag = Slugify(business.Category);

        return (templateId, categoryTag) switch
        {
            ("coffee-shop-signature", _) => "warm-terra",
            ("restaurant-signature", _) => "warm-terra",
            ("luxury", _) => "luxury-gold",
            ("creative", "bar") => "nightlife-neon",
            ("creative", _) => "warm-terra",
            ("corporate", _) => "monochrome-ink",
            ("premium", _) => "premium-cobalt",
            (_, "beauty-salon") => "rose-boutique",
            (_, "clothing-store") => "rose-boutique",
            (_, "grocery-store") => "botanical-green",
            (_, "bakery") => "warm-terra",
            (_, "restaurant") => "warm-terra",
            (_, "bar") => "nightlife-neon",
            _ => "coastal-blue"
        };
    }

    private static string ResolveDefaultFontDirection(
        string templateId,
        NormalizedBusiness business)
    {
        _ = business;
        return templateId switch
        {
            "coffee-shop-signature" => "premium-classic",
            "restaurant-signature" => "premium-classic",
            "luxury" => "editorial-serif",
            "creative" => "bold-display",
            "corporate" => "professional-clean",
            "premium" => "premium-classic",
            _ => "geometric-modern"
        };
    }

    private static string ResolveDefaultMotionStyle(string templateId)
    {
        return templateId switch
        {
            "coffee-shop-signature" => "gentle",
            "restaurant-signature" => "gentle",
            "luxury" => "dramatic",
            "creative" => "energetic",
            "corporate" => "gentle",
            "minimal" => "minimal",
            _ => "gentle"
        };
    }

    private static IReadOnlyList<string> GetDefaultSectionOrder(string templateId)
    {
        return templateId switch
        {
            "coffee-shop-signature" => ["about", "highlights", "services", "gallery", "reviews", "contact", "faq"],
            "restaurant-signature" => ["about", "services", "gallery", "highlights", "reviews", "contact", "faq"],
            "luxury" => ["about", "highlights", "services", "gallery", "reviews", "contact", "faq"],
            "minimal" => ["services", "about", "gallery", "highlights", "contact", "reviews", "faq"],
            "creative" => ["gallery", "highlights", "services", "about", "faq", "reviews", "contact"],
            "corporate" => ["about", "services", "reviews", "gallery", "highlights", "contact", "faq"],
            _ => ["highlights", "about", "services", "contact", "gallery", "reviews", "faq"]
        };
    }

    private ThemeChoice BuildTheme(TemplateDefinition template)
    {
        var fontPair = FontPairs[Random.Shared.Next(FontPairs.Count)];

        return template.Id switch
        {
            "luxury" => BuildDarkTheme(template, fontPair, baseHue: Random.Shared.Next(12, 42), accentShift: 38),
            "minimal" => BuildLightTheme(template, fontPair, baseHue: Random.Shared.Next(188, 238), accentShift: 42),
            "creative" => BuildDarkTheme(template, fontPair, baseHue: Random.Shared.Next(280, 342), accentShift: 76),
            "corporate" => BuildLightTheme(template, fontPair, baseHue: Random.Shared.Next(205, 236), accentShift: 24),
            _ => BuildDarkTheme(template, fontPair, baseHue: Random.Shared.Next(192, 238), accentShift: 58)
        };
    }

    private ThemeChoice BuildDarkTheme(
        TemplateDefinition template,
        FontPair fontPair,
        int baseHue,
        int accentShift)
    {
        var primary = HslToHex(baseHue, 84, 62);
        var secondary = HslToHex((baseHue + accentShift) % 360, 78, 58);
        var accent = HslToHex((baseHue + 110) % 360, 76, 66);
        var background = HslToHex((baseHue + 232) % 360, 42, 8);
        var surface = HslToHex((baseHue + 228) % 360, 34, 11);
        var surfaceAlt = HslToHex((baseHue + 222) % 360, 32, 16);

        return new ThemeChoice(
            FontPair: fontPair,
            PrimaryColor: primary,
            SecondaryColor: secondary,
            AccentColor: accent,
            Background: background,
            Surface: surface,
            SurfaceAlt: surfaceAlt,
            TextColor: "#f8fbff",
            MutedText: "rgba(220, 232, 250, 0.76)",
            BorderColor: "rgba(148, 163, 184, 0.18)",
            ButtonTextColor: "#08101f",
            RadiusLarge: "32px",
            RadiusMedium: "22px",
            RadiusSmall: "18px",
            SectionSpacing: "22px",
            HeroGradient: $"radial-gradient(circle at top right, {ToRgba(accent, 0.24)}, transparent 36%), linear-gradient(125deg, {ToRgba(primary, 0.14)}, {ToRgba(secondary, 0.12)})",
            ShadowStyle: $"0 30px 70px {ToRgba(background, 0.42)}",
            GlowColor: ToRgba(accent, 0.16));
    }

    private ThemeChoice BuildLightTheme(
        TemplateDefinition template,
        FontPair fontPair,
        int baseHue,
        int accentShift)
    {
        var primary = HslToHex(baseHue, 72, 48);
        var secondary = HslToHex((baseHue + accentShift) % 360, 72, 46);
        var accent = HslToHex((baseHue + 142) % 360, 74, 58);
        var background = HslToHex((baseHue + 210) % 360, 42, 97);
        var surface = "#ffffff";
        var surfaceAlt = HslToHex((baseHue + 208) % 360, 28, 93);

        return new ThemeChoice(
            FontPair: fontPair,
            PrimaryColor: primary,
            SecondaryColor: secondary,
            AccentColor: accent,
            Background: background,
            Surface: surface,
            SurfaceAlt: surfaceAlt,
            TextColor: "#0f172a",
            MutedText: "rgba(51, 65, 85, 0.8)",
            BorderColor: "rgba(148, 163, 184, 0.22)",
            ButtonTextColor: "#f8fbff",
            RadiusLarge: "30px",
            RadiusMedium: "20px",
            RadiusSmall: "16px",
            SectionSpacing: "20px",
            HeroGradient: $"radial-gradient(circle at top right, {ToRgba(accent, 0.18)}, transparent 34%), linear-gradient(135deg, {ToRgba(primary, 0.06)}, {ToRgba(secondary, 0.04)})",
            ShadowStyle: "0 26px 64px rgba(15, 23, 42, 0.12)",
            GlowColor: ToRgba(accent, 0.12));
    }

    private string BuildFallbackLogoSvg(NormalizedBusiness business, ThemeChoice theme)
    {
        var initials = GetInitials(business.Name);
        var businessName = SecurityElementEscape(business.Name);
        var category = SecurityElementEscape(business.Category);

        return $$"""
        <svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 512 512" role="img" aria-label="{{businessName}}">
          <defs>
            <linearGradient id="logoGradient" x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stop-color="{{theme.PrimaryColor}}" />
              <stop offset="100%" stop-color="{{theme.SecondaryColor}}" />
            </linearGradient>
          </defs>
          <rect width="512" height="512" rx="140" fill="{{theme.SurfaceAlt}}" />
          <rect x="36" y="36" width="440" height="440" rx="120" fill="url(#logoGradient)" />
          <circle cx="256" cy="184" r="70" fill="{{theme.AccentColor}}" opacity="0.18" />
          <text x="256" y="290" text-anchor="middle" fill="{{theme.ButtonTextColor}}" font-family="Arial, sans-serif" font-size="160" font-weight="800">{{SecurityElementEscape(initials)}}</text>
          <text x="256" y="372" text-anchor="middle" fill="{{theme.ButtonTextColor}}" font-family="Arial, sans-serif" font-size="28" letter-spacing="6">{{category.ToUpperInvariant()}}</text>
        </svg>
        """;
    }

    private string BuildPlaceholderImageSvg(
        NormalizedBusiness business,
        ThemeChoice theme,
        int index,
        string caption)
    {
        var captionText = SecurityElementEscape(caption);
        var businessName = SecurityElementEscape(business.Name);
        var category = SecurityElementEscape(business.Category);
        var location = SecurityElementEscape(business.Address ?? "Local business");

        return $$"""
        <svg xmlns="http://www.w3.org/2000/svg" width="1600" height="1000" viewBox="0 0 1600 1000" role="img" aria-label="{{businessName}} {{captionText}}">
          <defs>
            <linearGradient id="bg{{index}}" x1="0%" y1="0%" x2="100%" y2="100%">
              <stop offset="0%" stop-color="{{theme.PrimaryColor}}" />
              <stop offset="100%" stop-color="{{theme.SecondaryColor}}" />
            </linearGradient>
            <radialGradient id="glow{{index}}" cx="80%" cy="10%" r="70%">
              <stop offset="0%" stop-color="{{theme.AccentColor}}" stop-opacity="0.48" />
              <stop offset="100%" stop-color="{{theme.AccentColor}}" stop-opacity="0" />
            </radialGradient>
          </defs>
          <rect width="1600" height="1000" fill="{{theme.Surface}}" />
          <rect x="52" y="52" width="1496" height="896" rx="64" fill="url(#bg{{index}})" />
          <rect x="112" y="112" width="1376" height="776" rx="52" fill="{{theme.SurfaceAlt}}" opacity="0.76" />
          <circle cx="1240" cy="180" r="220" fill="url(#glow{{index}})" />
          <rect x="150" y="180" width="420" height="420" rx="42" fill="{{theme.Surface}}" opacity="0.95" />
          <rect x="650" y="180" width="790" height="88" rx="26" fill="{{theme.AccentColor}}" opacity="0.16" />
          <rect x="650" y="312" width="560" height="30" rx="15" fill="#ffffff" opacity="0.92" />
          <rect x="650" y="370" width="670" height="24" rx="12" fill="#ffffff" opacity="0.54" />
          <rect x="650" y="420" width="520" height="24" rx="12" fill="#ffffff" opacity="0.42" />
          <rect x="650" y="500" width="790" height="230" rx="38" fill="{{theme.Surface}}" opacity="0.9" />
          <text x="210" y="390" fill="{{theme.TextColor}}" font-family="Arial, sans-serif" font-size="172" font-weight="800">{{SecurityElementEscape(GetInitials(business.Name))}}</text>
          <text x="650" y="248" fill="#ffffff" font-family="Arial, sans-serif" font-size="42" font-weight="700" letter-spacing="10">{{captionText.ToUpperInvariant()}}</text>
          <text x="650" y="348" fill="#ffffff" font-family="Arial, sans-serif" font-size="92" font-weight="800">{{businessName}}</text>
          <text x="650" y="560" fill="{{theme.TextColor}}" font-family="Arial, sans-serif" font-size="46" font-weight="700">{{category}}</text>
          <text x="650" y="636" fill="{{theme.TextColor}}" font-family="Arial, sans-serif" font-size="32">{{location}}</text>
        </svg>
        """;
    }

    private static string BuildMetaTitle(LocalizedWebsiteContent content, SeoContent seo)
    {
        return string.IsNullOrWhiteSpace(seo.Title) ? content.HeroTitle : seo.Title;
    }

    private static string contentBundleSafeMetaTitle(LocalizedWebsiteContent content, NormalizedBusiness business)
    {
        return string.IsNullOrWhiteSpace(content.HeroTitle)
            ? $"{business.Name} | {business.Category}"
            : TrimToLengthSafe(content.HeroTitle, 65);
    }

    private static NormalizedBusiness NormalizeRequest(
        WebsiteGenerationRequest request,
        GooglePlaceWebsiteEnrichmentService.WebsiteGenerationEnrichment? enrichment,
        GoogleMapsPublicLeadEnrichmentService.PublicLeadEnrichment? publicGoogleEnrichment)
    {
        var businessName = CleanText(request.BusinessName);
        if (string.IsNullOrWhiteSpace(businessName))
        {
            throw new ArgumentException("BusinessName is required.", nameof(request));
        }

        var category = CleanText(request.BusinessCategory);
        if (string.IsNullOrWhiteSpace(category))
        {
            category = "Local business";
        }

        var latitude = enrichment?.Latitude ?? request.Latitude;
        var longitude = enrichment?.Longitude ?? request.Longitude;

        var emails = (request.EmailAddresses ?? [])
            .Where(IsValidEmail)
            .Select(static email => email.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var phoneNumber = CleanText(enrichment?.PhoneNumber) ??
                          CleanText(publicGoogleEnrichment?.PhoneNumber) ??
                          CleanText(request.PhoneNumber);
        var whatsappNumber = NormalizeWhatsappNumber(
            enrichment?.PhoneNumber ??
            publicGoogleEnrichment?.PhoneNumber ??
            request.WhatsappNumber ??
            request.PhoneNumber);
        var address = CleanText(request.Address);
        var googleMapsUri = TryGetAbsoluteHttpUri(enrichment?.GoogleMapsUri, out var enrichedMapsUri)
            ? enrichedMapsUri.ToString()
            : TryGetAbsoluteHttpUri(publicGoogleEnrichment?.GoogleMapsUri, out var publicMapsUri)
                ? publicMapsUri.ToString()
                : TryGetAbsoluteHttpUri(request.GoogleMapsUri, out var mapsUri)
                    ? mapsUri.ToString()
                    : BuildGoogleMapsUri(latitude, longitude, address ?? businessName);
        var mapEmbedUri = BuildMapEmbedUri(latitude, longitude, address ?? businessName);

        var openingHours = NormalizeList((request.OpeningHours ?? []).Concat(enrichment?.OpeningHours ?? []).ToList());
        var services = NormalizeList(request.Services);
        if (services.Count == 0)
        {
            services = InferServices(category, "fr");
        }

        var features = NormalizeList((request.Features ?? []).Concat(enrichment?.Features ?? []).ToList());
        if (features.Count == 0)
        {
            features =
            [
                "Contact direct",
                "Presentation claire",
                "Presence locale"
            ];
        }

        var photoUris = NormalizeList((request.PhotoUris ?? []).Concat(enrichment?.PhotoUris ?? []).ToList())
            .Where(IsAcceptableBusinessPhotoUri)
            .ToList();

        var socialLinks = (request.SocialLinks ?? new Dictionary<string, string>())
            .Where(entry => !string.IsNullOrWhiteSpace(entry.Key) && TryGetAbsoluteHttpUri(entry.Value, out _))
            .ToDictionary(
                entry => entry.Key.Trim(),
                entry => entry.Value.Trim(),
                StringComparer.OrdinalIgnoreCase);

        var officialReviewHighlights = (enrichment?.ReviewHighlights ?? [])
            .Select(review => new ReviewHighlight(
                AuthorName: CleanText(review.AuthorName) ?? "Client Google",
                Rating: review.Rating,
                RelativePublishTimeDescription: CleanText(review.RelativePublishTimeDescription),
                Text: CleanText(review.Text) ?? string.Empty,
                GoogleMapsUri: TryGetAbsoluteHttpUri(review.GoogleMapsUri, out var reviewUri)
                    ? reviewUri.ToString()
                    : null));
        var publicReviewHighlights = (publicGoogleEnrichment?.ReviewHighlights ?? [])
            .Select(review => new ReviewHighlight(
                AuthorName: CleanText(review.AuthorName) ?? "Client Google",
                Rating: review.Rating,
                RelativePublishTimeDescription: CleanText(review.RelativePublishTimeDescription),
                Text: CleanText(review.Text) ?? string.Empty,
                GoogleMapsUri: TryGetAbsoluteHttpUri(review.GoogleMapsUri, out var reviewUri)
                    ? reviewUri.ToString()
                    : publicGoogleEnrichment?.ReviewsUri));
        var reviewHighlights = officialReviewHighlights
            .Concat(publicReviewHighlights)
            .Where(static review => !string.IsNullOrWhiteSpace(review.Text))
            .GroupBy(static review => $"{review.AuthorName}\n{review.Text}", StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(3)
            .ToList();

        return new NormalizedBusiness(
            PlaceId: CleanText(enrichment?.GooglePlaceId) ?? CleanText(request.PlaceId) ?? Guid.NewGuid().ToString("N"),
            Name: businessName,
            Slug: Slugify(businessName),
            Category: category,
            PrimaryType: CleanText(request.PrimaryType),
            Description: CleanText(request.Description) ?? CleanText(enrichment?.Description),
            Address: address,
            PhoneNumber: phoneNumber,
            WhatsappNumber: whatsappNumber,
            PrimaryEmail: emails.FirstOrDefault(),
            WebsiteUri: CleanText(request.WebsiteUri) ?? CleanText(enrichment?.WebsiteUri),
            GoogleMapsUri: googleMapsUri,
            MapEmbedUri: mapEmbedUri,
            Latitude: latitude,
            Longitude: longitude,
            Rating: enrichment?.Rating ?? publicGoogleEnrichment?.Rating ?? request.Rating,
            ReviewCount: enrichment?.ReviewCount ?? publicGoogleEnrichment?.ReviewCount ?? request.ReviewCount,
            ReviewsSummary: CleanText(enrichment?.ReviewSummary) ?? CleanText(request.ReviewsSummary),
            ReviewHighlights: reviewHighlights,
            ReviewsUri: CleanText(enrichment?.ReviewsUri) ??
                        CleanText(publicGoogleEnrichment?.ReviewsUri) ??
                        googleMapsUri,
            WriteAReviewUri: CleanText(enrichment?.WriteAReviewUri),
            OpeningHours: openingHours,
            Services: services,
            Features: features,
            PhotoUris: photoUris,
            LogoUri: CleanText(request.LogoUri),
            SocialLinks: socialLinks);
    }

    private async Task<NormalizedBusiness> EnrichBusinessVisualsAsync(
        NormalizedBusiness business,
        CancellationToken cancellationToken)
    {
        var galleryCandidates = new List<WebsiteAssetCandidate>();
        var logoCandidates = new List<WebsiteAssetCandidate>();

        AddGalleryCandidates(galleryCandidates, business.PhotoUris, 420);
        AddLogoCandidate(logoCandidates, business.LogoUri, 520);

        if (TryGetAbsoluteHttpUri(business.WebsiteUri, out var websiteUri))
        {
            try
            {
                var websiteDiscovery = await DiscoverWebsiteVisualsAsync(websiteUri, cancellationToken);
                if (websiteDiscovery is not null)
                {
                    AddGalleryCandidates(galleryCandidates, websiteDiscovery.ImageUris, 300);
                    AddLogoCandidate(logoCandidates, websiteDiscovery.LogoUri, 380);
                }
            }
            catch
            {
                // Ignore visual discovery failures from the official website.
            }
        }

        foreach (var socialUri in business.SocialLinks.Values
                     .Where(uri => TryGetAbsoluteHttpUri(uri, out _))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Take(2))
        {
            try
            {
                var socialDiscovery = await DiscoverWebsiteVisualsAsync(new Uri(socialUri), cancellationToken);
                if (socialDiscovery is null)
                {
                    continue;
                }

                AddGalleryCandidates(galleryCandidates, socialDiscovery.ImageUris, 250);
                AddLogoCandidate(logoCandidates, socialDiscovery.LogoUri, 320);
            }
            catch
            {
                // Ignore visual discovery failures from third-party pages.
            }
        }

        if (TryGetAbsoluteHttpUri(business.GoogleMapsUri, out var mapsUri))
        {
            try
            {
                var mapsDiscovery = await DiscoverWebsiteVisualsAsync(mapsUri, cancellationToken);
                if (mapsDiscovery is not null)
                {
                    AddGalleryCandidates(galleryCandidates, mapsDiscovery.ImageUris, 220);
                }
            }
            catch
            {
                // Ignore visual discovery failures from Google Maps pages.
            }
        }

        if (ShouldSearchInternetImages(business, galleryCandidates))
        {
            try
            {
                galleryCandidates.AddRange(await SearchPublicImageCandidatesAsync(business, cancellationToken));
            }
            catch
            {
                // Ignore public image search failures and fall back to uploaded or generated visuals.
            }
        }

        var mergedPhotoUris = galleryCandidates
            .Where(static candidate => TryGetAbsoluteHttpUri(candidate.Url, out _))
            .OrderByDescending(static candidate => candidate.Score)
            .Select(static candidate => candidate.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var mergedLogoUri = logoCandidates
            .OrderByDescending(static candidate => candidate.Score)
            .Select(static candidate => candidate.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return business with
        {
            PhotoUris = mergedPhotoUris,
            LogoUri = PreferNullable(mergedLogoUri, business.LogoUri)
        };
    }

    private static void AddGalleryCandidates(
        ICollection<WebsiteAssetCandidate> candidates,
        IEnumerable<string> urls,
        int baseScore)
    {
        foreach (var url in urls)
        {
            if (!IsAcceptableBusinessPhotoUri(url))
            {
                continue;
            }

            candidates.Add(new WebsiteAssetCandidate(url.Trim(), baseScore + ScoreDiscoveredImageUrl(url)));
        }
    }

    private static void AddLogoCandidate(
        ICollection<WebsiteAssetCandidate> candidates,
        string? url,
        int baseScore)
    {
        if (!TryGetAbsoluteHttpUri(url, out _))
        {
            return;
        }

        candidates.Add(new WebsiteAssetCandidate(url!.Trim(), baseScore + ScoreDiscoveredImageUrl(url)));
    }

    private static bool ShouldSearchInternetImages(
        NormalizedBusiness business,
        IReadOnlyCollection<WebsiteAssetCandidate> galleryCandidates)
    {
        return galleryCandidates.Count < 6 ||
               (galleryCandidates.Count < 8 && string.IsNullOrWhiteSpace(business.WebsiteUri));
    }

    private async Task<IReadOnlyList<WebsiteAssetCandidate>> SearchPublicImageCandidatesAsync(
        NormalizedBusiness business,
        CancellationToken cancellationToken)
    {
        var candidates = new List<WebsiteAssetCandidate>();
        var queries = BuildPublicImageSearchQueries(business);

        foreach (var query in queries)
        {
            IReadOnlyList<WebsiteAssetCandidate> queryCandidates;
            try
            {
                queryCandidates = await SearchDuckDuckGoImageCandidatesAsync(query, business, cancellationToken);
            }
            catch
            {
                continue;
            }

            candidates.AddRange(queryCandidates);
            if (candidates.Count >= 18)
            {
                break;
            }
        }

        return candidates
            .Where(static candidate => IsAcceptableBusinessPhotoUri(candidate.Url))
            .Where(static candidate => !IsLikelyLogoUrl(candidate.Url))
            .Where(static candidate => !IsLikelyDecorativeImageUrl(candidate.Url))
            .Where(static candidate => !candidate.Url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static candidate => candidate.Score)
            .GroupBy(static candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(10)
            .ToList();
    }

    private async Task<IReadOnlyList<WebsiteAssetCandidate>> SearchDuckDuckGoImageCandidatesAsync(
        string query,
        NormalizedBusiness business,
        CancellationToken cancellationToken)
    {
        var resultPages = await SearchDuckDuckGoResultPagesAsync(query, business, cancellationToken);
        if (resultPages.Count == 0)
        {
            return [];
        }

        var candidates = new List<WebsiteAssetCandidate>();
        foreach (var resultPage in resultPages)
        {
            WebsiteVisualDiscoveryResult? discovery;
            try
            {
                discovery = await DiscoverWebsiteVisualsAsync(new Uri(resultPage.Url), cancellationToken);
            }
            catch
            {
                continue;
            }

            if (discovery is null)
            {
                continue;
            }

            foreach (var imageUrl in discovery.ImageUris)
            {
                candidates.Add(new WebsiteAssetCandidate(
                    imageUrl,
                    resultPage.Score + ScorePublicImageSearchResult(
                        business,
                        imageUrl,
                        resultPage.Url,
                        BuildSearchQuery(resultPage.Title, resultPage.Snippet))));
            }
        }

        return candidates
            .OrderByDescending(static candidate => candidate.Score)
            .GroupBy(static candidate => candidate.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(12)
            .ToList();
    }

    private async Task<IReadOnlyList<WebsiteSearchResultCandidate>> SearchDuckDuckGoResultPagesAsync(
        string query,
        NormalizedBusiness business,
        CancellationToken cancellationToken)
    {
        var requestUri = new Uri($"https://duckduckgo.com/html/?q={Uri.EscapeDataString(query)}&kl=fr-fr");
        var referrer = new Uri("https://duckduckgo.com/");
        var html = await FetchHtmlDocumentAsync(
            requestUri,
            referrer,
            cancellationToken);

        if (string.IsNullOrWhiteSpace(html) ||
            !html.Contains("result__a", StringComparison.OrdinalIgnoreCase))
        {
            html = await TryFetchHtmlWithCurlAsync(requestUri, referrer, cancellationToken);
        }

        if (string.IsNullOrWhiteSpace(html) ||
            !html.Contains("result__a", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }

        var blocks = html.Split(
            "<div class=\"result results_links",
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var results = new List<WebsiteSearchResultCandidate>();
        foreach (var block in blocks.Skip(1))
        {
            var titleMatch = DuckDuckGoResultTitleRegex.Match(block);
            if (!titleMatch.Success)
            {
                continue;
            }

            var resolvedUrl = ExtractDuckDuckGoResultUrl(titleMatch.Groups["href"].Value);
            if (!TryGetAbsoluteHttpUri(resolvedUrl, out var resolvedUri) ||
                !IsUsefulSearchResultPageUri(resolvedUri))
            {
                continue;
            }

            var title = NormalizeHtmlText(titleMatch.Groups["title"].Value);
            var snippetMatch = DuckDuckGoResultSnippetRegex.Match(block);
            var snippet = snippetMatch.Success
                ? NormalizeHtmlText(snippetMatch.Groups["snippet"].Value)
                : null;

            results.Add(new WebsiteSearchResultCandidate(
                resolvedUri.ToString(),
                title,
                snippet,
                ScoreSearchResultPage(business, resolvedUri.ToString(), title, snippet)));
        }

        return results
            .OrderByDescending(static result => result.Score)
            .GroupBy(static result => result.Url, StringComparer.OrdinalIgnoreCase)
            .Select(static group => group.First())
            .Take(10)
            .ToList();
    }

    private static IReadOnlyList<string> BuildPublicImageSearchQueries(NormalizedBusiness business)
    {
        var location = ResolveHeadlineLocation(business.Address, string.Empty);
        var queries = new List<string>
            {
                BuildSearchQuery(business.Name, location, business.Category),
                BuildSearchQuery(business.Name, location, "photos"),
                BuildSearchQuery(business.Name, location, "instagram"),
                BuildSearchQuery(business.Name, location, "facebook"),
                BuildSearchQuery(business.Name, business.Category),
                BuildSearchQuery(business.Name, location, CleanText(business.Description))
            };

        queries.AddRange(
            WebsiteGenerationCreativeDirection
                .GetImageSearchTopics(business.Category, business.PrimaryType)
                .Select(topic => BuildSearchQuery(business.Name, location, topic)));

        if (IsHospitalityBusiness(business))
        {
            queries.AddRange(
            [
                BuildSearchQuery(business.Name, location, "restaurant interieur"),
                BuildSearchQuery(business.Name, location, "restaurant facade"),
                BuildSearchQuery(business.Name, location, "plats signature"),
                BuildSearchQuery(business.Name, location, "menu photos"),
                BuildSearchQuery(business.Name, location, "ambiance")
            ]);
        }

        if (IsCoffeeShopBusiness(business))
        {
            queries.AddRange(
            [
                BuildSearchQuery(business.Name, location, "coffee shop interior"),
                BuildSearchQuery(business.Name, location, "barista latte"),
                BuildSearchQuery(business.Name, location, "pastries"),
                BuildSearchQuery(business.Name, location, "espresso bar"),
                BuildSearchQuery(business.Name, location, "cafe ambiance")
            ]);
        }

        var normalizedQueries = queries
            .Where(static query => !string.IsNullOrWhiteSpace(query))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(10)
            .ToList();

        return normalizedQueries.Count == 0
            ? [business.Name]
            : normalizedQueries;
    }

    private static IReadOnlyList<string> GetRestaurantTemplateFallbackImageUris()
    {
        return
        [
            "https://images.unsplash.com/photo-1517248135467-4c7edcad34c4?auto=format&fit=crop&w=1920&q=80",
            "https://images.unsplash.com/photo-1414235077428-338989a2e8c0?auto=format&fit=crop&w=1400&q=80",
            "https://images.unsplash.com/photo-1577219491135-ce391730fb2c?auto=format&fit=crop&w=1200&q=80",
            "https://images.unsplash.com/photo-1550966871-3ed3cdb5ed0c?auto=format&fit=crop&w=1400&q=80",
            "https://images.unsplash.com/photo-1552566626-52f8b828add9?auto=format&fit=crop&w=1200&q=80",
            "https://images.unsplash.com/photo-1467003909585-2f8a72700288?auto=format&fit=crop&w=1200&q=80"
        ];
    }

    private static IReadOnlyList<string> GetCoffeeTemplateFallbackImageUris()
    {
        return
        [
            "https://images.unsplash.com/photo-1495474472287-4d71bcdd2085?auto=format&fit=crop&w=1920&q=80",
            "https://images.unsplash.com/photo-1521017432531-fbd92d768814?auto=format&fit=crop&w=1400&q=80",
            "https://images.unsplash.com/photo-1461023058943-07fcbe16d735?auto=format&fit=crop&w=1200&q=80",
            "https://images.unsplash.com/photo-1447933601403-0c6688de566e?auto=format&fit=crop&w=1400&q=80",
            "https://images.unsplash.com/photo-1517686748843-bb360cd08434?auto=format&fit=crop&w=1200&q=80",
            "https://images.unsplash.com/photo-1509365465985-25d11c17e812?auto=format&fit=crop&w=1200&q=80"
        ];
    }

    private static string BuildSearchQuery(params string?[] values)
    {
        var query = string.Join(" ", values.Where(static value => !string.IsNullOrWhiteSpace(value)));
        return CollapseWhitespaceRegex.Replace(query, " ").Trim();
    }

    private static string? ExtractDuckDuckGoResultUrl(string? href)
    {
        var value = CleanText(WebUtility.HtmlDecode(href));
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = $"https:{value}";
        }

        if (!TryGetAbsoluteHttpUri(value, out var uri))
        {
            return null;
        }

        if (!uri.Host.Contains("duckduckgo.com", StringComparison.OrdinalIgnoreCase))
        {
            return uri.ToString();
        }

        var query = uri.Query.TrimStart('?');
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var separatorIndex = pair.IndexOf('=');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = pair[..separatorIndex];
            if (!key.Equals("uddg", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var rawTarget = pair[(separatorIndex + 1)..];
            return CleanText(Uri.UnescapeDataString(rawTarget));
        }

        return null;
    }

    private static string? NormalizeHtmlText(string? value)
    {
        var decoded = WebUtility.HtmlDecode(value ?? string.Empty);
        var stripped = HtmlTagRegex.Replace(decoded, " ");
        return CleanText(stripped);
    }

    private static bool IsUsefulSearchResultPageUri(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        var value = uri.ToString().ToLowerInvariant();

        if (host.Contains("duckduckgo.com", StringComparison.Ordinal) ||
            host.Contains("bing.com", StringComparison.Ordinal) ||
            host.Contains("google.com", StringComparison.Ordinal) ||
            host.Contains("youtube.com", StringComparison.Ordinal) ||
            host.Contains("youtu.be", StringComparison.Ordinal))
        {
            return false;
        }

        if (value.Contains("/maps", StringComparison.Ordinal) ||
            value.Contains("accounts.google.com", StringComparison.Ordinal) ||
            value.Contains("/privacy", StringComparison.Ordinal) ||
            value.Contains("/terms", StringComparison.Ordinal))
        {
            return false;
        }

        return true;
    }

    private static int ScoreSearchResultPage(
        NormalizedBusiness business,
        string pageUrl,
        string? title,
        string? snippet)
    {
        var haystack = BuildSearchQuery(pageUrl, title, snippet)?.ToLowerInvariant() ?? string.Empty;
        var score = 220;
        var businessTokens = TokenizeSearchValue(business.Name);
        var categoryTokens = TokenizeSearchValue(business.Category);
        var locationTokens = TokenizeSearchValue(ResolveHeadlineLocation(business.Address, string.Empty));

        score += CountMatchingTokens(haystack, businessTokens) * 34;
        score += CountMatchingTokens(haystack, categoryTokens) * 20;
        score += CountMatchingTokens(haystack, locationTokens) * 16;

        if (haystack.Contains("photo", StringComparison.Ordinal) ||
            haystack.Contains("photos", StringComparison.Ordinal) ||
            haystack.Contains("gallery", StringComparison.Ordinal) ||
            haystack.Contains("menu", StringComparison.Ordinal) ||
            haystack.Contains("avis", StringComparison.Ordinal) ||
            haystack.Contains("review", StringComparison.Ordinal))
        {
            score += 48;
        }

        if (haystack.Contains("instagram", StringComparison.Ordinal) ||
            haystack.Contains("facebook", StringComparison.Ordinal) ||
            haystack.Contains("restaurantguru", StringComparison.Ordinal) ||
            haystack.Contains("tripadvisor", StringComparison.Ordinal) ||
            haystack.Contains("localoria", StringComparison.Ordinal) ||
            haystack.Contains("mapstr", StringComparison.Ordinal))
        {
            score += 28;
        }

        if (haystack.Contains("stock", StringComparison.Ordinal) ||
            haystack.Contains("shutterstock", StringComparison.Ordinal) ||
            haystack.Contains("freepik", StringComparison.Ordinal) ||
            haystack.Contains("depositphotos", StringComparison.Ordinal) ||
            haystack.Contains("wikipedia", StringComparison.Ordinal))
        {
            score -= 160;
        }

        return score;
    }

    private static int ScorePublicImageSearchResult(
        NormalizedBusiness business,
        string imageUrl,
        string? sourceUrl,
        string? title)
    {
        var haystack = $"{sourceUrl} {title}".ToLowerInvariant();
        var score = 180 + ScoreDiscoveredImageUrl(imageUrl);
        var businessTokens = TokenizeSearchValue(business.Name);
        var categoryTokens = TokenizeSearchValue(business.Category);
        var locationTokens = TokenizeSearchValue(ResolveHeadlineLocation(business.Address, string.Empty));

        score += CountMatchingTokens(haystack, businessTokens) * 34;
        score += CountMatchingTokens(haystack, categoryTokens) * 18;
        score += CountMatchingTokens(haystack, locationTokens) * 16;

        if (haystack.Contains("stock", StringComparison.Ordinal) ||
            haystack.Contains("shutterstock", StringComparison.Ordinal) ||
            haystack.Contains("istock", StringComparison.Ordinal) ||
            haystack.Contains("freepik", StringComparison.Ordinal) ||
            haystack.Contains("depositphotos", StringComparison.Ordinal))
        {
            score -= 160;
        }

        if (haystack.Contains("facebook", StringComparison.Ordinal) ||
            haystack.Contains("instagram", StringComparison.Ordinal) ||
            haystack.Contains("restaurantguru", StringComparison.Ordinal) ||
            haystack.Contains("tripadvisor", StringComparison.Ordinal) ||
            haystack.Contains("tiktok", StringComparison.Ordinal))
        {
            score += 12;
        }

        if (imageUrl.Contains("lookaside.fbsbx.com", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("tiktok.com/api/img", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("thfvnext.bing.com", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("bing.com/th", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("gstatic.com", StringComparison.OrdinalIgnoreCase) ||
            imageUrl.Contains("/maps/vt", StringComparison.OrdinalIgnoreCase))
        {
            score -= 180;
        }

        return score;
    }

    private static IReadOnlyList<string> TokenizeSearchValue(string? value)
    {
        return (value ?? string.Empty)
            .ToLowerInvariant()
            .Split([' ', '-', ',', '.', ';', ':', '_', '/', '\\', '|', '(', ')', '[', ']', '{', '}', '&', '?', '!', '+', '"', '\''],
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(static token => token.Length >= 3)
            .Distinct(StringComparer.Ordinal)
            .Take(8)
            .ToList();
    }

    private static int CountMatchingTokens(string haystack, IEnumerable<string> tokens)
    {
        var count = 0;
        foreach (var token in tokens)
        {
            if (haystack.Contains(token, StringComparison.Ordinal))
            {
                count++;
            }
        }

        return count;
    }

    private static void ApplyBrowserLikeHeaders(HttpRequestMessage request)
    {
        request.Version = HttpVersion.Version11;
        request.VersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
        request.Headers.AcceptLanguage.ParseAdd(BrowserAcceptLanguage);
        request.Headers.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            MaxAge = TimeSpan.Zero
        };
        request.Headers.Pragma.ParseAdd("no-cache");
    }

    private async Task<string?> FetchHtmlDocumentAsync(
        Uri requestUri,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
        ApplyBrowserLikeHeaders(request);
        if (referrer is not null)
        {
            request.Headers.Referrer = referrer;
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.TryAddWithoutValidation("Upgrade-Insecure-Requests", "1");

        try
        {
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.IsSuccessStatusCode)
            {
                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType) ||
                    contentType.Contains("html", StringComparison.OrdinalIgnoreCase))
                {
                    var html = await response.Content.ReadAsStringAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(html))
                    {
                        return html;
                    }
                }
            }
        }
        catch
        {
            // Fall back to curl below when available.
        }

        return await TryFetchHtmlWithCurlAsync(requestUri, referrer, cancellationToken);
    }

    private static async Task<string?> TryFetchHtmlWithCurlAsync(
        Uri requestUri,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        var fileName = OperatingSystem.IsWindows()
            ? "curl.exe"
            : "curl";

        Process? process = null;
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = fileName,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            process.StartInfo.ArgumentList.Add("-L");
            process.StartInfo.ArgumentList.Add("--silent");
            process.StartInfo.ArgumentList.Add("--show-error");
            process.StartInfo.ArgumentList.Add("--max-time");
            process.StartInfo.ArgumentList.Add("15");
            process.StartInfo.ArgumentList.Add("-A");
            process.StartInfo.ArgumentList.Add(BrowserUserAgent);
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add($"Accept-Language: {BrowserAcceptLanguage}");
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add("Accept: text/html,application/xhtml+xml");
            process.StartInfo.ArgumentList.Add("-H");
            process.StartInfo.ArgumentList.Add("Cache-Control: no-cache");

            if (referrer is not null)
            {
                process.StartInfo.ArgumentList.Add("-e");
                process.StartInfo.ArgumentList.Add(referrer.ToString());
            }

            process.StartInfo.ArgumentList.Add(requestUri.ToString());

            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);

            await process.WaitForExitAsync(timeoutCts.Token);

            var stdout = await stdoutTask;
            _ = await stderrTask;

            return process.ExitCode == 0 && !string.IsNullOrWhiteSpace(stdout)
                ? stdout
                : null;
        }
        catch
        {
            if (process is { HasExited: false })
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch
                {
                    // Ignore cleanup failures for the optional curl fallback.
                }
            }

            return null;
        }
        finally
        {
            process?.Dispose();
        }
    }

    private static bool ShouldUseRenderedVisualDiscovery(
        Uri websiteUri,
        IReadOnlyCollection<WebsiteAssetCandidate> galleryCandidates)
    {
        if (IsGoogleMapsUri(websiteUri))
        {
            return true;
        }

        if (galleryCandidates.Count >= 4)
        {
            return false;
        }

        var host = websiteUri.Host.ToLowerInvariant();
        return host.Contains("instagram.com", StringComparison.Ordinal) ||
               host.Contains("facebook.com", StringComparison.Ordinal) ||
               host.Contains("tiktok.com", StringComparison.Ordinal) ||
               host.Contains("tripadvisor.", StringComparison.Ordinal) ||
               host.Contains("restaurantguru.", StringComparison.Ordinal);
    }

    private static bool ShouldLoadRenderedImages(Uri websiteUri)
    {
        if (IsGoogleMapsUri(websiteUri))
        {
            return true;
        }

        var host = websiteUri.Host.ToLowerInvariant();
        return host.Contains("instagram.com", StringComparison.Ordinal) ||
               host.Contains("facebook.com", StringComparison.Ordinal);
    }

    private static bool IsGoogleMapsUri(Uri websiteUri)
    {
        return websiteUri.Host.Contains("google.", StringComparison.OrdinalIgnoreCase) &&
               websiteUri.AbsolutePath.Contains("/maps", StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<string?> TryFetchRenderedHtmlDocumentAsync(
        Uri requestUri,
        bool includeImages,
        CancellationToken cancellationToken)
    {
        var browserExecutable = BrowserExecutable.Value;
        if (string.IsNullOrWhiteSpace(browserExecutable))
        {
            return null;
        }

        var normalizedRequestUri = NormalizeRenderedRequestUri(requestUri);
        var html = await TryRunRenderedBrowserDumpAsync(
            browserExecutable,
            normalizedRequestUri,
            includeImages,
            useCompatibilityArguments: false,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        html = await TryRunRenderedBrowserDumpAsync(
            browserExecutable,
            normalizedRequestUri,
            includeImages,
            useCompatibilityArguments: true,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(html))
        {
            return html;
        }

        return OperatingSystem.IsWindows()
            ? await TryRunRenderedBrowserDumpViaPowerShellAsync(
                browserExecutable,
                normalizedRequestUri,
                includeImages,
                cancellationToken)
            : null;
    }

    private static async Task<string?> TryRunRenderedBrowserDumpAsync(
        string browserExecutable,
        Uri requestUri,
        bool includeImages,
        bool useCompatibilityArguments,
        CancellationToken cancellationToken)
    {
        var userDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "website-visual-browser",
            Guid.NewGuid().ToString("N"));

        Directory.CreateDirectory(userDataDirectory);

        Process? process = null;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RenderedDomTimeout);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = browserExecutable,
                    Arguments = BuildProcessArguments(BuildRenderedBrowserArguments(
                        userDataDirectory,
                        requestUri,
                        includeImages,
                        useCompatibilityArguments)),
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            if (!process.Start())
            {
                return null;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeoutSource.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return null;
            }

            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (string.IsNullOrWhiteSpace(stdout))
            {
                return null;
            }

            return process.ExitCode != 0 && !stdout.Contains("<html", StringComparison.OrdinalIgnoreCase)
                ? null
                : stdout;
        }
        catch
        {
            if (process is not null)
            {
                TryKillProcess(process);
            }

            return null;
        }
        finally
        {
            process?.Dispose();
            TryDeleteDirectory(userDataDirectory);
        }
    }

    private static async Task<string?> TryRunRenderedBrowserDumpViaPowerShellAsync(
        string browserExecutable,
        Uri requestUri,
        bool includeImages,
        CancellationToken cancellationToken)
    {
        var userDataDirectory = Path.Combine(
            Path.GetTempPath(),
            "website-visual-browser",
            Guid.NewGuid().ToString("N"));
        var outputPath = Path.Combine(
            Path.GetTempPath(),
            "website-visual-browser",
            $"{Guid.NewGuid():N}.html");

        Directory.CreateDirectory(userDataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

        Process? process = null;
        try
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(RenderedDomTimeout);

            var browserArguments = BuildRenderedBrowserArguments(
                userDataDirectory,
                requestUri,
                includeImages,
                useCompatibilityArguments: true);
            var command = BuildPowerShellBrowserCommand(browserExecutable, browserArguments, outputPath);

            process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };

            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-NonInteractive");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(command);

            if (!process.Start())
            {
                return null;
            }

            var stderrTask = process.StandardError.ReadToEndAsync(timeoutSource.Token);

            try
            {
                await process.WaitForExitAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                TryKillProcess(process);
                return null;
            }

            _ = await stderrTask;
            if (!File.Exists(outputPath))
            {
                return null;
            }

            var stdout = await File.ReadAllTextAsync(outputPath, cancellationToken);

            return string.IsNullOrWhiteSpace(stdout) ||
                   !stdout.Contains("<html", StringComparison.OrdinalIgnoreCase)
                ? null
                : stdout;
        }
        catch
        {
            if (process is not null)
            {
                TryKillProcess(process);
            }

            return null;
        }
        finally
        {
            process?.Dispose();
            TryDeleteDirectory(userDataDirectory);
            TryDeleteFile(outputPath);
        }
    }

    private static Uri NormalizeRenderedRequestUri(Uri requestUri)
    {
        if (!IsGoogleMapsUri(requestUri) ||
            requestUri.Query.Contains("hl=", StringComparison.OrdinalIgnoreCase))
        {
            return requestUri;
        }

        var separator = string.IsNullOrWhiteSpace(requestUri.Query) ? "?" : "&";
        return new Uri($"{requestUri}{separator}hl=en");
    }

    private static IReadOnlyList<string> BuildRenderedBrowserArguments(
        string userDataDirectory,
        Uri requestUri,
        bool includeImages,
        bool useCompatibilityArguments)
    {
        var arguments = new List<string>
        {
            "--headless=new",
            "--disable-gpu",
            "--disable-dev-shm-usage",
            "--blink-settings=imagesEnabled=false",
            "--window-size=1440,2200",
            "--lang=en-US",
            "--no-first-run",
            "--no-default-browser-check",
            $"--user-data-dir={userDataDirectory}",
            $"--virtual-time-budget={RenderedDomVirtualTimeBudgetMs}",
            "--dump-dom",
            requestUri.ToString()
        };

        if (!useCompatibilityArguments)
        {
            arguments.Insert(3, "--disable-blink-features=AutomationControlled");
            arguments.Insert(5, "--hide-scrollbars");
            arguments.Insert(arguments.Count - 2, $"--user-agent={BrowserUserAgent}");
        }

        return arguments;
    }

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

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static string BuildProcessArguments(IReadOnlyList<string> arguments)
    {
        return string.Join(" ", arguments.Select(EscapeProcessArgument));
    }

    private static string EscapeProcessArgument(string argument)
    {
        if (string.IsNullOrEmpty(argument))
        {
            return "\"\"";
        }

        if (!argument.Any(static character => char.IsWhiteSpace(character) || character is '"' or '\\'))
        {
            return argument;
        }

        return $"\"{argument.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal)}\"";
    }

    private static string BuildPowerShellBrowserCommand(
        string browserExecutable,
        IReadOnlyList<string> browserArguments,
        string outputPath)
    {
        return string.Join(
            " ",
            ["$ErrorActionPreference='SilentlyContinue';", "&", EscapePowerShellLiteral(browserExecutable), .. browserArguments.Select(EscapePowerShellLiteral), ">", EscapePowerShellLiteral(outputPath), "2>$null"]);
    }

    private static string EscapePowerShellLiteral(string value)
    {
        return $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";
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

    private async Task<WebsiteVisualDiscoveryResult?> DiscoverWebsiteVisualsAsync(
        Uri websiteUri,
        CancellationToken cancellationToken)
    {
        var galleryCandidates = new List<WebsiteAssetCandidate>();
        var logoCandidates = new List<WebsiteAssetCandidate>();

        var html = await FetchHtmlDocumentAsync(websiteUri, null, cancellationToken);
        if (!string.IsNullOrWhiteSpace(html))
        {
            PopulateVisualCandidatesFromHtml(
                websiteUri,
                html,
                galleryCandidates,
                logoCandidates,
                scoreBoost: 0);
        }

        if (ShouldUseRenderedVisualDiscovery(websiteUri, galleryCandidates))
        {
            try
            {
                var renderedHtml = await TryFetchRenderedHtmlDocumentAsync(
                    websiteUri,
                    includeImages: ShouldLoadRenderedImages(websiteUri),
                    cancellationToken);

                if (!string.IsNullOrWhiteSpace(renderedHtml))
                {
                    PopulateVisualCandidatesFromHtml(
                        websiteUri,
                        renderedHtml,
                        galleryCandidates,
                        logoCandidates,
                        scoreBoost: IsGoogleMapsUri(websiteUri) ? 45 : 20);
                }
            }
            catch
            {
                // Ignore optional browser rendering failures and keep the HTTP discovery results.
            }
        }

        var imageUris = galleryCandidates
            .Where(static candidate => !IsLikelyLogoUrl(candidate.Url))
            .Where(static candidate => !IsLikelyDecorativeImageUrl(candidate.Url))
            .Where(static candidate => !candidate.Url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static candidate => candidate.Score)
            .Select(static candidate => candidate.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8)
            .ToList();

        var logoUri = logoCandidates
            .OrderByDescending(static candidate => candidate.Score)
            .Select(static candidate => candidate.Url)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();

        return imageUris.Count == 0 && string.IsNullOrWhiteSpace(logoUri)
            ? null
            : new WebsiteVisualDiscoveryResult(imageUris, logoUri);
    }

    private static void PopulateVisualCandidatesFromHtml(
        Uri websiteUri,
        string html,
        ICollection<WebsiteAssetCandidate> galleryCandidates,
        ICollection<WebsiteAssetCandidate> logoCandidates,
        int scoreBoost)
    {
        foreach (Match match in MetaImageRegex.Matches(html))
        {
            var resolved = ResolveWebsiteAssetUri(websiteUri, match.Groups["url"].Value);
            if (resolved is null)
            {
                continue;
            }

            var score = 260 + scoreBoost + ScoreDiscoveredImageUrl(resolved);
            galleryCandidates.Add(new WebsiteAssetCandidate(resolved, score));
            if (IsLikelyLogoUrl(resolved))
            {
                logoCandidates.Add(new WebsiteAssetCandidate(resolved, score + 60));
            }
        }

        foreach (Match match in LinkIconRegex.Matches(html))
        {
            var resolved = ResolveWebsiteAssetUri(websiteUri, match.Groups["url"].Value);
            if (resolved is null)
            {
                continue;
            }

            logoCandidates.Add(new WebsiteAssetCandidate(
                resolved,
                320 + scoreBoost + ScoreDiscoveredImageUrl(resolved)));
        }

        foreach (Match match in ImageSourceRegex.Matches(html))
        {
            foreach (var candidateUrl in ExpandImageCandidateValues(match.Groups["url"].Value))
            {
                var resolved = ResolveWebsiteAssetUri(websiteUri, candidateUrl);
                if (resolved is null)
                {
                    continue;
                }

                var score = 120 + scoreBoost + ScoreDiscoveredImageUrl(resolved);
                if (IsLikelyLogoUrl(resolved))
                {
                    logoCandidates.Add(new WebsiteAssetCandidate(resolved, score + 80));
                    continue;
                }

                galleryCandidates.Add(new WebsiteAssetCandidate(resolved, score));
            }
        }

        foreach (Match match in BackgroundImageUrlRegex.Matches(html))
        {
            foreach (var candidateUrl in ExpandImageCandidateValues(match.Groups["url"].Value))
            {
                var resolved = ResolveWebsiteAssetUri(websiteUri, candidateUrl);
                if (resolved is null)
                {
                    continue;
                }

                var score = 150 + scoreBoost + ScoreDiscoveredImageUrl(resolved);
                if (IsLikelyLogoUrl(resolved))
                {
                    logoCandidates.Add(new WebsiteAssetCandidate(resolved, score + 40));
                    continue;
                }

                galleryCandidates.Add(new WebsiteAssetCandidate(resolved, score));
            }
        }

        foreach (Match match in EmbeddedRemoteImageUrlRegex.Matches(WebUtility.HtmlDecode(html)))
        {
            var resolved = ResolveWebsiteAssetUri(websiteUri, match.Value);
            if (resolved is null)
            {
                continue;
            }

            var score = 90 + scoreBoost + ScoreDiscoveredImageUrl(resolved);
            if (IsLikelyLogoUrl(resolved))
            {
                logoCandidates.Add(new WebsiteAssetCandidate(resolved, score + 50));
                continue;
            }

            galleryCandidates.Add(new WebsiteAssetCandidate(resolved, score));
        }
    }

    private static IEnumerable<string> ExpandImageCandidateValues(string rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            yield break;
        }

        if (rawValue.Contains(',', StringComparison.Ordinal))
        {
            foreach (var part in rawValue.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var firstToken = part.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(firstToken) &&
                    !IsClearlyInvalidAssetValue(firstToken))
                {
                    yield return firstToken;
                }
            }

            yield break;
        }

        var trimmed = rawValue.Trim();
        if (!IsClearlyInvalidAssetValue(trimmed))
        {
            yield return trimmed;
        }
    }

    private static string? ResolveWebsiteAssetUri(Uri websiteUri, string? rawUrl)
    {
        var value = CleanText(WebUtility.HtmlDecode(rawUrl));
        if (!string.IsNullOrWhiteSpace(value))
        {
            value = value.Trim('"', '\'');
        }

        if (string.IsNullOrWhiteSpace(value) ||
            value.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
            value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            IsClearlyInvalidAssetValue(value))
        {
            return null;
        }

        if (value.StartsWith("//", StringComparison.Ordinal))
        {
            value = $"https:{value}";
        }

        if (Uri.TryCreate(value, UriKind.Absolute, out var absoluteUri))
        {
            return absoluteUri.Scheme is "http" or "https"
                ? absoluteUri.ToString()
                : null;
        }

        return Uri.TryCreate(websiteUri, value, out var resolvedUri) &&
               resolvedUri.Scheme is "http" or "https"
            ? resolvedUri.ToString()
            : null;
    }

    private static bool IsClearlyInvalidAssetValue(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return true;
        }

        var trimmed = value.Trim();
        if (trimmed.Length >= 64 &&
            Base64LikePayloadRegex.IsMatch(trimmed) &&
            !trimmed.Contains('.', StringComparison.Ordinal) &&
            !trimmed.Contains(':', StringComparison.Ordinal))
        {
            return true;
        }

        if (trimmed.StartsWith("iVBOR", StringComparison.Ordinal) ||
            trimmed.StartsWith("/9j/", StringComparison.Ordinal) ||
            trimmed.StartsWith("R0lGOD", StringComparison.Ordinal) ||
            trimmed.StartsWith("UklGR", StringComparison.Ordinal) ||
            trimmed.StartsWith("PHN2Zy", StringComparison.Ordinal))
        {
            return true;
        }

        return trimmed.Length > 1200 &&
               !trimmed.Contains('/', StringComparison.Ordinal) &&
               !trimmed.Contains('.', StringComparison.Ordinal);
    }

    private static bool IsLikelyLogoUrl(string url)
    {
        var value = url.ToLowerInvariant();
        return value.Contains("logo", StringComparison.Ordinal) ||
               value.Contains("icon", StringComparison.Ordinal) ||
               value.Contains("favicon", StringComparison.Ordinal) ||
               value.Contains("apple-touch", StringComparison.Ordinal) ||
               value.Contains("brand", StringComparison.Ordinal);
    }

    private static bool IsAcceptableBusinessPhotoUri(string? url)
    {
        return TryGetAbsoluteHttpUri(url, out var uri) &&
               !IsLikelyLogoUrl(uri.ToString()) &&
               !IsLikelyDecorativeImageUrl(uri.ToString()) &&
               !uri.AbsolutePath.EndsWith(".svg", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsLikelyDecorativeImageUrl(string url)
    {
        var value = url.ToLowerInvariant();
        return HasTinyDimensionHint(value) ||
               value.Contains("maps/api/staticmap", StringComparison.Ordinal) ||
               value.Contains("staticmap?", StringComparison.Ordinal) ||
               value.Contains("/tpl/images/", StringComparison.Ordinal) ||
               value.Contains("/maps/vt", StringComparison.Ordinal) ||
               value.Contains("maps.gstatic.com/tactile", StringComparison.Ordinal) ||
               value.Contains("streetview", StringComparison.Ordinal) ||
               value.Contains("streetviewpixels", StringComparison.Ordinal) ||
               value.Contains("googleusercontent.com/gpms-cs-s", StringComparison.Ordinal) ||
               value.Contains("googleusercontent.com/gpms-cs", StringComparison.Ordinal) ||
               value.Contains("googleusercontent.com/maptile", StringComparison.Ordinal) ||
               value.Contains("googleusercontent.com/vt", StringComparison.Ordinal) ||
               value.Contains("mapslogo", StringComparison.Ordinal) ||
               value.Contains("leg-bullet", StringComparison.Ordinal) ||
               value.Contains("ssl.gstatic.com/gb/images/bar", StringComparison.Ordinal) ||
               value.Contains("cleardot", StringComparison.Ordinal) ||
               value.Contains("static.cdninstagram.com/rsrc.php", StringComparison.Ordinal) ||
               value.Contains("favicon", StringComparison.Ordinal) ||
               value.Contains("flag", StringComparison.Ordinal) ||
               value.Contains("phone", StringComparison.Ordinal) ||
               value.Contains("marker", StringComparison.Ordinal) ||
               value.Contains("avatar", StringComparison.Ordinal) ||
               value.Contains("sprite", StringComparison.Ordinal) ||
               value.Contains("no_img", StringComparison.Ordinal) ||
               value.Contains("no-image", StringComparison.Ordinal) ||
               value.Contains("placeholder", StringComparison.Ordinal) ||
               value.Contains("banner", StringComparison.Ordinal) ||
               value.Contains("promo", StringComparison.Ordinal) ||
               value.Contains("pub", StringComparison.Ordinal) ||
               value.Contains("ad-", StringComparison.Ordinal) ||
               value.Contains("_ad", StringComparison.Ordinal) ||
               value.Contains("32x32", StringComparison.Ordinal) ||
               value.Contains("64x64", StringComparison.Ordinal) ||
               value.Contains("128x128", StringComparison.Ordinal) ||
               value.Contains("158x158", StringComparison.Ordinal) ||
               value.Contains("-32.", StringComparison.Ordinal) ||
               value.Contains("_32.", StringComparison.Ordinal);
    }

    private static int ScoreDiscoveredImageUrl(string url)
    {
        var value = url.ToLowerInvariant();
        var dimensionHint = GetLargestDimensionHint(value);
        var score = 0;

        if (value.Contains("hero", StringComparison.Ordinal) ||
            value.Contains("cover", StringComparison.Ordinal) ||
            value.Contains("slider", StringComparison.Ordinal))
        {
            score += 120;
        }

        if (value.Contains("gallery", StringComparison.Ordinal) ||
            value.Contains("restaurant", StringComparison.Ordinal) ||
            value.Contains("food", StringComparison.Ordinal) ||
            value.Contains("menu", StringComparison.Ordinal) ||
            value.Contains("interior", StringComparison.Ordinal) ||
            value.Contains("ambiance", StringComparison.Ordinal) ||
            value.Contains("product", StringComparison.Ordinal))
        {
            score += 80;
        }

        if (value.Contains("upload", StringComparison.Ordinal) ||
            value.Contains("wp-content", StringComparison.Ordinal) ||
            value.Contains("media", StringComparison.Ordinal))
        {
            score += 28;
        }

        if (value.Contains("gps-cs-s", StringComparison.Ordinal) ||
            value.Contains("lh3.googleusercontent.com", StringComparison.Ordinal) ||
            value.Contains("lh5.googleusercontent.com", StringComparison.Ordinal))
        {
            if (dimensionHint >= 320)
            {
                score += 150;
            }
            else if (dimensionHint >= 160)
            {
                score += 70;
            }
        }

        if (value.Contains("streetview", StringComparison.Ordinal) ||
            value.Contains("grass-cs", StringComparison.Ordinal))
        {
            score -= 220;
        }

        if (value.Contains("maps/api/staticmap", StringComparison.Ordinal))
        {
            score -= 280;
        }

        if (value.Contains("thumb", StringComparison.Ordinal) ||
            value.Contains("thumbnail", StringComparison.Ordinal) ||
            value.Contains("placeholder", StringComparison.Ordinal) ||
            value.Contains("avatar", StringComparison.Ordinal) ||
            value.Contains("sprite", StringComparison.Ordinal))
        {
            score -= 140;
        }

        if (HasTinyDimensionHint(value))
        {
            score -= 220;
        }

        if (value.Contains("lookaside.fbsbx.com", StringComparison.Ordinal) ||
            value.Contains("tiktok.com/api/img", StringComparison.Ordinal) ||
            value.Contains("thfvnext.bing.com", StringComparison.Ordinal) ||
            value.Contains("bing.com/th", StringComparison.Ordinal) ||
            value.Contains("gstatic.com", StringComparison.Ordinal) ||
            value.Contains("/maps/vt", StringComparison.Ordinal))
        {
            score -= 180;
        }

        if (IsLikelyDecorativeImageUrl(value))
        {
            score -= 240;
        }

        if (value.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
        {
            score -= 120;
        }

        return score;
    }

    private static int GetLargestDimensionHint(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return 0;
        }

        var match = DimensionHintRegex.Match(value);
        return match.Success &&
               int.TryParse(match.Groups["width"].Value, out var width) &&
               int.TryParse(match.Groups["height"].Value, out var height)
            ? Math.Max(width, height)
            : 0;
    }

    private static bool HasTinyDimensionHint(string value)
    {
        var largestDimension = GetLargestDimensionHint(value);
        return largestDimension is > 0 and <= 96;
    }

    private static List<WebsiteCard> BuildFrenchServiceCards(NormalizedBusiness business)
    {
        return InferServices(business.Category, "fr")
            .Take(4)
            .Select(service => new WebsiteCard(
                service,
                $"{service} pense pour offrir une experience fluide, claire et agreable avant, pendant et apres la visite."))
            .ToList();
    }

    private static List<WebsiteCard> BuildEnglishServiceCards(NormalizedBusiness business)
    {
        return InferServices(business.Category, "en")
            .Take(4)
            .Select(service => new WebsiteCard(
                service,
                $"{service} presented with a polished, welcoming tone that makes the experience easier to understand and easier to enjoy."))
            .ToList();
    }

    private static List<WebsiteCard> BuildArabicServiceCards(NormalizedBusiness business)
    {
        return InferServices(business.Category, "ar")
            .Take(4)
            .Select(service => new WebsiteCard(
                service,
                $"{service} مع عرض واضح يساعد الزائر على فهم النشاط والتواصل بسرعة."))
            .ToList();
    }

    private static List<WebsiteCard> BuildFrenchHighlightCards(NormalizedBusiness business)
    {
        return
        [
            new WebsiteCard("Adresse facile a retrouver", $"Toutes les informations utiles pour rejoindre {business.Name} sont reunies autour de {business.Address ?? "votre zone"}."),
            new WebsiteCard("Contact direct", "Telephone, carte, horaires et WhatsApp sont accessibles sans effort depuis desktop comme depuis mobile."),
            new WebsiteCard("Presentation plus soignee", "Une identite visuelle plus premium met mieux en valeur le lieu, l ambiance et la qualite de service.")
        ];
    }

    private static List<WebsiteCard> BuildEnglishHighlightCards(NormalizedBusiness business)
    {
        return
        [
            new WebsiteCard("Easy to find", $"Visitors can quickly locate {business.Name} around {business.Address ?? "your area"} with the key details presented clearly."),
            new WebsiteCard("Direct contact", "Phone, map, hours, and WhatsApp work together to make every enquiry faster and simpler."),
            new WebsiteCard("Stronger first impression", "A more premium presentation makes the place feel warmer, more credible, and more memorable.")
        ];
    }

    private static List<WebsiteCard> BuildArabicHighlightCards(NormalizedBusiness business)
    {
        return
        [
            new WebsiteCard("ظهور محلي واضح", $"صفحة واضحة تبرز {business.Address ?? "منطقتك"} وتسهل الوصول إلى النشاط."),
            new WebsiteCard("تواصل سريع من الهاتف", "الهاتف والخريطة وواتساب في مكان واحد لتقليل أي تعقيد."),
            new WebsiteCard("صورة أكثر احترافية", "عرض منظم وحديث يساعد على بناء الثقة قبل أول اتصال.")
        ];
    }

    private static List<FaqItem> BuildFrenchFaqItems(NormalizedBusiness business)
    {
        return
        [
            new FaqItem("Ou se trouve l etablissement ?", business.Address ?? "L adresse complete est communiquee sur demande ou visible sur la carte."),
            new FaqItem("Comment prendre contact rapidement ?", "Le telephone, la carte Google Maps et le formulaire WhatsApp permettent un contact simple et direct."),
            new FaqItem("Quels services sont proposes ?", $"L activite est orientee autour de {string.Join(", ", business.Services.Take(3))}."),
            new FaqItem("Peut-on envoyer une demande specifique ?", "Oui, le formulaire WhatsApp permet d envoyer un message detaille en quelques secondes.")
        ];
    }

    private static List<FaqItem> BuildEnglishFaqItems(NormalizedBusiness business)
    {
        return
        [
            new FaqItem("Where is the business located?", business.Address ?? "The full address is shared on request or visible on the map section."),
            new FaqItem("What is the fastest way to get in touch?", "Phone, Google Maps, and the WhatsApp form create a very direct contact path."),
            new FaqItem("What services are available?", $"The business is positioned around {string.Join(", ", business.Services.Take(3))}."),
            new FaqItem("Can I send a specific request?", "Yes. The WhatsApp form lets visitors send a detailed message in just a few seconds.")
        ];
    }

    private static List<FaqItem> BuildArabicFaqItems(NormalizedBusiness business)
    {
        return
        [
            new FaqItem("أين يقع النشاط؟", business.Address ?? "العنوان الكامل ظاهر في قسم الخريطة أو متاح عند الطلب."),
            new FaqItem("ما أسرع طريقة للتواصل؟", "الهاتف والخريطة ونموذج واتساب يجعلون التواصل مباشراً وسريعاً."),
            new FaqItem("ما الخدمات المتوفرة؟", $"يركز النشاط على {string.Join("، ", business.Services.Take(3))}."),
            new FaqItem("هل يمكن إرسال طلب خاص؟", "نعم، يمكنك إرسال رسالة مفصلة مباشرة عبر نموذج واتساب.")
        ];
    }

    private static string BuildFrenchReviewSummary(NormalizedBusiness business)
    {
        if (!string.IsNullOrWhiteSpace(business.ReviewsSummary))
        {
            return business.ReviewsSummary;
        }

        if (business.Rating is not null && business.ReviewCount is > 0)
        {
            return $"{business.Name} affiche une note de {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5 sur {business.ReviewCount} avis publics. Ces retours renforcent l image de confiance et donnent un apercu immediat de l experience proposee.";
        }

        return $"{business.Name} peut mettre en avant son univers, son adresse et ses informations essentielles dans une presentation plus claire, plus rassurante et plus agreable a consulter.";
    }

    private static string BuildEnglishReviewSummary(NormalizedBusiness business)
    {
        if (business.Rating is not null && business.ReviewCount is > 0)
        {
            return $"{business.Name} currently shows a {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5 rating across {business.ReviewCount} public reviews. Those signals immediately reinforce trust and help visitors understand the overall experience.";
        }

        return $"{business.Name} can still feel more credible and more memorable with a clearer presentation of its atmosphere, location, and key contact details.";
    }

    private static string BuildArabicReviewSummary(NormalizedBusiness business)
    {
        if (business.Rating is not null && business.ReviewCount is > 0)
        {
            return $"يحمل {business.Name} تقييماً قدره {business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture)}/5 بناءً على {business.ReviewCount} مراجعة عامة. إبراز هذه الثقة في صفحة حديثة يساعد على إقناع الزائر بسرعة.";
        }

        return $"يمكن لـ {business.Name} تعزيز حضوره المحلي من خلال صفحة احترافية وسهلة على الهاتف تعطي انطباعاً أقوى وتسهل التواصل حتى مع عدد مراجعات محدود.";
    }

    private static string ResolveSchemaType(string category)
    {
        var normalized = Slugify(category);
        return normalized switch
        {
            "restaurant" => "Restaurant",
            "cafe" => "CafeOrCoffeeShop",
            "bar" => "BarOrPub",
            "bakery" => "Bakery",
            "beauty-salon" => "BeautySalon",
            "hotel" => "Hotel",
            "grocery-store" => "GroceryStore",
            _ => "LocalBusiness"
        };
    }

    private static List<WebsiteCard> MergeCards(
        IReadOnlyList<WebsiteCard> fallback,
        IReadOnlyList<AiCardItem>? aiItems)
    {
        if (aiItems is null || aiItems.Count == 0)
        {
            return fallback.ToList();
        }

        var merged = new List<WebsiteCard>();
        foreach (var item in aiItems.Take(4))
        {
            var title = CleanText(item.Title);
            var description = CleanText(item.Description);

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(description))
            {
                merged.Add(new WebsiteCard(title, description));
            }
        }

        return merged.Count > 0 ? merged : fallback.ToList();
    }

    private static List<FaqItem> MergeFaq(
        IReadOnlyList<FaqItem> fallback,
        IReadOnlyList<AiFaqItem>? aiItems)
    {
        if (aiItems is null || aiItems.Count == 0)
        {
            return fallback.ToList();
        }

        var merged = new List<FaqItem>();
        foreach (var item in aiItems.Take(4))
        {
            var question = CleanText(item.Question);
            var answer = CleanText(item.Answer);

            if (!string.IsNullOrWhiteSpace(question) && !string.IsNullOrWhiteSpace(answer))
            {
                merged.Add(new FaqItem(question, answer));
            }
        }

        return merged.Count > 0 ? merged : fallback.ToList();
    }

    private static List<string> MergeCaptions(
        IReadOnlyList<string> fallback,
        IReadOnlyList<string>? aiCaptions)
    {
        if (aiCaptions is null || aiCaptions.Count == 0)
        {
            return fallback.ToList();
        }

        var merged = aiCaptions
            .Where(static caption => !string.IsNullOrWhiteSpace(caption))
            .Select(static caption => caption.Trim())
            .Take(4)
            .ToList();

        return merged.Count > 0 ? merged : fallback.ToList();
    }

    private static string Prefer(string? preferred, string fallback)
    {
        return string.IsNullOrWhiteSpace(preferred) ? fallback : preferred.Trim();
    }

    private static string FormatRating(NormalizedBusiness business)
    {
        return business.Rating is null
            ? "—"
            : business.Rating.Value.ToString("0.0", CultureInfo.InvariantCulture) + "/5";
    }

    private static string FormatReviewCount(NormalizedBusiness business)
    {
        return business.ReviewCount is > 0
            ? $"{business.ReviewCount} avis Google"
            : "Avis vérifiés sur Google";
    }

    private static List<string> InferServices(string category, string language)
    {
        var normalized = Slugify(category);
        return (normalized, language) switch
        {
            ("restaurant", "en") => ["Dine-in service", "Takeaway options", "Local reservations", "Group-friendly orders"],
            ("restaurant", "ar") => ["خدمة داخل المطعم", "طلبات خارجية", "حجوزات محلية", "طلبات للمجموعات"],
            ("restaurant", _) => ["Service sur place", "Vente a emporter", "Reservations locales", "Commandes pour groupes"],

            ("cafe", "en") => ["Coffee and hot drinks", "Snacks and pastries", "Local breakfast stop", "Relaxed meeting space"],
            ("cafe", "ar") => ["قهوة ومشروبات ساخنة", "سناك ومعجنات", "فطور محلي", "جلسات مريحة"],
            ("cafe", _) => ["Cafe et boissons chaudes", "Snacking et douceurs", "Pause petit-dejeuner", "Espace convivial"],

            ("bar", "en") => ["Drinks menu", "Evening atmosphere", "Group bookings", "Afterwork moments"],
            ("bar", "ar") => ["قائمة المشروبات", "أجواء مسائية", "حجوزات للمجموعات", "لقاءات بعد العمل"],
            ("bar", _) => ["Carte boissons", "Ambiance de soiree", "Reservations de groupe", "Afterwork local"],

            ("bakery", "en") => ["Fresh bread", "Pastries and desserts", "Daily specials", "Custom orders"],
            ("bakery", "ar") => ["خبز طازج", "حلويات ومعجنات", "عروض يومية", "طلبات خاصة"],
            ("bakery", _) => ["Pain du jour", "Patisseries et viennoiseries", "Suggestions quotidiennes", "Commandes speciales"],

            ("beauty-salon", "en") => ["Appointments", "Beauty treatments", "Personalized advice", "Wellness experience"],
            ("beauty-salon", "ar") => ["مواعيد", "خدمات تجميل", "نصائح شخصية", "تجربة عناية"],
            ("beauty-salon", _) => ["Prise de rendez-vous", "Soins beaute", "Conseils personnalises", "Experience bien-etre"],

            ("grocery-store", "en") => ["Daily essentials", "Local convenience", "Fast in-store service", "Neighborhood shopping"],
            ("grocery-store", "ar") => ["منتجات يومية", "خدمة قريبة", "تسوق سريع", "متجر حي"],
            ("grocery-store", _) => ["Produits du quotidien", "Commerce de proximite", "Service rapide", "Courses de quartier"],

            ("clothing-store", "en") => ["Ready-to-wear selection", "Accessories", "Seasonal arrivals", "In-store advice"],
            ("clothing-store", "ar") => ["ملابس جاهزة", "إكسسوارات", "وصولات موسمية", "نصائح داخل المتجر"],
            ("clothing-store", _) => ["Pret-a-porter", "Accessoires", "Nouvelles collections", "Conseils en boutique"],

            (_, "en") => ["Core offer", "Local support", "Fast contact", "Flexible customer service"],
            (_, "ar") => ["الخدمة الأساسية", "دعم محلي", "تواصل سريع", "مرونة في خدمة العملاء"],
            _ => ["Offre principale", "Accompagnement local", "Contact rapide", "Service client flexible"]
        };
    }

    private static string LocalizeCategory(string category, string language)
    {
        var normalized = Slugify(category);
        return (normalized, language) switch
        {
            ("restaurant", "en") => "Restaurant",
            ("restaurant", "ar") => "مطعم",
            ("cafe", "en") => "Cafe",
            ("cafe", "ar") => "مقهى",
            ("bar", "en") => "Bar",
            ("bar", "ar") => "بار",
            ("bakery", "en") => "Bakery",
            ("bakery", "ar") => "مخبز",
            ("beauty-salon", "en") => "Beauty salon",
            ("beauty-salon", "ar") => "صالون تجميل",
            ("grocery-store", "en") => "Grocery store",
            ("grocery-store", "ar") => "متجر مواد غذائية",
            ("clothing-store", "en") => "Fashion store",
            ("clothing-store", "ar") => "متجر أزياء",
            (_, "en") => "Local business",
            (_, "ar") => "نشاط محلي",
            _ => category
        };
    }

    private static List<string> NormalizeList(IReadOnlyList<string>? values)
    {
        return (values ?? [])
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string? CleanText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return CollapseWhitespaceRegex.Replace(value.Trim(), " ");
    }

    private static bool TryGetAbsoluteHttpUri(string? value, out Uri uri)
    {
        var normalized = value?.Trim();
        if (Uri.TryCreate(normalized, UriKind.Absolute, out uri!) &&
            (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(normalized) &&
            normalized.Contains(' ', StringComparison.Ordinal) &&
            Uri.TryCreate(normalized.Replace(" ", "%20", StringComparison.Ordinal), UriKind.Absolute, out var escapedUri) &&
            (escapedUri.Scheme == Uri.UriSchemeHttp || escapedUri.Scheme == Uri.UriSchemeHttps))
        {
            uri = escapedUri;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsValidEmail(string? value)
    {
        return !string.IsNullOrWhiteSpace(value) &&
               Regex.IsMatch(value.Trim(), @"^[^\s@]+@[^\s@]+\.[^\s@]+$");
    }

    private static string NormalizeWhatsappNumber(string? rawPhoneNumber)
    {
        var digits = Regex.Replace(rawPhoneNumber ?? string.Empty, "[^\\d]", string.Empty);
        return digits;
    }

    private static string BuildGoogleMapsUri(double? latitude, double? longitude, string searchText)
    {
        if (latitude is not null && longitude is not null)
        {
            return $"https://www.google.com/maps?q={latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}";
        }

        return $"https://www.google.com/maps/search/?api=1&query={Uri.EscapeDataString(searchText)}";
    }

    private static string BuildMapEmbedUri(double? latitude, double? longitude, string searchText)
    {
        if (latitude is not null && longitude is not null)
        {
            return $"https://www.google.com/maps?q={latitude.Value.ToString(CultureInfo.InvariantCulture)},{longitude.Value.ToString(CultureInfo.InvariantCulture)}&z=15&output=embed";
        }

        return $"https://www.google.com/maps?q={Uri.EscapeDataString(searchText)}&output=embed";
    }

    private static string Slugify(string value)
    {
        var slug = value
            .Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Aggregate(new StringBuilder(), (builder, ch) =>
            {
                builder.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : '-');
                return builder;
            })
            .ToString();

        slug = Regex.Replace(slug, "-{2,}", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "business" : slug;
    }

    private static string NormalizeSearchFingerprint(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value
            .Normalize(NormalizationForm.FormD)
            .Where(ch => CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            .Aggregate(new StringBuilder(value.Length), (builder, ch) =>
            {
                builder.Append(char.ToLowerInvariant(ch));
                return builder;
            })
            .ToString();

        return CollapseWhitespaceRegex.Replace(normalized, " ").Trim();
    }

    private static string GetInitials(string businessName)
    {
        var parts = businessName
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(2)
            .Select(static part => part[0])
            .ToArray();

        return parts.Length == 0
            ? "LB"
            : new string(parts).ToUpperInvariant();
    }

    private static string TrimToLengthSafe(string value, int maxLength)
    {
        var normalized = CleanText(value) ?? string.Empty;
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..Math.Max(1, maxLength - 3)].TrimEnd() + "...";
    }

    private static string TrimToLength(string value, int maxLength)
    {
        var normalized = CleanText(value) ?? string.Empty;
        if (normalized.Length <= maxLength)
        {
            return normalized;
        }

        return normalized[..(maxLength - 1)].TrimEnd() + "…";
    }

    private static void AddTextFile(Dictionary<string, byte[]> files, string archivePath, string content)
    {
        files[archivePath] = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(content);
    }

    private static byte[] BuildZip(Dictionary<string, byte[]> files)
    {
        using var memoryStream = new MemoryStream();
        using (var archive = new ZipArchive(memoryStream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files.OrderBy(static entry => entry.Key, StringComparer.OrdinalIgnoreCase))
            {
                var entry = archive.CreateEntry(file.Key, CompressionLevel.Optimal);
                using var entryStream = entry.Open();
                entryStream.Write(file.Value, 0, file.Value.Length);
            }
        }

        return memoryStream.ToArray();
    }

    private static string ResolveFileExtension(string? mediaType)
    {
        return mediaType?.ToLowerInvariant() switch
        {
            "image/jpeg" => "jpg",
            "image/jpg" => "jpg",
            "image/png" => "png",
            "image/webp" => "webp",
            "image/svg+xml" => "svg",
            _ => string.Empty
        };
    }

    private static string ResolveFileExtensionFromPath(string? path)
    {
        var extension = Path.GetExtension(path ?? string.Empty)
            .Trim()
            .TrimStart('.')
            .ToLowerInvariant();

        return extension switch
        {
            "jpeg" => "jpg",
            "jpg" or "png" or "webp" or "svg" => extension,
            _ => string.Empty
        };
    }

    private static bool TryResolveUploadedFileExtension(
        string? fileName,
        string? contentType,
        out string extension)
    {
        extension = ResolveFileExtension(contentType);
        if (!string.IsNullOrWhiteSpace(extension))
        {
            return true;
        }

        var rawExtension = Path.GetExtension(fileName ?? string.Empty)
            .Trim()
            .TrimStart('.')
            .ToLowerInvariant();

        if (rawExtension is "jpg" or "jpeg" or "png" or "webp" or "svg")
        {
            extension = rawExtension == "jpeg" ? "jpg" : rawExtension;
            return true;
        }

        extension = string.Empty;
        return false;
    }

    private static string HslToHex(double hue, double saturationPercent, double lightnessPercent)
    {
        var saturation = saturationPercent / 100d;
        var lightness = lightnessPercent / 100d;

        var c = (1 - Math.Abs((2 * lightness) - 1)) * saturation;
        var x = c * (1 - Math.Abs(((hue / 60d) % 2) - 1));
        var m = lightness - (c / 2);

        var (rPrime, gPrime, bPrime) = hue switch
        {
            >= 0 and < 60 => (c, x, 0d),
            >= 60 and < 120 => (x, c, 0d),
            >= 120 and < 180 => (0d, c, x),
            >= 180 and < 240 => (0d, x, c),
            >= 240 and < 300 => (x, 0d, c),
            _ => (c, 0d, x)
        };

        var r = (int)Math.Round((rPrime + m) * 255);
        var g = (int)Math.Round((gPrime + m) * 255);
        var b = (int)Math.Round((bPrime + m) * 255);

        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static string ToRgba(string hexColor, double alpha)
    {
        var color = hexColor.TrimStart('#');
        if (color.Length != 6)
        {
            return $"rgba(0, 0, 0, {alpha.ToString("0.##", CultureInfo.InvariantCulture)})";
        }

        var r = Convert.ToInt32(color[..2], 16);
        var g = Convert.ToInt32(color[2..4], 16);
        var b = Convert.ToInt32(color[4..6], 16);
        return $"rgba({r}, {g}, {b}, {alpha.ToString("0.##", CultureInfo.InvariantCulture)})";
    }

    private static string BlendHexColors(string firstHex, string secondHex, double secondWeight)
    {
        if (!TryParseHexColor(firstHex, out var first) || !TryParseHexColor(secondHex, out var second))
        {
            return firstHex;
        }

        var normalizedWeight = Math.Clamp(secondWeight, 0d, 1d);
        var firstWeight = 1d - normalizedWeight;
        var r = (int)Math.Round((first.r * firstWeight) + (second.r * normalizedWeight));
        var g = (int)Math.Round((first.g * firstWeight) + (second.g * normalizedWeight));
        var b = (int)Math.Round((first.b * firstWeight) + (second.b * normalizedWeight));
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private static bool TryParseHexColor(string? value, out (int r, int g, int b) color)
    {
        color = default;
        var normalized = CleanText(value)?.TrimStart('#');
        if (string.IsNullOrWhiteSpace(normalized) || normalized.Length != 6)
        {
            return false;
        }

        try
        {
            color = (
                Convert.ToInt32(normalized[..2], 16),
                Convert.ToInt32(normalized.Substring(2, 2), 16),
                Convert.ToInt32(normalized.Substring(4, 2), 16));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string EscapeHtml(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string EscapeHtmlAttribute(string value)
    {
        return WebUtility.HtmlEncode(value);
    }

    private static string SecurityElementEscape(string value)
    {
        return System.Security.SecurityElement.Escape(value) ?? string.Empty;
    }


    private sealed record TemplateDefinition(string Id, string DisplayName, bool IsDark);

    private sealed record FontPair(
        string DisplayName,
        string DisplayFamily,
        string BodyName,
        string BodyFamily,
        string StylesheetUri);

    private sealed record ThemeChoice(
        FontPair FontPair,
        string PrimaryColor,
        string SecondaryColor,
        string AccentColor,
        string Background,
        string Surface,
        string SurfaceAlt,
        string TextColor,
        string MutedText,
        string BorderColor,
        string ButtonTextColor,
        string RadiusLarge,
        string RadiusMedium,
        string RadiusSmall,
        string SectionSpacing,
        string HeroGradient,
        string ShadowStyle,
        string GlowColor);

    private sealed record NormalizedBusiness(
        string PlaceId,
        string Name,
        string Slug,
        string Category,
        string? PrimaryType,
        string? Description,
        string? Address,
        string? PhoneNumber,
        string WhatsappNumber,
        string? PrimaryEmail,
        string? WebsiteUri,
        string GoogleMapsUri,
        string MapEmbedUri,
        double? Latitude,
        double? Longitude,
        double? Rating,
        int? ReviewCount,
        string? ReviewsSummary,
        IReadOnlyList<ReviewHighlight> ReviewHighlights,
        string? ReviewsUri,
        string? WriteAReviewUri,
        IReadOnlyList<string> OpeningHours,
        IReadOnlyList<string> Services,
        IReadOnlyList<string> Features,
        IReadOnlyList<string> PhotoUris,
        string? LogoUri,
        IReadOnlyDictionary<string, string> SocialLinks);

    private sealed record WebsiteCard(string Title, string Description);

    private sealed record FaqItem(string Question, string Answer);

    private sealed record ReviewHighlight(
        string AuthorName,
        double? Rating,
        string? RelativePublishTimeDescription,
        string Text,
        string? GoogleMapsUri);

    private sealed record UiLabels(
        string NavAbout,
        string NavServices,
        string NavGallery,
        string NavReviews,
        string NavContact,
        string LanguageLabel,
        string AddressLabel,
        string PhoneLabel,
        string EmailLabel,
        string HoursLabel,
        string RatingLabel,
        string OpenMap,
        string WhatsAppLabel,
        string FormNameLabel,
        string FormPhoneLabel,
        string FormMessageLabel,
        string FormSubmitLabel,
        string FormNamePlaceholder,
        string FormPhonePlaceholder,
        string FormMessagePlaceholder,
        string NoHours,
        string GalleryBadge,
        string FeatureBadge,
        string ReviewBadge,
        string ContactBadge,
        string FaqBadge,
        string ViewOnMaps,
        string ViewReviews,
        string WriteReview,
        string CallNow,
        string SendOnWhatsapp);

    private sealed record LocalizedWebsiteContent(
        string LanguageCode,
        string LanguageLabel,
        string HeroEyebrow,
        string HeroTitle,
        string HeroSubtitle,
        string HeroDescription,
        string PrimaryCta,
        string SecondaryCta,
        string AboutEyebrow,
        string AboutTitle,
        string AboutBody,
        string ServicesEyebrow,
        string ServicesTitle,
        string ServicesIntro,
        IReadOnlyList<WebsiteCard> Services,
        string HighlightsEyebrow,
        string HighlightsTitle,
        IReadOnlyList<WebsiteCard> Highlights,
        string GalleryEyebrow,
        string GalleryTitle,
        string GalleryIntro,
        IReadOnlyList<string> GalleryCaptions,
        string ReviewsEyebrow,
        string ReviewsTitle,
        string ReviewsSummary,
        string HoursEyebrow,
        string HoursTitle,
        string ContactEyebrow,
        string ContactTitle,
        string ContactIntro,
        string FormTitle,
        string FormIntro,
        string FaqEyebrow,
        string FaqTitle,
        IReadOnlyList<FaqItem> Faq,
        string FooterTagline,
        UiLabels Ui);

    private sealed record LocalizedContentBundle(
        Dictionary<string, LocalizedWebsiteContent> Translations,
        SeoContent Seo,
        string ModelUsed,
        LocalizedWebsiteContent FallbackFrench);

    private sealed record SeoContent(
        string Title,
        string Description,
        IReadOnlyList<string> Keywords,
        string HeroTitle);

    private sealed record AiPayloadResult(string ModelName, AiWebsitePayload Payload);

    private sealed record AiEditPayloadResult(string ModelName, AiWebsiteEditPayload Payload);

    private sealed class AiWebsitePayload
    {
        public Dictionary<string, AiLocalizedContent>? Translations { get; init; }

        public AiSeoContent? Seo { get; init; }
    }

    private sealed class AiWebsiteEditPayload
    {
        public string? ChangeSummary { get; init; }

        public AiDesignEdit? Design { get; init; }

        public Dictionary<string, AiLocalizedContent>? Translations { get; init; }

        public AiSeoContent? Seo { get; init; }
    }

    private sealed class AiDesignEdit
    {
        public string? TemplateId { get; set; }

        public string? ColorMood { get; set; }

        public string? FontDirection { get; set; }

        public string? MotionStyle { get; set; }

        public IReadOnlyList<string>? SectionOrder { get; set; }

        public IReadOnlyList<string>? HiddenSections { get; set; }
    }

    private sealed class AiLocalizedContent
    {
        public string? HeroEyebrow { get; init; }

        public string? HeroTitle { get; init; }

        public string? HeroSubtitle { get; init; }

        public string? HeroDescription { get; init; }

        public string? AboutTitle { get; init; }

        public string? AboutBody { get; init; }

        public string? ServicesTitle { get; init; }

        public string? ServicesIntro { get; init; }

        public IReadOnlyList<AiCardItem>? ServiceItems { get; init; }

        public string? HighlightsTitle { get; init; }

        public IReadOnlyList<AiCardItem>? HighlightItems { get; init; }

        public string? GalleryTitle { get; init; }

        public string? GalleryIntro { get; init; }

        public IReadOnlyList<string>? GalleryCaptions { get; init; }

        public string? ReviewTitle { get; init; }

        public string? ReviewSummary { get; init; }

        public string? ContactTitle { get; init; }

        public string? ContactIntro { get; init; }

        public string? FormTitle { get; init; }

        public string? FormIntro { get; init; }

        public string? FaqTitle { get; init; }

        public IReadOnlyList<AiFaqItem>? FaqItems { get; init; }

        public string? FooterTagline { get; init; }
    }

    private sealed class AiSeoContent
    {
        public string? Title { get; init; }

        public string? Description { get; init; }

        public IReadOnlyList<string>? Keywords { get; init; }
    }

    private sealed class AiCardItem
    {
        public string? Title { get; init; }

        public string? Description { get; init; }
    }

    private sealed class AiFaqItem
    {
        public string? Question { get; init; }

        public string? Answer { get; init; }
    }

    private sealed record ClientSiteConfig(
        string SiteUrl,
        string DefaultLanguage,
        IReadOnlyList<string> AvailableLanguages,
        string BusinessName,
        string BusinessCategory,
        string? Address,
        string? PhoneNumber,
        string WhatsappNumber,
        string? PrimaryEmail,
        string GoogleMapsUri,
        string MapEmbedUri,
        double? Rating,
        int? ReviewCount,
        string? ReviewsUri,
        string? WriteAReviewUri,
        IReadOnlyList<string> OpeningHours,
        IReadOnlyDictionary<string, string> SocialLinks,
        string LogoPath,
        IReadOnlyList<ClientGalleryItem> Gallery);

    private sealed record ClientGalleryItem(string Src, string CssClass, int Width, int Height);

    private sealed record StoredMediaAsset(
        string ArchivePath,
        string WebPath,
        string Caption,
        string CssClass,
        int Width,
        int Height);

    private sealed record StoredLogoAsset(
        string WebPath,
        string SvgMarkup,
        string? ArchivePath,
        bool IsUploaded);

    private sealed record WebsiteProjectState(
        string StateVersion,
        NormalizedBusiness Business,
        string TemplateId,
        string TemplateName,
        string DesignConcept,
        string ColorMood,
        string FontDirection,
        string MotionStyle,
        string SiteUrl,
        string ModelUsed,
        ThemeChoice Theme,
        Dictionary<string, LocalizedWebsiteContent> Translations,
        SeoContent Seo,
        IReadOnlyList<string> SectionOrder,
        IReadOnlyList<string> HiddenSections,
        IReadOnlyList<StoredMediaAsset> MediaAssets,
        StoredLogoAsset LogoAsset,
        IReadOnlyList<string> PrioritizedAssets,
        IReadOnlyList<string> UploadedImageFileNames,
        string? UploadedLogoFileName);

    private sealed record GeneratedMediaAsset(
        string ArchivePath,
        string WebPath,
        string Caption,
        string CssClass,
        byte[] Content,
        int Width,
        int Height);

    private sealed record GeneratedLogoAsset(
        string WebPath,
        string SvgMarkup,
        string? ArchivePath,
        byte[]? Content,
        bool IsUploaded);

    private sealed record DownloadedBinaryAsset(
        string ArchivePath,
        string WebPath,
        byte[] Content,
        int Width,
        int Height);

    private sealed record WebsiteVisualDiscoveryResult(
        IReadOnlyList<string> ImageUris,
        string? LogoUri);

    private sealed record WebsiteAssetCandidate(
        string Url,
        int Score);

    private sealed record WebsiteSearchResultCandidate(
        string Url,
        string? Title,
        string? Snippet,
        int Score);

    private sealed record DesignConceptChoice(
        string Id,
        string TemplateId,
        string ColorMood,
        string FontDirection,
        string MotionStyle,
        IReadOnlyList<string> DefaultSectionOrder);

    private sealed record PaletteDefinition(
        string Id,
        bool IsDark,
        string PrimaryColor,
        string SecondaryColor,
        string AccentColor,
        string Background,
        string Surface,
        string SurfaceAlt,
        string TextColor,
        string MutedText,
        string BorderColor,
        string ButtonTextColor,
        IReadOnlyList<string> Tags);

    public sealed record GeneratedWebsiteArchive(
        string FileName,
        string ContentType,
        byte[] Content,
        string TemplateName,
        string ModelUsed);
}
