using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;

namespace Assistant.Services;

/// <summary>
/// Хранилище клиентов, которым запрещено выдавать сервисные роли.
/// </summary>
public sealed class ServiceRoleExclusionsRepository
{
    private readonly string _connString;
    private readonly IMemoryCache _cache;
    private readonly SemaphoreSlim _initLock = new(1, 1);
    private bool _initialized;

    private sealed class ChangeState
    {
        public ChangeState(long version, CancellationTokenSource source)
        {
            Version = version;
            Source = source;
        }

        public long Version { get; }
        public CancellationTokenSource Source { get; }
    }

    private ChangeState _changeState = new(0, new CancellationTokenSource());

    private const string CacheKey = "service-role-exclusions";

    private static readonly string[] DefaultClientIds =
    {
        "account",
        "account-console",
        "admin-cli",
        "broker",
        "realm-management",
        "security-admin-console"
    };

    public ServiceRoleExclusionsRepository(IConfiguration configuration, IMemoryCache cache)
    {
        _connString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
        _cache = cache;
    }

    private async Task EnsureInitializedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized) return;

            await using var conn = new NpgsqlConnection(_connString);
            await conn.OpenAsync(ct);

            const string createSql = @"CREATE TABLE IF NOT EXISTS service_role_exclusions (
                        client_id text PRIMARY KEY
                    );";
            await using (var cmd = new NpgsqlCommand(createSql, conn))
                await cmd.ExecuteNonQueryAsync(ct);

            foreach (var clientId in DefaultClientIds)
            {
                await using var insert = new NpgsqlCommand(
                    "INSERT INTO service_role_exclusions (client_id) VALUES (@cid) ON CONFLICT (client_id) DO NOTHING;",
                    conn);
                insert.Parameters.AddWithValue("cid", clientId);
                await insert.ExecuteNonQueryAsync(ct);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    private async Task<HashSet<string>> LoadAllAsync(CancellationToken ct)
    {
        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = "SELECT client_id FROM service_role_exclusions";
        await using var cmd = new NpgsqlCommand(sql, conn);
        await using var reader = await cmd.ExecuteReaderAsync(ct);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (await reader.ReadAsync(ct))
        {
            if (!reader.IsDBNull(0))
                set.Add(reader.GetString(0));
        }

        return set;
    }

    /// <summary>
    /// Возвращает множество clientId, которым нельзя назначать сервисные роли.
    /// </summary>
    public async Task<ServiceRoleExclusionsSnapshot> GetAllAsync(CancellationToken ct = default)
    {
        if (_cache.TryGetValue<ServiceRoleExclusionsSnapshot>(CacheKey, out var cached))
            return cached;

        while (true)
        {
            var state = Volatile.Read(ref _changeState);
            var set = await LoadAllAsync(ct);

            if (!ReferenceEquals(state, Volatile.Read(ref _changeState)))
            {
                continue;
            }

            var changeToken = new CancellationChangeToken(state.Source.Token);
            var snapshot = new ServiceRoleExclusionsSnapshot(set, new ServiceRoleExclusionsChangeToken(state.Version, changeToken));

            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
            };
            options.AddExpirationToken(changeToken);

            _cache.Set(CacheKey, snapshot, options);
            return snapshot;
        }
    }

    /// <summary>
    /// Проверяет, запрещено ли назначать роли от указанного клиента.
    /// </summary>
    public async Task<bool> IsExcludedAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId)) return false;
        var snapshot = await GetAllAsync(ct);
        return snapshot.Contains(clientId);
    }

    /// <summary>
    /// Добавляет clientId в список исключений.
    /// </summary>
    public async Task<bool> AddAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return false;
        }

        var normalized = clientId.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"INSERT INTO service_role_exclusions (client_id)
                             VALUES (@cid)
                             ON CONFLICT (client_id) DO NOTHING;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cid", normalized.ToLowerInvariant());

        var affected = await cmd.ExecuteNonQueryAsync(ct);
        if (affected > 0)
        {
            InvalidateCache();
            return true;
        }

        return false;
    }

    /// <summary>
    /// Удаляет clientId из списка исключений.
    /// </summary>
    public async Task<string?> RemoveAsync(string clientId, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            return null;
        }

        await EnsureInitializedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"DELETE FROM service_role_exclusions WHERE lower(client_id) = lower(@cid) RETURNING client_id;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("cid", clientId.Trim());

        var result = await cmd.ExecuteScalarAsync(ct);
        if (result is string removed)
        {
            InvalidateCache();
            return removed;
        }

        return null;
    }

    public ServiceRoleExclusionsChangeToken CreateChangeToken()
    {
        var state = Volatile.Read(ref _changeState);
        return new ServiceRoleExclusionsChangeToken(state.Version, new CancellationChangeToken(state.Source.Token));
    }

    public long GetVersion()
    {
        var state = Volatile.Read(ref _changeState);
        return state.Version;
    }

    public void InvalidateCache()
    {
        _cache.Remove(CacheKey);

        while (true)
        {
            var current = Volatile.Read(ref _changeState);
            var next = new ChangeState(current.Version + 1, new CancellationTokenSource());
            if (ReferenceEquals(current, Interlocked.CompareExchange(ref _changeState, next, current)))
            {
                try
                {
                    current.Source.Cancel();
                }
                finally
                {
                    current.Source.Dispose();
                }

                break;
            }
        }
    }
}

public readonly record struct ServiceRoleExclusionsChangeToken(long Version, IChangeToken Token);

public sealed class ServiceRoleExclusionsSnapshot
{
    private readonly HashSet<string> _clientIds;

    public ServiceRoleExclusionsSnapshot(HashSet<string> clientIds, ServiceRoleExclusionsChangeToken changeToken)
    {
        _clientIds = clientIds;
        ChangeToken = changeToken;
    }

    public IReadOnlySet<string> ClientIds => _clientIds;

    public ServiceRoleExclusionsChangeToken ChangeToken { get; }

    public long Version => ChangeToken.Version;

    public bool Contains(string clientId) => _clientIds.Contains(clientId);

    public int Count => _clientIds.Count;
}
