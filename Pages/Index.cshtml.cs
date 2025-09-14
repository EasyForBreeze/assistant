using Assistant.Interfaces;
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
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

        private const int PageSize = 20;

        public List<ClientSummary> Clients { get; private set; } = [];
        public string? Q { get; private set; }
        public bool ShowEmptyMessage { get; private set; }

        [BindProperty(SupportsGet = true)]
        [FromQuery(Name = "page")]
        public int PageNumber { get; set; } = 1;

        public int TotalPages { get; private set; }
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;

        public async Task OnGetAsync(string? q)
        {
            Q = q?.Trim();
            var isAdmin = User.IsInRole("assistant-admin");

            var list = new List<ClientSummary>();

            if (isAdmin)
            {
                if (!string.IsNullOrEmpty(Q))
                {
                    var realms = await _realms.GetRealmsAsync();
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
                }

                ShowEmptyMessage = !string.IsNullOrEmpty(Q);
            }
            else
            {
                list = (await _provider.GetClientsForUser(User)).ToList();
                ShowEmptyMessage = true;
            }

            TotalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
            PageNumber = Math.Clamp(PageNumber, 1, TotalPages);
            Clients = list.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        }
    }
}
