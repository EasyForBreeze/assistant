using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace Assistant.KeyCloak;

public sealed record EventEntry(string Type, DateTime At, string? User, string? Ip);

internal sealed class EventRep
{
    public string? Type { get; set; }
    public long? Time { get; set; }
    public string? ClientId { get; set; }
    public string? UserId { get; set; }
    public string? Username { get; set; }
    public string? IpAddress { get; set; }
}

internal sealed class EventConfigRep
{
    public List<string>? EnabledEventTypes { get; set; }
}

public class EventsService
{
    private readonly IHttpClientFactory _factory;
    private readonly AdminApiOptions _opt;

    public EventsService(IHttpClientFactory factory, IOptions<AdminApiOptions> opt)
    {
        _factory = factory;
        _opt = opt.Value;
    }

    private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

    public async Task<List<string>> GetEventTypesAsync(string realm, CancellationToken ct = default)
    {
        var http = _factory.CreateClient("kc-admin");
        var urlNew = $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(realm)}/events/config";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{Uri.EscapeDataString(realm)}/events/config";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        await resp.EnsureAdminSuccessAsync(ct);
        var cfg = await resp.Content.ReadFromJsonAsync<EventConfigRep>(cancellationToken: ct);
        return cfg?.EnabledEventTypes ?? new List<string>();
    }

    public async Task<List<EventEntry>> GetEventsAsync(
        string realm,
        string clientId,
        string? type,
        DateTime? from,
        DateTime? to,
        string? user,
        string? ip,
        int max = 50,
        CancellationToken ct = default)
    {
        var http = _factory.CreateClient("kc-admin");
        var qs = new List<string> { $"client={Uri.EscapeDataString(clientId)}", $"max={max}", "first=0" };
        if (!string.IsNullOrWhiteSpace(type)) qs.Add($"type={Uri.EscapeDataString(type)}");
        if (!string.IsNullOrWhiteSpace(user)) qs.Add($"user={Uri.EscapeDataString(user)}");
        if (!string.IsNullOrWhiteSpace(ip)) qs.Add($"ipAddress={Uri.EscapeDataString(ip)}");
        if (from.HasValue) qs.Add($"dateFrom={new DateTimeOffset(from.Value).ToUnixTimeMilliseconds()}");
        if (to.HasValue) qs.Add($"dateTo={new DateTimeOffset(to.Value).ToUnixTimeMilliseconds()}");
        var query = string.Join('&', qs);
        var urlNew = $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(realm)}/events?{query}";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{Uri.EscapeDataString(realm)}/events?{query}";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        await resp.EnsureAdminSuccessAsync(ct);
        var reps = await resp.Content.ReadFromJsonAsync<List<EventRep>>(cancellationToken: ct) ?? new List<EventRep>();
        return reps
            .Where(r => string.Equals(r.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            .Select(r => new EventEntry(
                r.Type ?? string.Empty,
                DateTimeOffset.FromUnixTimeMilliseconds(r.Time ?? 0).LocalDateTime,
                r.Username ?? r.UserId,
                r.IpAddress))
            .ToList();
    }
}
