namespace Backend.Models;

public sealed record EmailCampaignSendResponse(
    int RequestedCount,
    int SentCount,
    int FailedCount,
    IReadOnlyList<EmailCampaignSendFailure> Failures);

public sealed record EmailCampaignSendFailure(
    string EmailAddress,
    string? BusinessName,
    string ErrorMessage);
