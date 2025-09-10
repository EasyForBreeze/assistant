using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System.Net;
using System.Text.Json;

namespace Assistant.KeyCloak
{
    public class RealmRepresentation
    {
        public string? Id { get; set; }
        public string? Realm { get; set; }
        public string? DisplayName { get; set; }
        public bool? Enabled { get; set; }
    }

    public class RealmsService
    {
        private readonly IHttpClientFactory _factory;
        private readonly AdminApiOptions _opt;
        private readonly IMemoryCache _cache;

        private const string CacheKey = "kc:realms:list";
        private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(30);
        private static readonly SemaphoreSlim _refreshLock = new(1, 1);

        public RealmsService(IHttpClientFactory factory, IOptions<AdminApiOptions> opt, IMemoryCache cache)
        {
            _factory = factory;
            _opt = opt.Value;
            _cache = cache;
        }

        public Task<List<RealmRepresentation>> GetRealmsAsync(CancellationToken ct = default)
            => _cache.GetOrCreateAsync(CacheKey, async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                return await FetchRealmsAsync(ct);
            })!;

        public async Task<bool> RealmExistsAsync(string realm, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(realm)) return false;

            var cached = await GetRealmsAsync(ct);
            if (cached.Any(r => string.Equals(r.Realm, realm, StringComparison.OrdinalIgnoreCase)))
                return true;

            await RefreshRealmsAsync(force: true, ct);
            var fresh = await GetRealmsAsync(ct);
            return fresh.Any(r => string.Equals(r.Realm, realm, StringComparison.OrdinalIgnoreCase));
        }

        public async Task RefreshRealmsAsync(bool force = false, CancellationToken ct = default)
        {
            await _refreshLock.WaitAsync(ct);
            try
            {
                if (force) _cache.Remove(CacheKey);

                await _cache.GetOrCreateAsync(CacheKey, async entry =>
                {
                    entry.AbsoluteExpirationRelativeToNow = CacheTtl;
                    return await FetchRealmsAsync(ct);
                })!;
            }
            finally
            {
                _refreshLock.Release();
            }
        }

        private async Task<List<RealmRepresentation>> FetchRealmsAsync(CancellationToken ct)
        {
            var http = _factory.CreateClient("kc-admin");
            var baseUrl = _opt.BaseUrl.TrimEnd('/');

            var url = $"{baseUrl}/admin/realms?briefRepresentation=true";
            var resp = await http.GetAsync(url, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                var legacy = $"{baseUrl}/auth/admin/realms?briefRepresentation=true";
                resp = await http.GetAsync(legacy, ct);
            }

            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Недостаточно прав для чтения реалмов (нужны роли realm-management: view-realm/realm-admin).");

            resp.EnsureSuccessStatusCode();

            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var list = await resp.Content.ReadFromJsonAsync<List<RealmRepresentation>>(options, ct)
                       ?? new List<RealmRepresentation>();

            return list.Where(r => !string.IsNullOrWhiteSpace(r.Realm)).OrderBy(r => r.Realm, StringComparer.OrdinalIgnoreCase).ToList();
        }
    }
}
