using Microsoft.Extensions.Configuration;
using Npgsql;
using System;
using System.Collections.Generic;
using System.Text;
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
                                target_id     text         NOT NULL,
                                details       text         NULL
                            );";

        await using (var cmd = new NpgsqlCommand(sql, conn))
            await cmd.ExecuteNonQueryAsync(ct);

        const string alterSql = "alter table api_audit_logs add column if not exists details text null";

        await using (var alterCmd = new NpgsqlCommand(alterSql, conn))
            await alterCmd.ExecuteNonQueryAsync(ct);

        const string indexSql =
            "create index if not exists idx_api_audit_logs_created_at_id on api_audit_logs (created_at desc, id desc)";

        await using (var indexCmd = new NpgsqlCommand(indexSql, conn))
            await indexCmd.ExecuteNonQueryAsync(ct);

        const string usernameIndexSql =
            "create index if not exists idx_api_audit_logs_username on api_audit_logs (username)";

        await using (var usernameIndexCmd = new NpgsqlCommand(usernameIndexSql, conn))
            await usernameIndexCmd.ExecuteNonQueryAsync(ct);

        const string operationTypeIndexSql =
            "create index if not exists idx_api_audit_logs_operation_type_normalized on api_audit_logs (upper(coalesce(nullif(split_part(operation_type, ':', 2), ''), operation_type)))";

        await using (var operationTypeIndexCmd = new NpgsqlCommand(operationTypeIndexSql, conn))
            await operationTypeIndexCmd.ExecuteNonQueryAsync(ct);

        _initialized = true;
    }

    public async Task LogAsync(
        string operationType,
        string username,
        string realm,
        string targetId,
        string? details = null,
        CancellationToken ct = default)
    {
        var normalizedOperationType = operationType;
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = @"insert into api_audit_logs (operation_type, username, realm, target_id, details)
                              values (@op, @user, @realm, @target, @details);";

        await using var cmd = new NpgsqlCommand(sql, conn);
        cmd.Parameters.AddWithValue("op", normalizedOperationType);
        cmd.Parameters.AddWithValue("user", username);
        cmd.Parameters.AddWithValue("realm", realm);
        cmd.Parameters.AddWithValue("target", targetId);
        cmd.Parameters.AddWithValue("details", (object?)details ?? DBNull.Value);

        await cmd.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<ApiAuditLogEntry>> GetLogsAsync(
        string? username = null,
        string? operationType = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        int limit = 200,
        int offset = 0,
        CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        if (limit <= 0)
        {
            limit = 200;
        }

        if (offset < 0)
        {
            offset = 0;
        }

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        var sql = new StringBuilder();
        sql.Append("select id, created_at, operation_type, username, realm, target_id, details from api_audit_logs");

        var conditions = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn };

        ApplyFilters(cmd, conditions, username, operationType, fromUtc, toUtc);

        if (conditions.Count > 0)
        {
            sql.Append(" where ");
            sql.Append(string.Join(" and ", conditions));
        }

        sql.Append(" order by created_at desc, id desc limit @limit offset @offset");
        cmd.Parameters.AddWithValue("limit", limit);
        cmd.Parameters.AddWithValue("offset", offset);
        cmd.CommandText = sql.ToString();

        var list = new List<ApiAuditLogEntry>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var createdAt = reader.GetFieldValue<DateTime>(1);
            if (createdAt.Kind == DateTimeKind.Unspecified)
            {
                createdAt = DateTime.SpecifyKind(createdAt, DateTimeKind.Utc);
            }

            var normalizedOperationType = reader.GetString(2);

            list.Add(new ApiAuditLogEntry(
                reader.GetInt64(0),
                createdAt,
                normalizedOperationType,
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.IsDBNull(6) ? null : reader.GetString(6)));
        }

        return list;
    }

    public async Task<int> GetLogsCountAsync(
        string? username = null,
        string? operationType = null,
        DateTime? fromUtc = null,
        DateTime? toUtc = null,
        CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string baseSql = "select count(*) from api_audit_logs";
        var conditions = new List<string>();
        await using var cmd = new NpgsqlCommand { Connection = conn };

        ApplyFilters(cmd, conditions, username, operationType, fromUtc, toUtc);

        var sql = new StringBuilder(baseSql);
        if (conditions.Count > 0)
        {
            sql.Append(" where ");
            sql.Append(string.Join(" and ", conditions));
        }

        cmd.CommandText = sql.ToString();

        var result = await cmd.ExecuteScalarAsync(ct);
        return result is null ? 0 : Convert.ToInt32(result);
    }

    private static void ApplyFilters(
        NpgsqlCommand cmd,
        List<string> conditions,
        string? username,
        string? operationType,
        DateTime? fromUtc,
        DateTime? toUtc)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            cmd.Parameters.AddWithValue("user", username.Trim());
            conditions.Add("username = @user");
        }

        if (!string.IsNullOrWhiteSpace(operationType))
        {
            var trimmed = operationType.Trim();
            if (trimmed.Length > 0)
            {
                var colonIndex = trimmed.IndexOf(':');
                string normalized = trimmed;

                if (colonIndex >= 0 && colonIndex + 1 < trimmed.Length)
                {
                    var suffixStart = colonIndex + 1;
                    var nextColonIndex = trimmed.IndexOf(':', suffixStart);
                    var suffix = nextColonIndex >= 0
                        ? trimmed[suffixStart..nextColonIndex]
                        : trimmed[suffixStart..];
                    suffix = suffix.Trim();
                    if (suffix.Length > 0)
                    {
                        normalized = suffix;
                    }
                }

                var normalizedUpper = normalized.ToUpperInvariant();

                cmd.Parameters.AddWithValue("op", normalizedUpper);
                conditions.Add("upper(coalesce(nullif(split_part(operation_type, ':', 2), ''), operation_type)) = @op");
            }
        }

        if (fromUtc is not null)
        {
            var normalized = DateTime.SpecifyKind(fromUtc.Value, DateTimeKind.Utc);
            cmd.Parameters.AddWithValue("from", normalized);
            conditions.Add("created_at >= @from");
        }

        if (toUtc is not null)
        {
            var normalized = DateTime.SpecifyKind(toUtc.Value, DateTimeKind.Utc);
            cmd.Parameters.AddWithValue("to", normalized);
            conditions.Add("created_at <= @to");
        }
    }

    public async Task<IReadOnlyList<string>> GetOperationTypesAsync(CancellationToken ct = default)
    {
        await EnsureCreatedAsync(ct);

        await using var conn = new NpgsqlConnection(_connString);
        await conn.OpenAsync(ct);

        const string sql = "select distinct operation_type from api_audit_logs";

        await using var cmd = new NpgsqlCommand(sql, conn);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<string>();

        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var normalized = reader.GetString(0);
            if (string.IsNullOrEmpty(normalized) || !set.Add(normalized))
            {
                continue;
            }

            list.Add(normalized);
        }

        list.Sort(StringComparer.Ordinal);
        return list;
    }
}

public sealed record ApiAuditLogEntry(
    long Id,
    DateTime CreatedAtUtc,
    string OperationType,
    string Username,
    string Realm,
    string TargetId,
    string? Details);
