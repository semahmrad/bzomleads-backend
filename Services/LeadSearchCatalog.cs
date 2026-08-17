namespace Backend.Services;

internal static class LeadSearchCatalog
{
    private static readonly IReadOnlyDictionary<string, LeadSearchTypeDefinition> Definitions =
        new Dictionary<string, LeadSearchTypeDefinition>(StringComparer.OrdinalIgnoreCase)
        {
            ["restaurant"] = new(
                Label: "Restaurant",
                OverpassFilter: "[\"amenity\"=\"restaurant\"]"),
            ["bar"] = new(
                Label: "Bar",
                OverpassFilter: "[\"amenity\"=\"bar\"]"),
            ["cafe"] = new(
                Label: "Cafe",
                OverpassFilter: "[\"amenity\"=\"cafe\"]"),
            ["store"] = new(
                Label: "Boutique",
                OverpassFilter: "[shop]"),
            ["clothing_store"] = new(
                Label: "Boutique mode",
                OverpassFilter: "[\"shop\"=\"clothes\"]"),
            ["grocery_store"] = new(
                Label: "Epicerie",
                OverpassFilter: "[\"shop\"~\"^(supermarket|convenience|greengrocer|deli|grocery)$\"]"),
            ["bakery"] = new(
                Label: "Boulangerie",
                OverpassFilter: "[\"shop\"=\"bakery\"]"),
            ["beauty_salon"] = new(
                Label: "Salon beaute",
                OverpassFilter: "[\"shop\"~\"^(beauty|hairdresser|cosmetics)$\"]"),
            ["car_repair"] = new(
                Label: "Garage / Mécanicien",
                OverpassFilter: "[\"shop\"=\"car_repair\"]"),
            ["medical_office"] = new(
                Label: "Cabinet médical / Dentiste",
                OverpassFilter: "[\"amenity\"~\"^(doctors|dentist|clinic)$\"]"),
            ["hotel"] = new(
                Label: "Hôtel / Hébergement",
                OverpassFilter: "[\"tourism\"~\"^(hotel|guest_house|hostel)$\"]")
        };

    public static string NormalizeProvider(string? provider)
    {
        var normalized = (provider ?? "open_data").Trim().ToLowerInvariant();
        return normalized is "google_places" or "open_data"
            ? normalized
            : "open_data";
    }

    public static string NormalizeBusinessType(string? businessType)
    {
        var normalized = (businessType ?? "restaurant").Trim().ToLowerInvariant();
        return Definitions.ContainsKey(normalized) ? normalized : "restaurant";
    }

    public static string NormalizeWebsiteFilter(string? websiteFilter)
    {
        var normalized = (websiteFilter ?? "all").Trim().ToLowerInvariant();
        return normalized is "all" or "with_website" or "without_website"
            ? normalized
            : "all";
    }

    public static string GetBusinessLabel(string businessType)
        => Definitions[NormalizeBusinessType(businessType)].Label;

    public static string GetOverpassFilter(string businessType)
        => Definitions[NormalizeBusinessType(businessType)].OverpassFilter;

    private sealed record LeadSearchTypeDefinition(string Label, string OverpassFilter);
}
