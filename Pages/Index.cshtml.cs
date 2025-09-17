using System.Linq;
using Assistant.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Assistant.Pages
{
    [Authorize]
    public class IndexModel : ClientsPageModel
    {
        private readonly IClientsProvider _provider;

        public IndexModel(IClientsProvider provider)
        {
            _provider = provider;
        }

        public async Task OnGetAsync(string? q, int pageNumber = 1)
        {
            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

            var list = (await _provider.GetClientsForUser(User)).ToList();
            ShowEmptyMessage = true;

            ApplyPaging(list, pageNumber);
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
}
