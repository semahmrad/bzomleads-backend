namespace Backend.Models;

public sealed record EmailCampaignRecipient(
    string LeadId,
    string BusinessName,
    string EmailAddress,
    string? WebsiteUri);
