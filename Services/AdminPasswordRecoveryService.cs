using System.Net;
using System.Security.Cryptography;
using System.Text;
using Backend.Models;
using MailKit.Security;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using MimeKit;
using MailKitSmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Backend.Services;

public sealed class AdminPasswordRecoveryService
{
    private static readonly TimeSpan TokenLifetime = TimeSpan.FromMinutes(20);
    private readonly SaasStoreService _saasStore;
    private readonly SaasOptions _options;

    public AdminPasswordRecoveryService(
        SaasStoreService saasStore,
        IOptions<SaasOptions> options)
    {
        _saasStore = saasStore;
        _options = options.Value;
    }

    public async Task RequestResetAsync(
        string? username,
        CancellationToken cancellationToken = default)
    {
        var normalizedUsername = (username ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(normalizedUsername))
        {
            return;
        }

        var user = await _saasStore.FindUserByUsernameAsync(normalizedUsername, cancellationToken);
        if (user is null || !user.IsActive ||
            !string.Equals(user.Role, AppRoles.Admin, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        var settings = await _saasStore.GetAdminRecoverySettingsAsync(cancellationToken);
        EnsureSmtpConfigured(settings);

        var rawToken = WebEncoders.Base64UrlEncode(RandomNumberGenerator.GetBytes(32));
        var tokenHash = HashToken(rawToken);
        var expiresUtc = DateTimeOffset.UtcNow.Add(TokenLifetime);
        await _saasStore.CreateAdminPasswordResetTokenAsync(
            user.Id,
            tokenHash,
            expiresUtc,
            cancellationToken);

        var frontendBaseUrl = (_options.FrontendBaseUrl ?? string.Empty).Trim().TrimEnd('/');
        if (!Uri.TryCreate(frontendBaseUrl, UriKind.Absolute, out var frontendUri) ||
            frontendUri.Scheme is not ("http" or "https"))
        {
            await _saasStore.RevokeAdminPasswordResetTokenAsync(tokenHash, cancellationToken);
            throw new InvalidOperationException("L adresse publique du frontend est invalide.");
        }

        var resetUrl = $"{frontendUri.GetLeftPart(UriPartial.Path).TrimEnd('/')}?adminResetToken={Uri.EscapeDataString(rawToken)}";
        try
        {
            await SendEmailAsync(
                settings,
                "Reinitialisation du mot de passe administrateur Lead Radar",
                BuildResetHtml(user.DisplayName, resetUrl),
                BuildResetText(user.DisplayName, resetUrl),
                cancellationToken);
        }
        catch
        {
            await _saasStore.RevokeAdminPasswordResetTokenAsync(tokenHash, CancellationToken.None);
            throw;
        }
    }

    public Task ResetPasswordAsync(
        string? token,
        string? newPassword,
        CancellationToken cancellationToken = default)
    {
        var normalizedToken = (token ?? string.Empty).Trim();
        if (normalizedToken.Length is < 32 or > 256)
        {
            throw new ArgumentException("Ce lien de recuperation est invalide.");
        }
        return _saasStore.ResetAdminPasswordWithTokenAsync(
            HashToken(normalizedToken),
            newPassword ?? string.Empty,
            cancellationToken);
    }

    public async Task SendTestEmailAsync(CancellationToken cancellationToken = default)
    {
        var settings = await _saasStore.GetAdminRecoverySettingsAsync(cancellationToken);
        EnsureSmtpConfigured(settings);
        await SendEmailAsync(
            settings,
            "Test de recuperation Lead Radar",
            "<div style=\"font-family:Arial,sans-serif;max-width:600px;margin:auto;padding:24px\"><h2>Configuration Gmail active</h2><p>Lead Radar peut envoyer les liens de recuperation du compte administrateur a cette adresse.</p></div>",
            "Configuration Gmail active. Lead Radar peut envoyer les liens de recuperation du compte administrateur a cette adresse.",
            cancellationToken);
    }

    private static async Task SendEmailAsync(
        AdminRecoverySettings settings,
        string subject,
        string htmlBody,
        string textBody,
        CancellationToken cancellationToken)
    {
        EnsureSmtpConfigured(settings);
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Lead Radar", settings.SmtpUsername));
        message.To.Add(MailboxAddress.Parse(settings.RecoveryEmail));
        message.Subject = subject;
        message.Body = new BodyBuilder { HtmlBody = htmlBody, TextBody = textBody }.ToMessageBody();

        using var client = new MailKitSmtpClient { Timeout = 30_000 };
        try
        {
            await client.ConnectAsync("smtp.gmail.com", 587, SecureSocketOptions.StartTls, cancellationToken);
            await client.AuthenticateAsync(
                settings.SmtpUsername,
                settings.SmtpAppPassword!,
                cancellationToken);
            await client.SendAsync(message, cancellationToken);
            await client.DisconnectAsync(true, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "Gmail a refuse l envoi. Verifie l adresse Gmail et le mot de passe d application dans le back-office.",
                ex);
        }
    }

    private static void EnsureSmtpConfigured(AdminRecoverySettings settings)
    {
        if (string.IsNullOrWhiteSpace(settings.SmtpAppPassword))
        {
            throw new InvalidOperationException(
                "La recuperation est inactive. Ajoute le mot de passe d application Gmail dans le back-office.");
        }
    }

    private static string HashToken(string token)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static string BuildResetHtml(string displayName, string resetUrl)
    {
        var safeName = WebUtility.HtmlEncode(displayName);
        var safeUrl = WebUtility.HtmlEncode(resetUrl);
        return $$"""
            <div style="font-family:Arial,sans-serif;max-width:620px;margin:auto;padding:28px;color:#172033">
              <h2>Reinitialisation administrateur</h2>
              <p>Bonjour {{safeName}},</p>
              <p>Une demande de nouveau mot de passe a ete recue pour le compte administrateur Lead Radar.</p>
              <p style="margin:28px 0"><a href="{{safeUrl}}" style="padding:13px 20px;border-radius:10px;color:#fff;background:#2563eb;text-decoration:none;font-weight:700">Choisir un nouveau mot de passe</a></p>
              <p>Ce lien expire dans 20 minutes et ne peut etre utilise qu une seule fois.</p>
              <p>Si tu n es pas a l origine de cette demande, ignore cet email.</p>
            </div>
            """;
    }

    private static string BuildResetText(string displayName, string resetUrl)
        => $"Bonjour {displayName},\n\nUtilise ce lien pour choisir un nouveau mot de passe administrateur Lead Radar :\n{resetUrl}\n\nLe lien expire dans 20 minutes et ne peut etre utilise qu une seule fois.";
}
