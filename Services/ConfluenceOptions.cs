using System;
using System.Data.Common;

namespace Assistant.Services;

public sealed class ConfluenceOptions
{
    public string? BaseUrl { get; private set; }
    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public string? SpaceKey { get; private set; }
    public long? ParentPageId { get; private set; }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(SpaceKey)
        && ParentPageId.HasValue;

    public static ConfluenceOptions FromConnectionString(string? connectionString)
    {
        var options = new ConfluenceOptions();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return options;
        }

        var builder = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        options.BaseUrl = GetString(builder, "BaseUrl");
        options.Username = GetString(builder, "User") ?? GetString(builder, "Username");
        options.Password = GetString(builder, "Password");
        options.SpaceKey = GetString(builder, "SpaceKey");

        if (builder.TryGetValue("ParentId", out var parent) && parent is not null)
        {
            if (long.TryParse(Convert.ToString(parent), out var parsed))
            {
                options.ParentPageId = parsed;
            }
        }

        return options;
    }

    private static string? GetString(DbConnectionStringBuilder builder, string key)
    {
        if (!builder.TryGetValue(key, out var raw) || raw is null)
        {
            return null;
        }

        var value = Convert.ToString(raw)?.Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
