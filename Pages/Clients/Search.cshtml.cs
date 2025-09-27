using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assistant.Interfaces;
using Assistant.KeyCloak;
using Assistant.Pages;
using Microsoft.AspNetCore.Authorization;

namespace Assistant.Pages.Clients;

[Authorize(Roles = "assistant-admin")]
public class SearchModel : ClientsPageModel
{
    private readonly RealmsService _realms;
    private readonly ClientsService _clients;

    public SearchModel(RealmsService realms, ClientsService clients)
    {
        _realms = realms;
        _clients = clients;
    }

    public async Task OnGetAsync(string? q, int pageNumber = 1)
    {
        Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

        var list = new List<ClientSummary>();
        if (!string.IsNullOrEmpty(Q))
        {
            var realms = await _realms.GetRealmsAsync();
            var searchTasks = realms
                .Where(r => !string.IsNullOrWhiteSpace(r.Realm))
                .Select(async realm =>
                {
                    var hits = await _clients.SearchClientsAsync(realm.Realm!, Q);
                    return hits
                        .Select(c => ClientSummary.ForLookup(realm.Realm!, c.ClientId))
                        .ToList();
                })
                .ToList();

            var results = await Task.WhenAll(searchTasks);
            foreach (var hits in results)
            {
                list.AddRange(hits);
            }
        }

        ShowEmptyMessage = !string.IsNullOrEmpty(Q);

        var ordered = list
            .OrderBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyPaging(ordered, pageNumber);
    }

}
