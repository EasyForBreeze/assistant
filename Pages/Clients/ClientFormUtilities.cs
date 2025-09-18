using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assistant.Pages.Clients;

internal static class ClientFormUtilities
{
    private static readonly Regex RoleNameRegex = new("^[a-z][a-z0-9._:-]{2,63}$", RegexOptions.Compiled);
    private static readonly Regex ClientIdRegex = new("^[a-z0-9][a-z0-9-]{2,60}$", RegexOptions.Compiled);

    public static List<string> ParseStringList(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return new List<string>();
        }

        try
        {
            return JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    public static List<string> NormalizeDistinct(IEnumerable<string> items)
        => items
            .Select(static s => (s ?? string.Empty).Trim())
            .Where(static s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static bool IsValidHttpUrl(string url)
        => Uri.TryCreate(url, UriKind.Absolute, out var uri)
           && (uri.Scheme == Uri.UriSchemeHttps
               || (uri.Scheme == Uri.UriSchemeHttp
                   && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                       || IPAddress.TryParse(uri.Host, out _))));

    public static bool TryValidateRedirectTemplate(string redirectUri)
    {
        var starPos = redirectUri.IndexOf('*');
        if (starPos < 0)
        {
            return true;
        }

        if (starPos != redirectUri.Length - 1 || starPos == 0 || redirectUri[starPos - 1] != '/')
        {
            return false;
        }

        return Uri.TryCreate(redirectUri[..starPos], UriKind.Absolute, out _);
    }

    public static IEnumerable<string> ValidateRedirects(IEnumerable<string> redirects)
    {
        foreach (var redirect in redirects)
        {
            if (!TryValidateRedirectTemplate(redirect))
            {
                yield return redirect;
                continue;
            }

            var candidate = redirect.Replace("*", string.Empty, StringComparison.Ordinal);
            if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri)
                || !string.IsNullOrEmpty(uri.Fragment)
                || !(uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase)
                     || (uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                         && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                             || IPAddress.TryParse(uri.Host, out _)))))
            {
                yield return redirect;
            }
        }
    }

    public static List<(string ClientId, string Role)> ParseServiceRolePairs(string? json)
    {
        var result = new List<(string, string)>();
        foreach (var value in ParseStringList(json))
        {
            var parts = value.Split(':', 2);
            if (parts.Length != 2)
            {
                continue;
            }

            var clientId = parts[0].Trim();
            var role = parts[1].Trim();
            if (!string.IsNullOrWhiteSpace(clientId) && !string.IsNullOrWhiteSpace(role))
            {
                result.Add((clientId, role));
            }
        }

        return result;
    }

    public static IEnumerable<string> FindInvalidLocalRoles(IEnumerable<string> localRoles)
        => localRoles.Where(role => !RoleNameRegex.IsMatch(role));

    public static IEnumerable<string> FindInvalidServiceRoleEntries(IEnumerable<string> entries, ISet<string> restrictedClients)
    {
        foreach (var entry in entries)
        {
            var separatorIndex = entry.IndexOf(':');
            if (separatorIndex <= 0 || separatorIndex >= entry.Length - 1)
            {
                yield return entry;
                continue;
            }

            var client = entry[..separatorIndex].Trim();
            var role = entry[(separatorIndex + 1)..].Trim();

            if (!ClientIdRegex.IsMatch(client) || !RoleNameRegex.IsMatch(role) || restrictedClients.Contains(client))
            {
                yield return entry;
            }
        }
    }
}
