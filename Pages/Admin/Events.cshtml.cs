using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages.Admin;

[Authorize(Roles = "assistant-admin")]
public sealed class EventsModel : PageModel
{
    private const int DefaultLimitValue = 200;
    private const int MaxLimitValue = 1000;

    private readonly ApiLogRepository _repository;

    public EventsModel(ApiLogRepository repository)
    {
        _repository = repository;
    }

    [BindProperty(SupportsGet = true)]
    public string? Username { get; set; }

    [BindProperty(SupportsGet = true)]
    public string? OperationType { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? From { get; set; }

    [BindProperty(SupportsGet = true)]
    public DateTime? To { get; set; }

    [BindProperty(SupportsGet = true)]
    public int Limit { get; set; } = DefaultLimitValue;

    public IReadOnlyList<ApiAuditLogEntry> Logs { get; private set; } = Array.Empty<ApiAuditLogEntry>();

    public IReadOnlyList<string> OperationTypes { get; private set; } = Array.Empty<string>();

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Username)
        || !string.IsNullOrWhiteSpace(OperationType)
        || From.HasValue
        || To.HasValue;

    public int ResultCount => Logs.Count;

    public string? FromInput => FormatForInput(From);

    public string? ToInput => FormatForInput(To);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Username = Normalize(Username);
        OperationType = Normalize(OperationType);
        Limit = NormalizeLimit(Limit);

        OperationTypes = await _repository.GetOperationTypesAsync(ct);

        var fromUtc = ToUtc(From);
        var toUtc = ToUtc(To);

        Logs = await _repository.GetLogsAsync(
            username: Username,
            operationType: OperationType,
            fromUtc: fromUtc,
            toUtc: toUtc,
            limit: Limit,
            ct: ct);
    }

    public string FormatTimestamp(DateTime utc)
    {
        var value = utc;
        if (value.Kind == DateTimeKind.Utc)
        {
            value = value.ToLocalTime();
        }
        else if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc).ToLocalTime();
        }

        return value.ToString("dd.MM.yyyy HH:mm:ss");
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static DateTime? ToUtc(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var local = value.Value;
        if (local.Kind == DateTimeKind.Utc)
        {
            return local;
        }

        if (local.Kind == DateTimeKind.Unspecified)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
        }

        return local.ToUniversalTime();
    }

    private static string? FormatForInput(DateTime? value)
    {
        if (value is null)
        {
            return null;
        }

        var local = value.Value;
        if (local.Kind == DateTimeKind.Utc)
        {
            local = local.ToLocalTime();
        }
        else if (local.Kind == DateTimeKind.Unspecified)
        {
            local = DateTime.SpecifyKind(local, DateTimeKind.Local);
        }

        return local.ToString("yyyy-MM-ddTHH:mm");
    }

    private static int NormalizeLimit(int value)
    {
        if (value <= 0)
        {
            return DefaultLimitValue;
        }

        if (value > MaxLimitValue)
        {
            return MaxLimitValue;
        }

        return value;
    }
}
