using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Assistant.Interfaces;
using Assistant.KeyCloak;
using Assistant.Pages;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

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
            foreach (var realm in realms)
            {
                if (string.IsNullOrWhiteSpace(realm.Realm))
                {
                    continue;
                }

                var hits = await _clients.SearchClientsAsync(realm.Realm!, Q);
                foreach (var c in hits)
                {
                    list.Add(new ClientSummary(
                        Name: c.ClientId,
                        ClientId: c.ClientId,
                        Realm: realm.Realm!,
                        Enabled: true,
                        FlowStandard: false,
                        FlowService: false));
                }
            }
        }

        ShowEmptyMessage = !string.IsNullOrEmpty(Q);

        var ordered = list
            .OrderBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyPaging(ordered, pageNumber);
    }

    public async Task<IActionResult> OnGetClientsAsync(string? q, int pageNumber = 1)
    {
        await OnGetAsync(q, pageNumber);
        if (Request.Headers["X-Requested-With"] == "XMLHttpRequest")
        {
            return Partial("_ClientsList", this);
        }

        return Page();
    }
}
