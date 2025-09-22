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
    private const int PageLimit = 5;

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

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public IReadOnlyList<ApiAuditLogEntry> Logs { get; private set; } = Array.Empty<ApiAuditLogEntry>();

    public IReadOnlyList<string> OperationTypes { get; private set; } = Array.Empty<string>();

    public int Limit => PageLimit;

    public bool HasFilters =>
        !string.IsNullOrWhiteSpace(Username)
        || !string.IsNullOrWhiteSpace(OperationType)
        || From.HasValue
        || To.HasValue;

    public int ResultCount => Logs.Count;

    public int TotalCount { get; private set; }

    public int TotalPages { get; private set; }

    public bool HasPreviousPage => TotalPages > 0 && PageNumber > 1;

    public bool HasNextPage => TotalPages > 0 && PageNumber < TotalPages;

    public bool ShowPagination => TotalPages > 1;

    public int DisplayFrom => TotalCount == 0 ? 0 : (PageNumber - 1) * Limit + 1;

    public int DisplayTo => TotalCount == 0 ? 0 : DisplayFrom + ResultCount - 1;

    public string? FromInput => FormatForInput(From);

    public string? ToInput => FormatForInput(To);

    public async Task OnGetAsync(CancellationToken ct)
    {
        Username = Normalize(Username);
        OperationType = Normalize(OperationType);
        OperationTypes = await _repository.GetOperationTypesAsync(ct);

        var fromUtc = ToUtc(From);
        var toUtc = ToUtc(To);

        var requestedPage = PageNumber <= 0 ? 1 : PageNumber;

        TotalCount = await _repository.GetLogsCountAsync(
            username: Username,
            operationType: OperationType,
            fromUtc: fromUtc,
            toUtc: toUtc,
            ct: ct);

        TotalPages = TotalCount == 0
            ? 0
            : (int)Math.Ceiling(TotalCount / (double)Limit);

        if (TotalPages == 0)
        {
            PageNumber = 1;
        }
        else
        {
            PageNumber = Math.Min(requestedPage, TotalPages);
        }

        Logs = await _repository.GetLogsAsync(
            username: Username,
            operationType: OperationType,
            fromUtc: fromUtc,
            toUtc: toUtc,
            limit: Limit,
            offset: TotalPages == 0 ? 0 : (PageNumber - 1) * Limit,
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

    public string FormatOperationType(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value;
    }
}
