namespace Backend.Models;

public sealed record EmailCampaignSmtpSettings(
    string Host,
    int Port,
    string SecureMode,
    string? Username,
    string? Password,
    string? FromName,
    string FromEmail);
