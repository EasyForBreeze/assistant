using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Threading;
using System.Threading.Tasks;

namespace Assistant.Services;

/// <summary>
/// Хранилище соответствий клиентов и страниц Confluence.
/// </summary>
public sealed class ClientWikiRepository
{
    private readonly string _connectionString;
    private bool _initialized;
    private readonly SemaphoreSlim _initLock = new(1, 1);

    public ClientWikiRepository(IConfiguration configuration)
    {
        _connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_initialized)
        {
            return;
        }

        await _initLock.WaitAsync(ct);
        try
        {
            if (_initialized)
            {
                return;
            }

            await using var conn = new NpgsqlConnection(_connectionString);
            await conn.OpenAsync(ct);

            const string tableSql = @"CREATE TABLE IF NOT EXISTS client_wiki_pages (
                                        realm            text        NOT NULL,
                                        client_id        text        NOT NULL,
                                        page_id          text        NOT NULL,
                                        app_name         text        NULL,
                                        app_url          text        NULL,
                                        service_owner    text        NULL,
                                        service_manager  text        NULL,
                                        updated_at       timestamptz NOT NULL DEFAULT now()
                                    );";

            await using (var cmd = new NpgsqlCommand(tableSql, conn))
            {
                await cmd.ExecuteNonQueryAsync(ct);
            }

            const string indexSql =
                "CREATE UNIQUE INDEX IF NOT EXISTS ix_client_wiki_pages_identity ON client_wiki_pages(realm, client_id);";

            await using (var index = new NpgsqlCommand(indexSql, conn))
            {
                await index.ExecuteNonQueryAsync(ct);
            }

            _initialized = true;
        }
        finally
        {
            _initLock.Release();
        }
    }

    public async Task<ClientWikiInfo?> GetAsync(string realm, string clientId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = @"select realm, client_id, page_id, app_name, app_url, service_owner, service_manager
                              from client_wiki_pages
                              where realm = @realm and client_id = @client
                              limit 1;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("realm", realm);
        cmd.Parameters.AddWithValue("client", clientId);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct))
        {
            return null;
        }

        string? GetValue(int ordinal) => reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);

        return new ClientWikiInfo(
            reader.GetString(0),
            reader.GetString(1),
            reader.GetString(2),
            GetValue(3),
            GetValue(4),
            GetValue(5),
            GetValue(6));
    }

    public async Task SetAsync(ClientWikiInfo info, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = @"insert into client_wiki_pages (realm, client_id, page_id, app_name, app_url, service_owner, service_manager)
                              values (@realm, @client, @page, @appName, @appUrl, @owner, @manager)
                              on conflict (realm, client_id) do update set
                                  page_id = excluded.page_id,
                                  app_name = excluded.app_name,
                                  app_url = excluded.app_url,
                                  service_owner = excluded.service_owner,
                                  service_manager = excluded.service_manager,
                                  updated_at = now();";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("realm", info.Realm);
        cmd.Parameters.AddWithValue("client", info.ClientId);
        cmd.Parameters.AddWithValue("page", info.PageId);
        cmd.Parameters.AddWithValue("appName", (object?)info.AppName ?? DBNull.Value);
        cmd.Parameters.AddWithValue("appUrl", (object?)info.AppUrl ?? DBNull.Value);
        cmd.Parameters.AddWithValue("owner", (object?)info.ServiceOwner ?? DBNull.Value);
        cmd.Parameters.AddWithValue("manager", (object?)info.ServiceManager ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string realm, string clientId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connectionString);
        await conn.OpenAsync(ct);

        const string sql = "delete from client_wiki_pages where realm = @realm and client_id = @client;";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("realm", realm);
        cmd.Parameters.AddWithValue("client", clientId);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public sealed record ClientWikiInfo(
        string Realm,
        string ClientId,
        string PageId,
        string? AppName,
        string? AppUrl,
        string? ServiceOwner,
        string? ServiceManager);
}
