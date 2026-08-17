using System.Security.Cryptography;
using Backend.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;

namespace Backend.Services;

public sealed class SaasStoreService
{
    public const string LeadSearchActivity = "lead_search";
    public const string EmailCampaignActivity = "email_campaign";
    public const string WebsiteCreatedActivity = "website_created";
    public const string WebsiteEditedActivity = "website_edited";

    private readonly string _connectionString;
    private readonly SaasOptions _options;
    private readonly ILogger<SaasStoreService> _logger;
    private readonly PasswordHasher<AppUserEntity> _passwordHasher = new();
    private readonly IDataProtector _aiKeyProtector;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private volatile bool _initialized;

    public SaasStoreService(
        IHostEnvironment environment,
        IOptions<SaasOptions> options,
        IDataProtectionProvider dataProtectionProvider,
        ILogger<SaasStoreService> logger)
    {
        var dataDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(dataDirectory);

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(dataDirectory, "saas.db"),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
        _options = options.Value;
        _aiKeyProtector = dataProtectionProvider.CreateProtector("LeadRadar.UserGoogleAiApiKey.v1");
        _logger = logger;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);

            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                CREATE TABLE IF NOT EXISTS app_users (
                  id TEXT PRIMARY KEY,
                  username TEXT NOT NULL COLLATE NOCASE UNIQUE,
                  display_name TEXT NOT NULL,
                  password_hash TEXT NOT NULL,
                  role TEXT NOT NULL,
                  country_code TEXT NOT NULL,
                  country_name TEXT NOT NULL,
                  is_active INTEGER NOT NULL,
                  must_change_password INTEGER NOT NULL,
                  created_utc TEXT NOT NULL,
                  created_by_user_id TEXT NULL,
                  last_login_utc TEXT NULL,
                  assigned_country_codes TEXT NULL
                );

                CREATE TABLE IF NOT EXISTS user_activity (
                  id INTEGER PRIMARY KEY AUTOINCREMENT,
                  user_id TEXT NOT NULL,
                  activity_type TEXT NOT NULL,
                  success INTEGER NOT NULL,
                  metric_value INTEGER NOT NULL,
                  secondary_value INTEGER NOT NULL,
                  details TEXT NULL,
                  occurred_utc TEXT NOT NULL,
                  FOREIGN KEY (user_id) REFERENCES app_users(id)
                );

                CREATE INDEX IF NOT EXISTS idx_user_activity_user_time
                  ON user_activity (user_id, occurred_utc DESC);

                CREATE TABLE IF NOT EXISTS user_ai_settings (
                  user_id TEXT PRIMARY KEY,
                  api_key_protected TEXT NOT NULL,
                  model TEXT NOT NULL,
                  updated_utc TEXT NOT NULL,
                  FOREIGN KEY (user_id) REFERENCES app_users(id) ON DELETE CASCADE
                );
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
            await EnsureAssignedCountryCodesColumnAsync(connection, cancellationToken);

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }

        await EnsureBootstrapAdminAsync(cancellationToken);
        await RefreshStoredCountryNamesAsync(cancellationToken);
    }

    internal async Task<AppUserEntity?> FindUserByUsernameAsync(
        string username,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await FindUserAsync("username = $value", username.Trim(), cancellationToken);
    }

    internal async Task<AppUserEntity?> FindUserByIdAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        return await FindUserAsync("id = $value", userId.Trim(), cancellationToken);
    }

    internal PasswordVerificationResult VerifyPassword(AppUserEntity user, string password)
        => _passwordHasher.VerifyHashedPassword(user, user.PasswordHash, password);

    internal async Task<AppUserEntity> CreateUserAsync(
        CreateUserRequest request,
        string createdByUserId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);

        var username = NormalizeUsername(request.Username);
        var displayName = (request.DisplayName ?? string.Empty).Trim();
        var password = request.Password ?? string.Empty;
        var countries = NormalizeCountries(request.CountryCodes, request.CountryCode);
        var country = countries[0];

        if (username.Length is < 3 or > 40)
        {
            throw new ArgumentException("Le nom d utilisateur doit contenir entre 3 et 40 caracteres.");
        }

        if (!username.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException("Le nom d utilisateur accepte uniquement lettres, chiffres, point, tiret et underscore.");
        }

        if (displayName.Length is < 2 or > 100)
        {
            throw new ArgumentException("Le nom du commercial doit contenir entre 2 et 100 caracteres.");
        }

        ValidatePassword(password);

        var nowUtc = DateTimeOffset.UtcNow;
        var user = new AppUserEntity(
            Guid.NewGuid().ToString("N"),
            username,
            displayName,
            string.Empty,
            AppRoles.User,
            country.Code,
            country.Name,
            true,
            true,
            nowUtc,
            createdByUserId,
            null,
            SerializeCountryCodes(countries));
        user = user with { PasswordHash = _passwordHasher.HashPassword(user, password) };

        await InsertUserAsync(user, cancellationToken);
        return user;
    }

    internal async Task ChangePasswordAsync(
        AppUserEntity user,
        string currentPassword,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        if (VerifyPassword(user, currentPassword) == PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("Le mot de passe actuel est incorrect.");
        }

        ValidatePassword(newPassword);
        if (VerifyPassword(user, newPassword) != PasswordVerificationResult.Failed)
        {
            throw new ArgumentException("Le nouveau mot de passe doit etre different du mot de passe actuel.");
        }

        var updatedUser = user with { MustChangePassword = false };
        var passwordHash = _passwordHasher.HashPassword(updatedUser, newPassword);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_users
            SET password_hash = $passwordHash,
                must_change_password = 0
            WHERE id = $userId;
            """;
        command.Parameters.AddWithValue("$passwordHash", passwordHash);
        command.Parameters.AddWithValue("$userId", user.Id);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<AppUserEntity> UpdateCommercialAsync(
        string userId,
        UpdateUserRequest request,
        CancellationToken cancellationToken = default)
    {
        var user = await FindUserByIdAsync(userId, cancellationToken)
            ?? throw new ArgumentException("Le compte commercial est introuvable.");
        EnsureCommercial(user);

        var username = request.Username is null ? user.Username : NormalizeUsername(request.Username);
        var displayName = request.DisplayName is null ? user.DisplayName : request.DisplayName.Trim();
        var countries = request.CountryCodes is not null || request.CountryCode is not null
            ? NormalizeCountries(request.CountryCodes, request.CountryCode)
            : GetAssignedCountries(user);
        var country = countries[0];
        var isActive = request.IsActive ?? user.IsActive;

        ValidateUserProfile(username, displayName);

        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                UPDATE app_users
                SET username = $username,
                    display_name = $displayName,
                    country_code = $countryCode,
                    country_name = $countryName,
                    assigned_country_codes = $assignedCountryCodes,
                    is_active = $isActive
                WHERE id = $userId AND role = $role;
                """;
            command.Parameters.AddWithValue("$username", username);
            command.Parameters.AddWithValue("$displayName", displayName);
            command.Parameters.AddWithValue("$countryCode", country.Code);
            command.Parameters.AddWithValue("$countryName", country.Name);
            command.Parameters.AddWithValue("$assignedCountryCodes", SerializeCountryCodes(countries));
            command.Parameters.AddWithValue("$isActive", isActive);
            command.Parameters.AddWithValue("$userId", user.Id);
            command.Parameters.AddWithValue("$role", AppRoles.User);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("Ce nom d utilisateur existe deja.", ex);
        }

        return user with
        {
            Username = username,
            DisplayName = displayName,
            CountryCode = country.Code,
            CountryName = country.Name,
            AssignedCountryCodes = SerializeCountryCodes(countries),
            IsActive = isActive
        };
    }

    internal async Task<AppUserEntity> ResetCommercialPasswordAsync(
        string userId,
        string newPassword,
        CancellationToken cancellationToken = default)
    {
        var user = await FindUserByIdAsync(userId, cancellationToken)
            ?? throw new ArgumentException("Le compte commercial est introuvable.");
        EnsureCommercial(user);
        ValidatePassword(newPassword);

        var updatedUser = user with { MustChangePassword = true };
        var passwordHash = _passwordHasher.HashPassword(updatedUser, newPassword);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE app_users
            SET password_hash = $passwordHash,
                must_change_password = 1
            WHERE id = $userId AND role = $role;
            """;
        command.Parameters.AddWithValue("$passwordHash", passwordHash);
        command.Parameters.AddWithValue("$userId", user.Id);
        command.Parameters.AddWithValue("$role", AppRoles.User);
        await command.ExecuteNonQueryAsync(cancellationToken);
        return updatedUser;
    }

    public async Task MarkLoginAsync(string userId, CancellationToken cancellationToken = default)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE app_users SET last_login_utc = $nowUtc WHERE id = $userId;";
        command.Parameters.AddWithValue("$nowUtc", DateTimeOffset.UtcNow.ToString("O"));
        command.Parameters.AddWithValue("$userId", userId);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal async Task<UserAiSettings?> GetUserAiSettingsAsync(
        string userId,
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT api_key_protected, model, updated_utc FROM user_ai_settings WHERE user_id = $userId LIMIT 1;";
        command.Parameters.AddWithValue("$userId", userId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        try
        {
            return new UserAiSettings(
                _aiKeyProtector.Unprotect(reader.GetString(0)),
                reader.GetString(1),
                DateTimeOffset.Parse(reader.GetString(2)));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            _logger.LogWarning(ex, "Unable to decrypt the Google AI key for user {UserId}.", userId);
            return null;
        }
    }

    internal async Task SaveUserAiSettingsAsync(
        string userId,
        string apiKey,
        string model,
        CancellationToken cancellationToken = default)
    {
        var normalizedKey = apiKey.Trim();
        if (normalizedKey.Length is < 20 or > 256)
        {
            throw new ArgumentException("La cle Google AI Studio semble invalide.");
        }
        if (!GoogleAiModelCatalog.IsAllowed(model))
        {
            throw new ArgumentException("Le modele IA selectionne n est pas autorise.");
        }

        var protectedKey = _aiKeyProtector.Protect(normalizedKey);
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO user_ai_settings (user_id, api_key_protected, model, updated_utc)
            VALUES ($userId, $apiKeyProtected, $model, $updatedUtc)
            ON CONFLICT(user_id) DO UPDATE SET
              api_key_protected = excluded.api_key_protected,
              model = excluded.model,
              updated_utc = excluded.updated_utc;
            """;
        command.Parameters.AddWithValue("$userId", userId);
        command.Parameters.AddWithValue("$apiKeyProtected", protectedKey);
        command.Parameters.AddWithValue("$model", model);
        command.Parameters.AddWithValue("$updatedUtc", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    internal static string MaskApiKey(string apiKey)
        => apiKey.Length <= 8 ? "••••••••" : $"••••••••••••{apiKey[^4..]}";

    public async Task<IReadOnlyList<AdminUserResponse>> GetUsersWithStatsAsync(
        CancellationToken cancellationToken = default)
    {
        await InitializeAsync(cancellationToken);
        var results = new List<AdminUserResponse>();

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              u.id,
              u.username,
              u.display_name,
              u.role,
              u.country_code,
              u.country_name,
              u.assigned_country_codes,
              u.is_active,
              u.must_change_password,
              u.created_utc,
              u.last_login_utc,
              MAX(a.occurred_utc) AS last_activity_utc,
              SUM(CASE WHEN a.activity_type = 'lead_search' AND a.success = 1 THEN 1 ELSE 0 END) AS search_count,
              SUM(CASE WHEN a.activity_type = 'lead_search' AND a.success = 1 THEN a.metric_value ELSE 0 END) AS new_leads_count,
              SUM(CASE WHEN a.activity_type = 'email_campaign' AND a.success = 1 THEN 1 ELSE 0 END) AS campaign_count,
              SUM(CASE WHEN a.activity_type = 'email_campaign' AND a.success = 1 THEN a.metric_value ELSE 0 END) AS emails_sent,
              SUM(CASE WHEN a.activity_type = 'website_created' AND a.success = 1 THEN 1 ELSE 0 END) AS websites_created,
              SUM(CASE WHEN a.activity_type = 'website_edited' AND a.success = 1 THEN 1 ELSE 0 END) AS websites_edited,
              EXISTS(SELECT 1 FROM user_ai_settings ai WHERE ai.user_id = u.id) AS ai_configured
            FROM app_users u
            LEFT JOIN user_activity a ON a.user_id = u.id
            GROUP BY u.id
            ORDER BY CASE WHEN u.role = 'Admin' THEN 0 ELSE 1 END, u.created_utc DESC;
            """;

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new AdminUserResponse(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetBoolean(7),
                reader.GetBoolean(8),
                DateTimeOffset.Parse(reader.GetString(9)),
                reader.IsDBNull(10) ? null : DateTimeOffset.Parse(reader.GetString(10)),
                reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
                reader.GetInt32(12),
                reader.GetInt32(13),
                reader.GetInt32(14),
                reader.GetInt32(15),
                reader.GetInt32(16),
                reader.GetInt32(17),
                ParseCountries(reader.IsDBNull(6) ? reader.GetString(4) : reader.GetString(6)),
                reader.GetBoolean(18)));
        }

        return results;
    }

    public async Task TryRecordActivityAsync(
        string userId,
        string activityType,
        bool success,
        int metricValue = 0,
        int secondaryValue = 0,
        string? details = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await InitializeAsync(cancellationToken);
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO user_activity (
                  user_id, activity_type, success, metric_value, secondary_value, details, occurred_utc
                ) VALUES (
                  $userId, $activityType, $success, $metricValue, $secondaryValue, $details, $occurredUtc
                );
                """;
            command.Parameters.AddWithValue("$userId", userId);
            command.Parameters.AddWithValue("$activityType", activityType);
            command.Parameters.AddWithValue("$success", success);
            command.Parameters.AddWithValue("$metricValue", metricValue);
            command.Parameters.AddWithValue("$secondaryValue", secondaryValue);
            command.Parameters.AddWithValue("$details", string.IsNullOrWhiteSpace(details) ? DBNull.Value : details.Trim());
            command.Parameters.AddWithValue("$occurredUtc", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Unable to record SaaS activity {ActivityType} for user {UserId}.", activityType, userId);
        }
    }

    internal static AuthUserResponse ToResponse(AppUserEntity user)
        => new(
            user.Id,
            user.Username,
            user.DisplayName,
            user.Role,
            user.CountryCode,
            user.CountryName,
            user.MustChangePassword,
            GetAssignedCountries(user),
            user.AiConfigured);

    private async Task EnsureBootstrapAdminAsync(CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM app_users;";
        var userCount = Convert.ToInt32(await countCommand.ExecuteScalarAsync(cancellationToken));
        if (userCount > 0)
        {
            return;
        }

        var username = NormalizeUsername(
            Environment.GetEnvironmentVariable("SAAS_ADMIN_USERNAME") ?? _options.BootstrapAdminUsername);
        var displayName = (Environment.GetEnvironmentVariable("SAAS_ADMIN_DISPLAY_NAME")
                           ?? _options.BootstrapAdminDisplayName).Trim();
        var country = CountryCatalog.Find(
            Environment.GetEnvironmentVariable("SAAS_ADMIN_COUNTRY")
            ?? _options.BootstrapAdminCountryCode)
            ?? CountryCatalog.Find("FR")!;
        var configuredPassword = Environment.GetEnvironmentVariable("SAAS_ADMIN_PASSWORD");
        var generatedPassword = string.IsNullOrWhiteSpace(configuredPassword)
            ? GenerateBootstrapPassword()
            : configuredPassword;
        ValidatePassword(generatedPassword);

        var user = new AppUserEntity(
            Guid.NewGuid().ToString("N"),
            username,
            displayName,
            string.Empty,
            AppRoles.Admin,
            country.Code,
            country.Name,
            true,
            true,
            DateTimeOffset.UtcNow,
            null,
            null,
            country.Code);
        user = user with { PasswordHash = _passwordHasher.HashPassword(user, generatedPassword) };
        await InsertUserAsync(user, cancellationToken);

        if (string.IsNullOrWhiteSpace(configuredPassword))
        {
            _logger.LogWarning(
                "BOOTSTRAP ADMIN CREATED. Username: {Username} Temporary password: {Password}. Change it at first login.",
                username,
                generatedPassword);
        }
        else
        {
            _logger.LogInformation(
                "Bootstrap admin {Username} created from environment configuration.",
                username);
        }
    }

    private async Task RefreshStoredCountryNamesAsync(CancellationToken cancellationToken)
    {
        var updates = new List<(string UserId, string CountryName)>();

        await using (var connection = new SqliteConnection(_connectionString))
        {
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT id, country_code, country_name FROM app_users;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var country = CountryCatalog.Find(reader.GetString(1));
                if (country is not null &&
                    !string.Equals(country.Name, reader.GetString(2), StringComparison.Ordinal))
                {
                    updates.Add((reader.GetString(0), country.Name));
                }
            }
        }

        foreach (var update in updates)
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "UPDATE app_users SET country_name = $countryName WHERE id = $userId;";
            command.Parameters.AddWithValue("$countryName", update.CountryName);
            command.Parameters.AddWithValue("$userId", update.UserId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private async Task<AppUserEntity?> FindUserAsync(
        string predicate,
        string value,
        CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""
            SELECT id, username, display_name, password_hash, role, country_code, country_name,
                   is_active, must_change_password, created_utc, created_by_user_id, last_login_utc,
                   assigned_country_codes,
                   EXISTS(SELECT 1 FROM user_ai_settings ai WHERE ai.user_id = app_users.id)
            FROM app_users
            WHERE {predicate}
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$value", value);

        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadUser(reader) : null;
    }

    private async Task InsertUserAsync(AppUserEntity user, CancellationToken cancellationToken)
    {
        try
        {
            await using var connection = new SqliteConnection(_connectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                """
                INSERT INTO app_users (
                  id, username, display_name, password_hash, role, country_code, country_name,
                  is_active, must_change_password, created_utc, created_by_user_id, last_login_utc,
                  assigned_country_codes
                ) VALUES (
                  $id, $username, $displayName, $passwordHash, $role, $countryCode, $countryName,
                  $isActive, $mustChangePassword, $createdUtc, $createdByUserId, $lastLoginUtc,
                  $assignedCountryCodes
                );
                """;
            command.Parameters.AddWithValue("$id", user.Id);
            command.Parameters.AddWithValue("$username", user.Username);
            command.Parameters.AddWithValue("$displayName", user.DisplayName);
            command.Parameters.AddWithValue("$passwordHash", user.PasswordHash);
            command.Parameters.AddWithValue("$role", user.Role);
            command.Parameters.AddWithValue("$countryCode", user.CountryCode);
            command.Parameters.AddWithValue("$countryName", user.CountryName);
            command.Parameters.AddWithValue("$isActive", user.IsActive);
            command.Parameters.AddWithValue("$mustChangePassword", user.MustChangePassword);
            command.Parameters.AddWithValue("$createdUtc", user.CreatedUtc.ToString("O"));
            command.Parameters.AddWithValue("$createdByUserId", user.CreatedByUserId is null ? DBNull.Value : user.CreatedByUserId);
            command.Parameters.AddWithValue("$lastLoginUtc", user.LastLoginUtc is null ? DBNull.Value : user.LastLoginUtc.Value.ToString("O"));
            command.Parameters.AddWithValue("$assignedCountryCodes", string.IsNullOrWhiteSpace(user.AssignedCountryCodes) ? user.CountryCode : user.AssignedCountryCodes);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
        {
            throw new ArgumentException("Ce nom d utilisateur existe deja.", ex);
        }
    }

    private static AppUserEntity ReadUser(SqliteDataReader reader)
        => new(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            reader.GetString(3),
            reader.GetString(4),
            reader.GetString(5),
            reader.GetString(6),
            reader.GetBoolean(7),
            reader.GetBoolean(8),
            DateTimeOffset.Parse(reader.GetString(9)),
            reader.IsDBNull(10) ? null : reader.GetString(10),
            reader.IsDBNull(11) ? null : DateTimeOffset.Parse(reader.GetString(11)),
            reader.IsDBNull(12) ? reader.GetString(5) : reader.GetString(12),
            reader.GetBoolean(13));

    private static IReadOnlyList<CountryOptionResponse> NormalizeCountries(
        IReadOnlyList<string>? countryCodes,
        string? legacyCountryCode)
    {
        var requestedCodes = countryCodes is { Count: > 0 }
            ? countryCodes
            : string.IsNullOrWhiteSpace(legacyCountryCode)
                ? []
                : [legacyCountryCode];
        var countries = requestedCodes
            .Select(CountryCatalog.Find)
            .Where(static country => country is not null)
            .Cast<CountryOptionResponse>()
            .DistinctBy(static country => country.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (countries.Count == 0 || countries.Count != requestedCodes.Distinct(StringComparer.OrdinalIgnoreCase).Count())
        {
            throw new ArgumentException("Selectionne au moins un pays valide.");
        }
        if (countries.Count > 25)
        {
            throw new ArgumentException("Un commercial ne peut pas avoir plus de 25 pays.");
        }
        return countries;
    }

    private static IReadOnlyList<CountryOptionResponse> GetAssignedCountries(AppUserEntity user)
        => ParseCountries(string.IsNullOrWhiteSpace(user.AssignedCountryCodes)
            ? user.CountryCode
            : user.AssignedCountryCodes);

    private static IReadOnlyList<CountryOptionResponse> ParseCountries(string serializedCodes)
        => serializedCodes
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(CountryCatalog.Find)
            .Where(static country => country is not null)
            .Cast<CountryOptionResponse>()
            .DistinctBy(static country => country.Code, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string SerializeCountryCodes(IEnumerable<CountryOptionResponse> countries)
        => string.Join(',', countries.Select(static country => country.Code));

    private static async Task EnsureAssignedCountryCodesColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasColumn = false;
        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "PRAGMA table_info(app_users);";
            await using var reader = await checkCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(reader.GetString(1), "assigned_country_codes", StringComparison.OrdinalIgnoreCase))
                {
                    hasColumn = true;
                    break;
                }
            }
        }

        if (!hasColumn)
        {
            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText = "ALTER TABLE app_users ADD COLUMN assigned_country_codes TEXT NULL;";
            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText =
            "UPDATE app_users SET assigned_country_codes = country_code WHERE assigned_country_codes IS NULL OR TRIM(assigned_country_codes) = '';";
        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string NormalizeUsername(string? username)
        => (username ?? string.Empty).Trim().ToLowerInvariant();

    private static void EnsureCommercial(AppUserEntity user)
    {
        if (!string.Equals(user.Role, AppRoles.User, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException("Le compte administrateur ne peut pas etre modifie depuis la gestion des commerciaux.");
        }
    }

    private static void ValidateUserProfile(string username, string displayName)
    {
        if (username.Length is < 3 or > 40)
        {
            throw new ArgumentException("Le nom d utilisateur doit contenir entre 3 et 40 caracteres.");
        }

        if (!username.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException("Le nom d utilisateur accepte uniquement lettres, chiffres, point, tiret et underscore.");
        }

        if (displayName.Length is < 2 or > 100)
        {
            throw new ArgumentException("Le nom du commercial doit contenir entre 2 et 100 caracteres.");
        }
    }

    private static void ValidatePassword(string password)
    {
        if (password.Length is < 10 or > 128 ||
            !password.Any(char.IsUpper) ||
            !password.Any(char.IsLower) ||
            !password.Any(char.IsDigit))
        {
            throw new ArgumentException(
                "Le mot de passe doit contenir entre 10 et 128 caracteres, une majuscule, une minuscule et un chiffre.");
        }
    }

    private static string GenerateBootstrapPassword()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@#$%";
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        var characters = bytes.ToArray().Select(value => alphabet[value % alphabet.Length]).ToArray();
        characters[0] = 'A';
        characters[1] = 'a';
        characters[2] = '7';
        return new string(characters);
    }
}
