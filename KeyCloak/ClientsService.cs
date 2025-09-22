using Assistant.KeyCloak.Models;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
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

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
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
        public string? FirstName { get; set; }
        public string? LastName { get; set; }
        public string? Email { get; set; }
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
        IHttpContextAccessor httpContextAccessor)
    {
        _factory = factory;
        _opt = opt.Value;
        _exclusions = exclusions;
        _logs = logs;
        _httpContextAccessor = httpContextAccessor;
    }

    private HttpClient CreateAdminClient() => _factory.CreateClient("kc-admin");

    private string BaseUrl => _opt.BaseUrl.TrimEnd('/');

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
        var afterServiceRoles = after.ServiceRoles
            .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
            .Select(p => $"{p.ClientId.Trim()}:{p.Role.Trim()}")
            .ToList();
        AddSetChange("serviceRoles", beforeServiceRoles, afterServiceRoles, includeRemoved: false);

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

        var http = CreateAdminClient();
        var excluded = await _exclusions.GetAllAsync(ct);

        var urlExactNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?clientId={UR(query)}&briefRepresentation=true";
        var urlExactLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?clientId={UR(query)}&briefRepresentation=true";

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
                return filteredExact;
            }
        }

        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?search={UR(query)}&first={first}&max={max}&briefRepresentation=true";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?search={UR(query)}&first={first}&max={max}&briefRepresentation=true";

        using var resp2 = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp2.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientRep>>(resp2, ct) ?? new();

        var mapped = list
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .Select(MapClient)
            .Where(c => c.ClientId.IndexOf(query, StringComparison.OrdinalIgnoreCase) >= 0)
            .ToList();

        return FilterExcluded(mapped, excluded);

        static ClientShort MapClient(ClientRep c) => new(c.Id ?? string.Empty, c.ClientId ?? string.Empty);
    }

    public async Task<(List<ClientShort> Clients, int TotalFetched)> ListClientsAsync(
        string realm, int first = 0, int max = 50, CancellationToken ct = default)
    {
        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

        var http = CreateAdminClient();
        var excluded = await _exclusions.GetAllAsync(ct);

        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?first={first}&max={max}&briefRepresentation=true";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?first={first}&max={max}&briefRepresentation=true";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientRep>>(resp, ct) ?? new();

        var mapped = list
            .Where(x => !string.IsNullOrWhiteSpace(x.ClientId))
            .Select(c => new ClientShort(c.Id ?? string.Empty, c.ClientId ?? string.Empty))
            .ToList();

        var filtered = FilterExcluded(mapped, excluded);
        await AuditAsync("client:list", realm, $"{first}:{max}", ct);
        return (filtered, mapped.Count);
    }

    public async Task<ClientDetails?> GetClientDetailsAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients?clientId={UR(clientId)}";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients?clientId={UR(clientId)}";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<ClientFullRep>>(resp, ct) ?? new();
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
            rep.RedirectUris?.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r!).ToList() ?? new List<string>(),
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

        var http = CreateAdminClient();
        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var rep = await ReadJsonAsync<ClientSecretRep>(resp, ct);
        return rep?.Value;
    }

    public async Task<string?> RegenerateClientSecretAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var details = await GetClientDetailsAsync(realm, clientId, ct);
        if (details == null)
        {
            await AuditAsync("client-secret:regenerate", realm, clientId, ct);
            return null;
        }

        var http = CreateAdminClient();
        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/client-secret";

        using var resp = await http.PostWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        resp.EnsureAdminSuccess();
        var rep = await ReadJsonAsync<ClientSecretRep>(resp, ct);
        var secret = rep?.Value;
        await AuditAsync("client-secret:regenerate", realm, details.ClientId, ct);
        return secret;
    }

    public async Task<TokenEvaluationResult?> EvaluateTokensAsync(string realm, string clientId, string username, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            throw new ArgumentException("Realm is required.", nameof(realm));
        }

        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new ArgumentException("ClientId is required.", nameof(clientId));
        }

        if (string.IsNullOrWhiteSpace(username))
        {
            throw new ArgumentException("Username is required.", nameof(username));
        }

        var details = await GetClientDetailsAsync(realm, clientId, ct);
        if (details == null)
        {
            return null;
        }

        var user = await FindUserByUsernameAsync(realm, username, ct);
        if (user == null || string.IsNullOrWhiteSpace(user.Id))
        {
            throw new InvalidOperationException($"Пользователь '{username}' не найден.");
        }

        var baseNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/evaluate-scopes";
        var baseLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(details.Id)}/evaluate-scopes";
        var userParam = $"user={UR(user.Id)}";

        var accessToken = await FetchExampleTokenAsync(
            $"{baseNew}/generate-example-access-token?{userParam}",
            $"{baseLegacy}/generate-example-access-token?{userParam}",
            ct);
        var idToken = await FetchExampleTokenAsync(
            $"{baseNew}/generate-example-id-token?{userParam}",
            $"{baseLegacy}/generate-example-id-token?{userParam}",
            ct);
        var userInfo = await FetchExampleUserInfoAsync(
            $"{baseNew}/generate-example-userinfo?{userParam}",
            $"{baseLegacy}/generate-example-userinfo?{userParam}",
            ct);

        await AuditAsync("client-token:evaluate", realm, details.ClientId, ct, $"username={user.Username}");

        return new TokenEvaluationResult(
            user.Id!,
            user.Username ?? username,
            FormatUserDisplay(user),
            accessToken.Raw,
            accessToken.Payload,
            idToken.Raw,
            idToken.Payload,
            userInfo);
    }

    private async Task<KcUserRep?> FindUserByUsernameAsync(string realm, string username, CancellationToken ct)
    {
        var http = CreateAdminClient();
        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/users?search={UR(username)}&first=0&max=20";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/users?search={UR(username)}&first=0&max=20";

        using var resp = await http.GetWithLegacyFallbackAsync(urlNew, urlLegacy, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureAdminSuccess();
        var list = await ReadJsonAsync<List<KcUserRep>>(resp, ct) ?? new List<KcUserRep>();

        foreach (var candidate in list)
        {
            if (candidate?.Username is null)
            {
                continue;
            }

            if (string.Equals(candidate.Username, username, StringComparison.OrdinalIgnoreCase))
            {
                return candidate;
            }
        }

        foreach (var candidate in list)
        {
            if (!string.IsNullOrWhiteSpace(candidate?.Username))
            {
                return candidate;
            }
        }

        return list.Count > 0 ? list[0] : null;
    }

    private async Task<(string? Raw, JsonElement? Payload)> FetchExampleTokenAsync(string modernUrl, string legacyUrl, CancellationToken ct)
    {
        var http = CreateAdminClient();
        using var resp = await http.PostWithLegacyFallbackAsync(modernUrl, legacyUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return (null, null);
        }

        resp.EnsureAdminSuccess();

        var content = await resp.Content.ReadAsStringAsync();
        var isJson = resp.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true;
        var token = ExtractTokenString(content, isJson);
        return (token, DecodeJwtPayload(token));
    }

    private async Task<JsonElement?> FetchExampleUserInfoAsync(string modernUrl, string legacyUrl, CancellationToken ct)
    {
        var http = CreateAdminClient();
        using var resp = await http.PostWithLegacyFallbackAsync(modernUrl, legacyUrl, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        resp.EnsureAdminSuccess();
        var content = await resp.Content.ReadAsStringAsync();
        return TryParseJsonElement(content);
    }

    private static string? ExtractTokenString(string? content, bool isJson)
    {
        if (string.IsNullOrWhiteSpace(content))
        {
            return null;
        }

        var trimmed = content.Trim();
        if (isJson || trimmed.StartsWith("{") || trimmed.StartsWith("\""))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                var root = doc.RootElement;
                if (root.ValueKind == JsonValueKind.String)
                {
                    return root.GetString();
                }

                foreach (var key in new[] { "access_token", "accessToken", "token", "value", "id_token", "idToken" })
                {
                    if (root.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String)
                    {
                        return prop.GetString();
                    }
                }

                return root.GetRawText();
            }
            catch
            {
                // ignore and fallback to raw string
            }
        }

        return trimmed.Trim('"');
    }

    private static JsonElement? DecodeJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split('.');
        if (parts.Length < 2)
        {
            return null;
        }

        var payload = parts[1];

        try
        {
            var normalized = payload.Replace('-', '+').Replace('_', '/');
            var padding = normalized.Length % 4;
            if (padding > 0)
            {
                normalized = normalized.PadRight(normalized.Length + (4 - padding), '=');
            }

            var bytes = Convert.FromBase64String(normalized);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryParseJsonElement(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string? FormatUserDisplay(KcUserRep user)
    {
        var username = (user.Username ?? string.Empty).Trim();
        var parts = new List<string>();

        if (!string.IsNullOrWhiteSpace(user.LastName))
        {
            parts.Add(user.LastName!.Trim());
        }

        if (!string.IsNullOrWhiteSpace(user.FirstName))
        {
            parts.Add(user.FirstName!.Trim());
        }

        var fullName = string.Join(" ", parts);
        var email = string.IsNullOrWhiteSpace(user.Email) ? null : user.Email!.Trim();

        if (!string.IsNullOrWhiteSpace(username))
        {
            if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(email))
            {
                return $"{username} — {fullName} ({email})";
            }

            if (!string.IsNullOrWhiteSpace(fullName))
            {
                return $"{username} — {fullName}";
            }

            if (!string.IsNullOrWhiteSpace(email))
            {
                return $"{username} — {email}";
            }

            return username;
        }

        if (!string.IsNullOrWhiteSpace(fullName) && !string.IsNullOrWhiteSpace(email))
        {
            return $"{fullName} ({email})";
        }

        if (!string.IsNullOrWhiteSpace(fullName))
        {
            return fullName;
        }

        return email;
    }

    public async Task<List<string>> GetClientRolesAsync(
        string realm, string clientUuid, int first = 0, int max = 50, string? search = null, CancellationToken ct = default)
    {
        first = Math.Max(0, first);
        max = Math.Clamp(max <= 0 ? 50 : max, 1, 200);

        var http = CreateAdminClient();
        var qs = $"briefRepresentation=true&first={first}&max={max}" +
                 (string.IsNullOrWhiteSpace(search) ? string.Empty : $"&search={UR(search!)}");

        var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles?{qs}";
        var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles?{qs}";

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

        foreach (var client in clients)
        {
            var roles = await GetClientRolesAsync(realm, client.Id, 0, rolesPerClient, roleQuery, ct);
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

        var postNew = $"{BaseUrl}/admin/realms/{UR(spec.Realm)}/clients";
        var postLegacy = $"{BaseUrl}/auth/admin/realms/{UR(spec.Realm)}/clients";

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

        await AuditAsync("CREATE", spec.Realm, spec.ClientId, ct);
        return createdId;
    }

    public async Task UpdateClientAsync(UpdateClientSpec spec, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var existingDetails = await GetClientDetailsAsync(spec.Realm, spec.CurrentClientId, ct)
            ?? throw new InvalidOperationException($"Client '{spec.CurrentClientId}' not found.");

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

        var putNew = $"{BaseUrl}/admin/realms/{UR(spec.Realm)}/clients/{UR(existingDetails.Id)}";
        var putLegacy = $"{BaseUrl}/auth/admin/realms/{UR(spec.Realm)}/clients/{UR(existingDetails.Id)}";

        using var resp = await http.PutJsonWithLegacyFallbackAsync(putNew, putLegacy, body, JsonOpts, ct);
        resp.EnsureAdminSuccess();

        if (spec.LocalRoles.Count > 0)
        {
            await EnsureLocalRolesAsync(spec.Realm, existingDetails.Id, spec.LocalRoles, ct);
        }

        if (spec.ServiceAccount && spec.ServiceRoles.Count > 0)
        {
            await AssignServiceRolesToServiceAccountAsync(spec.Realm, existingDetails.Id, spec.ServiceRoles, ct);
        }

        var diff = DescribeClientUpdateChanges(existingDetails, spec);
        await AuditAsync("client:update", spec.Realm, spec.ClientId, ct, diff);
    }

    public async Task DeleteClientAsync(string realm, string clientId, CancellationToken ct = default)
    {
        var http = CreateAdminClient();

        var existing = (await SearchClientsAsync(realm, clientId, 0, 1, ct))
            .FirstOrDefault(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"Client '{clientId}' not found.");

        var delNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(existing.Id)}";
        var delLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(existing.Id)}";

        using var resp = await http.DeleteWithLegacyFallbackAsync(delNew, delLegacy, ct);
        resp.EnsureAdminSuccess();
        var target = string.IsNullOrWhiteSpace(existing.Id)
            ? (string.IsNullOrWhiteSpace(existing.ClientId) ? clientId : existing.ClientId)
            : $"{existing.Id}:{existing.ClientId}";
        await AuditAsync("client:delete", realm, target, ct);
    }

    private async Task EnsureLocalRolesAsync(string realm, string clientUuid, IEnumerable<string> roles, CancellationToken ct)
    {
        var http = CreateAdminClient();

        foreach (var name in roles.Where(r => !string.IsNullOrWhiteSpace(r)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var payload = new { name = name.Trim() };

                var urlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles";
                var urlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/roles";

                using var resp = await http.PostJsonWithLegacyFallbackAsync(urlNew, urlLegacy, payload, JsonOpts, ct);

                if (resp.StatusCode == HttpStatusCode.Conflict)
                {
                    continue;
                }

                resp.EnsureAdminSuccess();
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Локальная роль '{name}' не назначена: {ex.Message}", ex);
            }
        }
    }

    private async Task AssignServiceRolesToServiceAccountAsync(string realm, string newClientUuid, IReadOnlyList<(string ClientId, string Role)> pairs, CancellationToken ct)
    {
        var http = CreateAdminClient();

        var getSvcUserNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(newClientUuid)}/service-account-user";
        var getSvcUserLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(newClientUuid)}/service-account-user";

        using var userResp = await http.GetWithLegacyFallbackAsync(getSvcUserNew, getSvcUserLegacy, ct);
        userResp.EnsureAdminSuccess();
        var svcUser = await ReadJsonAsync<KcUserRep>(userResp, ct) ?? throw new InvalidOperationException("Service account user not found.");
        if (string.IsNullOrWhiteSpace(svcUser.Id))
        {
            throw new InvalidOperationException("Service account user id is empty.");
        }

        var groups = pairs
            .Where(p => !string.IsNullOrWhiteSpace(p.ClientId) && !string.IsNullOrWhiteSpace(p.Role))
            .GroupBy(p => p.ClientId.Trim(), StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var srcClientId = group.Key;

            var srcClient = (await SearchClientsAsync(realm, srcClientId, 0, 2, ct))
                .FirstOrDefault(c => string.Equals(c.ClientId, srcClientId, StringComparison.OrdinalIgnoreCase));
            if (srcClient == null)
            {
                throw new InvalidOperationException($"Client '{srcClientId}' not found.");
            }

            var mapNewBase = $"{BaseUrl}/admin/realms/{UR(realm)}/users/{UR(svcUser.Id!)}/role-mappings/clients/{UR(srcClient.Id)}";
            var mapLegacyBase = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/users/{UR(svcUser.Id!)}/role-mappings/clients/{UR(srcClient.Id)}";

            foreach (var roleName in group.Select(x => x.Role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var getRoleNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(srcClient.Id)}/roles/{UR(roleName)}";
                    var getRoleLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(srcClient.Id)}/roles/{UR(roleName)}";

                    using var rr = await http.GetWithLegacyFallbackAsync(getRoleNew, getRoleLegacy, ct);
                    rr.EnsureAdminSuccess();
                    var rep = await ReadJsonAsync<KcRoleRep>(rr, ct) ?? new KcRoleRep { Name = roleName };
                    rep.ClientRole = true;
                    rep.ContainerId = srcClient.Id;

                    using var mapResp = await http.PostJsonWithLegacyFallbackAsync(mapNewBase, mapLegacyBase, new[] { rep }, JsonOpts, ct);
                    mapResp.EnsureAdminSuccess();
                    await AuditAsync("service-account:role-assign", realm, $"{newClientUuid}:{srcClientId}:{roleName}", ct);
                }
                catch (Exception ex)
                {
                    throw new InvalidOperationException($"Сервисная роль '{roleName}' клиента '{srcClientId}' не назначена: {ex.Message}", ex);
                }
            }
        }
    }

    private async Task<List<(string ClientId, string Role)>> GetServiceAccountRolesAsync(string realm, string clientUuid, CancellationToken ct)
    {
        var http = CreateAdminClient();

        var getSvcUserNew = $"{BaseUrl}/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/service-account-user";
        var getSvcUserLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/clients/{UR(clientUuid)}/service-account-user";

        using var userResp = await http.GetWithLegacyFallbackAsync(getSvcUserNew, getSvcUserLegacy, ct);
        userResp.EnsureAdminSuccess();
        var svcUser = await ReadJsonAsync<KcUserRep>(userResp, ct);
        if (svcUser?.Id == null)
        {
            return new List<(string, string)>();
        }

        var mapUrlNew = $"{BaseUrl}/admin/realms/{UR(realm)}/users/{UR(svcUser.Id)}/role-mappings";
        var mapUrlLegacy = $"{BaseUrl}/auth/admin/realms/{UR(realm)}/users/{UR(svcUser.Id)}/role-mappings";

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
}
