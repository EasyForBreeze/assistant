using System;
using System.Collections.Generic;
using System.Data.Common;
using System.Linq;

namespace Assistant.Services;

public sealed class ConfluenceOptions
{
    public string? BaseUrl { get; private set; }
    public string? Username { get; private set; }
    public string? Password { get; private set; }
    public string? SpaceKey { get; private set; }
    public long? ParentPageId { get; private set; }
    public IReadOnlyList<string> Labels { get; private set; } = Array.Empty<string>();

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(BaseUrl)
        && !string.IsNullOrWhiteSpace(Username)
        && !string.IsNullOrWhiteSpace(Password)
        && !string.IsNullOrWhiteSpace(SpaceKey)
        && ParentPageId.HasValue;

    public static ConfluenceOptions FromConnectionString(string? connectionString, IEnumerable<string>? labels = null)
    {
        var options = new ConfluenceOptions();
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            options.Labels = NormalizeLabels(labels);
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

        options.Labels = NormalizeLabels(labels);

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

    private static IReadOnlyList<string> NormalizeLabels(IEnumerable<string>? labels)
    {
        if (labels is null)
        {
            return Array.Empty<string>();
        }

        var normalized = labels
            .Select(label => label?.Trim())
            .Where(label => !string.IsNullOrWhiteSpace(label))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(label => label!)
            .ToArray();

        return normalized.Length == 0 ? Array.Empty<string>() : normalized;
    }
}
