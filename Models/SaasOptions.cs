namespace Backend.Models;

public sealed class SaasOptions
{
    public string BootstrapAdminUsername { get; set; } = "admin";

    public string BootstrapAdminDisplayName { get; set; } = "Administrateur";

    public string BootstrapAdminCountryCode { get; set; } = "FR";

    public string AdminRecoveryEmail { get; set; } = "Semahmrad@gmail.com";

    public string FrontendBaseUrl { get; set; } = "http://127.0.0.1:4200";

    public IReadOnlyList<string> AllowedOrigins { get; set; } =
    [
        "http://127.0.0.1:4200",
        "http://localhost:4200"
    ];
}
