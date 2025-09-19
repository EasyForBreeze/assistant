using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Net.Http.Json;
using System.Text.Json;

namespace Assistant.KeyCloak;

public sealed record UserSearchResult(string Username, string? FirstName, string? LastName, string? Email)
{
    public string DisplayName
    {
        get
        {
            var segments = new List<string>();

            if (!string.IsNullOrWhiteSpace(LastName))
            {
                segments.Add(LastName!.Trim());
            }

            if (!string.IsNullOrWhiteSpace(FirstName))
            {
                segments.Add(FirstName!.Trim());
            }

            var name = string.Join(" ", segments);

            if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(Email))
            {
                return $"{Username} — {name} ({Email!.Trim()})";
            }

            if (!string.IsNullOrWhiteSpace(name))
            {
                return $"{Username} — {name}";
            }

            if (!string.IsNullOrWhiteSpace(Email))
            {
                return $"{Username} — {Email!.Trim()}";
            }

            return Username;
        }
    }
}

public sealed class UsersService
{
    private readonly IHttpClientFactory _factory;
    private readonly AdminApiOptions _opt;
    private readonly string _primaryRealm;

    private sealed class UserRepresentation
    {
        public string? Id { get; set; }
        public string? Username { get; set; }
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public UsersService(
        IHttpClientFactory factory,
        IOptions<AdminApiOptions> opt,
        IConfiguration configuration)
    {
        _factory = factory;
        _opt = opt.Value;
        _primaryRealm = (configuration["Keycloak:PrimaryRealm"] ?? configuration["Keycloak:Realm"])
            ?? throw new InvalidOperationException("Keycloak primary realm is not configured.");
    }

    public string PrimaryRealm => _primaryRealm;

    private HttpClient CreateAdminClient() => _factory.CreateClient("kc-admin");

    private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

    private static string UR(string value) => Uri.EscapeDataString(value);

    public async Task<List<UserSearchResult>> SearchUsersAsync(
        string query,
        int first = 0,
        int max = 20,
        CancellationToken ct = default)
    {
        query = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(query))
        {
            return new List<UserSearchResult>();
        }

        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 20 : max, 1, 200);

        var http = CreateAdminClient();

        var urlNew = $"{BaseUrl}/admin/realms/{UR(_primaryRealm)}/users?search={UR(query)}&first={first}&max={max}";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(_primaryRealm)}/users?search={UR(query)}&first={first}&max={max}";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();

        var raw = await resp.Content.ReadFromJsonAsync<List<UserRepresentation>>(JsonOpts, ct)
                  ?? new List<UserRepresentation>();

        return raw
            .Where(u => !string.IsNullOrWhiteSpace(u.Username))
            .Select(MapUser)
            .GroupBy(u => u.Username, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(u => u.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static UserSearchResult MapUser(UserRepresentation user)
    {
        var username = user.Username!.Trim();
        return new UserSearchResult(
            username,
            Normalize(user.FirstName),
            Normalize(user.LastName),
            Normalize(user.Email));
    }

    private static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length == 0 ? null : trimmed;
    }
}
