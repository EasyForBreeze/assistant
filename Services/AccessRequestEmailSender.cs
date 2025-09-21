using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Services;

public interface IAccessRequestEmailSender
{
    Task SendAsync(string fullName, string email, CancellationToken cancellationToken = default);
}

public sealed class AccessRequestEmailSender : IAccessRequestEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<AccessRequestEmailSender> _logger;

    public AccessRequestEmailSender(IOptions<EmailOptions> options, ILogger<AccessRequestEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string fullName, string email, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fullName);
        ArgumentException.ThrowIfNullOrWhiteSpace(email);

        var host = _options.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        var recipient = _options.SupportRecipient;
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new InvalidOperationException("Support recipient email is not configured.");
        }

        var fromAddress = _options.From;
        if (string.IsNullOrWhiteSpace(fromAddress))
        {
            fromAddress = _options.Username;
            if (string.IsNullOrWhiteSpace(fromAddress))
            {
                throw new InvalidOperationException("Sender email is not configured.");
            }
        }

        var trimmedFullName = fullName.Trim();
        var trimmedEmail = email.Trim();

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress),
            Subject = "Заявка на доступ к Assistant",
            Body = $"Прошу дать доступ к Assistant{Environment.NewLine}{trimmedFullName}.{Environment.NewLine}{Environment.NewLine}Email: {trimmedEmail}",
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(new MailAddress(recipient));

        using var client = new SmtpClient(host, _options.Port)
        {
            EnableSsl = _options.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Sent access request email for {FullName} ({Email}).", trimmedFullName, trimmedEmail);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            _logger.LogError(ex, "Failed to send access request email for {FullName} ({Email}).", trimmedFullName, trimmedEmail);
            throw;
        }
    }
}
