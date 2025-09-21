using Assistant.KeyCloak;
using Assistant.KeyCloak.Models;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System.Text.Json;

namespace Assistant.Pages.Clients;

[Authorize(Roles = "assistant-user")]
public class DetailsModel : PageModel
{
    private readonly ClientsService _clients;
    private readonly UserClientsRepository _repo;
    private readonly EventsService _events;

    public DetailsModel(ClientsService clients, UserClientsRepository repo, EventsService events)
    {
        _clients = clients;
        _repo = repo;
        _events = events;
    }

    [BindProperty(SupportsGet = true)] public string? Realm { get; set; }
    [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

    public ClientVm Client { get; set; } = default!;

    [BindProperty(SupportsGet = true)]
    public string? ReturnUrl { get; set; }

    public string BackUrl { get; private set; } = string.Empty;
    [BindProperty] public string? NewClientId { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public bool Enabled { get; set; }
    [BindProperty] public bool ClientAuth { get; set; }
    [BindProperty] public bool StandardFlow { get; set; }
    [BindProperty] public bool ServiceAccount { get; set; }
    [BindProperty] public string RedirectUrisJson { get; set; } = "[]";
    [BindProperty] public string LocalRolesJson { get; set; } = "[]";
    [BindProperty] public string ServiceRolesJson { get; set; } = "[]";
    public string EventTypesJson { get; set; } = "[]";

    public async Task<IActionResult> OnGetAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
        {
            return NotFound();
        }

        var details = await _clients.GetClientDetailsAsync(Realm!, ClientId!, ct);
        if (details == null)
        {
            return NotFound();
        }

        BackUrl = ResolveBackUrl(ReturnUrl);
        ReturnUrl = BackUrl;

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
        EventTypesJson = JsonSerializer.Serialize(await _events.GetEventTypesAsync(Realm!, ct));

        return Page();
    }

    public async Task<IActionResult> OnPostSaveAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
        {
            return NotFound();
        }

        var newId = NewClientId?.Trim() ?? ClientId!;
        var redirects = ClientFormUtilities.ParseStringList(RedirectUrisJson);
        var locals = ClientFormUtilities.ParseStringList(LocalRolesJson);
        var svc = ClientFormUtilities.ParseServiceRolePairs(ServiceRolesJson);

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
            svc);

        try
        {
            await _clients.UpdateClientAsync(spec, ct);
        }
        catch (Exception ex)
        {
            TempData["FlashError"] = $"Не удалось обновить клиента: {ex.Message}";
            return RedirectToPage(new { realm = Realm, clientId = ClientId, returnUrl = ReturnUrl });
        }

        TempData["FlashOk"] = "Клиент успешно обновлён.";
        return RedirectToPage(new { realm = Realm, clientId = newId, returnUrl = ReturnUrl });
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
        {
            return NotFound();
        }

        await _clients.DeleteClientAsync(Realm!, ClientId!, ct);
        await _repo.RemoveAsync(ClientId!, Realm!, ct);
        TempData["FlashOk"] = "Client deleted.";
        var backUrl = ResolveBackUrl(ReturnUrl);
        return LocalRedirect(backUrl);
    }

    public async Task<IActionResult> OnGetEventsAsync(string realm, string clientId, string? type, DateTime? from, DateTime? to, string? user, string? ip, CancellationToken ct)
    {
        var list = await _events.GetEventsAsync(realm, clientId, type, from, to, user, ip, ct: ct);
        return new JsonResult(list);
    }

    public async Task<IActionResult> OnGetRoleLookupAsync(string realm, string q, int clientFirst = 0, int clientsToScan = 25, int rolesPerClient = 10, CancellationToken ct = default)
    {
        var (hits, next) = await _clients.FindRolesAcrossClientsAsync(realm, q, clientFirst, clientsToScan, rolesPerClient, ct);
        return new JsonResult(new { hits, nextClientFirst = next });
    }

    public async Task<IActionResult> OnGetClientsSearchAsync(string realm, string q, int first = 0, int max = 20, CancellationToken ct = default)
        => new JsonResult(await _clients.SearchClientsAsync(realm, q ?? string.Empty, first, max, ct));

    public async Task<IActionResult> OnGetClientRolesAsync(string realm, string id, int first = 0, int max = 50, string? q = null, CancellationToken ct = default)
        => new JsonResult(await _clients.GetClientRolesAsync(realm, id, first, max, q, ct));

    public class ClientVm
    {
        public string ClientId { get; set; } = default!;
        public string Realm { get; set; } = default!;
        public bool Enabled { get; set; }
        public string? Description { get; set; }
        public bool ClientAuth { get; set; }
        public bool StandardFlow { get; set; }
        public bool ServiceAccount { get; set; }
    }

    private string ResolveBackUrl(string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && Url.IsLocalUrl(candidate))
        {
            return candidate;
        }

        var fallbackPage = User.IsInRole("assistant-admin") ? "/Clients/Search" : "/Index";
        return Url.Page(fallbackPage) ?? "/";
    }
}
