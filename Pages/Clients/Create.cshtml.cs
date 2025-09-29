using Assistant.KeyCloak;
using Assistant.KeyCloak.Models;
using Assistant.Interfaces;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Configuration;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Net.Mail;
using System.Text;

namespace Assistant.Pages.Clients;

[Authorize(Roles = "assistant-user,assistant-admin")]
public class CreateModel : PageModel
{
    private readonly RealmsService _realms;
    private readonly ClientsService _clients;
    private readonly UserClientsRepository _repo;
    private readonly ServiceRoleExclusionsRepository _exclusions;
    private readonly ConfluenceWikiService _wiki;
    private readonly ClientWikiRepository _wikiPages;
    private readonly ClientSecretDistributionService _secretDistribution;
    private readonly RealmLinkProvider _realmLinks;
    private readonly ILogger<CreateModel> _logger;
    private readonly string _kcBaseUrl;

    public CreateModel(
        RealmsService realms,
        ClientsService clients,
        UserClientsRepository repo,
        ServiceRoleExclusionsRepository exclusions,
        ConfluenceWikiService wiki,
        ClientWikiRepository wikiPages,
        ClientSecretDistributionService secretDistribution,
        RealmLinkProvider realmLinks,
        IConfiguration configuration,
        ILogger<CreateModel> logger)
    {
        _realms = realms;
        _clients = clients;
        _repo = repo;
        _exclusions = exclusions;
        _wiki = wiki;
        _wikiPages = wikiPages;
        _secretDistribution = secretDistribution;
        _realmLinks = realmLinks;
        _logger = logger;
        _kcBaseUrl = (configuration["Keycloak:BaseUrl"] ?? string.Empty).TrimEnd('/');
    }

    public int StepToShow { get; set; }
    public bool IsAdmin { get; private set; }
    public bool ShowSuccessModal { get; private set; }
    public string? ModalMessage { get; private set; }
    public string? SecretArchiveBase64 { get; private set; }
    public string? SecretArchiveFileName { get; private set; }
    public string? SecretArchiveContentType { get; private set; }

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
    [BindProperty] public string? CreatioSecretEmail { get; set; }

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
        if (Hit(nameof(CreatioRequestNumber)) || Hit(nameof(CreatioSecretEmail))) return 8;
        return 1;
    }

    private async Task LoadViewDataAsync()
    {
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
        IsAdmin = User.IsInRole("assistant-admin");
        ShowSuccessModal = false;
        ModalMessage = null;
        SecretArchiveBase64 = null;
        SecretArchiveFileName = null;
        SecretArchiveContentType = null;

        CreatioRequestNumber = CreatioRequestNumber?.Trim();
        CreatioSecretEmail = CreatioSecretEmail?.Trim();

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

        if (!IsAdmin && string.Equals(Realm, "master", StringComparison.OrdinalIgnoreCase))
        {
            ModelState.AddModelError(nameof(Realm), "Realm 'master' недоступен.");
        }

        if (FlowService)
        {
            ClientAuth = true;
        }

        if (IsAdmin && ClientAuth)
        {
            if (string.IsNullOrWhiteSpace(CreatioRequestNumber))
            {
                ModelState.AddModelError(nameof(CreatioRequestNumber), "Укажите номер заявки Creatio.");
            }

            if (!IsValidEmailAddress(CreatioSecretEmail))
            {
                ModelState.AddModelError(nameof(CreatioSecretEmail), "Укажите корректный email.");
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
        var restrictedSnapshot = await _exclusions.GetAllAsync(ct);
        var badSvc = ClientFormUtilities.FindInvalidServiceRoleEntries(svcEntries, restrictedSnapshot.ClientIds).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            if (!string.IsNullOrEmpty(username) && !IsAdmin)
            {
                await _repo.AddAsync(username, summary, ct);
            }

            var pageId = await _wiki.CreatePageAsync(new ConfluenceWikiService.ClientWikiPayload(
                Realm: spec.Realm,
                ClientId: spec.ClientId,
                ClientEnabled: true,
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

            string? wikiLink = null;
            if (!string.IsNullOrWhiteSpace(pageId))
            {
                wikiLink = _wiki.BuildPageUrl(pageId, spec.Realm, spec.ClientId);
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
            }
            var flashMessage = BuildClientCreatedFlashMessage(wikiLink);
            if (!IsAdmin)
            {
                TempData["FlashOk"] = flashMessage;
                return RedirectToPage("/Index");
            }

            string? distributionError = null;
            var secretPrepared = false;
            if (spec.ClientAuthentication)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(CreatioSecretEmail) || string.IsNullOrWhiteSpace(CreatioRequestNumber))
                    {
                        throw new InvalidOperationException("Не заполнены данные для доставки client_secret.");
                    }

                    var secret = await _clients.GetClientSecretAsync(spec.Realm, spec.ClientId, ct);
                    if (string.IsNullOrWhiteSpace(secret))
                    {
                        throw new InvalidOperationException("Не удалось получить secret клиента.");
                    }

                    var archive = await _secretDistribution.CreateAsync(
                        spec.ClientId,
                        secret,
                        CreatioSecretEmail!,
                        CreatioRequestNumber!,
                        ct);

                    SecretArchiveFileName = archive.FileName;
                    SecretArchiveContentType = archive.ContentType;
                    SecretArchiveBase64 = Convert.ToBase64String(archive.Content);
                    secretPrepared = true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Failed to distribute client secret for {ClientId}", spec.ClientId);
                    distributionError = $"Клиент создан, но не удалось подготовить архив с client_secret: {ex.Message}";
                }
            }

            var secretLine = spec.ClientAuthentication
                ? (secretPrepared
                    ? "Secret: в архиве, пароль от архива выслан на почту"
                    : "Secret: не удалось подготовить архив, обратитесь к администратору.")
                : "Secret: клиент публичный, secret отсутствует.";

            ModalMessage = BuildModalMessage(spec, wikiLink, secretLine);
            ShowSuccessModal = true;
            StepToShow = 1;

            var currentRealm = Realm;
            ClientId = null;
            Description = null;
            ClientAuth = false;
            FlowStandard = false;
            FlowService = false;
            RedirectUrisJson = JsonSerializer.Serialize(Array.Empty<string>());
            LocalRolesJson = JsonSerializer.Serialize(Array.Empty<string>());
            ServiceRolesJson = JsonSerializer.Serialize(Array.Empty<string>());
            AppName = null;
            AppUrl = null;
            ServiceOwner = null;
            ServiceManager = null;
            CreatioRequestNumber = null;
            CreatioSecretEmail = null;
            Realm = currentRealm;

            ModelState.Clear();

            if (!string.IsNullOrEmpty(distributionError))
            {
                ModelState.AddModelError(string.Empty, distributionError);
            }

            return Page();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create Keycloak client or wiki page for {ClientId}", ClientId);
            ModelState.AddModelError(string.Empty, $"Ошибка создания клиента: {ex.Message}");
            StepToShow = DetermineStepFromErrors(ModelState);
            return Page();
        }
    }

    private static bool IsValidEmailAddress(string? value)
        => !string.IsNullOrWhiteSpace(value) && MailAddress.TryCreate(value, out _);

    private string BuildClientCreatedFlashMessage(string? wikiLink)
    {
        var message = "Клиент успешно создан.";
        if (!string.IsNullOrWhiteSpace(wikiLink))
        {
            var encodedLink = System.Net.WebUtility.HtmlEncode(wikiLink);
            message += $" <a href=\"{encodedLink}\" target=\"_blank\" rel=\"noopener noreferrer\">Открыть страницу в Confluence</a>.";
        }

        return message;
    }

    private string BuildModalMessage(NewClientSpec spec, string? wikiLink, string secretLine)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Создана конфигурация в TEST среде");

        var issuer = ComposeRealmIssuer(spec.Realm);
        if (!string.IsNullOrWhiteSpace(issuer))
        {
            sb.AppendLine($"Base URL: {issuer}");
        }

        sb.AppendLine($"Realm: {spec.Realm}");
        sb.AppendLine($"ClientID: {spec.ClientId}");
        sb.AppendLine(secretLine);

        if (!string.IsNullOrWhiteSpace(wikiLink))
        {
            sb.AppendLine($"Ссылка на конфигурацию в реестре: {wikiLink}");
        }

        var endpoints = ComposeEndpointsUrl(spec.Realm);
        if (!string.IsNullOrWhiteSpace(endpoints))
        {
            sb.AppendLine($"Endpoints: {endpoints}");
        }

        return sb.ToString().TrimEnd();
    }

    private string ComposeRealmIssuer(string realm)
    {
        if (string.IsNullOrWhiteSpace(realm))
        {
            return string.Empty;
        }

        if (!string.IsNullOrWhiteSpace(_kcBaseUrl))
        {
            return NormalizeIssuer(_kcBaseUrl, realm);
        }

        if (_realmLinks.TryGetRealmLink(realm, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
        {
            return NormalizeIssuer(mapped, realm);
        }

        return realm;
    }

    private static string NormalizeIssuer(string baseUrl, string realm)
    {
        var normalized = baseUrl.TrimEnd('/');
        return normalized.Contains("/realms/", StringComparison.OrdinalIgnoreCase)
            ? normalized
            : $"{normalized}/realms/{realm}";
    }

    private string ComposeEndpointsUrl(string realm)
    {
        var issuer = ComposeRealmIssuer(realm);
        if (string.IsNullOrWhiteSpace(issuer))
        {
            return string.Empty;
        }

        return $"{issuer.TrimEnd('/')}/.well-known/openid-configuration";
    }

}
