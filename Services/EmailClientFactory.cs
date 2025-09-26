using System;
using System.Net;
using System.Net.Mail;
using Microsoft.Extensions.Options;

namespace Assistant.Services;

public sealed class EmailClientFactory
{
    private readonly EmailOptions _options;

    public EmailClientFactory(IOptions<EmailOptions> options)
    {
        _options = options.Value;
    }

    public MailAddress CreateSenderAddress()
    {
        var from = string.IsNullOrWhiteSpace(_options.From) ? _options.Username : _options.From;
        if (string.IsNullOrWhiteSpace(from))
        {
            throw new InvalidOperationException("Sender email is not configured.");
        }

        return new MailAddress(from);
    }

    public SmtpClient CreateClient()
    {
        if (string.IsNullOrWhiteSpace(_options.Host))
        {
            throw new InvalidOperationException("SMTP host is not configured.");
        }

        var client = new SmtpClient(_options.Host, _options.Port)
        {
            EnableSsl = _options.EnableSsl
        };

        if (!string.IsNullOrWhiteSpace(_options.Username))
        {
            client.Credentials = new NetworkCredential(_options.Username, _options.Password);
        }

        return client;
    }

    public string GetSupportRecipient()
    {
        if (string.IsNullOrWhiteSpace(_options.SupportRecipient))
        {
            throw new InvalidOperationException("Support recipient email is not configured.");
        }

        return _options.SupportRecipient;
    }
}
