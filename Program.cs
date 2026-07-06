using Backend.Models;
using Backend.Services;
using System.Text.Json;

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
builder.Services.Configure<GooglePlacesOptions>(builder.Configuration.GetSection("GooglePlaces"));
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

builder.Services.PostConfigure<GooglePlacesOptions>(options =>
{
    if (string.IsNullOrWhiteSpace(options.ApiKey))
    {
        options.ApiKey = Environment.GetEnvironmentVariable("GOOGLE_MAPS_API_KEY") ?? string.Empty;
    }
});

builder.Services.AddHttpClient<GeminiProxyService>();
builder.Services.AddHttpClient<WebsiteEmailExtractionService>();
builder.Services.AddHttpClient<GooglePlacesLeadService>();
builder.Services.AddHttpClient<OpenStreetMapLeadService>();
builder.Services.AddHttpClient<GooglePlaceWebsiteEnrichmentService>();
builder.Services.AddHttpClient<BusinessWebsiteGenerationService>();
builder.Services.AddHttpClient<GitHubPagesDeploymentService>();
builder.Services.AddSingleton<GoogleMapsPublicLeadEnrichmentService>();
builder.Services.AddSingleton<LeadSearchStoreService>();
builder.Services.AddSingleton<WebsiteProjectStoreService>();
builder.Services.AddScoped<LeadSearchService>();
builder.Services.AddScoped<SmtpCampaignService>();
builder.Services.AddScoped<WebsiteProjectService>();

var app = builder.Build();

app.UseCors();

app.MapGet("/", () => Results.Ok(new
{
    message = "Gemini .NET + Angular clone is running.",
    endpoints = new[]
    {
        "GET /api/ask?prompt=hello",
        "POST /api/ask",
        "POST /api/leads/search",
        "POST /api/email-campaigns/send",
        "POST /api/websites/generate",
        "POST /api/websites/projects/{projectId}/edit",
        "GET /api/websites/projects/{projectId}/download"
    }
}));

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapMethods("/api/ask", new[] { "OPTIONS" }, () => Results.NoContent());
app.MapMethods("/api/leads/search", new[] { "OPTIONS" }, () => Results.NoContent());
app.MapMethods("/api/email-campaigns/send", new[] { "OPTIONS" }, () => Results.NoContent());
app.MapMethods("/api/websites/generate", new[] { "OPTIONS" }, () => Results.NoContent());
app.MapMethods("/api/websites/projects/{projectId}/edit", new[] { "OPTIONS" }, () => Results.NoContent());

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

app.MapPost("/api/leads/search", async (
    LeadSearchRequest request,
    LeadSearchService leadService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await leadService.SearchAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/email-campaigns/send", async (
    EmailCampaignSendRequest request,
    SmtpCampaignService smtpCampaignService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var response = await smtpCampaignService.SendAsync(request, cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/websites/generate", async (
    HttpContext httpContext,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var requestOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        WebsiteGenerationRequest? generationRequest;
        var uploadedImages = new List<WebsiteUploadedAsset>();
        WebsiteUploadedAsset? uploadedLogo = null;

        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var requestJson = form["requestJson"].ToString();
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return Results.BadRequest(new { error = "requestJson is required in multipart form-data." });
            }

            generationRequest = JsonSerializer.Deserialize<WebsiteGenerationRequest>(requestJson, requestOptions);
            if (generationRequest is null)
            {
                return Results.BadRequest(new { error = "The website generation payload is invalid." });
            }

            foreach (var file in form.Files.Where(file => string.Equals(file.Name, "uploadedImages", StringComparison.OrdinalIgnoreCase)))
            {
                uploadedImages.Add(await ToUploadedAssetAsync(file, cancellationToken));
            }

            var logoFile = form.Files.FirstOrDefault(file => string.Equals(file.Name, "uploadedLogo", StringComparison.OrdinalIgnoreCase));
            if (logoFile is not null)
            {
                uploadedLogo = await ToUploadedAssetAsync(logoFile, cancellationToken);
            }
        }
        else
        {
            generationRequest = await httpContext.Request.ReadFromJsonAsync<WebsiteGenerationRequest>(requestOptions, cancellationToken);
            if (generationRequest is null)
            {
                return Results.BadRequest(new { error = "The website generation payload is required." });
            }
        }

        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var response = await websiteProjectService.GenerateAsync(
            generationRequest,
            uploadedImages,
            uploadedLogo,
            baseUrl,
            cancellationToken);

        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapPost("/api/websites/projects/{projectId}/edit", async (
    HttpContext httpContext,
    string projectId,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var requestOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        WebsiteProjectEditRequest? editRequest;
        var uploadedImages = new List<WebsiteUploadedAsset>();
        WebsiteUploadedAsset? uploadedLogo = null;

        if (httpContext.Request.HasFormContentType)
        {
            var form = await httpContext.Request.ReadFormAsync(cancellationToken);
            var requestJson = form["requestJson"].ToString();
            if (string.IsNullOrWhiteSpace(requestJson))
            {
                return Results.BadRequest(new { error = "requestJson is required in multipart form-data." });
            }

            editRequest = JsonSerializer.Deserialize<WebsiteProjectEditRequest>(requestJson, requestOptions);
            if (editRequest is null)
            {
                return Results.BadRequest(new { error = "The website edit payload is invalid." });
            }

            foreach (var file in form.Files.Where(file => string.Equals(file.Name, "uploadedImages", StringComparison.OrdinalIgnoreCase)))
            {
                uploadedImages.Add(await ToUploadedAssetAsync(file, cancellationToken));
            }

            var logoFile = form.Files.FirstOrDefault(file => string.Equals(file.Name, "uploadedLogo", StringComparison.OrdinalIgnoreCase));
            if (logoFile is not null)
            {
                uploadedLogo = await ToUploadedAssetAsync(logoFile, cancellationToken);
            }
        }
        else
        {
            editRequest = await httpContext.Request.ReadFromJsonAsync<WebsiteProjectEditRequest>(requestOptions, cancellationToken);
            if (editRequest is null)
            {
                return Results.BadRequest(new { error = "The website edit payload is required." });
            }
        }

        var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
        var response = await websiteProjectService.EditAsync(
            projectId,
            editRequest.Prompt,
            uploadedImages,
            uploadedLogo,
            baseUrl,
            cancellationToken);

        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
});

app.MapGet("/api/websites/projects/{projectId}/download", async (
    string projectId,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    var archive = await websiteProjectService.LoadArchiveAsync(projectId, cancellationToken);
    return archive is null
        ? Results.NotFound(new { error = "The generated website archive was not found." })
        : Results.File(archive.Value.Content, "application/zip", fileDownloadName: archive.Value.FileName);
});

static async Task<WebsiteUploadedAsset> ToUploadedAssetAsync(
    IFormFile file,
    CancellationToken cancellationToken)
{
    await using var memoryStream = new MemoryStream();
    await file.CopyToAsync(memoryStream, cancellationToken);

    return new WebsiteUploadedAsset(
        file.FileName,
        file.ContentType,
        memoryStream.ToArray());
}

app.Run();
