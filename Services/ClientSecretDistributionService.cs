using System;
using System.IO;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.Extensions.Logging;

namespace Assistant.Services;

public sealed record ClientSecretArchiveResult(byte[] Content, string FileName, string ContentType);

public sealed class ClientSecretDistributionService
{
    private static readonly char[] UpperChars = "ABCDEFGHJKLMNPQRSTUVWXYZ".ToCharArray();
    private static readonly char[] LowerChars = "abcdefghijkmnopqrstuvwxyz".ToCharArray();
    private static readonly char[] DigitChars = "23456789".ToCharArray();
    private static readonly char[] SymbolChars = "!@$?*-_#".ToCharArray();
    private static readonly char[] PasswordChars =
        "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789!@$?*-_#".ToCharArray();

    private readonly EmailClientFactory _emailFactory;
    private readonly ILogger<ClientSecretDistributionService> _logger;

    public ClientSecretDistributionService(EmailClientFactory emailFactory, ILogger<ClientSecretDistributionService> logger)
    {
        _emailFactory = emailFactory;
        _logger = logger;
    }

    public async Task<ClientSecretArchiveResult> CreateAsync(
        string clientId,
        string secret,
        string recipientEmail,
        string requestNumber,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(clientId);
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(recipientEmail);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestNumber);

        var safeClientId = GetSafeFileName(clientId.Trim());
        var password = GeneratePassword();
        var archiveBytes = CreateArchive(safeClientId, secret.Trim(), password);

        await SendEmailAsync(safeClientId, recipientEmail.Trim(), requestNumber.Trim(), password, cancellationToken);

        var archiveFileName = safeClientId + ".zip";
        return new ClientSecretArchiveResult(archiveBytes, archiveFileName, "application/zip");
    }

    private async Task SendEmailAsync(
        string clientId,
        string recipientEmail,
        string requestNumber,
        string password,
        CancellationToken cancellationToken)
    {
        using var message = new MailMessage
        {
            From = _emailFactory.CreateSenderAddress(),
            Subject = $"Пароль от архива по заявке - {requestNumber}",
            Body = $"Добрый день пароль от архива - {password}",
            BodyEncoding = Encoding.UTF8,
            SubjectEncoding = Encoding.UTF8
        };

        message.To.Add(new MailAddress(recipientEmail));

        using var client = _emailFactory.CreateClient();

        try
        {
            await client.SendMailAsync(message, cancellationToken);
            _logger.LogInformation(
                "Sent archive password for client {ClientId} to {Recipient} (request {RequestNumber}).",
                clientId,
                recipientEmail,
                requestNumber);
        }
        catch (Exception ex) when (ex is SmtpException or InvalidOperationException or FormatException)
        {
            _logger.LogError(
                ex,
                "Failed to send archive password for client {ClientId} to {Recipient} (request {RequestNumber}).",
                clientId,
                recipientEmail,
                requestNumber);
            throw;
        }
    }

    private static byte[] CreateArchive(string safeClientId, string secret, string password)
    {
        using var buffer = new MemoryStream();
        using (var zip = new ZipOutputStream(buffer))
        {
            zip.SetLevel(9);
            zip.Password = password;
            zip.IsStreamOwner = false;

            var entry = new ZipEntry(safeClientId + ".txt")
            {
                AESKeySize = 256,
                DateTime = DateTime.UtcNow
            };

            zip.PutNextEntry(entry);
            var content = Encoding.UTF8.GetBytes(secret + Environment.NewLine);
            zip.Write(content, 0, content.Length);
            zip.CloseEntry();
            zip.Finish();
        }

        return buffer.ToArray();
    }

    private static string GetSafeFileName(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "client";
        }

        var builder = new StringBuilder(value.Length);
        var invalid = Path.GetInvalidFileNameChars();
        foreach (var ch in value)
        {
            builder.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);
        }

        return builder.Length == 0 ? "client" : builder.ToString();
    }

    private static string GeneratePassword(int length = 16)
    {
        length = Math.Max(length, 12);

        Span<char> chars = stackalloc char[length];
        Span<byte> buffer = stackalloc byte[length];
        RandomNumberGenerator.Fill(buffer);

        for (var i = 0; i < length; i++)
        {
            chars[i] = PasswordChars[buffer[i] % PasswordChars.Length];
        }

        chars[0] = UpperChars[buffer[0] % UpperChars.Length];
        chars[1] = LowerChars[buffer[1] % LowerChars.Length];
        chars[2] = DigitChars[buffer[2] % DigitChars.Length];
        chars[3] = SymbolChars[buffer[3] % SymbolChars.Length];

        for (var i = length - 1; i > 0; i--)
        {
            var j = buffer[i] % (i + 1);
            (chars[i], chars[j]) = (chars[j], chars[i]);
        }

        return new string(chars);
    }
}
