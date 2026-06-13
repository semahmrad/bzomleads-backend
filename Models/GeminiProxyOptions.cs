namespace Backend.Models;

public sealed class GeminiProxyOptions
{
    public string RequestConfigPath { get; set; } = string.Empty;

    public int TimeoutSeconds { get; set; } = 60;
}
