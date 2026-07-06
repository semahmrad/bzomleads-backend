namespace Backend.Models;

public sealed record WebsiteContactExtractionResult(
    IReadOnlyList<string> Emails,
    IReadOnlyList<string> PhoneNumbers,
    IReadOnlyList<string> ContactPageUris,
    string Source);
