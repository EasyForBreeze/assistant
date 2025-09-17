using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Threading;
using System.Threading.Tasks;

namespace Assistant.Services;

/// <summary>
/// Хранилище логов действий REST API в PostgreSQL.
/// </summary>
public sealed class ApiLogRepository
{
    private readonly string _connString;
    private bool _initialized;

    public ApiLogRepository(IConfiguration configuration)
    {
        _connString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");
    }

    private async Task EnsureCreatedAsync(CancellationToken ct)
    {
        if (_initialized) return;

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"CREATE TABLE IF NOT EXISTS api_audit_logs (
                                id            bigserial    PRIMARY KEY,
                                created_at    timestamptz  NOT NULL DEFAULT now(),
                                operation_type text        NOT NULL,
                                username      text         NOT NULL,
                                realm         text         NOT NULL,
                                target_id     text         NOT NULL
                            );";

        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
    }

    public async Task LogAsync(string operationType, string username, string realm, string targetId, CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"insert into api_audit_logs (operation_type, username, realm, target_id)
                              values (@op, @user, @realm, @target);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("op", operationType);
        cmd.Parameters.AddWithValue("user", username);
        cmd.Parameters.AddWithValue("realm", realm);
        cmd.Parameters.AddWithValue("target", targetId);

        await cmd.ExecuteNonQueryAsync(ct);
    }
}
