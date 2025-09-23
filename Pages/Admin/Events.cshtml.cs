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

    [BindProperty(SupportsGet = true)]
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
        var local = NormalizeToLocal(utc);
        return local.ToString("dd.MM.yyyy HH:mm:ss");
    }

    public string GetRelativeTime(DateTime utc)
    {
        var local = NormalizeToLocal(utc);
        var diff = DateTime.Now - local;
        var isPast = diff >= TimeSpan.Zero;
        diff = diff.Duration();

        if (diff < TimeSpan.FromMinutes(1))
        {
            return isPast ? "меньше минуты назад" : "через меньше минуты";
        }

        if (diff < TimeSpan.FromHours(1))
        {
            var minutes = Math.Max(1, (int)Math.Round(diff.TotalMinutes));
            var unit = Declension(minutes, "минута", "минуты", "минут");
            return isPast ? $"{minutes} {unit} назад" : $"через {minutes} {unit}";
        }

        if (diff < TimeSpan.FromDays(1))
        {
            var hours = Math.Max(1, (int)Math.Round(diff.TotalHours));
            var unit = Declension(hours, "час", "часа", "часов");
            return isPast ? $"{hours} {unit} назад" : $"через {hours} {unit}";
        }

        var days = Math.Max(1, (int)Math.Round(diff.TotalDays));
        var dayUnit = Declension(days, "день", "дня", "дней");
        return isPast ? $"{days} {dayUnit} назад" : $"через {days} {dayUnit}";
    }

    public OperationAccentStyles GetOperationAccentStyles(string? operationType)
    {
        if (string.IsNullOrWhiteSpace(operationType))
        {
            return OperationAccentPalette[0];
        }

        var hash = ComputeHash(operationType, seed: 97);
        var index = (int)((uint)hash % (uint)OperationAccentPalette.Length);
        return OperationAccentPalette[index];
    }

    public string GetAvatarInitials(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "?";
        }

        var separators = new[] { '.', '-', '_', ' ', '@' };
        var parts = username.Split(separators, StringSplitOptions.RemoveEmptyEntries);

        static char Initial(string value, int index)
        {
            if (string.IsNullOrEmpty(value) || index >= value.Length)
            {
                return '?';
            }

            return char.ToUpperInvariant(value[index]);
        }

        if (parts.Length == 0)
        {
            var trimmed = username.Trim();
            if (trimmed.Length == 0)
            {
                return "?";
            }

            var first = Initial(trimmed, 0);
            var second = trimmed.Length > 1 ? Initial(trimmed, trimmed.Length - 1) : first;
            return string.Concat(first, second);
        }

        var firstInitial = Initial(parts[0], 0);
        var secondInitial = parts.Length > 1
            ? Initial(parts[^1], 0)
            : (parts[0].Length > 1 ? Initial(parts[0], 1) : firstInitial);

        return string.Concat(firstInitial, secondInitial);
    }

    public string GetAvatarAccentClass(string? username)
    {
        if (string.IsNullOrWhiteSpace(username))
        {
            return "bg-slate-800/70 text-slate-100 ring-1 ring-inset ring-white/10";
        }

        var hash = ComputeHash(username, seed: 173);
        var index = (int)((uint)hash % (uint)AvatarPalette.Length);
        return AvatarPalette[index];
    }

    public string GetSoftPillClasses(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "border border-white/10 bg-white/5 text-slate-200";
        }

        var hash = ComputeHash(value, seed: 131);
        var index = (int)((uint)hash % (uint)SoftPillPalette.Length);
        return SoftPillPalette[index];
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

    private static DateTime NormalizeToLocal(DateTime value)
    {
        if (value.Kind == DateTimeKind.Utc)
        {
            return value.ToLocalTime();
        }

        if (value.Kind == DateTimeKind.Unspecified)
        {
            value = DateTime.SpecifyKind(value, DateTimeKind.Utc);
            return value.ToLocalTime();
        }

        return value;
    }

    private static int ComputeHash(string value, int seed)
    {
        unchecked
        {
            var hash = seed;
            foreach (var ch in value)
            {
                hash = (hash * 31) ^ char.ToLowerInvariant(ch);
            }

            return hash;
        }
    }

    private static string Declension(int number, string singular, string dual, string plural)
    {
        var n = Math.Abs(number) % 100;
        var n1 = n % 10;

        if (n > 10 && n < 20)
        {
            return plural;
        }

        return n1 switch
        {
            1 => singular,
            2 or 3 or 4 => dual,
            _ => plural,
        };
    }

    private static readonly OperationAccentStyles[] OperationAccentPalette =
    {
        new(
            BarClass: "bg-gradient-to-b from-sky-400/0 via-sky-400/90 to-cyan-400/0 shadow-[0_0_18px_-6px_rgba(14,165,233,0.8)]",
            BadgeClass: "border border-sky-500/40 bg-sky-500/15 text-sky-100 shadow-[0_12px_30px_-18px_rgba(14,165,233,0.6)]",
            PulseClass: "bg-sky-300 shadow-[0_0_0_3px_rgba(56,189,248,0.35)]"),
        new(
            BarClass: "bg-gradient-to-b from-violet-400/0 via-fuchsia-400/90 to-purple-400/0 shadow-[0_0_18px_-6px_rgba(168,85,247,0.7)]",
            BadgeClass: "border border-fuchsia-500/40 bg-fuchsia-500/15 text-fuchsia-100 shadow-[0_12px_30px_-18px_rgba(217,70,239,0.55)]",
            PulseClass: "bg-fuchsia-300 shadow-[0_0_0_3px_rgba(217,70,239,0.35)]"),
        new(
            BarClass: "bg-gradient-to-b from-emerald-400/0 via-teal-400/90 to-emerald-400/0 shadow-[0_0_18px_-6px_rgba(16,185,129,0.65)]",
            BadgeClass: "border border-emerald-500/40 bg-emerald-500/15 text-emerald-100 shadow-[0_12px_30px_-18px_rgba(16,185,129,0.55)]",
            PulseClass: "bg-emerald-300 shadow-[0_0_0_3px_rgba(16,185,129,0.35)]"),
        new(
            BarClass: "bg-gradient-to-b from-amber-400/0 via-orange-400/90 to-yellow-400/0 shadow-[0_0_18px_-6px_rgba(245,158,11,0.6)]",
            BadgeClass: "border border-amber-500/40 bg-amber-500/15 text-amber-100 shadow-[0_12px_30px_-18px_rgba(245,158,11,0.5)]",
            PulseClass: "bg-amber-300 shadow-[0_0_0_3px_rgba(251,191,36,0.35)]"),
        new(
            BarClass: "bg-gradient-to-b from-rose-400/0 via-rose-400/90 to-pink-400/0 shadow-[0_0_18px_-6px_rgba(244,63,94,0.65)]",
            BadgeClass: "border border-rose-500/40 bg-rose-500/15 text-rose-100 shadow-[0_12px_30px_-18px_rgba(244,63,94,0.55)]",
            PulseClass: "bg-rose-300 shadow-[0_0_0_3px_rgba(244,63,94,0.35)]"),
    };

    private static readonly string[] SoftPillPalette =
    {
        "border border-sky-500/35 bg-sky-500/10 text-sky-100 shadow-[0_12px_28px_-18px_rgba(14,165,233,0.55)]",
        "border border-fuchsia-500/35 bg-fuchsia-500/10 text-fuchsia-100 shadow-[0_12px_28px_-18px_rgba(217,70,239,0.5)]",
        "border border-emerald-500/35 bg-emerald-500/10 text-emerald-100 shadow-[0_12px_28px_-18px_rgba(16,185,129,0.5)]",
        "border border-amber-500/35 bg-amber-500/10 text-amber-100 shadow-[0_12px_28px_-18px_rgba(245,158,11,0.45)]",
        "border border-rose-500/35 bg-rose-500/10 text-rose-100 shadow-[0_12px_28px_-18px_rgba(244,63,94,0.5)]",
    };

    private static readonly string[] AvatarPalette =
    {
        "bg-gradient-to-br from-sky-500/35 via-cyan-500/20 to-blue-500/35 text-sky-50 ring-1 ring-inset ring-sky-500/40 shadow-[0_20px_45px_-30px_rgba(14,165,233,0.75)]",
        "bg-gradient-to-br from-violet-500/35 via-fuchsia-500/25 to-purple-500/35 text-fuchsia-50 ring-1 ring-inset ring-fuchsia-500/40 shadow-[0_20px_45px_-30px_rgba(168,85,247,0.75)]",
        "bg-gradient-to-br from-emerald-500/35 via-teal-500/25 to-green-500/35 text-emerald-50 ring-1 ring-inset ring-emerald-500/40 shadow-[0_20px_45px_-30px_rgba(16,185,129,0.75)]",
        "bg-gradient-to-br from-amber-500/40 via-orange-500/25 to-yellow-500/35 text-amber-50 ring-1 ring-inset ring-amber-500/40 shadow-[0_20px_45px_-30px_rgba(245,158,11,0.7)]",
        "bg-gradient-to-br from-rose-500/35 via-pink-500/25 to-red-500/35 text-rose-50 ring-1 ring-inset ring-rose-500/40 shadow-[0_20px_45px_-30px_rgba(244,63,94,0.7)]",
    };

    public readonly record struct OperationAccentStyles(string BarClass, string BadgeClass, string PulseClass);
}
