// Pages/Clients/Details.cshtml.cs
using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Collections.Generic;
using System.Text.Json;
using System.Linq;

namespace Assistant.Pages.Clients
{
    public class DetailsModel : PageModel
    {
        private readonly ClientsService _clients;
        private readonly UserClientsRepository _repo;

        public DetailsModel(ClientsService clients, UserClientsRepository repo)
        {
            _clients = clients;
            _repo = repo;
        }

        // Параметры из query: ?realm=...&clientId=...
        [BindProperty(SupportsGet = true)] public string? Realm { get; set; }
        [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

        // То самое, что используется в cshtml: client?.ClientAuth и т.д.
        public ClientVm Client { get; set; } = default!;
        [BindProperty] public string? NewClientId { get; set; }
        [BindProperty] public string? Description { get; set; }
        [BindProperty] public bool Enabled { get; set; }
        [BindProperty] public bool ClientAuth { get; set; }
        [BindProperty] public bool StandardFlow { get; set; }
        [BindProperty] public bool ServiceAccount { get; set; }
        [BindProperty] public string RedirectUrisJson { get; set; } = "[]";
        [BindProperty] public string LocalRolesJson { get; set; } = "[]";
        [BindProperty] public string ServiceRolesJson { get; set; } = "[]";
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
            NewClientId = details.ClientId;
            Description = details.Description;
            Enabled = details.Enabled;
            ClientAuth = details.ClientAuth;
            StandardFlow = details.StandardFlow;
            ServiceAccount = details.ServiceAccount;

            RedirectUrisJson = JsonSerializer.Serialize(details.RedirectUris);
            LocalRolesJson = JsonSerializer.Serialize(details.LocalRoles);
            ServiceRolesJson = JsonSerializer.Serialize(details.ServiceRoles.Select(p => $"{p.ClientId}: {p.Role}"));
            // DefaultScopesJson = JsonSerializer.Serialize(details.DefaultScopes);

            return Page();
        }

        public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
                return NotFound();

            string newId = NewClientId?.Trim() ?? ClientId!;
            var redirects = TryParseList(RedirectUrisJson);
            var locals = TryParseList(LocalRolesJson);
            var svc = ParseServiceRoles(ServiceRolesJson);

            var spec = new UpdateClientSpec(
                Realm!,
                ClientId!,
                newId,
                Enabled,
                Description,
                ClientAuth,
                StandardFlow,
                ServiceAccount,
                redirects,
                locals,
                svc
            );

            await _clients.UpdateClientAsync(spec, ct);
            TempData["Flash"] = "Changes saved.";
            return RedirectToPage(new { realm = Realm, clientId = newId });
        }

        public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
                return NotFound();

            await _clients.DeleteClientAsync(Realm!, ClientId!, ct);
            await _repo.RemoveAsync(ClientId!, Realm!, ct);
            TempData["FlashOk"] = "Client deleted.";
            return RedirectToPage("/Index");
        }

        private static List<string> TryParseList(string? json)
        {
            try
            {
                return string.IsNullOrWhiteSpace(json)
                    ? new List<string>()
                    : (JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>());
            }
            catch { return new List<string>(); }
        }

        private static List<(string ClientId, string Role)> ParseServiceRoles(string? json)
        {
            var list = new List<(string, string)>();
            foreach (var s in TryParseList(json))
            {
                var parts = s.Split(':', 2);
                if (parts.Length == 2)
                {
                    var cid = parts[0].Trim();
                    var role = parts[1].Trim();
                    if (!string.IsNullOrWhiteSpace(cid) && !string.IsNullOrWhiteSpace(role))
                        list.Add((cid, role));
                }
            }
            return list;
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
