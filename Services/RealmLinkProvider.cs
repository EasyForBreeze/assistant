using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Assistant.Services;

public sealed class RealmLinkProvider
{
    private readonly IReadOnlyDictionary<string, string> _links;

    public RealmLinkProvider(IHostEnvironment environment, ILogger<RealmLinkProvider> logger)
    {
        var path = Path.Combine(environment.ContentRootPath, "link_realms.json");
        if (!File.Exists(path))
        {
            logger.LogWarning("Realm links file was not found at {Path}.", path);
            _links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            return;
        }

        try
        {
            using var stream = File.OpenRead(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, string>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (data is null || data.Count == 0)
            {
                _links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                return;
            }

            var comparer = StringComparer.OrdinalIgnoreCase;
            var normalized = new Dictionary<string, string>(data.Count, comparer);
            foreach (var pair in data)
            {
                if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value))
                {
                    continue;
                }

                normalized[pair.Key.Trim()] = pair.Value.Trim();
            }

            _links = normalized;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to read realm links from {Path}.", path);
            _links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    public bool TryGetRealmLink(string? realm, out string link)
    {
        link = string.Empty;
        if (string.IsNullOrWhiteSpace(realm))
        {
            return false;
        }

        if (_links.TryGetValue(realm, out var value) && !string.IsNullOrWhiteSpace(value))
        {
            link = value;
            return true;
        }

        return false;
    }
}
