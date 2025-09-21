using System;
using System.Collections.Generic;
using System.Linq;
using Assistant.Interfaces;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages;

public abstract class ClientsPageModel : PageModel
{
    private const int PageSize = 20;

    public List<ClientSummary> Clients { get; protected set; } = [];

    public string? Q { get; protected set; }

    public bool ShowEmptyMessage { get; protected set; }

    public int PageNumber { get; protected set; }

    public int TotalPages { get; protected set; }

    public bool HasPreviousPage => PageNumber > 1;

    public bool HasNextPage => PageNumber < TotalPages;

    public string MapEnv(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
    {
        "prod" or "production" => "PROD",
        "stage" or "staging" => "STAGE",
        "test" => "TEST",
        "dev" or "development" => "TEST",
        _ => (v ?? "").ToUpperInvariant()
    };

    public IEnumerable<string> Envs(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? Enumerable.Empty<string>()
            : raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                 .Select(MapEnv)
                 .Distinct(StringComparer.OrdinalIgnoreCase);

    public string EnvBarGradient(string? env) => (env ?? "").ToUpperInvariant() switch
    {
        "PROD" => "from-fuchsia-500/70 to-pink-500/70",
        "STAGE" => "from-amber-400/80 to-orange-500/70",
        "TEST" => "from-emerald-400/80 to-teal-500/70",
        _ => "from-slate-500/60 to-slate-400/60"
    };

    protected void ApplyPaging(List<ClientSummary> list, int pageNumber)
    {
        TotalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
        PageNumber = Math.Clamp(pageNumber, 1, TotalPages);
        Clients = list.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
    }
}
