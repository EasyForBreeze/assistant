using System;
using System.Net.Mail;
using System.Text;
using Microsoft.Extensions.Logging;

namespace Assistant.Services;

public interface IAccessRequestEmailSender
{
    Task SendAsync(string login, CancellationToken cancellationToken = default);
}

public sealed class AccessRequestEmailSender : IAccessRequestEmailSender
{
    private readonly EmailClientFactory _emailFactory;
    private readonly ILogger<AccessRequestEmailSender> _logger;

    public AccessRequestEmailSender(EmailClientFactory emailFactory, ILogger<AccessRequestEmailSender> logger)
    {
        _emailFactory = emailFactory;
        _logger = logger;
    }

    public async Task SendAsync(string login, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(login);

        var trimmedLogin = login.Trim();
        var supportRecipient = _emailFactory.GetSupportRecipient();

        using var message = new MailMessage
        {
            From = _emailFactory.CreateSenderAddress(),
            Subject = "Заявка на доступ к Assistant",
            Body = $"Прошу предоставить доступ в Assistant.{Environment.NewLine}Логин: {trimmedLogin}",
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8
        };

        message.To.Add(new MailAddress(supportRecipient));

        using var client = _emailFactory.CreateClient();

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation("Sent access request email for {Login}.", trimmedLogin);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            _logger.LogError(ex, "Failed to send access request email for {Login}.", trimmedLogin);
            throw;
        }
    }
}
