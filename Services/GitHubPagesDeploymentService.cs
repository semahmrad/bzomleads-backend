using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.Json;
using Backend.Models;

namespace Backend.Services;

public sealed class GitHubPagesDeploymentService
{
    private readonly HttpClient _httpClient;
    private readonly SemaphoreSlim _ownerLock = new(1, 1);
    private string? _cachedOwner;

    public GitHubPagesDeploymentService(HttpClient httpClient)
    {
        _httpClient = httpClient;
        _httpClient.Timeout = TimeSpan.FromSeconds(20);
        _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LeadRadar-AI/1.0");
    }

    public async Task<GitHubRepositoryPlan> PlanRepositoryAsync(
        string businessSlug,
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var owner = await GetOwnerAsync(cancellationToken);
        var repositoryName = BuildRepositoryName(businessSlug, projectId);
        return new GitHubRepositoryPlan(
            owner,
            repositoryName,
            $"https://github.com/{owner}/{repositoryName}",
            $"https://{owner}.github.io/{repositoryName}/");
    }

    public async Task<GitHubDeploymentResult> CreateProjectAsync(
        WebsiteProjectManifest manifest,
        GitHubRepositoryPlan repositoryPlan,
        string siteDirectory,
        CancellationToken cancellationToken = default)
    {
        var repositorySlug = $"{repositoryPlan.Owner}/{repositoryPlan.RepositoryName}";

        await RunProcessAsync(
            "git",
            ["init", "-b", "main"],
            siteDirectory,
            cancellationToken);

        await GitAddCommitAsync(
            siteDirectory,
            commitMessage: $"Initial AI website for {manifest.BusinessName}",
            allowEmpty: false,
            cancellationToken);

        await RunProcessAsync(
            "gh",
            [
                "repo",
                "create",
                repositorySlug,
                "--public",
                "--source",
                ".",
                "--remote",
                "origin",
                "--push",
                "--disable-issues",
                "--disable-wiki",
                "--description",
                $"AI-generated static website for {manifest.BusinessName}"
            ],
            siteDirectory,
            cancellationToken);

        await EnsurePagesEnabledAsync(repositoryPlan.Owner, repositoryPlan.RepositoryName, cancellationToken);
        var productionUrl = await ResolveProductionUrlAsync(
            repositoryPlan.Owner,
            repositoryPlan.RepositoryName,
            repositoryPlan.ProductionUrl,
            cancellationToken);

        return new GitHubDeploymentResult(
            repositoryPlan.Owner,
            repositoryPlan.RepositoryName,
            repositoryPlan.RepositoryUrl,
            productionUrl);
    }

    public async Task<GitHubDeploymentResult> UpdateProjectAsync(
        WebsiteProjectManifest manifest,
        string siteDirectory,
        string? commitMessage,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(manifest.RepositoryOwner) ||
            string.IsNullOrWhiteSpace(manifest.RepositoryName) ||
            string.IsNullOrWhiteSpace(manifest.RepositoryUrl) ||
            string.IsNullOrWhiteSpace(manifest.ProductionUrl))
        {
            throw new InvalidOperationException("This project is not linked to a GitHub repository yet.");
        }

        var hasPendingChanges = await HasPendingChangesAsync(siteDirectory, cancellationToken);
        if (hasPendingChanges)
        {
            await GitAddCommitAsync(
                siteDirectory,
                commitMessage: commitMessage ?? $"AI edit for {manifest.BusinessName}",
                allowEmpty: false,
                cancellationToken);

            await RunProcessAsync(
                "git",
                ["push", "origin", "main"],
                siteDirectory,
                cancellationToken);
        }

        await EnsurePagesEnabledAsync(manifest.RepositoryOwner, manifest.RepositoryName, cancellationToken);
        var productionUrl = await ResolveProductionUrlAsync(
            manifest.RepositoryOwner,
            manifest.RepositoryName,
            manifest.ProductionUrl,
            cancellationToken);

        return new GitHubDeploymentResult(
            manifest.RepositoryOwner,
            manifest.RepositoryName,
            manifest.RepositoryUrl,
            productionUrl);
    }

    private async Task<string> GetOwnerAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_cachedOwner))
        {
            return _cachedOwner;
        }

        await _ownerLock.WaitAsync(cancellationToken);
        try
        {
            if (!string.IsNullOrWhiteSpace(_cachedOwner))
            {
                return _cachedOwner;
            }

            await RunProcessAsync("gh", ["auth", "status"], Environment.CurrentDirectory, cancellationToken);
            var result = await RunProcessAsync(
                "gh",
                ["api", "user", "--jq", ".login"],
                Environment.CurrentDirectory,
                cancellationToken);

            var owner = result.StandardOutput.Trim();
            if (string.IsNullOrWhiteSpace(owner))
            {
                throw new InvalidOperationException("Unable to resolve the authenticated GitHub owner.");
            }

            _cachedOwner = owner;
            return owner;
        }
        finally
        {
            _ownerLock.Release();
        }
    }

    private async Task EnsurePagesEnabledAsync(
        string owner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAllowFailureAsync(
            "gh",
            [
                "api",
                "repos/" + owner + "/" + repositoryName + "/pages",
                "-X",
                "POST",
                "-f",
                "source[branch]=main",
                "-f",
                "source[path]=/"
            ],
            Environment.CurrentDirectory,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return;
        }

        var output = (result.StandardOutput + "\n" + result.StandardError).ToLowerInvariant();
        if (output.Contains("already exists", StringComparison.Ordinal) ||
            output.Contains("unprocessable", StringComparison.Ordinal))
        {
            return;
        }

        throw new InvalidOperationException(
            $"GitHub Pages could not be enabled: {result.StandardError.Trim()}");
    }

    private async Task<string> ResolveProductionUrlAsync(
        string owner,
        string repositoryName,
        string? preferredUrl,
        CancellationToken cancellationToken)
    {
        var fallbackUrl = $"https://{owner}.github.io/{repositoryName}/";
        var productionUrl = !string.IsNullOrWhiteSpace(preferredUrl)
            ? preferredUrl.Trim()
            : fallbackUrl;

        var timeoutAt = DateTimeOffset.UtcNow.AddMinutes(5);
        while (DateTimeOffset.UtcNow < timeoutAt)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pagesDetails = await GetPagesDetailsAsync(owner, repositoryName, cancellationToken);
            if (!string.IsNullOrWhiteSpace(pagesDetails?.HtmlUrl))
            {
                productionUrl = pagesDetails.HtmlUrl.Trim();
            }

            if (await IsProductionUrlReadyAsync(productionUrl, cancellationToken))
            {
                return productionUrl;
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        }

        var finalPagesDetails = await GetPagesDetailsAsync(owner, repositoryName, cancellationToken);
        if (!string.IsNullOrWhiteSpace(finalPagesDetails?.HtmlUrl))
        {
            productionUrl = finalPagesDetails.HtmlUrl.Trim();
        }

        throw new InvalidOperationException(
            $"GitHub Pages is still provisioning for {productionUrl}. The repository was created correctly, but the live site is not ready yet. Retry in about one minute.");
    }

    private async Task<GitHubPagesDetails?> GetPagesDetailsAsync(
        string owner,
        string repositoryName,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAllowFailureAsync(
            "gh",
            [
                "api",
                "repos/" + owner + "/" + repositoryName + "/pages"
            ],
            Environment.CurrentDirectory,
            cancellationToken);

        if (result.ExitCode != 0 || string.IsNullOrWhiteSpace(result.StandardOutput))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;

            var htmlUrl = root.TryGetProperty("html_url", out var htmlUrlElement)
                ? htmlUrlElement.GetString()
                : null;

            var status = root.TryGetProperty("status", out var statusElement)
                ? statusElement.GetString()
                : null;

            return new GitHubPagesDetails(htmlUrl, status);
        }
        catch
        {
            return null;
        }
    }

    private async Task<bool> IsProductionUrlReadyAsync(
        string productionUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, productionUrl);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseContentRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return false;
            }

            var mediaType = response.Content.Headers.ContentType?.MediaType;
            if (!string.IsNullOrWhiteSpace(mediaType) &&
                mediaType.Contains("html", StringComparison.OrdinalIgnoreCase))
            {
                var markup = await response.Content.ReadAsStringAsync(cancellationToken);
                if (markup.Contains("There isn't a GitHub Pages site here.", StringComparison.OrdinalIgnoreCase) ||
                    markup.Contains("<title>404", StringComparison.OrdinalIgnoreCase))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private async Task<bool> HasPendingChangesAsync(
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAsync(
            "git",
            ["status", "--porcelain"],
            workingDirectory,
            cancellationToken);

        return !string.IsNullOrWhiteSpace(result.StandardOutput);
    }

    private async Task GitAddCommitAsync(
        string workingDirectory,
        string commitMessage,
        bool allowEmpty,
        CancellationToken cancellationToken)
    {
        await RunProcessAsync(
            "git",
            ["add", "--all"],
            workingDirectory,
            cancellationToken);

        var commitArguments = new List<string>
        {
            "-c", "user.name=Lead Radar AI",
            "-c", "user.email=lead-radar-ai@users.noreply.github.com",
            "commit",
            "-m",
            commitMessage
        };

        if (allowEmpty)
        {
            commitArguments.Add("--allow-empty");
        }

        await RunProcessAsync(
            "git",
            commitArguments,
            workingDirectory,
            cancellationToken);
    }

    private static string BuildRepositoryName(string businessSlug, string projectId)
    {
        var suffix = projectId.Length >= 8 ? projectId[..8] : projectId;
        return $"lead-radar-{businessSlug}-{suffix}".ToLowerInvariant();
    }

    private static async Task<ProcessResult> RunProcessAllowFailureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        var standardOutput = new StringBuilder();
        var standardError = new StringBuilder();

        process.OutputDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardOutput.AppendLine(eventArgs.Data);
            }
        };

        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (eventArgs.Data is not null)
            {
                standardError.AppendLine(eventArgs.Data);
            }
        };

        if (!process.Start())
        {
            throw new InvalidOperationException($"Unable to start process {fileName}.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        await process.WaitForExitAsync(cancellationToken);

        return new ProcessResult(
            process.ExitCode,
            standardOutput.ToString(),
            standardError.ToString());
    }

    private static async Task<ProcessResult> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        CancellationToken cancellationToken)
    {
        var result = await RunProcessAllowFailureAsync(
            fileName,
            arguments,
            workingDirectory,
            cancellationToken);

        if (result.ExitCode == 0)
        {
            return result;
        }

        throw new InvalidOperationException(
            $"{fileName} {string.Join(' ', arguments)} failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
    }

    public sealed record GitHubDeploymentResult(
        string Owner,
        string RepositoryName,
        string RepositoryUrl,
        string ProductionUrl);

    public sealed record GitHubRepositoryPlan(
        string Owner,
        string RepositoryName,
        string RepositoryUrl,
        string ProductionUrl);

    private sealed record GitHubPagesDetails(
        string? HtmlUrl,
        string? Status);

    private sealed record ProcessResult(
        int ExitCode,
        string StandardOutput,
        string StandardError);
}
