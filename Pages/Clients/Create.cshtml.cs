using Assistant.KeyCloak;
using Assistant.Interfaces;
using Assistant.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Assistant.Pages.Clients
{
    public class CreateModel : PageModel
    {
        private readonly RealmsService _realms;
        private readonly ClientsService _clients;
        private readonly UserClientsRepository _repo;

        public CreateModel(RealmsService realms, ClientsService clients, UserClientsRepository repo)
        {
            _realms = realms;
            _clients = clients;
            _repo = repo;
        }

        public int StepToShow { get; set; } = 0;

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

        [BindProperty] public string? AppName { get; set; }       // Название АС (обяз.)
        [BindProperty] public string? AppUrl { get; set; }        // Ссылка на АС
        [BindProperty] public string? ServiceOwner { get; set; }  // Владелец сервиса
        [BindProperty] public string? ServiceManager { get; set; }// Менеджер сервиса

        public async Task OnGet() => await LoadViewDataAsync();

        // ===== helpers =====
        private static List<string> NormalizeList(IEnumerable<string> items)
            => items.Select(s => (s ?? "").Trim())
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();

        private static bool IsValidClientId(string? id)
        {
            if (string.IsNullOrWhiteSpace(id)) return false;
            if (id.Length < 10 || id.Length > 80) return false;
            if (!id.StartsWith("app-bank-", StringComparison.OrdinalIgnoreCase)) return false;
            var slug = id["app-bank-".Length..];
            return Regex.IsMatch(slug, "^[a-z0-9-]+$", RegexOptions.IgnoreCase);
        }

        private static int DetermineStepFromErrors(ModelStateDictionary ms)
        {
            int min = int.MaxValue;
            foreach (var kv in ms)
            {
                if (kv.Value?.Errors?.Count > 0)
                {
                    var key = kv.Key ?? string.Empty;
                    min = Math.Min(min, MapFieldToStep(key));
                }
            }
            return (min == int.MaxValue) ? 1 : min;
        }

        private static int MapFieldToStep(string key)
        {
            bool Hit(string name) =>
                key.Equals(name, StringComparison.OrdinalIgnoreCase)
                || key.StartsWith(name + "[", StringComparison.OrdinalIgnoreCase)
                || key.Equals(name + "Json", StringComparison.OrdinalIgnoreCase);

            if (Hit(nameof(Realm))) return 1;                               // Step 1
            if (Hit(nameof(ClientId)) || Hit(nameof(Description))) return 2; // Step 2
            if (Hit(nameof(AppName)) || Hit(nameof(AppUrl)) ||
                Hit(nameof(ServiceOwner)) || Hit(nameof(ServiceManager))) return 3; // Step 3
            if (Hit(nameof(ClientAuth)) || Hit(nameof(FlowStandard)) || Hit(nameof(FlowService))) return 4; // Step 4
            if (Hit(nameof(RedirectUrisJson))) return 5;                    // Step 5
            if (Hit(nameof(LocalRolesJson))) return 6;                      // Step 6
            if (Hit(nameof(ServiceRolesJson))) return 7;                    // Step 7
            return 1;
        }

        private static bool IsValidHttpUrl(string url)
            => Uri.TryCreate(url, UriKind.Absolute, out var u)
               && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

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

        private async Task LoadViewDataAsync()
        {
            var names = await _realms.GetRealmsAsync();
            RealmOptions = names.Select(n => new SelectListItem { Value = n.Realm, Text = n.Realm }).ToList();
            Realm ??= RealmOptions.FirstOrDefault()?.Value;
            var dict = names.ToDictionary(r => r.Realm, r => r.DisplayName ?? "");
            RealmMapJson = JsonSerializer.Serialize(dict);
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

        // ===== Создание клиента =====
        public async Task<IActionResult> OnPostCreate(CancellationToken ct)
        {
            await LoadViewDataAsync(); // чтобы при ошибке повторно отрисовать селекты/описания

            // 1) Базовая валидация
            if (string.IsNullOrWhiteSpace(Realm))
                ModelState.AddModelError(nameof(Realm), "Выберите realm.");

            if (!IsValidClientId(ClientId))
                ModelState.AddModelError(nameof(ClientId), "Client ID должен начинаться с 'app-bank-' и содержать латиницу/цифры/дефисы (10–80 символов).");

            if (string.IsNullOrWhiteSpace(AppName) || AppName!.Trim().Length < 3)
                ModelState.AddModelError(nameof(AppName), "Название АС обязательно (минимум 3 символа).");

            if (!string.IsNullOrWhiteSpace(AppUrl) && !IsValidHttpUrl(AppUrl!))
                ModelState.AddModelError(nameof(AppUrl), "Укажите корректный http/https URL.");

            if (!await _realms.RealmExistsAsync(Realm ?? ""))
                ModelState.AddModelError(nameof(Realm), "Такого realm не существует.");

            // Service account => обязательно включаем Client Authentication
            if (FlowService) ClientAuth = true;

            // 2) Redirect URIs
            var redirects = NormalizeList(TryParseList(RedirectUrisJson));
            var badRedirects = new List<string>();
            foreach (var s in redirects)
            {
                var candidate = s;
                var starPos = s.IndexOf('*');
                if (starPos >= 0)
                {
                    // Разрешаем шаблон типа https://host/path/*
                    if (starPos != s.Length - 1 || starPos == 0 || s[starPos - 1] != '/')
                    {
                        badRedirects.Add(s);
                        continue;
                    }
                    candidate = s[..starPos];
                }

                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri))
                {
                    badRedirects.Add(s);
                    continue;
                }

                var httpsOk = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                var httpLocalOk = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                                  && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                                      || IPAddress.TryParse(uri.Host, out _));
                if (!(httpsOk || httpLocalOk)) badRedirects.Add(s);
                if (!string.IsNullOrEmpty(uri.Fragment)) badRedirects.Add(s);
            }
            if (badRedirects.Count > 0)
                ModelState.AddModelError(nameof(RedirectUrisJson), $"Некорректные Redirect URI: {string.Join(", ", badRedirects.Distinct())}");

            if (FlowStandard && redirects.Count == 0)
                ModelState.AddModelError(nameof(RedirectUrisJson), "Для Standard flow требуется минимум один Redirect URI.");

            // 3) Local roles
            var localsRaw = TryParseList(LocalRolesJson);
            var locals = localsRaw.Select(s => (s ?? "").Trim())
                                  .Where(s => !string.IsNullOrWhiteSpace(s))
                                  .Distinct(StringComparer.OrdinalIgnoreCase)
                                  .ToList();
            LocalRolesJson = JsonSerializer.Serialize(locals);

            var roleRx = new Regex(@"^[a-z][a-z0-9._:-]{2,63}$");
            var badLocal = locals.Where(r => !roleRx.IsMatch(r)).ToList();
            if (badLocal.Any())
                ModelState.AddModelError(nameof(LocalRolesJson), $"Некорректные локальные роли: {string.Join(", ", badLocal)}");
            if (locals.Count > 20)
                ModelState.AddModelError(nameof(LocalRolesJson), "Слишком много локальных ролей (максимум 10).");

            // 4) Service roles: "serviceId: roleName"
            var svcRaw = NormalizeList(TryParseList(ServiceRolesJson));
            var badSvc = new List<string>();
            var clientIdRx = new Regex(@"^[a-z0-9][a-z0-9-]{2,60}$");
            foreach (var s in svcRaw)
            {
                var idx = s.IndexOf(':');
                if (idx <= 0 || idx >= s.Length - 1) { badSvc.Add(s); continue; }
                var svc = s[..idx].Trim();
                var role = s[(idx + 1)..].Trim();
                if (!clientIdRx.IsMatch(svc) || !roleRx.IsMatch(role)) badSvc.Add(s);
            }
            if (badSvc.Any())
                ModelState.AddModelError(nameof(ServiceRolesJson), $"Некорректные сервисные роли: {string.Join(", ", badSvc)}");

            // 5) Ошибки?
            if (!ModelState.IsValid)
            {
                StepToShow = DetermineStepFromErrors(ModelState);
                return Page();
            }

            // 6) Сбор спецификации и вызов ClientsService.CreateClientAsync
            static List<(string ClientId, string Role)> ParseService(string? json)
            {
                var res = new List<(string, string)>();
                var arr = string.IsNullOrWhiteSpace(json)? new List<string>(): (JsonSerializer.Deserialize<List<string>>(json!) ?? new List<string>());
                foreach (var s in arr)
                {
                    var idx = s.IndexOf(':');
                    if (idx <= 0) continue;
                    var cid = s[..idx].Trim();
                    var role = s[(idx + 1)..].Trim();
                    if (!string.IsNullOrWhiteSpace(cid) && !string.IsNullOrWhiteSpace(role))
                        res.Add((cid, role));
                }
                return res;
            }

            var spec = new NewClientSpec(
                Realm: Realm!,
                ClientId: ClientId!,
                Description: Description,
                ClientAuthentication: ClientAuth,
                StandardFlow: FlowStandard,
                ServiceAccount: FlowService,
                RedirectUris: redirects,
                LocalRoles: locals,
                ServiceRoles: ParseService(ServiceRolesJson)
            );

            try
            {
                var createdId = await _clients.CreateClientAsync(spec, ct);

                var summary = new ClientSummary(
                    Name: AppName ?? spec.ClientId,
                    ClientId: spec.ClientId,
                    Realm: spec.Realm,
                    Enabled: true,
                    FlowStandard: spec.StandardFlow,
                    FlowService: spec.ServiceAccount);
                var username = User.Identity?.Name ?? string.Empty;
                if (!string.IsNullOrEmpty(username))
                    await _repo.AddAsync(username, summary, ct);

                TempData["FlashOk"] = $"Клиент '{spec.ClientId}' создан (id={createdId}).";
                return RedirectToPage("/Index");
            }
            catch (Exception ex)
            {
                ModelState.AddModelError(string.Empty, $"Ошибка создания клиента: {ex.Message}");
                StepToShow = DetermineStepFromErrors(ModelState);
                return Page();
            }
        }
    }
}
