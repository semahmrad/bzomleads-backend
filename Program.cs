using Backend.Models;
using Backend.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

builder.Services.Configure<GeminiProxyOptions>(builder.Configuration.GetSection("GeminiProxy"));
builder.Services.PostConfigure<GeminiProxyOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.RequestConfigPath))
    {
        options.RequestConfigPath = Path.Combine("Config", "gemini_request.json");
    }

    if (!Path.IsPathRooted(options.RequestConfigPath))
    {
        options.RequestConfigPath = Path.GetFullPath(
            Path.Combine(builder.Environment.ContentRootPath, options.RequestConfigPath));
    }
});

builder.Services.AddHttpClient<GeminiProxyService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
    message = "Gemini .NET + Angular clone is running.",
    endpoints = new[]
    {
        "GET /api/ask?prompt=hello",
        "POST /api/ask"
    }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapMethods("/api/ask", new[] { "OPTIONS" }, () => Results.NoContent());

app.MapGet("/api/ask", async (
    string? prompt,
    GeminiProxyService geminiProxy,
    CancellationToken cancellationToken) =>
{
    var normalizedPrompt = prompt?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedPrompt))
    {
        return Results.BadRequest(new { error = "No prompt provided" });
    }

    try
    {
        var response = await geminiProxy.AskAsync(normalizedPrompt, cancellationToken);
        return Results.Ok(new AskResponse(normalizedPrompt, response));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/ask", async (
    AskRequest? request,
    GeminiProxyService geminiProxy,
    CancellationToken cancellationToken) =>
{
    var normalizedPrompt = request?.Prompt?.Trim();
    if (string.IsNullOrWhiteSpace(normalizedPrompt))
    {
        return Results.BadRequest(new { error = "No prompt provided" });
    }

    try
    {
        var response = await geminiProxy.AskAsync(normalizedPrompt, cancellationToken);
        return Results.Ok(new AskResponse(normalizedPrompt, response));
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.Run();
