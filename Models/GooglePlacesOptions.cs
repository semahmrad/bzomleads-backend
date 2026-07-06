namespace Backend.Models;

public sealed class GooglePlacesOptions
{
    public string ApiKey { get; set; } = string.Empty;

    public string DefaultLanguageCode { get; set; } = "fr";

    public int MaxWebsitePagesToScan { get; set; } = 3;

    public int WebsiteRequestTimeoutSeconds { get; set; } = 15;
}
