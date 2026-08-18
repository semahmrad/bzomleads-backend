using Backend.Models;
using Backend.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System.Text.Json;
using System.Threading.RateLimiting;

var builder = WebApplication.CreateBuilder(args);

if (int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var renderPort))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{renderPort}");
}

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.Configure<SaasOptions>(builder.Configuration.GetSection("Saas"));

var allowedOrigins = builder.Configuration
    .GetSection("Saas:AllowedOrigins")
    .GetChildren()
    .Select(static entry => entry.Value)
    .Where(static value => !string.IsNullOrWhiteSpace(value))
    .Select(static value => value!)
    .ToArray();

if (allowedOrigins.Length == 0)
{
    allowedOrigins = ["http://127.0.0.1:4200", "http://localhost:4200"];
}

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy
            .WithOrigins(allowedOrigins)
            .AllowAnyMethod()
            .AllowAnyHeader()
            .AllowCredentials();
    });
});

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "lead-radar-session";
        options.Cookie.HttpOnly = true;
        options.Cookie.IsEssential = true;
        options.Cookie.SameSite = SameSiteMode.Strict;
        options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        options.ExpireTimeSpan = TimeSpan.FromHours(12);
        options.SlidingExpiration = true;
        options.Events.OnRedirectToLogin = context =>
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
        options.Events.OnValidatePrincipal = async context =>
        {
            var userId = context.Principal?.FindFirstValue(ClaimTypes.NameIdentifier);
            var saasStore = context.HttpContext.RequestServices.GetRequiredService<SaasStoreService>();
            var user = string.IsNullOrWhiteSpace(userId)
                ? null
                : await saasStore.FindUserByIdAsync(userId, context.HttpContext.RequestAborted);

            if (user is null || !user.IsActive)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var sessionVersionClaim = context.Principal?.FindFirstValue("session_version");
            if (int.TryParse(sessionVersionClaim, out var cookieSessionVersion) &&
                cookieSessionVersion != user.SessionVersion)
            {
                context.RejectPrincipal();
                await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
                return;
            }

            var claimsChanged =
                !string.Equals(context.Principal?.FindFirstValue(ClaimTypes.Name), user.Username, StringComparison.Ordinal) ||
                !string.Equals(context.Principal?.FindFirstValue("display_name"), user.DisplayName, StringComparison.Ordinal) ||
                !string.Equals(context.Principal?.FindFirstValue("country_code"), user.CountryCode, StringComparison.Ordinal) ||
                !string.Equals(context.Principal?.FindFirstValue("country_codes"), user.AssignedCountryCodes, StringComparison.Ordinal) ||
                !string.Equals(
                    context.Principal?.FindFirstValue("must_change_password"),
                    user.MustChangePassword ? "true" : "false",
                    StringComparison.Ordinal) ||
                !string.Equals(
                    sessionVersionClaim,
                    user.SessionVersion.ToString(),
                    StringComparison.Ordinal);

            if (claimsChanged)
            {
                context.ReplacePrincipal(BuildPrincipal(user));
                context.ShouldRenew = true;
            }
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("SaasUser", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim("must_change_password", "false");
    });
    options.AddPolicy("AdminOnly", policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireRole(AppRoles.Admin);
        policy.RequireClaim("must_change_password", "false");
    });
});
builder.Services.AddDataProtection();
builder.Services.AddHttpContextAccessor();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.AddPolicy("login", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
    options.AddPolicy("password-recovery", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            static _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 3,
                Window = TimeSpan.FromMinutes(15),
                QueueLimit = 0,
                AutoReplenishment = true
            }));
});

builder.Services.Configure<GeminiProxyOptions>(builder.Configuration.GetSection("GeminiProxy"));
builder.Services.Configure<GooglePlacesOptions>(builder.Configuration.GetSection("GooglePlaces"));

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
builder.Services.AddSingleton<SaasStoreService>();
builder.Services.AddSingleton<SearchCancellationRegistry>();
builder.Services.AddSingleton<WebsiteProjectStoreService>();
builder.Services.AddScoped<LeadSearchService>();
builder.Services.AddScoped<SmtpCampaignService>();
builder.Services.AddScoped<AdminPasswordRecoveryService>();
builder.Services.AddScoped<WebsiteProjectService>();

var app = builder.Build();

app.UseSwagger();
app.UseSwaggerUI();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

await app.Services.GetRequiredService<SaasStoreService>().InitializeAsync();

app.MapGet("/", () => Results.Ok(new
{
    message = "Lead Radar SaaS API is running.",
    endpoints = new[]
    {
        "POST /api/auth/login",
        "POST /api/auth/forgot-password (Admin only)",
        "POST /api/auth/reset-admin-password (Admin only)",
        "GET /api/auth/me",
        "POST /api/auth/change-password",
        "GET|POST /api/admin/users (Admin)",
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

app.MapPost("/api/auth/login", async (
    HttpContext httpContext,
    LoginRequest? request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var username = request?.Username?.Trim();
    var password = request?.Password ?? string.Empty;
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password) || password.Length > 256)
    {
        return Results.BadRequest(new { error = "Nom d utilisateur et mot de passe obligatoires." });
    }

    var user = await saasStore.FindUserByUsernameAsync(username, cancellationToken);
    if (user is null || !user.IsActive ||
        saasStore.VerifyPassword(user, password) == PasswordVerificationResult.Failed)
    {
        return Results.Json(
            new { error = "Identifiants incorrects." },
            statusCode: StatusCodes.Status401Unauthorized);
    }

    await SignInUserAsync(httpContext, user);
    await saasStore.MarkLoginAsync(user.Id, cancellationToken);
    return Results.Ok(SaasStoreService.ToResponse(user));
}).RequireRateLimiting("login");

app.MapPost("/api/auth/forgot-password", async (
    ForgotAdminPasswordRequest? request,
    AdminPasswordRecoveryService recoveryService,
    ILogger<AdminPasswordRecoveryService> logger,
    CancellationToken cancellationToken) =>
{
    try
    {
        await recoveryService.RequestResetAsync(request?.Username, cancellationToken);
    }
    catch (Exception ex) when (ex is not OperationCanceledException)
    {
        logger.LogWarning(ex, "Administrator password recovery email could not be sent.");
    }

    return Results.Ok(new
    {
        message = "Si ce nom correspond au compte administrateur et si Gmail est configure, un lien vient d etre envoye a l adresse de recuperation."
    });
}).RequireRateLimiting("password-recovery");

app.MapPost("/api/auth/reset-admin-password", async (
    ResetAdminPasswordRequest? request,
    AdminPasswordRecoveryService recoveryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await recoveryService.ResetPasswordAsync(
            request?.Token,
            request?.NewPassword,
            cancellationToken);
        return Results.Ok(new { message = "Mot de passe administrateur modifie. Tu peux maintenant te connecter." });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireRateLimiting("password-recovery");

app.MapGet("/api/auth/me", async (
    HttpContext httpContext,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
    if (string.IsNullOrWhiteSpace(userId))
    {
        return Results.Unauthorized();
    }

    var user = await saasStore.FindUserByIdAsync(userId, cancellationToken);
    if (user is null || !user.IsActive)
    {
        await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
        return Results.Unauthorized();
    }

    return Results.Ok(SaasStoreService.ToResponse(user));
}).RequireAuthorization();

app.MapPost("/api/auth/logout", async (HttpContext httpContext) =>
{
    await httpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.NoContent();
}).RequireAuthorization();

app.MapPost("/api/auth/change-password", async (
    HttpContext httpContext,
    ChangePasswordRequest? request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier);
    var user = string.IsNullOrWhiteSpace(userId)
        ? null
        : await saasStore.FindUserByIdAsync(userId, cancellationToken);
    if (user is null || !user.IsActive)
    {
        return Results.Unauthorized();
    }

    try
    {
        var updatedUser = await saasStore.ChangePasswordAsync(
            user,
            request?.CurrentPassword ?? string.Empty,
            request?.NewPassword ?? string.Empty,
            cancellationToken);
        await SignInUserAsync(httpContext, updatedUser);
        return Results.Ok(SaasStoreService.ToResponse(updatedUser));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization();

app.MapGet("/api/meta/countries", () => Results.Ok(CountryCatalog.GetAll()))
    .RequireAuthorization("SaasUser");

app.MapGet("/api/account/ai-settings", async (
    HttpContext httpContext,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var settings = await saasStore.GetUserAiSettingsAsync(userId, cancellationToken);
    return Results.Ok(new AiSettingsResponse(
        Configured: settings is not null,
        MaskedApiKey: settings is null ? null : SaasStoreService.MaskApiKey(settings.ApiKey),
        Model: settings?.Model ?? GoogleAiModelCatalog.DefaultModel,
        AvailableModels: GoogleAiModelCatalog.Models));
}).RequireAuthorization("SaasUser");

app.MapPut("/api/account/ai-settings", async (
    HttpContext httpContext,
    UpdateAiSettingsRequest request,
    SaasStoreService saasStore,
    GeminiProxyService geminiProxy,
    CancellationToken cancellationToken) =>
{
    var userId = httpContext.User.FindFirstValue(ClaimTypes.NameIdentifier)!;
    var user = await saasStore.FindUserByIdAsync(userId, cancellationToken);
    if (user is null || !user.IsActive)
    {
        return Results.Unauthorized();
    }

    try
    {
        var existing = await saasStore.GetUserAiSettingsAsync(userId, cancellationToken);
        var apiKey = string.IsNullOrWhiteSpace(request.ApiKey) ? existing?.ApiKey : request.ApiKey.Trim();
        var model = string.IsNullOrWhiteSpace(request.Model)
            ? existing?.Model ?? GoogleAiModelCatalog.DefaultModel
            : request.Model.Trim();
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Results.BadRequest(new { error = "Ajoute ta cle Google AI Studio." });
        }

        await geminiProxy.ValidateCredentialsAsync(apiKey, model, cancellationToken);
        await saasStore.SaveUserAiSettingsAsync(userId, apiKey, model, cancellationToken);
        return Results.Ok(SaasStoreService.ToResponse(user with { AiConfigured = true }));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (HttpRequestException)
    {
        return Results.BadRequest(new { error = "Impossible de verifier la cle avec Google AI Studio." });
    }
}).RequireAuthorization("SaasUser");

app.MapGet("/api/admin/security-settings", async (
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var settings = await saasStore.GetAdminRecoverySettingsAsync(cancellationToken);
    return Results.Ok(new AdminRecoverySettingsResponse(
        settings.RecoveryEmail,
        settings.SmtpUsername,
        !string.IsNullOrWhiteSpace(settings.SmtpAppPassword),
        "smtp.gmail.com",
        587));
}).RequireAuthorization("AdminOnly");

app.MapPut("/api/admin/security-settings", async (
    UpdateAdminRecoverySettingsRequest request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var settings = await saasStore.SaveAdminRecoverySettingsAsync(request, cancellationToken);
        return Results.Ok(new AdminRecoverySettingsResponse(
            settings.RecoveryEmail,
            settings.SmtpUsername,
            !string.IsNullOrWhiteSpace(settings.SmtpAppPassword),
            "smtp.gmail.com",
            587));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/security-settings/test-email", async (
    AdminPasswordRecoveryService recoveryService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await recoveryService.SendTestEmailAsync(cancellationToken);
        return Results.Ok(new { message = "Email de test envoye a l adresse de recuperation." });
    }
    catch (InvalidOperationException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/users", async (
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    return Results.Ok(await saasStore.GetUsersWithStatsAsync(cancellationToken));
}).RequireAuthorization("AdminOnly");

app.MapGet("/api/admin/websites", async (
    HttpContext httpContext,
    SaasStoreService saasStore,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    var users = await saasStore.GetUsersWithStatsAsync(cancellationToken);
    var projects = await websiteProjectService.ListAdminProjectsAsync(
        baseUrl,
        users,
        cancellationToken);
    return Results.Ok(projects);
}).RequireAuthorization("AdminOnly");

app.MapPatch("/api/admin/websites/{projectId}/client-delivery", async (
    string projectId,
    UpdateClientDeliveryRequest request,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    try
    {
        await websiteProjectService.UpdateClientDeliveryAsync(projectId, request, cancellationToken);
        return Results.NoContent();
    }
    catch (FileNotFoundException ex)
    {
        return Results.NotFound(new { error = ex.Message });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/users", async (
    HttpContext httpContext,
    CreateUserRequest request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var actor = GetUserActor(httpContext.User);
        var user = await saasStore.CreateUserAsync(request, actor.UserId, cancellationToken);
        return Results.Created($"/api/admin/users/{user.Id}", SaasStoreService.ToResponse(user));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPatch("/api/admin/users/{userId}", async (
    string userId,
    UpdateUserRequest request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var user = await saasStore.UpdateCommercialAsync(userId, request, cancellationToken);
        return Results.Ok(SaasStoreService.ToResponse(user));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

app.MapPost("/api/admin/users/{userId}/reset-password", async (
    string userId,
    AdminResetPasswordRequest? request,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    try
    {
        var user = await saasStore.ResetCommercialPasswordAsync(
            userId,
            request?.NewPassword ?? string.Empty,
            cancellationToken);
        return Results.Ok(SaasStoreService.ToResponse(user));
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("AdminOnly");

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
}).RequireAuthorization("SaasUser");

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
}).RequireAuthorization("SaasUser");

app.MapPost("/api/leads/search", async (
    HttpContext httpContext,
    LeadSearchRequest request,
    LeadSearchService leadService,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
    CountryOptionResponse searchCountry;
    try
    {
        searchCountry = ResolveSearchCountry(actor, request.CountryCode);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    var scopedRequest = request with
    {
        CountryCode = searchCountry.Code,
        CountryName = searchCountry.Name
    };

    try
    {
        var response = await leadService.SearchAsync(scopedRequest, cancellationToken);
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: true,
            metricValue: response.NewResultsCount,
            secondaryValue: response.Total,
            details: $"{searchCountry.Code}|{response.Query}|{response.BusinessType}",
            cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("SaasUser");

app.MapPost("/api/leads/search/stream", async (
    HttpContext httpContext,
    LeadSearchRequest request,
    LeadSearchService leadService,
    SaasStoreService saasStore,
    SearchCancellationRegistry cancellationRegistry,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
    CountryOptionResponse searchCountry;
    try
    {
        searchCountry = ResolveSearchCountry(actor, request.CountryCode);
    }
    catch (ArgumentException ex)
    {
        httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
        await httpContext.Response.WriteAsJsonAsync(new { error = ex.Message }, cancellationToken);
        return;
    }
    var searchSessionId = Guid.TryParse(request.SearchSessionId, out var parsedSessionId)
        ? parsedSessionId.ToString("N")
        : Guid.NewGuid().ToString("N");
    using var searchLease = cancellationRegistry.Register(
        actor.UserId,
        searchSessionId,
        cancellationToken);
    var searchCancellationToken = searchLease.CancellationToken;
    var scopedRequest = request with
    {
        CountryCode = searchCountry.Code,
        CountryName = searchCountry.Name
    };
    var response = httpContext.Response;
    response.ContentType = "application/x-ndjson";
    response.StatusCode = StatusCodes.Status200OK;
    await response.StartAsync(searchCancellationToken);

    var options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    try
    {
        LeadSearchResponseSummary? finalSummary = null;
        string? streamError = null;
        var messages = leadService.SearchStreamAsync(scopedRequest, searchCancellationToken);
        await foreach (var message in messages.WithCancellation(searchCancellationToken))
        {
            if (message.Type == "done")
            {
                finalSummary = message.Summary;
            }
            else if (message.Type == "error")
            {
                streamError = message.ErrorMessage;
            }

            var line = JsonSerializer.Serialize(message, options);
            await response.WriteAsync(line + "\n", searchCancellationToken);
            await response.Body.FlushAsync(searchCancellationToken);
        }

        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: finalSummary is not null && string.IsNullOrWhiteSpace(streamError),
            metricValue: finalSummary?.NewResultsCount ?? 0,
            secondaryValue: finalSummary?.Total ?? 0,
            details: streamError ?? $"{searchCountry.Code}|{scopedRequest.LocationQuery}|{scopedRequest.BusinessType}",
            cancellationToken: CancellationToken.None);
    }
    catch (OperationCanceledException) when (searchCancellationToken.IsCancellationRequested)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: false,
            details: "Recherche arretee par l utilisateur.",
            cancellationToken: CancellationToken.None);
    }
    catch (Exception ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.LeadSearchActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        var errMessage = new LeadStreamMessage("error", ErrorMessage: ex.Message);
        var line = JsonSerializer.Serialize(errMessage, options);
        await response.WriteAsync(line + "\n", searchCancellationToken);
    }
}).RequireAuthorization("SaasUser");

app.MapPost("/api/leads/search/{searchSessionId}/cancel", (
    HttpContext httpContext,
    string searchSessionId,
    SearchCancellationRegistry cancellationRegistry) =>
{
    try
    {
        var actor = GetUserActor(httpContext.User);
        var cancelled = cancellationRegistry.Cancel(actor.UserId, searchSessionId);
        return Results.Ok(new { cancelled });
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
}).RequireAuthorization("SaasUser");

app.MapPost("/api/email-campaigns/send", async (
    HttpContext httpContext,
    EmailCampaignSendRequest request,
    SmtpCampaignService smtpCampaignService,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
    try
    {
        var response = await smtpCampaignService.SendAsync(request, cancellationToken);
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.EmailCampaignActivity,
            success: true,
            metricValue: response.SentCount,
            secondaryValue: response.RequestedCount,
            details: response.FailedCount == 0 ? null : $"{response.FailedCount} echec(s)",
            cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.EmailCampaignActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.EmailCampaignActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("SaasUser");

app.MapPost("/api/websites/generate", async (
    HttpContext httpContext,
    WebsiteProjectService websiteProjectService,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
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
            actor,
            cancellationToken);

        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.WebsiteCreatedActivity,
            success: true,
            metricValue: 1,
            details: response.ProjectId,
            cancellationToken: cancellationToken);
        return Results.Ok(response);
    }
    catch (ArgumentException ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.WebsiteCreatedActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (Exception ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.WebsiteCreatedActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("SaasUser");

app.MapPost("/api/websites/projects/{projectId}/edit", async (
    HttpContext httpContext,
    string projectId,
    WebsiteProjectService websiteProjectService,
    SaasStoreService saasStore,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
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
            actor,
            cancellationToken);

        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.WebsiteEditedActivity,
            success: true,
            metricValue: 1,
            details: response.ProjectId,
            cancellationToken: cancellationToken);
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
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
    catch (Exception ex)
    {
        await saasStore.TryRecordActivityAsync(
            actor.UserId,
            SaasStoreService.WebsiteEditedActivity,
            success: false,
            details: ex.Message,
            cancellationToken: CancellationToken.None);
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status500InternalServerError);
    }
}).RequireAuthorization("SaasUser");

app.MapGet("/api/websites/projects", async (
    HttpContext httpContext,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    var actor = GetUserActor(httpContext.User);
    var baseUrl = $"{httpContext.Request.Scheme}://{httpContext.Request.Host}";
    return Results.Ok(await websiteProjectService.ListOwnedProjectsAsync(
        actor,
        baseUrl,
        cancellationToken));
}).RequireAuthorization("SaasUser");

app.MapGet("/api/websites/projects/{projectId}/download", async (
    HttpContext httpContext,
    string projectId,
    WebsiteProjectService websiteProjectService,
    CancellationToken cancellationToken) =>
{
    try
    {
        var actor = GetUserActor(httpContext.User);
        var archive = await websiteProjectService.LoadArchiveAsync(projectId, actor, cancellationToken);
        return archive is null
            ? Results.NotFound(new { error = "The generated website archive was not found." })
            : Results.File(archive.Value.Content, "application/zip", fileDownloadName: archive.Value.FileName);
    }
    catch (ArgumentException ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
    catch (UnauthorizedAccessException ex)
    {
        return Results.Json(new { error = ex.Message }, statusCode: StatusCodes.Status403Forbidden);
    }
}).RequireAuthorization("SaasUser");

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

static async Task SignInUserAsync(HttpContext httpContext, AppUserEntity user)
{
    var principal = BuildPrincipal(user);
    var properties = new AuthenticationProperties
    {
        IsPersistent = false,
        AllowRefresh = true,
        ExpiresUtc = DateTimeOffset.UtcNow.AddHours(12)
    };

    await httpContext.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        principal,
        properties);
}

static ClaimsPrincipal BuildPrincipal(AppUserEntity user)
{
    var claims = new List<Claim>
    {
        new(ClaimTypes.NameIdentifier, user.Id),
        new(ClaimTypes.Name, user.Username),
        new(ClaimTypes.Role, user.Role),
        new("display_name", user.DisplayName),
        new("country_code", user.CountryCode),
        new("country_name", user.CountryName),
        new("country_codes", string.IsNullOrWhiteSpace(user.AssignedCountryCodes) ? user.CountryCode : user.AssignedCountryCodes),
        new("must_change_password", user.MustChangePassword ? "true" : "false"),
        new("session_version", user.SessionVersion.ToString())
    };
    var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);
    return new ClaimsPrincipal(identity);
}

static UserActor GetUserActor(ClaimsPrincipal principal)
{
    var userId = principal.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new UnauthorizedAccessException("Authenticated user id is missing.");
    var username = principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty;
    var role = principal.FindFirstValue(ClaimTypes.Role) ?? AppRoles.User;
    var displayName = principal.FindFirstValue("display_name") ?? username;
    var countryCode = principal.FindFirstValue("country_code") ?? string.Empty;
    var countryName = principal.FindFirstValue("country_name") ?? countryCode;
    var countryCodes = (principal.FindFirstValue("country_codes") ?? countryCode)
        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    return new UserActor(userId, username, displayName, role, countryCode, countryName, countryCodes);
}

static CountryOptionResponse ResolveSearchCountry(UserActor actor, string? requestedCountryCode)
{
    var normalizedCode = CountryCatalog.NormalizeCode(requestedCountryCode);
    if (string.IsNullOrWhiteSpace(normalizedCode))
    {
        normalizedCode = actor.CountryCode;
    }
    if (!actor.AllowedCountryCodes.Contains(normalizedCode, StringComparer.OrdinalIgnoreCase))
    {
        throw new ArgumentException("Ce pays n est pas autorise pour ce compte commercial.");
    }
    return CountryCatalog.Find(normalizedCode)
        ?? throw new ArgumentException("Le pays selectionne est invalide.");
}

app.Run();
