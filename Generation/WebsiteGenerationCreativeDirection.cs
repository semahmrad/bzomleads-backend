using System.Globalization;
using System.Text;

namespace Backend.Generation;

internal static class WebsiteGenerationCreativeDirection
{
    private sealed record BusinessProfile(
        string Id,
        IReadOnlyList<string> Keywords,
        string Atmosphere,
        string CopyTone,
        string ImageMood,
        string CompositionLanguage,
        IReadOnlyList<string> ImageSubjects);

    private static readonly IReadOnlyList<BusinessProfile> Profiles =
    [
        new(
            "coffee-shop",
            ["coffee", "cafe", "café", "espresso", "tea house", "salon de the", "salon de thé"],
            "warm, aromatic, relaxed, and ritual-driven",
            "warm, polished, personal, and slightly sensory without becoming theatrical",
            "warm daylight, texture-rich close-ups, and inviting neighborhood ambiance",
            "macro details, intimate seating moments, barista gestures, and calm lifestyle framing",
            ["latte art close-up", "barista pulling espresso", "cozy seating nook", "coffee beans macro", "pastries on stone counter"]),
        new(
            "restaurant",
            ["restaurant", "bistro", "trattoria", "dining", "restauration"],
            "refined, welcoming, and taste-driven",
            "confident, appetizing, elegant, and local-SEO aware",
            "plated dishes, warm interiors, and confident service moments",
            "hero food shots, composed table scenes, and intimate room details",
            ["signature plated dish", "dining room ambiance", "chef finishing plate", "table setting close-up", "facade at golden hour"]),
        new(
            "bakery",
            ["bakery", "boulangerie", "patisserie", "pastry"],
            "artisan, early-morning, tactile, and comforting",
            "warm, proud of craft, and product-first",
            "golden pastry textures, flour-dust atmosphere, and handcrafted freshness",
            "macro crumb details, hands at work, and display-case storytelling",
            ["fresh bread loaves", "pastry lamination close-up", "hands shaping dough", "bakery display case", "morning counter scene"]),
        new(
            "bar",
            ["bar", "pub", "cocktail", "wine bar", "night club"],
            "moody, social, and after-dark",
            "confident, stylish, and a little playful",
            "low-key lighting, reflective glass, and night energy",
            "backlit bottles, drink close-ups, and cinematic social scenes",
            ["craft cocktail close-up", "bartender shaking drink", "backlit bottles", "moody bar top", "night facade glow"]),
        new(
            "beauty-wellness",
            ["beauty", "spa", "salon", "wellness", "massage"],
            "clean, restorative, and premium",
            "calm, reassuring, polished, and trust-building",
            "soft diffused light, ritual gestures, and minimalist product focus",
            "clean compositions, tactile materials, and serene facial or treatment scenes",
            ["spa interior calm", "product close-up", "hands in treatment", "minimal wellness shelf", "soft towel and stone detail"]),
        new(
            "retail",
            ["boutique", "retail", "shop", "store", "clothing"],
            "curated, personal, and product-led",
            "warm, premium, and quality-focused",
            "styled product scenes, tactile materials, and intimate store identity",
            "curated flatlays, interior vignettes, and hands-presenting-product framing",
            ["curated product flatlay", "shop interior styling", "packaging detail", "hands presenting product", "signature display feature"]),
        new(
            "generic",
            [],
            "welcoming, professional, and local",
            "clear, credible, premium, and conversion-oriented",
            "honest business photography with natural light and clear context",
            "balanced storefront, team-at-work, product-or-service detail, and customer-friendly scenes",
            ["storefront exterior", "team at work", "signature service detail", "customer interaction", "interior overview"])
    ];

    public static string BuildBrief(
        string category,
        string? primaryType,
        string templateId,
        string templateName,
        string colorMood,
        string fontDirection,
        string motionStyle,
        string headingFont,
        string bodyFont,
        string primaryColor,
        string secondaryColor,
        string accentColor,
        string backgroundColor,
        string surfaceColor,
        string textColor,
        IReadOnlyList<string> sectionOrder,
        IReadOnlyList<string> services,
        IReadOnlyList<string> features,
        string? description)
    {
        var profile = ResolveProfile(category, primaryType);
        var builder = new StringBuilder();

        builder.AppendLine("Design this website as one coherent creative decision, not a generic business page.");
        builder.AppendLine($"Business atmosphere: {profile.Atmosphere}.");
        builder.AppendLine($"Copy attitude: {profile.CopyTone}.");
        builder.AppendLine($"Selected template family: {templateName} ({templateId}).");
        builder.AppendLine($"Color mood: {colorMood}. Font direction: {fontDirection}. Motion style: {motionStyle}.");
        builder.AppendLine($"Typography pairing: heading in {headingFont}, body in {bodyFont}.");
        builder.AppendLine("Palette anchors:");
        builder.AppendLine($"- primary: {primaryColor}");
        builder.AppendLine($"- secondary: {secondaryColor}");
        builder.AppendLine($"- accent: {accentColor}");
        builder.AppendLine($"- background: {backgroundColor}");
        builder.AppendLine($"- surface: {surfaceColor}");
        builder.AppendLine($"- text: {textColor}");
        builder.AppendLine($"Image mood: {profile.ImageMood}.");
        builder.AppendLine($"Composition language: {profile.CompositionLanguage}.");

        if (!string.IsNullOrWhiteSpace(description))
        {
            builder.AppendLine($"Existing business description to respect: {description.Trim()}");
        }

        if (services.Count > 0)
        {
            builder.AppendLine("Service/product anchors:");
            foreach (var service in services.Take(6))
            {
                builder.AppendLine($"- {service}");
            }
        }

        if (features.Count > 0)
        {
            builder.AppendLine("Business strengths to weave into the copy:");
            foreach (var feature in features.Take(6))
            {
                builder.AppendLine($"- {feature}");
            }
        }

        builder.AppendLine("Section rhythm in order:");
        foreach (var section in sectionOrder)
        {
            builder.AppendLine($"- {DescribeSection(section)}");
        }

        builder.AppendLine("Visual search/image cues:");
        foreach (var subject in profile.ImageSubjects)
        {
            builder.AppendLine($"- {subject}");
        }

        return builder.ToString().Trim();
    }

    public static IReadOnlyList<string> GetImageSearchTopics(string category, string? primaryType)
        => ResolveProfile(category, primaryType).ImageSubjects;

    private static string DescribeSection(string section)
    {
        return section switch
        {
            "about" => "About / story / positioning",
            "highlights" => "value points / benefits / trust signals",
            "services" => "services, menu, or product showcase",
            "gallery" => "gallery with atmosphere and detail shots",
            "reviews" => "ratings, testimonials, public reputation",
            "contact" => "contact, map, WhatsApp, and practical details",
            "faq" => "FAQ and objection-handling",
            _ => section
        };
    }

    private static BusinessProfile ResolveProfile(string? category, string? primaryType)
    {
        var fingerprint = Normalize($"{category} {primaryType}");
        foreach (var profile in Profiles)
        {
            if (profile.Keywords.Count == 0)
            {
                continue;
            }

            if (profile.Keywords.Any(keyword => fingerprint.Contains(Normalize(keyword), StringComparison.Ordinal)))
            {
                return profile;
            }
        }

        return Profiles[^1];
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(value.Length);
        foreach (var character in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }
}
