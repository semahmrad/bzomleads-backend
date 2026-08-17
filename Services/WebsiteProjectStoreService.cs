using System.Text.Json;
using Backend.Models;

namespace Backend.Services;

public sealed class WebsiteProjectStoreService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _projectsRootDirectory;

    public WebsiteProjectStoreService(IHostEnvironment environment)
    {
        _projectsRootDirectory = Path.Combine(
            environment.ContentRootPath,
            "App_Data",
            "generated-websites");

        Directory.CreateDirectory(_projectsRootDirectory);
    }

    public string GetProjectDirectory(string projectId)
    {
        if (!Guid.TryParseExact(projectId, "N", out _))
        {
            throw new ArgumentException("The website project id is invalid.", nameof(projectId));
        }

        return Path.Combine(_projectsRootDirectory, projectId);
    }

    public string GetSiteDirectory(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), "site");

    public string GetArchivePath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), "website.zip");

    public string GetManifestPath(string projectId)
        => Path.Combine(GetProjectDirectory(projectId), "manifest.json");

    public bool ArchiveExists(string projectId)
        => File.Exists(GetArchivePath(projectId));

    public async Task SaveNewProjectAsync(
        WebsiteProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        byte[] archiveContent,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = GetProjectDirectory(manifest.ProjectId);
        var siteDirectory = GetSiteDirectory(manifest.ProjectId);

        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(siteDirectory);

        await ReplaceGeneratedFilesAsync(siteDirectory, files, preserveGitDirectory: false, cancellationToken);
        await File.WriteAllBytesAsync(GetArchivePath(manifest.ProjectId), archiveContent, cancellationToken);
        await SaveManifestAsync(manifest, cancellationToken);
    }

    public async Task UpdateProjectAsync(
        WebsiteProjectManifest manifest,
        IReadOnlyDictionary<string, byte[]> files,
        byte[] archiveContent,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = GetProjectDirectory(manifest.ProjectId);
        var siteDirectory = GetSiteDirectory(manifest.ProjectId);

        Directory.CreateDirectory(projectDirectory);
        Directory.CreateDirectory(siteDirectory);

        await ReplaceGeneratedFilesAsync(siteDirectory, files, preserveGitDirectory: true, cancellationToken);
        await File.WriteAllBytesAsync(GetArchivePath(manifest.ProjectId), archiveContent, cancellationToken);
        await SaveManifestAsync(manifest, cancellationToken);
    }

    public async Task SaveManifestAsync(
        WebsiteProjectManifest manifest,
        CancellationToken cancellationToken = default)
    {
        var projectDirectory = GetProjectDirectory(manifest.ProjectId);
        Directory.CreateDirectory(projectDirectory);

        var rawJson = JsonSerializer.Serialize(manifest, JsonOptions);
        await File.WriteAllTextAsync(GetManifestPath(manifest.ProjectId), rawJson, cancellationToken);
    }

    public async Task<WebsiteProjectManifest?> GetManifestAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var manifestPath = GetManifestPath(projectId);
        if (!File.Exists(manifestPath))
        {
            return null;
        }

        var rawJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
        return JsonSerializer.Deserialize<WebsiteProjectManifest>(rawJson, JsonOptions);
    }

    public async Task<IReadOnlyList<WebsiteProjectManifest>> FindByBusinessKeyAsync(
        string businessKey,
        CancellationToken cancellationToken = default)
    {
        var results = new List<WebsiteProjectManifest>();

        foreach (var directory in Directory.EnumerateDirectories(_projectsRootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var rawJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<WebsiteProjectManifest>(rawJson, JsonOptions);
                if (manifest is null)
                {
                    continue;
                }

                if (string.Equals(manifest.BusinessKey, businessKey, StringComparison.OrdinalIgnoreCase))
                {
                    results.Add(manifest);
                }
            }
            catch
            {
                // Ignore malformed project manifests and continue scanning the rest.
            }
        }

        return results
            .OrderByDescending(static manifest => manifest.UpdatedUtc)
            .ToList();
    }

    public async Task<IReadOnlyList<WebsiteProjectManifest>> GetAllManifestsAsync(
        CancellationToken cancellationToken = default)
    {
        var results = new List<WebsiteProjectManifest>();

        foreach (var directory in Directory.EnumerateDirectories(_projectsRootDirectory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var manifestPath = Path.Combine(directory, "manifest.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }

            try
            {
                var rawJson = await File.ReadAllTextAsync(manifestPath, cancellationToken);
                var manifest = JsonSerializer.Deserialize<WebsiteProjectManifest>(rawJson, JsonOptions);
                if (manifest is not null)
                {
                    results.Add(manifest);
                }
            }
            catch
            {
                // Ignore malformed legacy manifests.
            }
        }

        return results.OrderByDescending(static manifest => manifest.UpdatedUtc).ToList();
    }

    public async Task<byte[]?> LoadArchiveAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var archivePath = GetArchivePath(projectId);
        if (!File.Exists(archivePath))
        {
            return null;
        }

        return await File.ReadAllBytesAsync(archivePath, cancellationToken);
    }

    private static async Task ReplaceGeneratedFilesAsync(
        string siteDirectory,
        IReadOnlyDictionary<string, byte[]> files,
        bool preserveGitDirectory,
        CancellationToken cancellationToken)
    {
        if (Directory.Exists(siteDirectory))
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(siteDirectory))
            {
                if (preserveGitDirectory &&
                    string.Equals(Path.GetFileName(entry), ".git", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                DeleteFileSystemEntry(entry);
            }
        }
        else
        {
            Directory.CreateDirectory(siteDirectory);
        }

        foreach (var file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var destinationPath = Path.Combine(siteDirectory, file.Key.Replace('/', Path.DirectorySeparatorChar));
            var destinationDirectory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(destinationDirectory))
            {
                Directory.CreateDirectory(destinationDirectory);
            }

            await File.WriteAllBytesAsync(destinationPath, file.Value, cancellationToken);
        }
    }

    private static void DeleteFileSystemEntry(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
            return;
        }

        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
