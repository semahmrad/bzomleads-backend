namespace Backend.Models;

public sealed record LoginRequest(string? Username, string? Password);

public sealed record ChangePasswordRequest(string? CurrentPassword, string? NewPassword);

public sealed record CreateUserRequest(
    string? Username,
    string? DisplayName,
    string? Password,
    string? CountryCode,
    IReadOnlyList<string>? CountryCodes = null);

public sealed record UpdateUserRequest(
    string? Username,
    string? DisplayName,
    string? CountryCode,
    bool? IsActive,
    IReadOnlyList<string>? CountryCodes = null);

public sealed record AdminResetPasswordRequest(string? NewPassword);

public sealed record AuthUserResponse(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    string CountryCode,
    string CountryName,
    bool MustChangePassword,
    IReadOnlyList<CountryOptionResponse> AllowedCountries,
    bool AiConfigured);

public sealed record AdminUserResponse(
    string Id,
    string Username,
    string DisplayName,
    string Role,
    string CountryCode,
    string CountryName,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? LastLoginUtc,
    DateTimeOffset? LastActivityUtc,
    int SearchCount,
    int NewLeadsCount,
    int EmailCampaignCount,
    int EmailsSentCount,
    int WebsitesCreatedCount,
    int WebsitesEditedCount,
    IReadOnlyList<CountryOptionResponse> AllowedCountries,
    bool AiConfigured);

public sealed record CountryOptionResponse(string Code, string Name);

public sealed record UserActor(
    string UserId,
    string Username,
    string DisplayName,
    string Role,
    string CountryCode,
    string CountryName,
    IReadOnlyList<string>? CountryCodes = null)
{
    public bool IsAdmin => string.Equals(Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase);
    public IReadOnlyList<string> AllowedCountryCodes => CountryCodes is { Count: > 0 }
        ? CountryCodes
        : [CountryCode];
}

public static class AppRoles
{
    public const string Admin = "Admin";
    public const string User = "User";
}

internal sealed record AppUserEntity(
    string Id,
    string Username,
    string DisplayName,
    string PasswordHash,
    string Role,
    string CountryCode,
    string CountryName,
    bool IsActive,
    bool MustChangePassword,
    DateTimeOffset CreatedUtc,
    string? CreatedByUserId,
    DateTimeOffset? LastLoginUtc,
    string AssignedCountryCodes = "",
    bool AiConfigured = false);
