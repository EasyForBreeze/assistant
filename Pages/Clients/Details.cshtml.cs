// Pages/Clients/Details.cshtml.cs
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;
using System.Linq;

namespace Assistant.Pages.Clients
{
    public class DetailsModel : PageModel
    {
        private readonly ClientsService _clients;

        public DetailsModel(ClientsService clients)
        {
            _clients = clients;
        }

        // Параметры из query: ?realm=...&clientId=...
        [BindProperty(SupportsGet = true)] public string? Realm { get; set; }
        [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

        // То самое, что используется в cshtml: client?.ClientAuth и т.д.
        public ClientVm Client { get; set; } = default!;
        public string RedirectUrisJson { get; private set; } = "[]";
        public string LocalRolesJson { get; private set; } = "[]";
        public string ServiceRolesJson { get; private set; } = "[]";
        // public string DefaultScopesJson { get; private set; } = "[]";

        public async Task<IActionResult> OnGetAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
                return NotFound();

            var details = await _clients.GetClientDetailsAsync(Realm!, ClientId!, ct);
            if (details == null) return NotFound();

            Client = new ClientVm
            {
                ClientId = details.ClientId,
                Realm = Realm!,
                Enabled = details.Enabled,
                Description = details.Description,
                ClientAuth = details.ClientAuth,
                StandardFlow = details.StandardFlow,
                ServiceAccount = details.ServiceAccount
            };

            RedirectUrisJson = JsonSerializer.Serialize(details.RedirectUris);
            LocalRolesJson = JsonSerializer.Serialize(details.LocalRoles);
            ServiceRolesJson = JsonSerializer.Serialize(details.ServiceRoles.Select(p => $"{p.ClientId}: {p.Role}"));
            // DefaultScopesJson = JsonSerializer.Serialize(details.DefaultScopes);

            return Page();
        }

        public IActionResult OnPostSave()
        {
            // Здесь возьмёшь значения из Request.Form или через [BindProperty] на нужных полях.
            // Пока заглушка:
            TempData["Flash"] = "Changes saved (stub).";
            return RedirectToPage(new { realm = Realm ?? "internal-bank-idm", clientId = ClientId ?? "app-bank-sample" });
        }

        public IActionResult OnPostDelete()
        {
            // Заглушка удаления:
            TempData["Flash"] = "Client deleted (stub).";
            return RedirectToPage("/Index");
        }

        // ===== AJAX handlers для фронта Service Roles =====
        public async Task<IActionResult> OnGetRoleLookupAsync(string realm, string q, int clientFirst = 0, int clientsToScan = 25, int rolesPerClient = 10, CancellationToken ct = default)
        {
            var (hits, next) = await _clients.FindRolesAcrossClientsAsync(realm, q, clientFirst, clientsToScan, rolesPerClient, ct);
            return new JsonResult(new { hits, nextClientFirst = next });
        }

        public async Task<IActionResult> OnGetClientsSearchAsync(string realm, string q, int first = 0, int max = 20, CancellationToken ct = default)
            => new JsonResult(await _clients.SearchClientsAsync(realm, q ?? "", first, max, ct));

        public async Task<IActionResult> OnGetClientRolesAsync(string realm, string id, int first = 0, int max = 50, string? q = null, CancellationToken ct = default)
            => new JsonResult(await _clients.GetClientRolesAsync(realm, id, first, max, q, ct));

        public class ClientVm
        {
            public string ClientId { get; set; } = default!;
            public string Realm { get; set; } = default!;
            public bool Enabled { get; set; }
            public string? Description { get; set; }

            // НУЖНЫЕ поля для Razor
            public bool ClientAuth { get; set; }   // конфиденциальный клиент?  = !publicClient
            public bool StandardFlow { get; set; }   // standardFlowEnabled
            public bool ServiceAccount { get; set; }   // serviceAccountsEnabled
        }
    }
}
