using Assistant.KeyCloak.Models;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using System;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.KeyCloak;

public sealed class ClientsService
{
    private readonly IHttpClientFactory _factory;
    private readonly AdminApiOptions _opt;
    private readonly ServiceRoleExclusionsRepository _exclusions;
    private readonly ApiLogRepository _logs;
    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly IMemoryCache _cache;

    private const int ClientDetailsRolePreviewLimit = 50;
    private static readonly TimeSpan ClientRolesCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ServiceRolesCacheDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ClientRoleMapCacheDuration = TimeSpan.FromMinutes(3);
    private static readonly TimeSpan ClientSearchCacheDuration = TimeSpan.FromMinutes(5);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private static readonly IReadOnlyDictionary<TokenExampleKind, string[]> TokenExampleRoutes =
        new Dictionary<TokenExampleKind, string[]>
        {
            [TokenExampleKind.AccessToken] = new[] { "generate-example-access-token" },
            [TokenExampleKind.IdToken] = new[] { "generate-example-id-token" },
            [TokenExampleKind.UserInfo] = new[] { "generate-example-userinfo", "generate-example-user-info" }
        };

    internal sealed class ClientRep
    {
        public string? Id { get; set; }
        public string? ClientId { get; set; }
    }

    internal sealed class ClientFullRep
    {
        public string? Id { get; set; }
        public string? ClientId { get; set; }
        public bool? Enabled { get; set; }
        public string? Description { get; set; }
        public bool? PublicClient { get; set; }
        public bool? StandardFlowEnabled { get; set; }
        public bool? ServiceAccountsEnabled { get; set; }
        public List<string>? RedirectUris { get; set; }
        public List<string>? DefaultClientScopes { get; set; }
    }

    internal sealed class ClientSecretRep
    {
        public string? Value { get; set; }
    }

    internal sealed class RoleRep
    {
        public string? Name { get; set; }
    }

    internal sealed class KcRoleRep
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public bool? ClientRole { get; set; }
        public string? ContainerId { get; set; }
    }

    internal sealed class KcUserRep
    {
        public string? Id { get; set; }
        public string? Username { get; set; }
    }

    internal sealed class RoleMappingsRep
    {
        public Dictionary<string, ClientMapping>? ClientMappings { get; set; }
    }

    internal sealed class ClientMapping
    {
        public List<RoleRep>? Mappings { get; set; }
    }

    public ClientsService(
        IHttpClientFactory factory,
        IOptions<AdminApiOptions> opt,
        ServiceRoleExclusionsRepository exclusions,
        ApiLogRepository logs,
        IHttpContextAccessor httpContextAccessor,
        IMemoryCache cache)
    {
        _factory = factory;
        _opt = opt.Value;
        _exclusions = exclusions;
        _logs = logs;
        _httpContextAccessor = httpContextAccessor;
        _cache = cache;
    }

    private HttpClient CreateAdminClient() => _factory.CreateClient("kc-admin");

    private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

    private (string Primary, string Legacy) BuildAdminUrls(string realm, string relativePath)
    {
        var path = relativePath.TrimStart('/');
        var encodedRealm = UR(realm);
        return ($"{BaseUrl}/admin/realms/{encodedRealm}/{path}", $"{BaseUrl}/auth/admin/realms/{encodedRealm}/{path}");
    }

    private string ResolveUsername()
    {
        var username = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(username) ? "unknown" : username;
    }

    private Task AuditAsync(string operationType, string realm, string targetId, CancellationToken ct, string? details = null)
    {
        realm = string.IsNullOrWhiteSpace(realm) ? "-" : realm;
        targetId = string.IsNullOrWhiteSpace(targetId) ? "-" : targetId;
        return _logs.LogAsync(operationType, ResolveUsername(), realm, targetId, details, ct);
    }

    private static string? DescribeClientUpdateChanges(ClientDetails before, UpdateClientSpec after)
    {
        var changes = new List<string>();

        static string NormalizeSingle(string? value)
            => string.IsNullOrWhiteSpace(value) ? "-" : value.Trim();

        void AddStringChange(string field, string? oldValue, string? newValue)
        {
            var oldNorm = NormalizeSingle(oldValue);
            var newNorm = NormalizeSingle(newValue);
            if (!string.Equals(oldNorm, newNorm, StringComparison.Ordinal))
            {
                changes.Add($"{field}: '{oldNorm}' → '{newNorm}'");
            }
        }

        void AddBoolChange(string field, bool oldValue, bool newValue)
        {
            if (oldValue != newValue)
            {
                changes.Add($"{field}: {oldValue.ToString().ToLowerInvariant()} → {newValue.ToString().ToLowerInvariant()}");
            }
        }

        static IEnumerable<string> NormalizeList(IEnumerable<string> source)
            => source
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim());

        void AddSetChange(string field, IEnumerable<string> beforeSet, IEnumerable<string> afterSet, bool includeRemoved = true)
        {
            var beforeList = NormalizeList(beforeSet).Distinct(StringComparer.Ordinal).ToList();
            var afterList = NormalizeList(afterSet).Distinct(StringComparer.Ordinal).ToList();

            var beforeHash = new HashSet<string>(beforeList, StringComparer.Ordinal);
            var afterHash = new HashSet<string>(afterList, StringComparer.Ordinal);

            var added = afterList.Where(item => !beforeHash.Contains(item)).ToList();
            var removed = includeRemoved
                ? beforeList.Where(item => !afterHash.Contains(item)).ToList()
                : new List<string>();

            if (added.Count == 0 && removed.Count == 0)
            {
                return;
            }

            var parts = new List<string>();
            if (added.Count > 0)
            {
                parts.Add($"added [{string.Join(", ", added)}]");
            }

            if (includeRemoved && removed.Count > 0)
            {
                parts.Add($"removed [{string.Join(", ", removed)}]");
            }

            if (parts.Count > 0)
            {
                changes.Add($"{field}: {string.Join("; ", parts)}");
            }
        }

        AddStringChange("clientId", before.ClientId, after.ClientId);
        AddBoolChange("enabled", before.Enabled, after.Enabled);
        AddStringChange("description", before.Description, after.Description);
        AddBoolChange("clientAuth", before.ClientAuth, after.ClientAuth);
        AddBoolChange("standardFlow", before.StandardFlow, after.StandardFlow);
        AddBoolChange("serviceAccount", before.ServiceAccount, after.ServiceAccount);

        var desiredRedirects = after.StandardFlow ? after.RedirectUris : Array.Empty<string>();
        AddSetChange("redirectUris", before.RedirectUris, desiredRedirects);

        AddSetChange("localRoles", before.LocalRoles, after.LocalRoles, includeRemoved: false);

        var beforeServiceRoles = before.ServiceRoles
            .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
            .Select(p => $"{p.ClientId.Trim()}:{p.Role.Trim()}")
            .ToList();
        var afterServiceRoles = after.ServiceAccount
            ? after.ServiceRoles
                .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
                .Select(p => $"{p.ClientId.Trim()}:{p.Role.Trim()}")
                .ToList()
            : new List<string>();
        AddSetChange("serviceRoles", beforeServiceRoles, afterServiceRoles);

        return changes.Count == 0 ? null : string.Join("; ", changes);
    }

    private static string UR(string s) => Uri.EscapeDataString(s);

    public async Task<List<ClientShort>> SearchClientsAsync(
        string realm, string query, int first = 0, int max = 20, CancellationToken ct = default)
    {
        query = (query ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(query))
        {
            return new();
        }

        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 20 : max, 1, 200);

        var cacheKey = BuildClientSearchCacheKey(realm, query, first, max);
        if (_cache.TryGetValue(cacheKey, out ClientShort[] cachedClients))
        {
            return new List<ClientShort>(cachedClients);
        }

        var http = CreateAdminClient();
        var excluded = await _exclusions.GetAllAsync(ct);

        var (urlExactNew, urlExactLegacy) =
            BuildAdminUrls(realm, $"clients?clientId={UR(query)}&briefRepresentation=true");

        using (var resp = await http.GetWithLegacyFallbackAsync(urlExactNew, urlExactLegacy, ct))
        {
            resp.EnsureAdminSuccess();
            var exact = await ReadJsonAsync<List<ClientRep>>(resp, ct) ?? new();

            static bool ContainsCi(string? s, string needle)
                => !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            var mappedExact = exact
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(MapClient)
                .Where(c => ContainsCi(c.ClientId, query))
                .ToList();

            var filteredExact = FilterExcluded(mappedExact, excluded);
            if (filteredExact.Count > 0)
            {
                return CacheSearchResult(cacheKey, filteredExact);
            }
        }

        var (urlNew, urlLegacy) = BuildAdminUrls(
            realm,
            $"clients?search=true&clientId={UR(query)}&first={first}&max={max}&briefRepresentation=true");

        using var resp2 = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp2.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientRep>>(resp2, ct) ?? new();

        var mapped = list
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .Select(MapClient)
            .Where(c => c.ClientId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        var filtered = FilterExcluded(mapped, excluded);
        return CacheSearchResult(cacheKey, filtered);

        static ClientShort MapClient(ClientRep c) => new(c.Id ?? string.Empty, c.ClientId ?? string.Empty);

        List<ClientShort> CacheSearchResult(string key, List<ClientShort> result)
        {
            var snapshot = result.ToArray();
            _cache.Set(key, snapshot, new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = ClientSearchCacheDuration
            });

            return new List<ClientShort>(snapshot);
        }
    }

    private static string BuildClientSearchCacheKey(string realm, string query, int first, int max)
        => $"kc:clients:search:{realm}:{first}:{max}:{query}";

    private async Task<ClientShort?> GetClientShortByClientIdAsync(string realm, string clientId, CancellationToken ct)
    {
        clientId = (clientId ?? string.Empty).Trim();
        if (clientId.Length == 0)
        {
            return null;
        }

        var http = CreateAdminClient();
        var (urlNew, urlLegacy) =
            BuildAdminUrls(realm, $"clients?clientId={UR(clientId)}&briefRepresentation=true");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientRep>>(resp, ct) ?? new();

        var rep = list
            .Where(x => !string.IsNullOrWhiteSpace(x.Id) && !string.IsNullOrWhiteSpace(x.ClientId))
            .FirstOrDefault(x => string.Equals(x.ClientId, clientId, StringComparison.OrdinalIgnoreCase));

        return rep == null ? null : new ClientShort(rep.Id!, rep.ClientId!);
    }

    public async Task<(List<ClientShort> Clients, int TotalFetched)> ListClientsAsync(
        string realm, int first = 0, int max = 50, CancellationToken ct = default)
    {
        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

        var http = CreateAdminClient();
        var excluded = await _exclusions.GetAllAsync(ct);

        var (urlNew, urlLegacy) =
            BuildAdminUrls(realm, $"clients?first={first}&max={max}&briefRepresentation=true");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientRep>>(resp, ct) ?? new();

        var mapped = list
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .Select(c => new ClientShort(c.Id ?? string.Empty, c.ClientId ?? string.Empty))
            .ToList();

        var filtered = FilterExcluded(mapped, excluded);
        //await AuditAsync("client:list", realm, $"{first}:{max}", ct);
        return (filtered, mapped.Count);
    }

    public async Task<ClientDetails?> GetClientDetailsAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var (urlNew, urlLegacy) = BuildAdminUrls(realm, $"clients?clientId={UR(clientId)}");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientFullRep>>(resp, ct) ?? new();
        var rep = list.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
        if (rep == null || string.IsNullOrWhiteSpace(rep.Id) || string.IsNullOrWhiteSpace(rep.ClientId))
        {
            return null;
        }

        var localRolesTask = GetClientRolePreviewAsync(realm, rep.Id!, ct);
        Task<List<(string ClientId, string Role)>> svcRolesTask = (rep.ServiceAccountsEnabled ?? false)
            ? GetServiceAccountRolesCachedAsync(realm, rep.Id!, ct)
            : Task.FromResult(new List<(string ClientId, string Role)>());

        var localRoles = await localRolesTask;
        var svcRoles = await svcRolesTask;
        var defaultScopes = rep.DefaultClientScopes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() ?? new();

        var details = new ClientDetails(
            rep.Id!,
            rep.ClientId!,
            rep.Enabled ?? false,
            rep.Description,
            !(rep.PublicClient ?? true),
            rep.StandardFlowEnabled ?? false,
            rep.ServiceAccountsEnabled ?? false,
            rep.RedirectUris?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToList() ?? new List<string>(),
            localRoles,
            svcRoles,
            defaultScopes
        );
        return details;
    }

    public async Task<string?> FindUserIdByUsernameAsync(string realm, string username, CancellationToken ct = default)
    {
        username = (username ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            return null;
        }

        var http = CreateAdminClient();

        var (urlNew, urlLegacy) = BuildAdminUrls(realm, $"users?username={UR(username)}");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var users = await ReadJsonAsync<List<KcUserRep>>(resp, ct) ?? new();

        var match = users
            .Where(u => !string.IsNullOrWhiteSpace(u.Username) && !string.IsNullOrWhiteSpace(u.Id))
            .FirstOrDefault(u => string.Equals(u.Username!.Trim(), username, StringComparison.OrdinalIgnoreCase));

        return match?.Id?.Trim();
    }

    public async Task<string> GenerateExampleTokenAsync(
        string realm,
        string clientUuid,
        string userId,
        TokenExampleKind kind,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            throw new ArgumentException("Realm is required.", nameof(realm));
        }

        if (string.IsNullOrWhiteSpace(clientUuid))
        {
            throw new ArgumentException("Client UUID is required.", nameof(clientUuid));
        }

        if (string.IsNullOrWhiteSpace(userId))
        {
            throw new ArgumentException("User id is required.", nameof(userId));
        }

        if (!TokenExampleRoutes.TryGetValue(kind, out var segments) || segments.Length == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(kind));
        }

        var http = CreateAdminClient();
        var userIdEncoded = UR(userId);
        foreach (var segment in segments)
        {
            var (urlNew, urlLegacy) = BuildAdminUrls(
                realm,
                $"clients/{UR(clientUuid)}/evaluate-scopes/{segment}?userId={userIdEncoded}");

            using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                continue;
            }

            resp.EnsureAdminSuccess();
            return await resp.Content.ReadAsStringAsync();
        }

        throw new InvalidOperationException("Endpoint для генерации примера токена не найден.");
    }

    public async Task<string?> GetClientSecretAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var client = await GetClientShortByClientIdAsync(realm, clientId, ct);
        if (client == null)
        {
            return null;
        }

        var http = CreateAdminClient();
        var (urlNew, urlLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(client.Id)}/client-secret");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var rep = await ReadJsonAsync<ClientSecretRep>(resp, ct);
        return rep?.Value;
    }

    public async Task<string?> RegenerateClientSecretAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var client = await GetClientShortByClientIdAsync(realm, clientId, ct);
        if (client == null)
        {
            await AuditAsync("CLIENT:secret-regenerate", realm, clientId, ct);
            return null;
        }

        var http = CreateAdminClient();
        var (urlNew, urlLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(client.Id)}/client-secret");

        using var resp = await http.PostWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var rep = await ReadJsonAsync<ClientSecretRep>(resp, ct);
        var secret = rep?.Value;
        await AuditAsync("CLIENT:secret-regenerate", realm, client.ClientId, ct);
        return secret;
    }

    public async Task<List<string>> GetClientRolesAsync(
        string realm, string clientUuid, int first = 0, int max = 50, string? search = null, CancellationToken ct = default)
    {
        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

        var http = CreateAdminClient();
        var qs = $"briefRepresentation=true&first={first}&max={max}" +
                 (string.IsNullOrWhiteSpace(search) ? string.Empty : $"&search={UR(search!)}");

        var (urlNew, urlLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(clientUuid)}/roles?{qs}");

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var roles = await ReadJsonAsync<List<RoleRep>>(resp, ct) ?? new();

        return roles
            .Select(r => r.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!)
            .ToList();
    }

    public async Task<(List<RoleHit> Hits, int NextClientFirst)> FindRolesAcrossClientsAsync(
        string realm, string roleQuery, int clientFirst = 0, int clientsToScan = 25, int rolesPerClient = 10, CancellationToken ct = default)
    {
        roleQuery = (roleQuery ?? string.Empty).Trim();
        if (roleQuery.Length == 0)
        {
            return (new List<RoleHit>(), -1);
        }

        var (clients, fetched) = await ListClientsAsync(realm, clientFirst, clientsToScan, ct);
        var hits = new List<RoleHit>();

        var roleTasks = clients
            .Select(async client =>
            {
                var roles = await GetClientRolesAsync(realm, client.Id, 0, rolesPerClient, roleQuery, ct);
                return (client, roles);
            })
            .ToList();

        var results = await Task.WhenAll(roleTasks);
        foreach (var (client, roles) in results)
        {
            foreach (var role in roles)
            {
                hits.Add(new RoleHit(client.Id, client.ClientId, role));
            }
        }

        var next = fetched < clientsToScan ? -1 : clientFirst + fetched;
        return (hits, next);
    }

    public async Task<string> CreateClientAsync(NewClientSpec spec, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var body = new
        {
            clientId = spec.ClientId,
            protocol = "openid-connect",
            publicClient = !spec.ClientAuthentication,
            serviceAccountsEnabled = spec.ServiceAccount,
            standardFlowEnabled = spec.StandardFlow,
            directAccessGrantsEnabled = false,
            redirectUris = spec.RedirectUris.Distinct().ToArray(),
            description = spec.Description
        };

        var (postNew, postLegacy) = BuildAdminUrls(spec.Realm, "clients");

        using var createResp = await http.PostJsonWithLegacyFallbackAsync(postNew, postLegacy, body, JsonOpts, ct);
        if (createResp.StatusCode == HttpStatusCode.Conflict)
        {
            throw new InvalidOperationException($"Client '{spec.ClientId}' already exists.");
        }

        createResp.EnsureAdminSuccess();

        string? createdId = null;
        if (createResp.Headers.Location is Uri loc)
        {
            var segment = loc.Segments.LastOrDefault();
            if (!string.IsNullOrWhiteSpace(segment))
            {
                createdId = segment.TrimEnd('/');
            }
        }

        createdId ??= (await SearchClientsAsync(spec.Realm, spec.ClientId, 0, 1, ct))
            .FirstOrDefault(c => string.Equals(c.ClientId, spec.ClientId, StringComparison.OrdinalIgnoreCase))?.Id
            ?? throw new InvalidOperationException("Cannot resolve created client id.");

        try
        {
            if (spec.LocalRoles.Count > 0)
            {
                await EnsureLocalRolesAsync(spec.Realm, createdId, spec.LocalRoles, ct);
            }

            if (spec.ServiceAccount && spec.ServiceRoles.Count > 0)
            {
                await AssignServiceRolesToServiceAccountAsync(spec.Realm, createdId, spec.ServiceRoles, ct);
            }
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Клиент '{spec.ClientId}' создан, но при назначении ролей произошла ошибка: {ex.Message}", ex);
        }

        await AuditAsync("CLIENT:CREATE", spec.Realm, spec.ClientId, ct);
        return createdId;
    }

    public async Task UpdateClientAsync(UpdateClientSpec spec, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var existingDetails = await GetClientDetailsAsync(spec.Realm, spec.CurrentClientId, ct)
            ?? throw new InvalidOperationException($"Client '{spec.CurrentClientId}' not found.");

        static IEnumerable<(string ClientId, string Role)> NormalizePairs(IEnumerable<(string ClientId, string Role)> source)
        {
            foreach (var (client, role) in source)
            {
                var clientId = (client ?? string.Empty).Trim();
                var roleName = (role ?? string.Empty).Trim();
                if (clientId.Length == 0 || roleName.Length == 0)
                {
                    continue;
                }

                yield return (clientId, roleName);
            }
        }

        var comparer = ServiceRolePairComparer.Instance;
        var existingServiceRoles = NormalizePairs(existingDetails.ServiceRoles)
            .Distinct(comparer)
            .ToList();
        var desiredServiceRoles = spec.ServiceAccount
            ? NormalizePairs(spec.ServiceRoles).Distinct(comparer).ToList()
            : new List<(string ClientId, string Role)>();

        var desiredSet = new HashSet<(string ClientId, string Role)>(desiredServiceRoles, comparer);
        var existingSet = new HashSet<(string ClientId, string Role)>(existingServiceRoles, comparer);

        var rolesToRemove = existingServiceRoles
            .Where(pair => !desiredSet.Contains(pair))
            .ToList();
        var rolesToAdd = desiredServiceRoles
            .Where(pair => !existingSet.Contains(pair))
            .ToList();

        var body = new
        {
            clientId = spec.ClientId,
            enabled = spec.Enabled,
            publicClient = !spec.ClientAuth,
            serviceAccountsEnabled = spec.ServiceAccount,
            standardFlowEnabled = spec.StandardFlow,
            directAccessGrantsEnabled = false,
            redirectUris = spec.StandardFlow ? spec.RedirectUris.Distinct().ToArray() : Array.Empty<string>(),
            description = spec.Description
        };

        var (putNew, putLegacy) = BuildAdminUrls(spec.Realm, $"clients/{UR(existingDetails.Id)}");

        using var resp = await http.PutJsonWithLegacyFallbackAsync(putNew, putLegacy, body, JsonOpts, ct);
        resp.EnsureAdminSuccess();

        if (spec.LocalRoles.Count > 0)
        {
            await EnsureLocalRolesAsync(spec.Realm, existingDetails.Id, spec.LocalRoles, ct);
        }

        if (rolesToRemove.Count > 0)
        {
            await RemoveServiceRolesFromServiceAccountAsync(spec.Realm, existingDetails.Id, rolesToRemove, ct);
        }

        if (spec.ServiceAccount && rolesToAdd.Count > 0)
        {
            await AssignServiceRolesToServiceAccountAsync(spec.Realm, existingDetails.Id, rolesToAdd, ct);
        }

        var diff = DescribeClientUpdateChanges(existingDetails, spec);
        await AuditAsync("CLIENT:UPDATE", spec.Realm, spec.ClientId, ct, diff);
    }

    public async Task DeleteClientAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var existing = (await SearchClientsAsync(realm, clientId, 0, 1, ct))
            .FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Client '{clientId}' not found.");

        var (delNew, delLegacy) = BuildAdminUrls(realm, $"clients/{UR(existing.Id)}");

        using var resp = await http.DeleteWithLegacyFallbackAsync(delNew, delLegacy, ct);
        resp.EnsureAdminSuccess();
        var target = string.IsNullOrWhiteSpace(existing.Id)
            ? (string.IsNullOrWhiteSpace(existing.ClientId) ? clientId : existing.ClientId)
            : $"{existing.Id}:{existing.ClientId}";
        await AuditAsync("CLIENT:DELETE", realm, target, ct);
    }

    private async Task EnsureLocalRolesAsync(string realm, string clientUuid, IEnumerable<string> roles, CancellationToken ct)
    {
        var http = CreateAdminClient();

        var created = false;
        foreach (var name in roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var payload = new { name = name.Trim() };

                var (urlNew, urlLegacy) = BuildAdminUrls(realm, $"clients/{UR(clientUuid)}/roles");

                using var resp = await http.PostJsonWithLegacyFallbackAsync(urlNew, urlLegacy, payload, JsonOpts, ct);

                if (resp.StatusCode == HttpStatusCode.Conflict)
                {
                    continue;
                }

                resp.EnsureAdminSuccess();
                created = true;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Локальная роль '{name}' не назначена: {ex.Message}", ex);
            }
        }

        if (created)
        {
            InvalidateClientRolesCache(realm, clientUuid);
        }
    }

    private async Task AssignServiceRolesToServiceAccountAsync(string realm, string newClientUuid, IReadOnlyList<(string ClientId, string Role)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return;
        }

        var http = CreateAdminClient();

        var (getSvcUserNew, getSvcUserLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(newClientUuid)}/service-account-user");

        using var userResp = await http.GetWithLegacyFallbackAsync(getSvcUserNew, getSvcUserLegacy, ct);
        userResp.EnsureAdminSuccess();
        var svcUser = await ReadJsonAsync<KcUserRep>(userResp, ct) ?? throw new InvalidOperationException("Service account user not found.");
        if (string.IsNullOrWhiteSpace(svcUser.Id))
        {
            throw new InvalidOperationException("Service account user id is empty.");
        }

        var groups = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
            .GroupBy(p => p.ClientId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }

        var clientLookups = await Task.WhenAll(groups.Select(async group =>
        {
            var client = await GetClientShortByClientIdAsync(realm, group.Key, ct);
            return (group.Key, Client: client);
        }));

        var clients = new Dictionary<string, ClientShort>(StringComparer.OrdinalIgnoreCase);
        foreach (var (clientId, client) in clientLookups)
        {
            if (client == null)
            {
                throw new InvalidOperationException($"Client '{clientId}' not found.");
            }

            clients[clientId] = client;
        }

        var roleMaps = await Task.WhenAll(clients.Select(async kvp =>
        {
            var map = await GetClientRoleMapCachedAsync(http, realm, kvp.Value.Id, ct);
            return (kvp.Key, Roles: map);
        }));

        var roleLookup = roleMaps.ToDictionary(x => x.Key, x => x.Roles, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var srcClientId = group.Key;
            var srcClient = clients[srcClientId];

            var (mapNewBase, mapLegacyBase) = BuildAdminUrls(
                realm,
                $"users/{UR(svcUser.Id!)}/role-mappings/clients/{UR(srcClient.Id)}");

            foreach (var roleName in group.Select(x => x.Role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!roleLookup.TryGetValue(srcClientId, out var availableRoles) ||
                    !availableRoles.TryGetValue(roleName, out var rep))
                {
                    throw new InvalidOperationException($"Роль '{roleName}' клиента '{srcClientId}' не найдена.");
                }

                try
                {
                    using var mapResp = await http.PostJsonWithLegacyFallbackAsync(mapNewBase, mapLegacyBase, new[] { rep }, JsonOpts, ct);
                    mapResp.EnsureAdminSuccess();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Сервисная роль '{roleName}' клиента '{srcClientId}' не назначена: {ex.Message}", ex);
                }
            }
        }

        InvalidateServiceRolesCache(realm, newClientUuid);

        foreach (var client in clients.Values)
        {
            InvalidateClientRoleMapCache(realm, client.Id);
        }
    }

    private async Task RemoveServiceRolesFromServiceAccountAsync(string realm, string clientUuid, IReadOnlyList<(string ClientId, string Role)> pairs, CancellationToken ct)
    {
        if (pairs.Count == 0)
        {
            return;
        }

        var http = CreateAdminClient();

        var (getSvcUserNew, getSvcUserLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(clientUuid)}/service-account-user");

        using var userResp = await http.GetWithLegacyFallbackAsync(getSvcUserNew, getSvcUserLegacy, ct);
        if (userResp.StatusCode == HttpStatusCode.NotFound)
        {
            return;
        }

        userResp.EnsureAdminSuccess();
        var svcUser = await ReadJsonAsync<KcUserRep>(userResp, ct);
        if (svcUser?.Id == null)
        {
            return;
        }

        var groups = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
            .GroupBy(p => p.ClientId.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (groups.Count == 0)
        {
            return;
        }

        var clientLookups = await Task.WhenAll(groups.Select(async group =>
        {
            var client = await GetClientShortByClientIdAsync(realm, group.Key, ct);
            return (group.Key, Client: client);
        }));

        var clients = clientLookups
            .Where(x => x.Client != null)
            .ToDictionary(x => x.Key, x => x.Client!, StringComparer.OrdinalIgnoreCase);

        if (clients.Count == 0)
        {
            return;
        }

        var roleMaps = await Task.WhenAll(clients.Select(async kvp =>
        {
            var map = await GetClientRoleMapCachedAsync(http, realm, kvp.Value.Id, ct);
            return (kvp.Key, Roles: map);
        }));

        var roleLookup = roleMaps.ToDictionary(x => x.Key, x => x.Roles, StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var srcClientId = group.Key;

            if (!clients.TryGetValue(srcClientId, out var srcClient))
            {
                continue;
            }

            var (mapNewBase, mapLegacyBase) = BuildAdminUrls(
                realm,
                $"users/{UR(svcUser.Id)}/role-mappings/clients/{UR(srcClient.Id)}");

            foreach (var roleName in group.Select(x => x.Role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!roleLookup.TryGetValue(srcClientId, out var availableRoles) ||
                    !availableRoles.TryGetValue(roleName, out var rep))
                {
                    continue;
                }

                try
                {
                    using var deleteResp = await http.DeleteJsonWithLegacyFallbackAsync(mapNewBase, mapLegacyBase, new[] { rep }, JsonOpts, ct);
                    if (deleteResp.StatusCode == HttpStatusCode.NotFound)
                    {
                        continue;
                    }

                    deleteResp.EnsureAdminSuccess();
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Сервисная роль '{roleName}' клиента '{srcClientId}' не удалена: {ex.Message}", ex);
                }
            }
        }

        InvalidateServiceRolesCache(realm, clientUuid);

        foreach (var client in clients.Values)
        {
            InvalidateClientRoleMapCache(realm, client.Id);
        }
    }

    private async Task<List<(string ClientId, string Role)>> GetServiceAccountRolesAsync(string realm, string clientUuid, CancellationToken ct)
    {
        var http = CreateAdminClient();

        var (getSvcUserNew, getSvcUserLegacy) =
            BuildAdminUrls(realm, $"clients/{UR(clientUuid)}/service-account-user");

        using var userResp = await http.GetWithLegacyFallbackAsync(getSvcUserNew, getSvcUserLegacy, ct);
        userResp.EnsureAdminSuccess();
        var svcUser = await ReadJsonAsync<KcUserRep>(userResp, ct);
        if (svcUser?.Id == null)
        {
            return new List<(string, string)>();
        }

        var (mapUrlNew, mapUrlLegacy) =
            BuildAdminUrls(realm, $"users/{UR(svcUser.Id)}/role-mappings");

        using var mapResp = await http.GetWithLegacyFallbackAsync(mapUrlNew, mapUrlLegacy, ct);
        mapResp.EnsureAdminSuccess();
        var mappings = await ReadJsonAsync<RoleMappingsRep>(mapResp, ct);

        var list = new List<(string ClientId, string Role)>();
        if (mappings?.ClientMappings != null)
        {
            foreach (var kv in mappings.ClientMappings)
            {
                var roles = kv.Value.Mappings;
                if (roles == null)
                {
                    continue;
                }

                foreach (var role in roles)
                {
                    if (!string.IsNullOrWhiteSpace(role.Name))
                    {
                        list.Add((kv.Key, role.Name!));
                    }
                }
            }
        }

        return list;
    }

    private Task<List<string>> GetClientRolePreviewAsync(string realm, string clientUuid, CancellationToken ct)
    {
        var cacheKey = BuildClientRolesCacheKey(realm, clientUuid);
        return _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ClientRolesCacheDuration;
            return await GetClientRolesAsync(realm, clientUuid, 0, ClientDetailsRolePreviewLimit, null, ct);
        }) ?? Task.FromResult(new List<string>());
    }

    private Task<List<(string ClientId, string Role)>> GetServiceAccountRolesCachedAsync(string realm, string clientUuid, CancellationToken ct)
    {
        var cacheKey = BuildServiceRolesCacheKey(realm, clientUuid);
        return _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ServiceRolesCacheDuration;
            return await GetServiceAccountRolesAsync(realm, clientUuid, ct);
        }) ?? Task.FromResult(new List<(string, string)>());
    }

    private static string BuildClientRolesCacheKey(string realm, string clientUuid)
        => $"client-roles:{realm}:{clientUuid}:{ClientDetailsRolePreviewLimit}";

    private static string BuildClientRoleMapCacheKey(string realm, string clientUuid)
        => $"client-role-map:{realm}:{clientUuid}";

    private static string BuildServiceRolesCacheKey(string realm, string clientUuid)
        => $"service-roles:{realm}:{clientUuid}";

    private void InvalidateClientRolesCache(string realm, string clientUuid)
    {
        _cache.Remove(BuildClientRolesCacheKey(realm, clientUuid));
        InvalidateClientRoleMapCache(realm, clientUuid);
    }

    private void InvalidateServiceRolesCache(string realm, string clientUuid)
        => _cache.Remove(BuildServiceRolesCacheKey(realm, clientUuid));

    private void InvalidateClientRoleMapCache(string realm, string clientUuid)
        => _cache.Remove(BuildClientRoleMapCacheKey(realm, clientUuid));

    private Task<Dictionary<string, KcRoleRep>> GetClientRoleMapCachedAsync(HttpClient http, string realm, string clientUuid, CancellationToken ct)
    {
        var cacheKey = BuildClientRoleMapCacheKey(realm, clientUuid);
        return _cache.GetOrCreateAsync(cacheKey, async entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = ClientRoleMapCacheDuration;
            return await GetClientRoleMapAsync(http, realm, clientUuid, ct);
        }) ?? Task.FromResult(new Dictionary<string, KcRoleRep>(StringComparer.OrdinalIgnoreCase));
    }

    private async Task<Dictionary<string, KcRoleRep>> GetClientRoleMapAsync(HttpClient http, string realm, string clientUuid, CancellationToken ct)
    {
        var roles = new Dictionary<string, KcRoleRep>(StringComparer.OrdinalIgnoreCase);
        var first = 0;
        const int pageSize = 200;

        while (true)
        {
            var (urlNew, urlLegacy) = BuildAdminUrls(realm, $"clients/{UR(clientUuid)}/roles?first={first}&max={pageSize}");

            using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
            resp.EnsureAdminSuccess();
            var batch = await ReadJsonAsync<List<KcRoleRep>>(resp, ct) ?? new List<KcRoleRep>();
            if (batch.Count == 0)
            {
                break;
            }

            foreach (var role in batch)
            {
                if (string.IsNullOrWhiteSpace(role.Name))
                {
                    continue;
                }

                role.ClientRole = true;
                role.ContainerId = clientUuid;
                roles[role.Name!] = role;
            }

            if (batch.Count < pageSize)
            {
                break;
            }

            first += batch.Count;
        }

        return roles;
    }

    private static List<ClientShort> FilterExcluded(IEnumerable<ClientShort> source, ISet<string> excluded)
    {
        if (excluded.Count == 0)
        {
            return source as List<ClientShort> ?? source.ToList();
        }

        return source.Where(c => !excluded.Contains(c.ClientId)).ToList();
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpResponseMessage resp, CancellationToken ct)
        => await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);

    private sealed class ServiceRolePairComparer : IEqualityComparer<(string ClientId, string Role)>
    {
        public static ServiceRolePairComparer Instance { get; } = new();

        public bool Equals((string ClientId, string Role) x, (string ClientId, string Role) y)
            => string.Equals(x.ClientId, y.ClientId, StringComparison.OrdinalIgnoreCase)
               && string.Equals(x.Role, y.Role, StringComparison.OrdinalIgnoreCase);

        public int GetHashCode((string ClientId, string Role) obj)
        {
            var hash = new HashCode();
            hash.Add(obj.ClientId, StringComparer.OrdinalIgnoreCase);
            hash.Add(obj.Role, StringComparer.OrdinalIgnoreCase);
            return hash.ToHashCode();
        }
    }
}
