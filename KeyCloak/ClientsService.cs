// Assistant/KeyCloak/ClientsService.cs
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using System.Collections.Generic;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Assistant.KeyCloak
{
    public sealed record ClientShort(string Id, string ClientId);

    public sealed record ClientDetails(
        string Id,
        string ClientId,
        bool Enabled,
        string? Description,
        bool ClientAuth,
        bool StandardFlow,
        bool ServiceAccount,
        List<string> RedirectUris,
        List<string> LocalRoles,
        List<(string ClientId, string Role)> ServiceRoles,
        List<string> DefaultScopes
    );

    // Ответы KC (берём только нужные поля)
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

    public sealed record RoleHit(string ClientUuid, string ClientId, string Role);

    public sealed record NewClientSpec(
        string Realm,
        string ClientId,
        string? Description,
        bool ClientAuthentication,      // true => confidential (publicClient=false)
        bool StandardFlow,
        bool ServiceAccount,
        List<string> RedirectUris,
        List<string> LocalRoles,
        List<(string ClientId, string Role)> ServiceRoles
    );

    public sealed record UpdateClientSpec(
        string Realm,
        string CurrentClientId,
        string ClientId,
        bool Enabled,
        string? Description,
        bool ClientAuth,
        bool StandardFlow,
        bool ServiceAccount,
        List<string> RedirectUris,
        List<string> LocalRoles,
        List<(string ClientId, string Role)> ServiceRoles
    );

    /// <summary>
    /// Работа с Keycloak Admin API: поиск/роли и создание клиента (без кэша).
    /// </summary>
    public class ClientsService
    {
        private readonly IHttpClientFactory _factory;
        private readonly AdminApiOptions _opt;
        private readonly ServiceRoleExclusionsRepository _exclusions;
        private readonly ApiLogRepository _logs;
        private readonly IHttpContextAccessor _httpContextAccessor;

        public ClientsService(
            IHttpClientFactory factory,
            IOptions<AdminApiOptions> opt,
            ServiceRoleExclusionsRepository exclusions,
            ApiLogRepository logs,
            IHttpContextAccessor httpContextAccessor)
        {
            _factory = factory;
            _opt = opt.Value;
            _exclusions = exclusions;
            _logs = logs;
            _httpContextAccessor = httpContextAccessor;
        }

        private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

        private string ResolveUsername()
        {
            var username = _httpContextAccessor.HttpContext?.User?.Identity?.Name;
            return string.IsNullOrWhiteSpace(username) ? "unknown" : username;
        }

        private Task AuditAsync(string operationType, string realm, string targetId, CancellationToken ct)
        {
            realm = string.IsNullOrWhiteSpace(realm) ? "-" : realm;
            targetId = string.IsNullOrWhiteSpace(targetId) ? "-" : targetId;
            return _logs.LogAsync(operationType, ResolveUsername(), realm, targetId, ct);
        }

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        private static string UR(string s) => Uri.EscapeDataString(s);

        // ======= Public: Search/List/Roles =======

        /// <summary>
        /// Поиск клиентов по подстроке в clientId (CI). Возвращает только Id и ClientId.
        /// </summary>
        public async Task<List<ClientShort>> SearchClientsAsync(
            string realm, string query, int first = 0, int max = 20, CancellationToken ct = default)
        {
            query = (query ?? "").Trim();
            if (string.IsNullOrEmpty(query))
            {
                return new();
            }
            first = Math.Max(0, first);
            max = Math.Clamp(max <= 0 ? 20 : max, 1, 200);

            var http = _factory.CreateClient("kc-admin");
            var excluded = await _exclusions.GetAllAsync(ct);

            // 1) Точное clientId=
            var urlExactNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?clientId={UR(query)}&briefRepresentation=true";
            var urlExactLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?clientId={UR(query)}&briefRepresentation=true";

            var resp = await GetAsyncWithFallback(http, urlExactNew, urlExactLegacy, ct);
            EnsureAuthOrThrow(resp);
            var exact = await ReadJson<List<ClientRep>>(resp, ct) ?? new();

            static bool ContainsCi(string? s, string needle) =>
                !string.IsNullOrEmpty(s) && s.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0;

            var mappedExact = exact
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(MapClient)
                .Where(c => ContainsCi(c.ClientId, query))
                .ToList();
            mappedExact = FilterExcluded(mappedExact, excluded);

            if (mappedExact.Count > 0)
            {
                return mappedExact;
            }

            // 2) search= + пагинация
            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?search={UR(query)}&first={first}&max={max}&briefRepresentation=true";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?search={UR(query)}&first={first}&max={max}&briefRepresentation=true";

            var resp2 = await GetAsyncWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp2);
            var list = await ReadJson<List<ClientRep>>(resp2, ct) ?? new();

            var mapped = list
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(MapClient)
                .Where(c => ContainsCi(c.ClientId, query))
                .ToList();

            var filtered = FilterExcluded(mapped, excluded);
            return filtered;

            static ClientShort MapClient(ClientRep c) => new ClientShort(
                c.Id ?? string.Empty,
                c.ClientId ?? string.Empty);
        }

        /// <summary>
        /// Плоский листинг клиентов (для сканирования при поиске роли, когда клиент неизвестен).
        /// </summary>
        public async Task<(List<ClientShort> Clients, int TotalFetched)> ListClientsAsync(string realm, int first = 0, int max = 50, CancellationToken ct = default)
        {
            first = Math.Max(0, first);
            max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

            var http = _factory.CreateClient("kc-admin");
            var excluded = await _exclusions.GetAllAsync(ct);

            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?first={first}&max={max}&briefRepresentation=true";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?first={first}&max={max}&briefRepresentation=true";

            var resp = await GetAsyncWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp);
            var list = await ReadJson<List<ClientRep>>(resp, ct) ?? new();

            var mapped = list
                .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
                .Select(c => new ClientShort(c.Id ?? string.Empty, c.ClientId ?? string.Empty))
                .ToList();
            var total = mapped.Count;
            var filtered = FilterExcluded(mapped, excluded);
            await AuditAsync("client:list", realm, $"{first}:{max}", ct);
            return (filtered, total);
        }

        /// <summary>
        /// Полные сведения о клиенте вместе с ролями и redirect URIs.
        /// </summary>
        public async Task<ClientDetails?> GetClientDetailsAsync(string realm, string clientId, CancellationToken ct = default)
        {
            var http = _factory.CreateClient("kc-admin");

            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?clientId={UR(clientId)}";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?clientId={UR(clientId)}";

            using var resp = await GetAsyncWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp);
            var list = await ReadJson<List<ClientFullRep>>(resp, ct) ?? new();
            var rep = list.FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
            if (rep == null || string.IsNullOrWhiteSpace(rep.Id) || string.IsNullOrWhiteSpace(rep.ClientId))
            {
                return null;
            }

            var localRoles = await GetClientRolesAsync(realm, rep.Id!, 0, 1000, null, ct);
            var svcRoles = (rep.ServiceAccountsEnabled ?? false)
                ? await GetServiceAccountRolesAsync(realm, rep.Id!, ct)
                : new List<(string ClientId, string Role)>();
            var defaultScopes = rep.DefaultClientScopes?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).ToList() ?? new();

            var details = new ClientDetails(
                rep.Id!,
                rep.ClientId!,
                rep.Enabled ?? false,
                rep.Description,
                !(rep.PublicClient ?? true),
                rep.StandardFlowEnabled ?? false,
                rep.ServiceAccountsEnabled ?? false,
                rep.RedirectUris?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToList() ?? new(),
                localRoles,
                svcRoles,
                defaultScopes
            );
            return details;
        }

        public async Task<string?> GetClientSecretAsync(string realm, string clientId, CancellationToken ct = default)
        {
            var details = await GetClientDetailsAsync(realm, clientId, ct);
            if (details == null)
            {
                return null;
            }

            var http = _factory.CreateClient("kc-admin");
            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";

            using var resp = await GetAsyncWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp);
            var rep = await ReadJson<ClientSecretRep>(resp, ct);
            var secret = rep?.Value;
            return secret;
        }

        public async Task<string?> RegenerateClientSecretAsync(string realm, string clientId, CancellationToken ct = default)
        {
            var details = await GetClientDetailsAsync(realm, clientId, ct);
            if (details == null)
            {
                await AuditAsync("client-secret:regenerate", realm, clientId, ct);
                return null;
            }

            var http = _factory.CreateClient("kc-admin");
            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";

            using var resp = await PostWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp);
            var rep = await ReadJson<ClientSecretRep>(resp, ct);
            var secret = rep?.Value;
            await AuditAsync("client-secret:regenerate", realm, details.ClientId, ct);
            return secret;
        }

        /// <summary>
        /// Роли клиента (с опциональным server-side search по названию роли).
        /// </summary>
        public async Task<List<string>> GetClientRolesAsync(
            string realm, string clientUuid, int first = 0, int max = 50, string? search = null, CancellationToken ct = default)
        {
            first = Math.Max(0, first);
            max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

            var http = _factory.CreateClient("kc-admin");
            var qs = $"briefRepresentation=true&first={first}&max={max}" +
                     (string.IsNullOrWhiteSpace(search) ? "" : $"&search={UR(search!)}");

            var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles?{qs}";
            var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles?{qs}";

            var resp = await GetAsyncWithFallback(http, urlNew, urlLegacy, ct);
            EnsureAuthOrThrow(resp);
            var roles = await ReadJson<List<RoleRep>>(resp, ct) ?? new();

            var list = roles.Select(r => r.Name)
                             .Where(n => !string.IsNullOrWhiteSpace(n))
                             .Select(n => n!)
                             .ToList();
            return list;
        }

        /// <summary>
        /// Поиск ролей по подстроке, когда клиент неизвестен: сканируем пачку клиентов и берём роли с search=.
        /// Возвращаем совпадения и смещение для следующей пачки.
        /// </summary>
        public async Task<(List<RoleHit> Hits, int NextClientFirst)> FindRolesAcrossClientsAsync(
            string realm, string roleQuery, int clientFirst = 0, int clientsToScan = 25, int rolesPerClient = 10, CancellationToken ct = default)
        {
            roleQuery = (roleQuery ?? "").Trim();
            if (roleQuery.Length == 0) return (new(), -1);

            var (clients, fetched) = await ListClientsAsync(realm, clientFirst, clientsToScan, ct);
            var hits = new List<RoleHit>();

            foreach (var c in clients)
            {
                var roles = await GetClientRolesAsync(realm, c.Id, 0, rolesPerClient, roleQuery, ct);
                foreach (var r in roles)
                    hits.Add(new RoleHit(c.Id, c.ClientId, r));
            }

            var next = fetched < clientsToScan ? -1 : clientFirst + fetched;
            return (hits, next);
        }

        private static List<ClientShort> FilterExcluded(List<ClientShort> source, HashSet<string> excluded)
        {
            if (excluded.Count == 0) return source;
            source.RemoveAll(c => excluded.Contains(c.ClientId));
            return source;
        }

        private async Task<List<(string ClientId, string Role)>> GetServiceAccountRolesAsync(string realm, string clientUuid, CancellationToken ct)
        {
            var http = _factory.CreateClient("kc-admin");

            var getSvcUserNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/service-account-user";
            var getSvcUserLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/service-account-user";

            using var userResp = await GetAsyncWithFallback(http, getSvcUserNew, getSvcUserLegacy, ct);
            EnsureAuthOrThrow(userResp);
            var svcUser = await ReadJson<KcUserRep>(userResp, ct);
            userResp.Dispose();
            if (svcUser?.Id == null)
            {
                return new();
            }

            var mapUrlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/users/{UR(svcUser.Id)}/role-mappings";
            var mapUrlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/users/{UR(svcUser.Id)}/role-mappings";

            using var mapResp = await GetAsyncWithFallback(http, mapUrlNew, mapUrlLegacy, ct);
            EnsureAuthOrThrow(mapResp);
            var mappings = await ReadJson<RoleMappingsRep>(mapResp, ct);
            mapResp.Dispose();

            var list = new List<(string ClientId, string Role)>();
            if (mappings?.ClientMappings != null)
            {
                foreach (var kv in mappings.ClientMappings)
                {
                    var roles = kv.Value.Mappings;
                    if (roles == null) continue;
                    foreach (var r in roles)
                        if (!string.IsNullOrWhiteSpace(r.Name))
                            list.Add((kv.Key, r.Name!));
                }
            }
            return list;
        }

        // ======= Public: Create client (+ roles + service roles) =======

        public async Task<string> CreateClientAsync(NewClientSpec spec, CancellationToken ct = default)
        {
            var http = _factory.CreateClient("kc-admin");

            // 1) Создаём клиента
            var body = new
            {
                clientId = spec.ClientId,
                protocol = "openid-connect",
                publicClient = !spec.ClientAuthentication,
                serviceAccountsEnabled = spec.ServiceAccount,
                standardFlowEnabled = spec.StandardFlow,
                directAccessGrantsEnabled = false,
                redirectUris = spec.RedirectUris?.Distinct().ToArray() ?? Array.Empty<string>(),
                description = spec.Description
            };

            var postNew = $"{BaseUrl}/admin/realms/{UR(spec.Realm)}/clients";
            var postLegacy = $"{BaseUrl}/auth/admin/realms/{UR(spec.Realm)}/clients";

            var createResp = await PostJsonWithFallback(http, postNew, postLegacy, body, ct);
            if (createResp.StatusCode == HttpStatusCode.Conflict)
                throw new InvalidOperationException($"Client '{spec.ClientId}' already exists.");
            EnsureAuthOrThrow(createResp);

            // извлекаем id из Location
            string? createdId = null;
            if (createResp.Headers.Location is Uri loc)
            {
                var seg = loc.Segments.LastOrDefault();
                if (!string.IsNullOrWhiteSpace(seg)) createdId = seg.TrimEnd('/');
            }
            createResp.Dispose();

            // fallback: найдём по clientId, если Location не пришёл
            createdId ??= (await SearchClientsAsync(spec.Realm, spec.ClientId, 0, 1, ct))
                .FirstOrDefault(c => string.Equals(c.ClientId, spec.ClientId, StringComparison.OrdinalIgnoreCase))?.Id
                ?? throw new InvalidOperationException("Cannot resolve created client id.");

            // 2) Локальные роли
            try
            {
                if (spec.LocalRoles?.Count > 0)
                    await EnsureLocalRolesAsync(spec.Realm, createdId, spec.LocalRoles, ct);

                // 3) Service roles → назначаем на сервис-аккаунт нового клиента
                if (spec.ServiceAccount && spec.ServiceRoles?.Count > 0)
                    await AssignServiceRolesToServiceAccountAsync(spec.Realm, createdId, spec.ServiceRoles, ct);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Клиент '{spec.ClientId}' создан, но при назначении ролей произошла ошибка: {ex.Message}", ex);
            }

            await AuditAsync("client:create", spec.Realm, spec.ClientId, ct);
            return createdId;
        }

        public async Task UpdateClientAsync(UpdateClientSpec spec, CancellationToken ct = default)
        {
            var http = _factory.CreateClient("kc-admin");

            var existing = (await SearchClientsAsync(spec.Realm, spec.CurrentClientId, 0, 1, ct))
                .FirstOrDefault(c => string.Equals(c.ClientId, spec.CurrentClientId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Client '{spec.CurrentClientId}' not found.");

            var body = new
            {
                clientId = spec.ClientId,
                enabled = spec.Enabled,
                publicClient = !spec.ClientAuth,
                serviceAccountsEnabled = spec.ServiceAccount,
                standardFlowEnabled = spec.StandardFlow,
                directAccessGrantsEnabled = false,
                redirectUris = spec.StandardFlow ? spec.RedirectUris?.Distinct().ToArray() ?? Array.Empty<string>() : Array.Empty<string>(),
                description = spec.Description
            };

            var putNew = $"{BaseUrl}/admin/realms/{UR(spec.Realm)}/clients/{UR(existing.Id)}";
            var putLegacy = $"{BaseUrl}/auth/admin/realms/{UR(spec.Realm)}/clients/{UR(existing.Id)}";

            using var resp = await PutJsonWithFallback(http, putNew, putLegacy, body, ct);
            EnsureAuthOrThrow(resp);

            if (spec.LocalRoles?.Count > 0)
                await EnsureLocalRolesAsync(spec.Realm, existing.Id, spec.LocalRoles, ct);

            if (spec.ServiceAccount && spec.ServiceRoles?.Count > 0)
                await AssignServiceRolesToServiceAccountAsync(spec.Realm, existing.Id, spec.ServiceRoles, ct);

            await AuditAsync("client:update", spec.Realm, spec.ClientId, ct);
        }

        public async Task DeleteClientAsync(string realm, string clientId, CancellationToken ct = default)
        {
            var http = _factory.CreateClient("kc-admin");

            var existing = (await SearchClientsAsync(realm, clientId, 0, 1, ct))
                .FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException($"Client '{clientId}' not found.");

            var delNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(existing.Id)}";
            var delLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(existing.Id)}";

            using var resp = await DeleteWithFallback(http, delNew, delLegacy, ct);
            EnsureAuthOrThrow(resp);
            var target = string.IsNullOrWhiteSpace(existing.Id)
                ? (string.IsNullOrWhiteSpace(existing.ClientId) ? clientId : existing.ClientId)
                : $"{existing.Id}:{existing.ClientId}";
            await AuditAsync("client:delete", realm, target, ct);
        }

        // ======= Internal helpers for Create =======

        private async Task EnsureLocalRolesAsync(string realm, string clientUuid, IEnumerable<string> roles, CancellationToken ct)
        {
            var http = _factory.CreateClient("kc-admin");

            foreach (var name in roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var payload = new { name = name.Trim() };

                    var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles";
                    var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles";

                    using var resp = await PostJsonWithFallback(http, urlNew, urlLegacy, payload, ct);

                    if (resp.StatusCode == HttpStatusCode.Conflict)
                    {
                        // роль уже существует — ок
                        await AuditAsync("client-role:ensure", realm, $"{clientUuid}:{name}", ct);
                        continue;
                    }
                    EnsureAuthOrThrow(resp);
                    await AuditAsync("client-role:ensure", realm, $"{clientUuid}:{name}", ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Локальная роль '{name}' не назначена: {ex.Message}", ex);
                }
            }
        }

        private async Task AssignServiceRolesToServiceAccountAsync(string realm, string newClientUuid, List<(string ClientId, string Role)> pairs, CancellationToken ct)
        {
            var http = _factory.CreateClient("kc-admin");

            // userId сервис-аккаунта нового клиента
            var getSvcUserNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(newClientUuid)}/service-account-user";
            var getSvcUserLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(newClientUuid)}/service-account-user";

            var userResp = await GetAsyncWithFallback(http, getSvcUserNew, getSvcUserLegacy, ct);
            EnsureAuthOrThrow(userResp);
            var svcUser = await ReadJson<KcUserRep>(userResp, ct) ?? throw new InvalidOperationException("Service account user not found.");
            userResp.Dispose();
            if (string.IsNullOrWhiteSpace(svcUser.Id)) throw new InvalidOperationException("Service account user id is empty.");

            // Группируем роли по исходному клиенту: clientId → [roleName]
            var groups = pairs
                .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
                .GroupBy(p => p.ClientId.Trim(), StringComparer.OrdinalIgnoreCase);

            foreach (var g in groups)
            {
                var srcClientId = g.Key;

                // найдём UUID исходного клиента по его clientId
                var srcClient = (await SearchClientsAsync(realm, srcClientId, 0, 2, ct))
                    .FirstOrDefault(c => string.Equals(c.ClientId, srcClientId, StringComparison.OrdinalIgnoreCase));
                if (srcClient == null) throw new InvalidOperationException($"Client '{srcClientId}' not found.");

                var mapNewBase = $"{BaseUrl}/admin/realms/{UR(realm)}/users/{UR(svcUser.Id!)}/role-mappings/clients/{UR(srcClient.Id)}";
                var mapLegacyBase = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/users/{UR(svcUser.Id!)}/role-mappings/clients/{UR(srcClient.Id)}";

                foreach (var roleName in g.Select(x => x.Role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    try
                    {
                        var getRoleNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(srcClient.Id)}/roles/{UR(roleName)}";
                        var getRoleLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(srcClient.Id)}/roles/{UR(roleName)}";

                        using var rr = await GetAsyncWithFallback(http, getRoleNew, getRoleLegacy, ct);
                        EnsureAuthOrThrow(rr);
                        var rep = await ReadJson<KcRoleRep>(rr, ct) ?? new KcRoleRep { Name = roleName };
                        rep.ClientRole = true;
                        rep.ContainerId = srcClient.Id;

                        using var mapResp = await PostJsonWithFallback(http, mapNewBase, mapLegacyBase, new[] { rep }, ct);
                        EnsureAuthOrThrow(mapResp);
                        await AuditAsync("service-account:role-assign", realm, $"{newClientUuid}:{srcClientId}:{roleName}", ct);
                    }
                    catch (Exception ex)
                    {
                        throw new InvalidOperationException($"Сервисная роль '{roleName}' клиента '{srcClientId}' не назначена: {ex.Message}", ex);
                    }
                }
            }
        }

        // ======= HTTP helpers (new → legacy fallback) =======

        private static async Task<HttpResponseMessage> GetAsyncWithFallback(HttpClient http, string newUrl, string legacyUrl, CancellationToken ct)
        {
            var resp = await http.GetAsync(newUrl, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.GetAsync(legacyUrl, ct);
            }
            return resp;
        }

        private static async Task<HttpResponseMessage> PostJsonWithFallback(HttpClient http, string newUrl, string legacyUrl, object body, CancellationToken ct)
        {
            var resp = await http.PostAsJsonAsync(newUrl, body, JsonOpts, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.PostAsJsonAsync(legacyUrl, body, JsonOpts, ct);
            }
            return resp;
        }

        private static async Task<HttpResponseMessage> PutJsonWithFallback(HttpClient http, string newUrl, string legacyUrl, object body, CancellationToken ct)
        {
            var resp = await http.PutAsJsonAsync(newUrl, body, JsonOpts, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.PutAsJsonAsync(legacyUrl, body, JsonOpts, ct);
            }
            return resp;
        }

        private static async Task<HttpResponseMessage> PostWithFallback(HttpClient http, string newUrl, string legacyUrl, CancellationToken ct)
        {
            var resp = await http.PostAsync(newUrl, null, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.PostAsync(legacyUrl, null, ct);
            }
            return resp;
        }

        private static async Task<HttpResponseMessage> DeleteWithFallback(HttpClient http, string newUrl, string legacyUrl, CancellationToken ct)
        {
            var resp = await http.DeleteAsync(newUrl, ct);
            if (resp.StatusCode == HttpStatusCode.NotFound)
            {
                resp.Dispose();
                resp = await http.DeleteAsync(legacyUrl, ct);
            }
            return resp;
        }

        private static void EnsureAuthOrThrow(HttpResponseMessage resp)
        {
            if (resp.StatusCode == HttpStatusCode.Forbidden)
                throw new UnauthorizedAccessException("Недостаточно прав для операции (нужны права realm-management).");

            if (resp.StatusCode == HttpStatusCode.BadRequest)
            {
                var body = resp.Content.ReadAsStringAsync().ConfigureAwait(false).GetAwaiter().GetResult();
                throw new InvalidOperationException($"Запрос отклонён (400). Детали: {body}");
            }

            resp.EnsureSuccessStatusCode();
        }

        private static async Task<T?> ReadJson<T>(HttpResponseMessage resp, CancellationToken ct)
            => await resp.Content.ReadFromJsonAsync<T>(JsonOpts, ct);
    }
}
