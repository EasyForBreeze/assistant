using System.Net;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Assistant.Services;

public interface ISimpleEmailSender
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default);
}

public sealed class SimpleEmailSender : ISimpleEmailSender
{
    private readonly EmailOptions _options;
    private readonly ILogger<SimpleEmailSender> _logger;

    public SimpleEmailSender(IOptions<EmailOptions> options, ILogger<SimpleEmailSender> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(recipient))
        {
            throw new ArgumentException("Recipient email is required.", nameof(recipient));
        }

        var host = _options.Host;
        if (string.IsNullOrWhiteSpace(host))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
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

        using var message = new MailMessage
        {
            From = new MailAddress(fromAddress),
            Subject = subject ?? string.Empty,
            Body = body ?? string.Empty,
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(new MailAddress(recipient.Trim()));

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
            _logger.LogInformation("Sent email to {Recipient} with subject {Subject}.", recipient, subject);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            _logger.LogError(ex, "Failed to send email to {Recipient}.", recipient);
            throw;
        }
    }
}
