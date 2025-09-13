using Assistant.Interfaces;
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly IClientsProvider _provider;
        private readonly RealmsService _realms;
        private readonly ClientsService _clients;

        public IndexModel(IClientsProvider provider, RealmsService realms, ClientsService clients)
        {
            _provider = provider;
            _realms = realms;
            _clients = clients;
        }

        public List<ClientSummary> Clients { get; private set; } = [];
        public string? Q { get; private set; }
        public bool ShowEmptyMessage { get; private set; }

        public async Task OnGetAsync(string? q)
        {
            Q = q?.Trim();
            var isAdmin = User.IsInRole("assistant-admin");

            if (isAdmin)
            {
                if (!string.IsNullOrEmpty(Q))
                {
                    var realms = await _realms.GetRealmsAsync();
                    var list = new List<ClientSummary>();
                    foreach (var r in realms)
                    {
                        if (string.IsNullOrWhiteSpace(r.Realm)) continue;
                        var hits = await _clients.SearchClientsAsync(r.Realm!, Q);
                        foreach (var c in hits)
                        {
                            list.Add(new ClientSummary(
                                Name: c.ClientId,
                                ClientId: c.ClientId,
                                Realm: r.Realm!,
                                Enabled: true,
                                FlowStandard: false,
                                FlowService: false));
                        }
                    }
                    Clients = list;
                }

                ShowEmptyMessage = !string.IsNullOrEmpty(Q);
                return;
            }

            Clients = (await _provider.GetClientsForUser(User)).ToList();
            ShowEmptyMessage = true;
        }
    }
}
