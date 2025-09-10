using Assistant.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly IClientsProvider _provider;
        public IndexModel(IClientsProvider provider) => _provider = provider;

        public List<ClientSummary> Clients { get; private set; } = [];
        public string? Q { get; private set; }

        public async Task OnGetAsync(string? q)
        {
            Q = q?.Trim();
            var all = await _provider.GetClientsForUser(User);
            Clients = string.IsNullOrEmpty(Q)
                ? all.ToList()
                : all.Where(c =>
                    (c.Name?.Contains(Q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (c.ClientId?.Contains(Q, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (c.Realm?.Contains(Q, StringComparison.OrdinalIgnoreCase) ?? false)
                  ).ToList();
        }
    }
}
