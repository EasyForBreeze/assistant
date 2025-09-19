using Assistant.Interfaces;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace Assistant.Services;

/// <summary>
/// Хранение клиентов пользователей в PostgreSQL.
/// </summary>
public class UserClientsRepository
{
    private readonly string _connString;
    private bool _initialized;

    public UserClientsRepository(IConfiguration configuration)
    {
        _connString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var sql = @"CREATE TABLE IF NOT EXISTS user_clients (
                        username       text    NOT NULL,
                        name           text    NOT NULL,
                        client_id      text    NOT NULL,
                        realm          text    NOT NULL,
                        enabled        boolean NOT NULL,
                        flow_standard  boolean NOT NULL,
                        flow_service   boolean NOT NULL
                    );";

        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        const string indexSql =
            "CREATE UNIQUE INDEX IF NOT EXISTS ix_user_clients_identity ON user_clients(username, client_id, realm);";

        await using (var idx = new NpgsqlCommand(indexSql, conn))
            await idx.ExecuteNonQueryAsync(ct);

        _initialized = true;
    }

    public async Task AddAsync(string username, ClientSummary client, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var cmd = new NpgsqlCommand(@"insert into user_clients
                (username, name, client_id, realm, enabled, flow_standard, flow_service)
                values (@u, @n, @cid, @r, @en, @std, @svc)
                on conflict (username, client_id, realm) do update set
                    name = excluded.name,
                    enabled = excluded.enabled,
                    flow_standard = excluded.flow_standard,
                    flow_service = excluded.flow_service;", conn);

        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("n", client.Name);
        cmd.Parameters.AddWithValue("cid", client.ClientId);
        cmd.Parameters.AddWithValue("r", client.Realm);
        cmd.Parameters.AddWithValue("en", client.Enabled);
        cmd.Parameters.AddWithValue("std", client.FlowStandard);
        cmd.Parameters.AddWithValue("svc", client.FlowService);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveAsync(string clientId, string realm, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var cmd = new NpgsqlCommand("delete from user_clients where client_id=@cid and realm=@r", conn);
        cmd.Parameters.AddWithValue("cid", clientId);
        cmd.Parameters.AddWithValue("r", realm);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task RemoveForUserAsync(string username, string clientId, string realm, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var cmd = new NpgsqlCommand(
            "delete from user_clients where username=@u and client_id=@cid and realm=@r",
            conn);
        cmd.Parameters.AddWithValue("u", username);
        cmd.Parameters.AddWithValue("cid", clientId);
        cmd.Parameters.AddWithValue("r", realm);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<List<ClientSummary>> GetForUserAsync(string username, bool isAdmin, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        var list = new List<ClientSummary>();

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var sql = isAdmin
            ? "select name, client_id, realm, enabled, flow_standard, flow_service from user_clients"
            : "select name, client_id, realm, enabled, flow_standard, flow_service from user_clients where username=@u";

        await using var cmd = new NpgsqlCommand(sql, conn);
        if (!isAdmin)
            cmd.Parameters.AddWithValue("u", username);

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            list.Add(new ClientSummary(
                reader.GetString(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.GetBoolean(4),
                reader.GetBoolean(5)));
        }

        return list;
    }
}

