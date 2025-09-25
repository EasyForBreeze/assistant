using Assistant.KeyCloak;
using Assistant.KeyCloak.Models;
using Assistant.Interfaces;
using Assistant.Services;
using ICSharpCode.SharpZipLib.Zip;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.ComponentModel.DataAnnotations;
using System.Security.Cryptography;
using System.Text;

namespace Assistant.Pages.Clients;

[Authorize(Roles = "assistant-user,assistant-admin")]
public class CreateModel : PageModel
{
    private static readonly EmailAddressAttribute EmailValidator = new();

    private readonly RealmsService _realms;
    private readonly ClientsService _clients;
    private readonly UserClientsRepository _repo;
    private readonly ServiceRoleExclusionsRepository _exclusions;
    private readonly ConfluenceWikiService _wiki;
    private readonly ClientWikiRepository _wikiPages;
    private readonly ISimpleEmailSender _emailSender;
    private readonly ConfluenceOptions _confluenceOptions;
    private readonly RealmLinkProvider _realmLinks;
    private readonly AdminApiOptions _adminOptions;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(
        RealmsService realms,
        ClientsService clients,
        UserClientsRepository repo,
        ServiceRoleExclusionsRepository exclusions,
        ConfluenceWikiService wiki,
        ClientWikiRepository wikiPages,
        ISimpleEmailSender emailSender,
        ConfluenceOptions confluenceOptions,
        RealmLinkProvider realmLinks,
        IOptions<AdminApiOptions> adminOptions,
        ILogger<CreateModel> logger)
    {
        _realms = realms;
        _clients = clients;
        _repo = repo;
        _exclusions = exclusions;
        _wiki = wiki;
        _wikiPages = wikiPages;
        _emailSender = emailSender;
        _confluenceOptions = confluenceOptions;
        _realmLinks = realmLinks;
        _adminOptions = adminOptions.Value;
        _logger = logger;
    }

    public int StepToShow { get; set; }

    public bool IsAdmin { get; private set; }

    [BindProperty] public string? Realm { get; set; }
    public List<SelectListItem> RealmOptions { get; private set; } = new();
    public string RealmMapJson { get; private set; } = "{}";

    [BindProperty] public string? ClientId { get; set; }
    [BindProperty] public string? Description { get; set; }
    [BindProperty] public bool ClientAuth { get; set; }
    [BindProperty] public bool FlowStandard { get; set; }
    [BindProperty] public bool FlowService { get; set; }

    [BindProperty] public string? RedirectUrisJson { get; set; }
    [BindProperty] public string? LocalRolesJson { get; set; }
    [BindProperty] public string? ServiceRolesJson { get; set; }

    [BindProperty] public string? AppName { get; set; }
    [BindProperty] public string? AppUrl { get; set; }
    [BindProperty] public string? ServiceOwner { get; set; }
    [BindProperty] public string? ServiceManager { get; set; }

    [BindProperty] public string? CreatioRequestNumber { get; set; }
    [BindProperty] public string? ArchivePasswordEmail { get; set; }

    public string? ArchiveBase64 { get; private set; }
    public string? ArchiveFileName { get; private set; }
    public AdminCreationSummary? SuccessInfo { get; private set; }

    public sealed record AdminCreationSummary(string BaseUrl, string Realm, string ClientId, string? WikiUrl, string? EndpointsUrl);

    public async Task OnGet()
    {
        IsAdmin = User.IsInRole("assistant-admin");
        await LoadViewDataAsync();
    }

    private static bool IsValidClientId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (id.Length < 10 || id.Length > 80) return false;
        if (!id.StartsWith("app-bank-", StringComparison.OrdinalIgnoreCase)) return false;
        var slug = id["app-bank-".Length..];
        return Regex.IsMatch(slug, "^[a-z0-9-]+$", RegexOptions.IgnoreCase);
    }

    private static int DetermineStepFromErrors(ModelStateDictionary modelState)
    {
        var min = int.MaxValue;
        foreach (var entry in modelState)
        {
            if (entry.Value?.Errors?.Count > 0)
            {
                var key = entry.Key ?? string.Empty;
                min = Math.Min(min, MapFieldToStep(key));
            }
        }

        return min == int.MaxValue ? 1 : min;
    }

    private static int MapFieldToStep(string key)
    {
        bool Hit(string name) =>
            key.Equals(name, StringComparison.OrdinalIgnoreCase)
            || key.StartsWith(name + "[", StringComparison.OrdinalIgnoreCase)
            || key.Equals(name + "Json", StringComparison.OrdinalIgnoreCase);

        if (Hit(nameof(Realm))) return 1;
        if (Hit(nameof(ClientId)) || Hit(nameof(Description))) return 2;
        if (Hit(nameof(AppName)) || Hit(nameof(AppUrl)) || Hit(nameof(ServiceOwner)) || Hit(nameof(ServiceManager))) return 3;
        if (Hit(nameof(ClientAuth)) || Hit(nameof(FlowStandard)) || Hit(nameof(FlowService))) return 4;
        if (Hit(nameof(RedirectUrisJson))) return 5;
        if (Hit(nameof(LocalRolesJson))) return 6;
        if (Hit(nameof(ServiceRolesJson))) return 7;
        if (Hit(nameof(CreatioRequestNumber)) || Hit(nameof(ArchivePasswordEmail))) return 8;
        return 1;
    }

    private async Task LoadViewDataAsync()
    {
        IsAdmin = User.IsInRole("assistant-admin");
        var names = await _realms.GetRealmsAsync();
        if (User.IsInRole("assistant-user") && !User.IsInRole("assistant-admin"))
        {
            names = names.Where(n => !string.Equals(n.Realm, "master", StringComparison.OrdinalIgnoreCase)).ToList();
        }

        RealmOptions = names.Select(n => new SelectListItem { Value = n.Realm, Text = n.Realm }).ToList();
        Realm ??= RealmOptions.FirstOrDefault()?.Value;
        var dict = names.ToDictionary(r => r.Realm, r => r.DisplayName ?? string.Empty);
        RealmMapJson = JsonSerializer.Serialize(dict);
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

    public async Task<IActionResult> OnPostCreate(CancellationToken ct)
    {
        ArchiveBase64 = null;
        ArchiveFileName = null;
        SuccessInfo = null;
        IsAdmin = User.IsInRole("assistant-admin");
        CreatioRequestNumber = string.IsNullOrWhiteSpace(CreatioRequestNumber) ? null : CreatioRequestNumber.Trim();
        ArchivePasswordEmail = string.IsNullOrWhiteSpace(ArchivePasswordEmail) ? null : ArchivePasswordEmail.Trim();

        await LoadViewDataAsync();

        if (string.IsNullOrWhiteSpace(Realm))
        {
            ModelState.AddModelError(nameof(Realm), "Выберите realm.");
        }

        if (!IsValidClientId(ClientId))
        {
            ModelState.AddModelError(nameof(ClientId), "Client ID должен начинаться с 'app-bank-' и содержать латиницу/цифры/дефисы (10–80 символов).");
        }

        if (string.IsNullOrWhiteSpace(AppName) || AppName!.Trim().Length < 3)
        {
            ModelState.AddModelError(nameof(AppName), "Название АС обязательно (минимум 3 символа).");
        }

        if (!string.IsNullOrWhiteSpace(AppUrl) && !ClientFormUtilities.IsValidHttpUrl(AppUrl!))
        {
            ModelState.AddModelError(nameof(AppUrl), "Укажите корректный http/https URL.");
        }

        if (!await _realms.RealmExistsAsync(Realm ?? string.Empty))
        {
            ModelState.AddModelError(nameof(Realm), "Такого realm не существет.");
        }

        if (!User.IsInRole("assistant-admin") && string.Equals(Realm, "master", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Realm), "Realm 'master' недоступен.");
        }

        if (FlowService)
        {
            ClientAuth = true;
        }

        var requiresAdminExtras = IsAdmin && ClientAuth;
        if (requiresAdminExtras)
        {
            if (string.IsNullOrWhiteSpace(CreatioRequestNumber))
            {
                ModelState.AddModelError(nameof(CreatioRequestNumber), "Укажите номер заявки Creatio.");
            }

            if (string.IsNullOrWhiteSpace(ArchivePasswordEmail))
            {
                ModelState.AddModelError(nameof(ArchivePasswordEmail), "Укажите email для пароля.");
            }
            else if (!EmailValidator.IsValid(ArchivePasswordEmail))
            {
                ModelState.AddModelError(nameof(ArchivePasswordEmail), "Некорректный email.");
            }
        }

        var redirects = ClientFormUtilities.NormalizeDistinct(ClientFormUtilities.ParseStringList(RedirectUrisJson));
        RedirectUrisJson = JsonSerializer.Serialize(redirects);
        var badRedirects = ClientFormUtilities.ValidateRedirects(redirects).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (badRedirects.Count > 0)
        {
            ModelState.AddModelError(nameof(RedirectUrisJson), $"Некорректные Redirect URI: {string.Join(", ", badRedirects)}");
        }

        if (FlowStandard && redirects.Count == 0)
        {
            ModelState.AddModelError(nameof(RedirectUrisJson), "Для Standard flow требуется минимум один Redirect URI.");
        }

        var locals = ClientFormUtilities.NormalizeDistinct(ClientFormUtilities.ParseStringList(LocalRolesJson));
        LocalRolesJson = JsonSerializer.Serialize(locals);
        var badLocal = ClientFormUtilities.FindInvalidLocalRoles(locals).ToList();
        if (badLocal.Any())
        {
            ModelState.AddModelError(nameof(LocalRolesJson), $"Некорректные локальные роли: {string.Join(", ", badLocal)}");
        }

        if (locals.Count > 10)
        {
            ModelState.AddModelError(nameof(LocalRolesJson), "Слишком много локальных ролей (максимум 10).");
        }

        var svcEntries = ClientFormUtilities.NormalizeDistinct(ClientFormUtilities.ParseStringList(ServiceRolesJson));
        ServiceRolesJson = JsonSerializer.Serialize(svcEntries);
        var restrictedClients = await _exclusions.GetAllAsync(ct);
        var badSvc = ClientFormUtilities.FindInvalidServiceRoleEntries(svcEntries, restrictedClients).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (badSvc.Any())
        {
            ModelState.AddModelError(nameof(ServiceRolesJson), $"Некорректные сервисные роли: {string.Join(", ", badSvc)}");
        }

        if (!ModelState.IsValid)
        {
            StepToShow = DetermineStepFromErrors(ModelState);
            return Page();
        }

        var serviceRolePairs = ClientFormUtilities.ParseServiceRolePairs(ServiceRolesJson);
        var spec = new NewClientSpec(
            Realm: Realm!,
            ClientId: ClientId!,
            Description: Description,
            ClientAuthentication: ClientAuth,
            StandardFlow: FlowStandard,
            ServiceAccount: FlowService,
            RedirectUris: redirects,
            LocalRoles: locals,
            ServiceRoles: serviceRolePairs
        );

        try
        {
            var createdId = await _clients.CreateClientAsync(spec, ct);

            var normalizedAppName = string.IsNullOrWhiteSpace(AppName) ? null : AppName!.Trim();
            var normalizedAppUrl = string.IsNullOrWhiteSpace(AppUrl) ? null : AppUrl!.Trim();
            var normalizedOwner = string.IsNullOrWhiteSpace(ServiceOwner) ? null : ServiceOwner!.Trim();
            var normalizedManager = string.IsNullOrWhiteSpace(ServiceManager) ? null : ServiceManager!.Trim();

            var summary = new ClientSummary(
                Name: normalizedAppName ?? spec.ClientId,
                ClientId: spec.ClientId,
                Realm: spec.Realm,
                Enabled: true,
                FlowStandard: spec.StandardFlow,
                FlowService: spec.ServiceAccount);
            var username = User.Identity?.Name ?? string.Empty;
            if (!string.IsNullOrEmpty(username) && !User.IsInRole("assistant-admin"))
            {
                await _repo.AddAsync(username, summary, ct);
            }

            var pageId = await _wiki.CreatePageAsync(new ConfluenceWikiService.ClientWikiPayload(
                Realm: spec.Realm,
                ClientId: spec.ClientId,
                Description: Description,
                ClientAuthEnabled: ClientAuth,
                StandardFlowEnabled: spec.StandardFlow,
                ServiceAccountEnabled: spec.ServiceAccount,
                RedirectUris: redirects,
                LocalRoles: locals,
                ServiceRoles: serviceRolePairs,
                AppName: normalizedAppName,
                AppUrl: normalizedAppUrl,
                ServiceOwner: normalizedOwner,
                ServiceManager: normalizedManager),
                ct);

            string? wikiUrl = null;
            if (!string.IsNullOrWhiteSpace(pageId))
            {
                try
                {
                    await _wikiPages.SetAsync(new ClientWikiRepository.ClientWikiInfo(
                        spec.Realm,
                        spec.ClientId,
                        pageId,
                        normalizedAppName,
                        normalizedAppUrl,
                        normalizedOwner,
                        normalizedManager), ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to persist Confluence wiki mapping for {ClientId}", spec.ClientId);
                }

                wikiUrl = BuildWikiUrl(pageId);
            }

            string? endpointsUrl = null;
            if (_realmLinks.TryGetRealmLink(spec.Realm, out var link))
            {
                endpointsUrl = link;
            }

            if (IsAdmin)
            {
                var baseUrl = (_adminOptions.BaseUrl ?? string.Empty).Trim();
                SuccessInfo = new AdminCreationSummary(baseUrl, spec.Realm, spec.ClientId, wikiUrl, endpointsUrl);
            }

            if (requiresAdminExtras)
            {
                var secret = await _clients.GetClientSecretAsync(spec.Realm, spec.ClientId, ct);
                if (string.IsNullOrWhiteSpace(secret))
                {
                    ModelState.AddModelError(string.Empty, "Не удалось получить secret созданного клиента.");
                    StepToShow = DetermineStepFromErrors(ModelState);
                    return Page();
                }

                var password = GenerateSecurePassword();
                var archiveBytes = BuildSecretArchive(spec.ClientId, secret, password);
                var archiveName = $"{spec.ClientId}.zip";
                var requestNumber = CreatioRequestNumber ?? string.Empty;
                var subject = $"Пароль от архива по заявке - {requestNumber}";
                var body = $"Добрый день пароль от архива - {password}";

                try
                {
                    await _emailSender.SendAsync(ArchivePasswordEmail!, subject, body, ct);
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to send archive password email for {ClientId}", spec.ClientId);
                    ModelState.AddModelError(nameof(ArchivePasswordEmail), "Не удалось отправить письмо с паролем. Попробуйте другой email или повторите позднее.");
                    StepToShow = DetermineStepFromErrors(ModelState);
                    return Page();
                }

                ArchiveBase64 = Convert.ToBase64String(archiveBytes);
                ArchiveFileName = archiveName;
            }

            if (IsAdmin)
            {
                ModelState.Clear();
                StepToShow = requiresAdminExtras ? 8 : 1;
                return Page();
            }

            TempData["FlashOk"] = $"Клиент '{spec.ClientId}' создан (id={createdId}).";
            return RedirectToPage("/Index");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Keycloak client or wiki page for {ClientId}", ClientId);
            ModelState.AddModelError(string.Empty, $"Ошибка создания клиента: {ex.Message}");
            StepToShow = DetermineStepFromErrors(ModelState);
            return Page();
        }
    }

    private static string GenerateSecurePassword()
    {
        Span<byte> buffer = stackalloc byte[24];
        RandomNumberGenerator.Fill(buffer);
        return Convert.ToBase64String(buffer);
    }

    private static byte[] BuildSecretArchive(string clientId, string secret, string password)
    {
        using var memory = new MemoryStream();
        using (var zip = new ZipOutputStream(memory))
        {
            zip.SetLevel(9);
            zip.Password = password;
            var entry = new ZipEntry($"{clientId}.txt")
            {
                DateTime = DateTime.UtcNow,
                Size = Encoding.UTF8.GetByteCount(secret)
            };
            zip.PutNextEntry(entry);
            var data = Encoding.UTF8.GetBytes(secret);
            zip.Write(data, 0, data.Length);
            zip.CloseEntry();
            zip.IsStreamOwner = false;
        }

        return memory.ToArray();
    }

    private string? BuildWikiUrl(string? pageId)
    {
        if (string.IsNullOrWhiteSpace(pageId))
        {
            return null;
        }

        var baseUrl = _confluenceOptions.BaseUrl?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return null;
        }

        var spaceKey = _confluenceOptions.SpaceKey?.Trim();
        if (!string.IsNullOrWhiteSpace(spaceKey))
        {
            return $"{baseUrl}/spaces/{spaceKey}/pages/{pageId}";
        }

        return $"{baseUrl}/pages/{pageId}";
    }
}
