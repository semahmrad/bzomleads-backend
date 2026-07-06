using System.Net;
using System.Text.RegularExpressions;
using Backend.Models;
using MailKit.Security;
using MimeKit;
using MailAddress = System.Net.Mail.MailAddress;
using MailKitSmtpClient = MailKit.Net.Smtp.SmtpClient;

namespace Backend.Services;

public sealed class SmtpCampaignService
{
    private static readonly Regex HtmlTagRegex = new("<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex HtmlBlockBreakRegex = new(
        "<\\s*/?(p|div|li|tr|section|article|h1|h2|h3|h4|h5|h6|br)\\b[^>]*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public async Task<EmailCampaignSendResponse> SendAsync(
        EmailCampaignSendRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        ValidateRequest(request);

        var recipients = NormalizeRecipientsPreservingOrder(request.Recipients);

        if (recipients.Count == 0)
        {
            throw new ArgumentException("Aucun destinataire valide n a ete fourni.");
        }

        using var client = new MailKitSmtpClient();
        client.Timeout = 30_000;

        var secureMode = ResolveSecureMode(request.Smtp.SecureMode);

        await client.ConnectAsync(
            request.Smtp.Host.Trim(),
            request.Smtp.Port,
            secureMode,
            cancellationToken);

        if (!string.IsNullOrWhiteSpace(request.Smtp.Username))
        {
            await client.AuthenticateAsync(
                request.Smtp.Username.Trim(),
                request.Smtp.Password ?? string.Empty,
                cancellationToken);
        }

        var failures = new List<EmailCampaignSendFailure>();
        var sentCount = 0;

        foreach (var recipient in recipients)
        {
            try
            {
                var subject = ApplySubjectPlaceholders(request.Subject.Trim(), recipient);
                var htmlBody = ApplyHtmlPlaceholders(request.HtmlBody, recipient);
                var textBody = BuildTextBody(htmlBody);

                var message = new MimeMessage();
                message.From.Add(new MailboxAddress(
                    request.Smtp.FromName?.Trim() ?? string.Empty,
                    request.Smtp.FromEmail.Trim()));
                message.To.Add(MailboxAddress.Parse(recipient.EmailAddress));
                message.Subject = subject;

                var bodyBuilder = new BodyBuilder
                {
                    HtmlBody = htmlBody,
                    TextBody = textBody
                };

                message.Body = bodyBuilder.ToMessageBody();

                await client.SendAsync(message, cancellationToken);
                sentCount++;
            }
            catch (Exception ex)
            {
                failures.Add(new EmailCampaignSendFailure(
                    recipient.EmailAddress,
                    recipient.BusinessName,
                    ex.Message));
            }
        }

        if (client.IsConnected)
        {
            await client.DisconnectAsync(true, cancellationToken);
        }

        return new EmailCampaignSendResponse(
            RequestedCount: recipients.Count,
            SentCount: sentCount,
            FailedCount: failures.Count,
            Failures: failures);
    }

    private static void ValidateRequest(EmailCampaignSendRequest request)
    {
        if (request.Smtp is null)
        {
            throw new ArgumentException("La configuration SMTP est obligatoire.");
        }

        if (string.IsNullOrWhiteSpace(request.Smtp.Host))
        {
            throw new ArgumentException("Le serveur SMTP est obligatoire.");
        }

        if (request.Smtp.Port is <= 0 or > 65535)
        {
            throw new ArgumentException("Le port SMTP doit etre compris entre 1 et 65535.");
        }

        if (string.IsNullOrWhiteSpace(request.Smtp.FromEmail))
        {
            throw new ArgumentException("L email expediteur est obligatoire.");
        }

        if (!MailAddress.TryCreate(request.Smtp.FromEmail.Trim(), out _))
        {
            throw new ArgumentException("L email expediteur est invalide.");
        }

        if (!string.IsNullOrWhiteSpace(request.Smtp.Username) &&
            string.IsNullOrWhiteSpace(request.Smtp.Password))
        {
            throw new ArgumentException("Le mot de passe SMTP est obligatoire quand un identifiant est renseigne.");
        }

        if (string.IsNullOrWhiteSpace(request.Subject))
        {
            throw new ArgumentException("L objet de l email est obligatoire.");
        }

        if (string.IsNullOrWhiteSpace(BuildTextBody(request.HtmlBody)))
        {
            throw new ArgumentException("Le contenu de l email est vide.");
        }

        if (request.Recipients is null || request.Recipients.Count == 0)
        {
            throw new ArgumentException("Ajoute au moins un destinataire a la campagne email.");
        }

        foreach (var recipient in request.Recipients)
        {
            if (string.IsNullOrWhiteSpace(recipient.EmailAddress))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(recipient.BusinessName))
            {
                throw new ArgumentException($"Nom de societe manquant pour: {recipient.EmailAddress}");
            }

            if (!MailAddress.TryCreate(recipient.EmailAddress.Trim(), out _))
            {
                throw new ArgumentException($"Email destinataire invalide: {recipient.EmailAddress}");
            }
        }
    }

    private static IReadOnlyList<EmailCampaignRecipient> NormalizeRecipientsPreservingOrder(
        IReadOnlyList<EmailCampaignRecipient> recipients)
    {
        var normalizedRecipients = new List<EmailCampaignRecipient>(recipients.Count);
        var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var recipient in recipients)
        {
            var emailAddress = recipient.EmailAddress?.Trim();
            if (string.IsNullOrWhiteSpace(emailAddress))
            {
                continue;
            }

            if (!seenEmails.Add(emailAddress))
            {
                continue;
            }

            normalizedRecipients.Add(recipient with
            {
                BusinessName = recipient.BusinessName.Trim(),
                EmailAddress = emailAddress,
                WebsiteUri = string.IsNullOrWhiteSpace(recipient.WebsiteUri)
                    ? null
                    : recipient.WebsiteUri.Trim()
            });
        }

        return normalizedRecipients;
    }

    private static SecureSocketOptions ResolveSecureMode(string? secureMode) =>
        (secureMode ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "ssl_tls" => SecureSocketOptions.SslOnConnect,
            "none" => SecureSocketOptions.None,
            "auto" => SecureSocketOptions.Auto,
            _ => SecureSocketOptions.StartTls
        };

    private static string ApplySubjectPlaceholders(string value, EmailCampaignRecipient recipient) =>
        ApplyPlaceholders(
            value,
            recipient.BusinessName ?? string.Empty,
            recipient.EmailAddress,
            recipient.WebsiteUri ?? string.Empty);

    private static string ApplyHtmlPlaceholders(string value, EmailCampaignRecipient recipient)
    {
        var businessName = WebUtility.HtmlEncode(recipient.BusinessName ?? string.Empty);
        var emailAddress = WebUtility.HtmlEncode(recipient.EmailAddress);
        var websiteUri = WebUtility.HtmlEncode(recipient.WebsiteUri ?? string.Empty);

        return ApplyPlaceholders(value, businessName, emailAddress, websiteUri);
    }

    private static string ApplyPlaceholders(
        string value,
        string businessName,
        string emailAddress,
        string websiteUri) =>
        value
            .Replace("{{recipientName}}", businessName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{companyName}}", businessName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{businessName}}", businessName, StringComparison.OrdinalIgnoreCase)
            .Replace("{{email}}", emailAddress, StringComparison.OrdinalIgnoreCase)
            .Replace("{{website}}", websiteUri, StringComparison.OrdinalIgnoreCase);

    private static string BuildTextBody(string? htmlBody)
    {
        if (string.IsNullOrWhiteSpace(htmlBody))
        {
            return string.Empty;
        }

        var withLineBreaks = HtmlBlockBreakRegex.Replace(htmlBody, "\n");
        var withoutTags = HtmlTagRegex.Replace(withLineBreaks, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags)
            .ReplaceLineEndings("\n");

        return string.Join(
            "\n",
            decoded
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(line => Regex.Replace(line, "\\s+", " ").Trim())
                .Where(line => line.Length > 0));
    }
}
