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

        public async Task<IActionResult> OnGetAsync(string? q, int pageNumber = 1)
        {
            if (User.IsInRole("assistant-admin"))
            {
                return RedirectToPage("/Clients/Search");
            }

            Q = string.IsNullOrWhiteSpace(q) ? null : q.Trim();

            var list = (await _provider.GetClientsForUser(User)).ToList();
            ShowEmptyMessage = true;

            ApplyPaging(list, pageNumber);

            return Page();
        }

    }
}
