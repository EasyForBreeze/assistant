namespace Assistant.Services;

public sealed class EmailOptions
{
    public string? From { get; set; }

    public string? SupportRecipient { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 25;

    public bool EnableSsl { get; set; } = true;

    public string? Username { get; set; }

    public string? Password { get; set; }
}
