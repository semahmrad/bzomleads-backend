namespace Backend.Models;

public sealed record WebsiteUploadedAsset(
    string FileName,
    string ContentType,
    byte[] Content);
