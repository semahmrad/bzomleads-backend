namespace Backend.Models;

public sealed record ForgotAdminPasswordRequest(string? Username);

public sealed record ResetAdminPasswordRequest(string? Token, string? NewPassword);

public sealed record AdminRecoverySettingsResponse(
    string RecoveryEmail,
    string SmtpUsername,
    bool SmtpConfigured,
    string SmtpHost,
    int SmtpPort);

public sealed record UpdateAdminRecoverySettingsRequest(
    string? RecoveryEmail,
    string? SmtpUsername,
    string? SmtpAppPassword);

internal sealed record AdminRecoverySettings(
    string RecoveryEmail,
    string SmtpUsername,
    string? SmtpAppPassword,
    DateTimeOffset UpdatedUtc);
