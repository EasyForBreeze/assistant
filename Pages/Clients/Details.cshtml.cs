using Assistant.KeyCloak;
using Assistant.KeyCloak.Models;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.Net.Http;
using System.Security.Claims;
using System.Text.Json;

namespace Assistant.Pages.Clients;

[Authorize(Roles = "assistant-user,assistant-admin")]
public class DetailsModel : PageModel
{
    private readonly ClientsService _clients;
    private readonly UserClientsRepository _repo;
    private readonly EventsService _events;
    private readonly ConfluenceWikiService _wiki;
    private readonly ClientWikiRepository _wikiPages;
    private readonly ILogger<DetailsModel> _logger;
    private readonly ConfluenceOptions _confluenceOptions;

    public DetailsModel(
        ClientsService clients,
        UserClientsRepository repo,
        EventsService events,
        ConfluenceWikiService wiki,
        ClientWikiRepository wikiPages,
        ConfluenceOptions confluenceOptions,
        ILogger<DetailsModel> logger)
    {
        _clients = clients;
        _repo = repo;
        _events = events;
        _wiki = wiki;
        _wikiPages = wikiPages;
        _confluenceOptions = confluenceOptions;
        _logger = logger;
    }

    [BindProperty(SupportsGet = true)] public string? Realm { get; set; }
    [BindProperty(SupportsGet = true)] public string? ClientId { get; set; }

    public ClientVm Client { get; set; } = default!;
    public bool CanManageClient { get; private set; }

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

        var accessResult = await EnsureClientAccessAsync(Realm!, ClientId!, ct, redirectOnFailure: true);
        if (accessResult is not null)
        {
            return accessResult;
        }

        CanManageClient = await CanCurrentUserManageClientAsync(Realm!, ClientId!, ct);

        var details = await _clients.GetClientDetailsAsync(Realm!, ClientId!, ct);
        if (details == null)
        {
            return NotFound();
        }

        BackUrl = ResolveBackUrl(ReturnUrl);
        ReturnUrl = BackUrl;

        Client = new ClientVm
        {
            ClientUuid = details.Id,
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

        var accessResult = await EnsureClientAccessAsync(Realm!, ClientId!, ct, redirectOnFailure: true);
        if (accessResult is not null)
        {
            return accessResult;
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

        string? wikiLink = null;

        try
        {
            var wikiInfo = await _wikiPages.GetAsync(spec.Realm, spec.CurrentClientId, ct);
            if (wikiInfo is not null)
            {
                wikiLink = _wiki.BuildPageUrl(wikiInfo.PageId, spec.Realm, spec.CurrentClientId);
                var payload = new ConfluenceWikiService.ClientWikiPayload(
                    Realm: spec.Realm,
                    ClientId: spec.ClientId,
                    ClientEnabled: spec.Enabled,
                    Description: Description,
                    ClientAuthEnabled: ClientAuth,
                    StandardFlowEnabled: spec.StandardFlow,
                    ServiceAccountEnabled: spec.ServiceAccount,
                    RedirectUris: redirects,
                    LocalRoles: locals,
                    ServiceRoles: svc,
                    AppName: wikiInfo.AppName,
                    AppUrl: wikiInfo.AppUrl,
                    ServiceOwner: wikiInfo.ServiceOwner,
                    ServiceManager: wikiInfo.ServiceManager);

                var updated = await _wiki.UpdatePageAsync(wikiInfo.PageId, payload, ct);
                if (updated)
                {
                    if (!string.Equals(spec.ClientId, spec.CurrentClientId, StringComparison.Ordinal))
                    {
                        await _wikiPages.RemoveAsync(spec.Realm, spec.CurrentClientId, ct);
                    }

                    var infoToPersist = wikiInfo with { Realm = spec.Realm, ClientId = spec.ClientId };
                    await _wikiPages.SetAsync(infoToPersist, ct);
                    wikiLink = _wiki.BuildPageUrl(infoToPersist.PageId, infoToPersist.Realm, infoToPersist.ClientId);
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to update Confluence wiki page for {ClientId}", spec.ClientId);
        }

        if (string.IsNullOrWhiteSpace(wikiLink))
        {
            try
            {
                var fallbackInfo = await _wikiPages.GetAsync(spec.Realm, spec.ClientId, ct);
                if (fallbackInfo is not null)
                {
                    wikiLink = _wiki.BuildPageUrl(fallbackInfo.PageId, fallbackInfo.Realm, fallbackInfo.ClientId);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to resolve Confluence wiki link for {ClientId}", spec.ClientId);
            }
        }

        TempData["FlashOk"] = BuildClientUpdatedFlashMessage(wikiLink);
        return RedirectToPage(new { realm = Realm, clientId = newId, returnUrl = ReturnUrl });
    }

    public async Task<IActionResult> OnPostDeleteAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(Realm) || string.IsNullOrWhiteSpace(ClientId))
        {
            return NotFound();
        }

        var accessResult = await EnsureClientAccessAsync(Realm!, ClientId!, ct, redirectOnFailure: true);
        if (accessResult is not null)
        {
            return accessResult;
        }

        await _clients.DeleteClientAsync(Realm!, ClientId!, ct);
        await _repo.RemoveAsync(ClientId!, Realm!, ct);
        try
        {
            await _wikiPages.RemoveAsync(Realm!, ClientId!, ct);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to remove Confluence wiki mapping for {ClientId}", ClientId);
        }
        TempData["FlashOk"] = "Client deleted.";
        var backUrl = ResolveBackUrl(ReturnUrl);
        return LocalRedirect(backUrl);
    }

    public async Task<IActionResult> OnGetEventsAsync(string realm, string clientId, string? type, DateTime? from, DateTime? to, string? user, string? ip, CancellationToken ct)
    {
        var accessResult = await EnsureClientAccessAsync(realm, clientId, ct);
        if (accessResult is not null)
        {
            return accessResult;
        }

        var list = await _events.GetEventsAsync(realm, clientId, type, from, to, user, ip, ct: ct);
        return new JsonResult(list);
    }

    public async Task<IActionResult> OnGetRoleLookupAsync(string realm, string q, int clientFirst = 0, int clientsToScan = 25, int rolesPerClient = 10, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            return Forbid();
        }

        var accessResult = await EnsureClientAccessAsync(realm, ClientId!, ct);
        if (accessResult is not null)
        {
            return accessResult;
        }

        var (hits, next) = await _clients.FindRolesAcrossClientsAsync(realm, q, clientFirst, clientsToScan, rolesPerClient, ct);
        return new JsonResult(new { hits, nextClientFirst = next });
    }

    public async Task<IActionResult> OnGetClientsSearchAsync(string realm, string q, int first = 0, int max = 20, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(ClientId))
        {
            return Forbid();
        }

        var accessResult = await EnsureClientAccessAsync(realm, ClientId!, ct);
        if (accessResult is not null)
        {
            return accessResult;
        }

        return new JsonResult(await _clients.SearchClientsAsync(realm, q ?? string.Empty, first, max, ct));
    }

    public async Task<IActionResult> OnGetClientRolesAsync(string realm, string id, int first = 0, int max = 50, string? q = null, CancellationToken ct = default)
    {
        var accessResult = await EnsureClientAccessAsync(realm, id, ct);
        if (accessResult is not null)
        {
            return accessResult;
        }

        return new JsonResult(await _clients.GetClientRolesAsync(realm, id, first, max, q, ct));
    }

    private async Task<IActionResult?> EnsureClientAccessAsync(string realm, string clientId, CancellationToken ct, bool redirectOnFailure = false)
    {
        if (User.IsInRole("assistant-admin"))
        {
            return null;
        }

        if (!User.IsInRole("assistant-user"))
        {
            return Forbid();
        }

        var username = GetUserName();
        if (string.IsNullOrWhiteSpace(username))
        {
            return Forbid();
        }

        var hasAccess = await _repo.HasAccessAsync(username, realm, clientId, ct);
        if (hasAccess)
        {
            return null;
        }

        if (redirectOnFailure)
        {
            return RedirectToPage("AccessDenied");
        }

        return Forbid();
    }

    private async Task<bool> CanCurrentUserManageClientAsync(string realm, string clientId, CancellationToken ct)
    {
        if (User.IsInRole("assistant-admin"))
        {
            return true;
        }

        if (!User.IsInRole("assistant-user"))
        {
            return false;
        }

        var username = GetUserName();
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return await _repo.HasAccessAsync(username, realm, clientId, ct);
    }

    private string BuildClientUpdatedFlashMessage(string? wikiLink)
    {
        var message = "Клиент успешно обновлён.";
        if (!string.IsNullOrWhiteSpace(wikiLink))
        {
            var encodedLink = System.Net.WebUtility.HtmlEncode(wikiLink);
            message += $" <a href=\"{encodedLink}\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть страницу в Confluence</a>.";
        }

        return message;
    }

    public async Task<IActionResult> OnGetGenerateTokenAsync(
        string realm,
        string clientId,
        string? clientUuid,
        string username,
        string type,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(clientId))
        {
            return BadRequest(new { error = "Параметры realm и clientId обязательны." });
        }

        var accessResult = await EnsureClientAccessAsync(realm, clientId, ct);
        if (accessResult is not null)
        {
            return accessResult;
        }

        username = (username ?? string.Empty).Trim();
        if (username.Length == 0)
        {
            return BadRequest(new { error = "Введите username пользователя." });
        }

        if (!Enum.TryParse<TokenExampleKind>(type, ignoreCase: true, out var tokenKind))
        {
            return BadRequest(new { error = "Неизвестный тип токена." });
        }

        var resolvedClientUuid = string.IsNullOrWhiteSpace(clientUuid) ? null : clientUuid;
        if (resolvedClientUuid == null)
        {
            var clientDetails = await _clients.GetClientDetailsAsync(realm, clientId, ct);
            if (clientDetails == null)
            {
                return NotFound(new { error = "Клиент не найден." });
            }

            resolvedClientUuid = clientDetails.Id;
        }

        var userId = await _clients.FindUserIdByUsernameAsync(realm, username, ct);
        if (string.IsNullOrWhiteSpace(userId))
        {
            return NotFound(new { error = $"Пользователь '{username}' не найден." });
        }

        string rawPayload;
        try
        {
            rawPayload = await _clients.GenerateExampleTokenAsync(realm, resolvedClientUuid!, userId!, tokenKind, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = ex.Message });
        }
        catch (InvalidOperationException ex)
        {
            return BadRequest(new { error = ex.Message });
        }
        catch (HttpRequestException ex)
        {
            return StatusCode(StatusCodes.Status502BadGateway, new { error = $"Ошибка запроса к Keycloak: {ex.Message}" });
        }
        catch (Exception ex)
        {
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = $"Неожиданная ошибка: {ex.Message}" });
        }

        object payload;
        bool isJson;

        if (string.IsNullOrWhiteSpace(rawPayload))
        {
            payload = string.Empty;
            isJson = false;
        }
        else
        {
            try
            {
                using var doc = JsonDocument.Parse(rawPayload);
                payload = doc.RootElement.Clone();
                isJson = true;
            }
            catch (JsonException)
            {
                payload = rawPayload;
                isJson = false;
            }
        }

        return new JsonResult(new
        {
            tokenType = tokenKind.ToString(),
            username,
            userId,
            payload,
            isJson
        });
    }

    private string? GetUserName()
    {
        return User.Identity?.Name
               ?? User.FindFirst("preferred_username")?.Value
               ?? User.FindFirst(ClaimTypes.Name)?.Value
               ?? User.FindFirst(ClaimTypes.Email)?.Value;
    }

    public class ClientVm
    {
        public string ClientUuid { get; set; } = default!;
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
