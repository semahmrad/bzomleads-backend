using System.Globalization;
using Backend.Models;

namespace Backend.Services;

public static class CountryCatalog
{
    private static readonly Lazy<IReadOnlyList<CountryOptionResponse>> Countries = new(BuildCountries);

    public static IReadOnlyList<CountryOptionResponse> GetAll() => Countries.Value;

    public static CountryOptionResponse? Find(string? countryCode)
    {
        var normalized = NormalizeCode(countryCode);
        return Countries.Value.FirstOrDefault(country =>
            string.Equals(country.Code, normalized, StringComparison.OrdinalIgnoreCase));
    }

    public static string NormalizeCode(string? countryCode)
        => (countryCode ?? string.Empty).Trim().ToUpperInvariant();

    private static IReadOnlyList<CountryOptionResponse> BuildCountries()
    {
        var regions = new Dictionary<string, RegionInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var culture in CultureInfo.GetCultures(CultureTypes.SpecificCultures))
        {
            try
            {
                var region = new RegionInfo(culture.Name);
                var code = region.TwoLetterISORegionName.ToUpperInvariant();
                if (code.Length != 2 || regions.ContainsKey(code))
                {
                    continue;
                }

                regions[code] = region;
            }
            catch
            {
                // Some synthetic cultures do not map to a geographic region.
            }
        }

        return regions
            .Select(entry =>
            {
                try
                {
                    var canonicalRegion = new RegionInfo(entry.Key);
                    return new CountryOptionResponse(entry.Key, canonicalRegion.DisplayName);
                }
                catch
                {
                    return new CountryOptionResponse(entry.Key, entry.Value.EnglishName);
                }
            })
            .OrderBy(static country => country.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }
}
