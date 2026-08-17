using System.Text.Json;
using Backend.Models;
using Microsoft.Data.Sqlite;

namespace Backend.Services;

public sealed class LeadSearchStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly string _connectionString;
    private volatile bool _initialized;

    public LeadSearchStoreService(IHostEnvironment environment)
    {
        var databaseDirectory = Path.Combine(environment.ContentRootPath, "App_Data");
        Directory.CreateDirectory(databaseDirectory);

        var databasePath = Path.Combine(databaseDirectory, "lead-radar.db");
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared
        }.ToString();
    }

    public async Task<IReadOnlyList<LeadSearchResultItem>> GetStoredResultsAsync(
        string provider,
        string locationQuery,
        string businessType,
        string countryCode,
        CancellationToken cancellationToken = default)
    {
        await EnsureInitializedAsync(cancellationToken);

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT
              place_id,
              name,
              business_label,
              primary_type,
              formatted_address,
              phone_number,
              website_uri,
              google_maps_uri,
              business_status,
              rating,
              user_rating_count,
              latitude,
              longitude,
              has_website,
              email_extraction_source,
              email_addresses_json,
              contact_phone_numbers_json,
              contact_page_uris_json
            FROM lead_search_items
            WHERE search_key = $searchKey
            ORDER BY first_seen_utc ASC, name COLLATE NOCASE ASC;
            """;
        command.Parameters.AddWithValue(
            "$searchKey",
            BuildSearchKey(provider, locationQuery, businessType, countryCode));

        var items = new List<LeadSearchResultItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            items.Add(new LeadSearchResultItem(
                PlaceId: reader.GetString(0),
                Name: reader.GetString(1),
                BusinessLabel: reader.GetString(2),
                PrimaryType: ReadNullableString(reader, 3),
                FormattedAddress: ReadNullableString(reader, 4),
                PhoneNumber: ReadNullableString(reader, 5),
                WebsiteUri: ReadNullableString(reader, 6),
                GoogleMapsUri: ReadNullableString(reader, 7),
                BusinessStatus: ReadNullableString(reader, 8),
                Rating: ReadNullableDouble(reader, 9),
                UserRatingCount: ReadNullableInt32(reader, 10),
                Latitude: ReadNullableDouble(reader, 11),
                Longitude: ReadNullableDouble(reader, 12),
                HasWebsite: reader.GetBoolean(13),
                EmailExtractionSource: reader.GetString(14),
                EmailAddresses: DeserializeStringList(ReadNullableString(reader, 15)),
                ContactPhoneNumbers: DeserializeStringList(ReadNullableString(reader, 16)),
                ContactPageUris: DeserializeStringList(ReadNullableString(reader, 17))));
        }

        return items;
    }

    public async Task UpsertResultsAsync(
        string provider,
        string locationQuery,
        string businessType,
        string countryCode,
        IReadOnlyList<LeadSearchResultItem> items,
        CancellationToken cancellationToken = default)
    {
        if (items.Count == 0)
        {
            return;
        }

        await EnsureInitializedAsync(cancellationToken);

        var searchKey = BuildSearchKey(provider, locationQuery, businessType, countryCode);
        var normalizedLocation = NormalizeLocationKey(locationQuery);
        var normalizedBusinessType = LeadSearchCatalog.NormalizeBusinessType(businessType);
        var nowUtc = DateTimeOffset.UtcNow.ToString("O");

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);

        foreach (var item in items)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText =
                """
                INSERT INTO lead_search_items (
                  search_key,
                  provider,
                  location_query_key,
                  business_type,
                  place_id,
                  name,
                  business_label,
                  primary_type,
                  formatted_address,
                  phone_number,
                  website_uri,
                  google_maps_uri,
                  business_status,
                  rating,
                  user_rating_count,
                  latitude,
                  longitude,
                  has_website,
                  email_extraction_source,
                  email_addresses_json,
                  contact_phone_numbers_json,
                  contact_page_uris_json,
                  first_seen_utc,
                  last_seen_utc
                )
                VALUES (
                  $searchKey,
                  $provider,
                  $locationQueryKey,
                  $businessType,
                  $placeId,
                  $name,
                  $businessLabel,
                  $primaryType,
                  $formattedAddress,
                  $phoneNumber,
                  $websiteUri,
                  $googleMapsUri,
                  $businessStatus,
                  $rating,
                  $userRatingCount,
                  $latitude,
                  $longitude,
                  $hasWebsite,
                  $emailExtractionSource,
                  $emailAddressesJson,
                  $contactPhoneNumbersJson,
                  $contactPageUrisJson,
                  $firstSeenUtc,
                  $lastSeenUtc
                )
                ON CONFLICT(search_key, place_id) DO UPDATE SET
                  provider = excluded.provider,
                  location_query_key = excluded.location_query_key,
                  business_type = excluded.business_type,
                  name = excluded.name,
                  business_label = excluded.business_label,
                  primary_type = excluded.primary_type,
                  formatted_address = excluded.formatted_address,
                  phone_number = excluded.phone_number,
                  website_uri = excluded.website_uri,
                  google_maps_uri = excluded.google_maps_uri,
                  business_status = excluded.business_status,
                  rating = excluded.rating,
                  user_rating_count = excluded.user_rating_count,
                  latitude = excluded.latitude,
                  longitude = excluded.longitude,
                  has_website = excluded.has_website,
                  email_extraction_source = excluded.email_extraction_source,
                  email_addresses_json = excluded.email_addresses_json,
                  contact_phone_numbers_json = excluded.contact_phone_numbers_json,
                  contact_page_uris_json = excluded.contact_page_uris_json,
                  last_seen_utc = excluded.last_seen_utc;
                """;

            command.Parameters.AddWithValue("$searchKey", searchKey);
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$locationQueryKey", normalizedLocation);
            command.Parameters.AddWithValue("$businessType", normalizedBusinessType);
            command.Parameters.AddWithValue("$placeId", item.PlaceId);
            command.Parameters.AddWithValue("$name", item.Name);
            command.Parameters.AddWithValue("$businessLabel", item.BusinessLabel);
            command.Parameters.AddWithValue("$primaryType", ToDbValue(item.PrimaryType));
            command.Parameters.AddWithValue("$formattedAddress", ToDbValue(item.FormattedAddress));
            command.Parameters.AddWithValue("$phoneNumber", ToDbValue(item.PhoneNumber));
            command.Parameters.AddWithValue("$websiteUri", ToDbValue(item.WebsiteUri));
            command.Parameters.AddWithValue("$googleMapsUri", ToDbValue(item.GoogleMapsUri));
            command.Parameters.AddWithValue("$businessStatus", ToDbValue(item.BusinessStatus));
            command.Parameters.AddWithValue("$rating", ToDbValue(item.Rating));
            command.Parameters.AddWithValue("$userRatingCount", ToDbValue(item.UserRatingCount));
            command.Parameters.AddWithValue("$latitude", ToDbValue(item.Latitude));
            command.Parameters.AddWithValue("$longitude", ToDbValue(item.Longitude));
            command.Parameters.AddWithValue("$hasWebsite", item.HasWebsite);
            command.Parameters.AddWithValue("$emailExtractionSource", item.EmailExtractionSource);
            command.Parameters.AddWithValue("$emailAddressesJson", SerializeStringList(item.EmailAddresses));
            command.Parameters.AddWithValue("$contactPhoneNumbersJson", SerializeStringList(item.ContactPhoneNumbers));
            command.Parameters.AddWithValue("$contactPageUrisJson", SerializeStringList(item.ContactPageUris));
            command.Parameters.AddWithValue("$firstSeenUtc", nowUtc);
            command.Parameters.AddWithValue("$lastSeenUtc", nowUtc);

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
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
                CREATE TABLE IF NOT EXISTS lead_search_items (
                  search_key TEXT NOT NULL,
                  provider TEXT NOT NULL,
                  location_query_key TEXT NOT NULL,
                  business_type TEXT NOT NULL,
                  place_id TEXT NOT NULL,
                  name TEXT NOT NULL,
                  business_label TEXT NOT NULL,
                  primary_type TEXT NULL,
                  formatted_address TEXT NULL,
                  phone_number TEXT NULL,
                  website_uri TEXT NULL,
                  google_maps_uri TEXT NULL,
                  business_status TEXT NULL,
                  rating REAL NULL,
                  user_rating_count INTEGER NULL,
                  latitude REAL NULL,
                  longitude REAL NULL,
                  has_website INTEGER NOT NULL,
                  email_extraction_source TEXT NOT NULL,
                  email_addresses_json TEXT NOT NULL,
                  contact_phone_numbers_json TEXT NOT NULL,
                  contact_page_uris_json TEXT NOT NULL,
                  first_seen_utc TEXT NOT NULL,
                  last_seen_utc TEXT NOT NULL,
                  PRIMARY KEY (search_key, place_id)
                );

                CREATE INDEX IF NOT EXISTS idx_lead_search_items_lookup
                  ON lead_search_items (search_key, first_seen_utc);
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private static string BuildSearchKey(
        string provider,
        string locationQuery,
        string businessType,
        string countryCode)
    {
        return string.Join(
            "::",
            LeadSearchCatalog.NormalizeProvider(provider),
            CountryCatalog.NormalizeCode(countryCode),
            NormalizeLocationKey(locationQuery),
            LeadSearchCatalog.NormalizeBusinessType(businessType));
    }

    private static string NormalizeLocationKey(string? locationQuery)
    {
        return string.Join(
            ' ',
            (locationQuery ?? string.Empty)
                .Trim()
                .ToLowerInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static object ToDbValue(string? value)
        => string.IsNullOrWhiteSpace(value) ? DBNull.Value : value;

    private static object ToDbValue(double? value)
        => value is null ? DBNull.Value : value.Value;

    private static object ToDbValue(int? value)
        => value is null ? DBNull.Value : value.Value;

    private static string SerializeStringList(IReadOnlyList<string> values)
    {
        var normalized = values
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        return JsonSerializer.Serialize(normalized, JsonOptions);
    }

    private static IReadOnlyList<string> DeserializeStringList(string? rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson))
        {
            return Array.Empty<string>();
        }

        try
        {
            var values = JsonSerializer.Deserialize<List<string>>(rawJson, JsonOptions)
                ?.Where(static value => !string.IsNullOrWhiteSpace(value))
                .Select(static value => value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return values ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? ReadNullableString(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

    private static double? ReadNullableDouble(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetDouble(ordinal);

    private static int? ReadNullableInt32(SqliteDataReader reader, int ordinal)
        => reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
}
