// Assistant/KeyCloak/ClientsService.cs
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace Assistant.KeyCloak
{
    public sealed class ClientShort
    {
        public string Id { get; set; } = "";
        public string ClientId { get; set; } = "";
        public string? Name { get; set; }
    }

    internal sealed class ClientRep
    {
        public string? Id { get; set; }
        public string? ClientId { get; set; }
        public string? Name { get; set; }
    }
    internal sealed class RoleRep
    {
        public string? Name { get; set; }
        public string? Description { get; set; }
    }

    /// <summary>
    /// Поиск клиентов и получение ролей через Keycloak Admin API (без кэша).
    /// Возвращает только клиентов, у которых подстрока встречается в clientId или name (case-insensitive).
    /// </summary>
    public class ClientsService
    {
        private readonly IHttpClientFactory _factory;
        private readonly AdminApiOptions _opt;

        public ClientsService(IHttpClientFactory factory, IOptions<AdminApiOptions> opt)
        {
            _factory = factory;
            _opt = opt.Value;
        }

        private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

        public async Task<List<ClientShort>> SearchClientsAsync(
            string realm, string query, int first = 0, int max = 20, CancellationToken ct = default)
        {
            query = (query ?? "").Trim();
            if (string.IsNullOrEmpty(query)) return new(); // пустой ввод — пустой список
            first = Math.Max(0, first);
            max = Math.Clamp(max <= 0 ? 20 : max, 1, 200);

            var http = _factory.CreateClient("kc-admin");

            // Берём search= из KC и ДОПОЛНИТЕЛЬНО фильтруем строгим подстрочным совпадением
            var urlNew = $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(realm)}/clients?search={Uri.EscapeDataString(query)}&first={first}&max={max}";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{Uri.EscapeDataString(realm)}/clients?search={Uri.EscapeDataString(query)}&first={first}&max={max}";

            var resp = await http.GetAsync(urlNew, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.GetAsync(urlLegacy, ct);
            }

            await EnsureAuthOrThrow(resp);
            var list = await ReadJson<List<ClientRep>>(resp, ct) ?? new();

            static bool ContainsCi(string? s, string needle) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            return list
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(c => new ClientShort { Id = c.Id ?? "", ClientId = c.ClientId ?? "", Name = c.Name })
                .Where(c => ContainsCi(c.ClientId, query) || ContainsCi(c.Name, query))
                .ToList();
        }

        public async Task<List<string>> GetClientRolesAsync(
            string realm, string clientUuid, int first = 0, int max = 50, string? search = null, CancellationToken ct = default)
        {
            first = Math.Max(0, first);
            max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

            var http = _factory.CreateClient("kc-admin");
            var qs = $"briefRepresentation=true&first={first}&max={max}" +
                     (string.IsNullOrWhiteSpace(search) ? "" : $"&search={Uri.EscapeDataString(search!)}");

            var urlNew = $"{BaseUrl}/admin/realms/{Uri.EscapeDataString(realm)}/clients/{Uri.EscapeDataString(clientUuid)}/roles?{qs}";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{Uri.EscapeDataString(realm)}/clients/{Uri.EscapeDataString(clientUuid)}/roles?{qs}";

            var resp = await http.GetAsync(urlNew, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.GetAsync(urlLegacy, ct);
            }

            await EnsureAuthOrThrow(resp);
            var roles = await ReadJson<List<RoleRep>>(resp, ct) ?? new();
            return roles.Select(r => r.Name)
                        .Where(n => !string.IsNullOrWhiteSpace(n))
                        .Select(n => n!)
                        .ToList();
        }

        private static async Task EnsureAuthOrThrow(HttpResponseMessage resp)
        {
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Недостаточно прав для чтения клиентов/ролей (realm-management).");
            resp.EnsureSuccessStatusCode();
            await Task.CompletedTask;
        }

        private static async Task<T?> ReadJson<T>(HttpResponseMessage resp, CancellationToken ct)
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return await resp.Content.ReadFromJsonAsync<T>(options, ct);
        }
    }
}
