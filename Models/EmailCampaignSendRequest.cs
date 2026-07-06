namespace Backend.Models;

public sealed record EmailCampaignSendRequest(
    EmailCampaignSmtpSettings Smtp,
    string Subject,
    string HtmlBody,
    IReadOnlyList<EmailCampaignRecipient> Recipients);
