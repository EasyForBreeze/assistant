using Assistant.KeyCloak;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.Rendering;
using System.Diagnostics;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using static Assistant.Pages.Clients.DetailsModel;

namespace Assistant.Pages.Clients
{
    public class CreateModel : PageModel
    {
        private readonly RealmsService _realms;
        private readonly ClientsService _clients;
        public CreateModel(RealmsService realms, ClientsService clients)
        {
            _realms = realms;
            _clients = clients;
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

        public async Task OnGet()                                
        {
            await LoadViewDataAsync();
        }
        private static List<string> NormalizeList(IEnumerable<string> items) => items.Select(s => (s ?? "").Trim()).Where(s => !string.IsNullOrEmpty(s)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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
            bool Hit(string name) => key.Equals(name, StringComparison.OrdinalIgnoreCase) ||key.StartsWith(name + "[", StringComparison.OrdinalIgnoreCase) ||key.Equals(name + "Json", StringComparison.OrdinalIgnoreCase);

            // Шаг 1 — выбор realm
            if (Hit(nameof(Realm))) return 1;

            // Шаг 2 — ClientId
            if (Hit(nameof(ClientId)) || Hit(nameof(Description))) return 2;

            // Шаг 3 — сведения об АС
            if (Hit(nameof(AppName)) || Hit(nameof(AppUrl)) || Hit(nameof(ServiceOwner)) || Hit(nameof(ServiceManager))) return 3;

            // Шаг 4 — Flows (если когда-либо появятся серверные ошибки по ним)
            if (Hit(nameof(ClientAuth)) || Hit(nameof(FlowStandard)) || Hit(nameof(FlowService))) return 4;

            // Шаг 5 — Redirect URIs
            if (Hit(nameof(RedirectUrisJson))) return 5;

            // Шаг 6 — Local roles
            if (Hit(nameof(LocalRolesJson))) return 6;

            // Шаг 7 — Service roles
            if (Hit(nameof(ServiceRolesJson))) return 7;

            return 1; // дефолт — на первый
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
        public async Task<IActionResult> OnPostCreate()
        {
            await LoadViewDataAsync(); // чтобы при ошибке повторно отрисовать селекты/описания

            // 1) Базовые поля
            if (string.IsNullOrWhiteSpace(Realm))
                ModelState.AddModelError(nameof(Realm), "Выберите realm.");

            if (!IsValidClientId(ClientId))
                ModelState.AddModelError(nameof(ClientId), "Client ID должен начинаться с 'app-bank-' и содержать латиницу/цифры/дефисы (10–80 символов).");

            if (string.IsNullOrWhiteSpace(AppName) || AppName!.Trim().Length < 3)
                ModelState.AddModelError(nameof(AppName), "Название АС обязательно (минимум 3 символа).");

            if (!string.IsNullOrWhiteSpace(AppUrl) && !IsValidHttpUrl(AppUrl!))
                ModelState.AddModelError(nameof(AppUrl), "Укажите корректный http/https URL.");


            // 3) Валидация Realm по справочнику
            if(!await _realms.RealmExistsAsync(Realm ?? ""))
             ModelState.AddModelError(nameof(Realm), "Такого realm не существует.");

            // 4) Redirect URIs
            var redirects = NormalizeList(TryParseList(RedirectUrisJson));
            var badRedirects = new List<string>();
            foreach (var s in redirects)
            {
                if (!Uri.TryCreate(s, UriKind.Absolute, out var uri))
                    badRedirects.Add(s);
                else
                {
                    var httpsOk = uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase);
                    var httpLocalOk = uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)
                                      && (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
                                          || IPAddress.TryParse(uri.Host, out var _));
                    if (!(httpsOk || httpLocalOk)) badRedirects.Add(s);
                    if (!string.IsNullOrEmpty(uri.Fragment)) badRedirects.Add(s);
                    if (s.Contains("*")) badRedirects.Add(s);
                }
            }
            if (badRedirects.Count > 0)
                ModelState.AddModelError(nameof(RedirectUrisJson), $"Некорректные Redirect URI: {string.Join(", ", badRedirects.Distinct())}");

            if (FlowStandard && redirects.Count == 0)
                ModelState.AddModelError(nameof(RedirectUrisJson), "Для Standard flow требуется минимум один Redirect URI.");

            // 5) Local roles
            var localsRaw = TryParseList(LocalRolesJson);
            var localsNormalized = localsRaw
                .Select(s => (s ?? "").Trim())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var locals = localsNormalized
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            LocalRolesJson = JsonSerializer.Serialize(locals);
            var roleRx = new Regex(@"^[a-z][a-z0-9._:-]{2,63}$");
            var badLocal = locals.Where(r => !roleRx.IsMatch(r)).ToList();
            if (badLocal.Any())
                ModelState.AddModelError(nameof(LocalRolesJson), $"Некорректные локальные роли: {string.Join(", ", badLocal)}");
            if (locals.Count > 20)
                ModelState.AddModelError(nameof(LocalRolesJson), "Слишком много локальных ролей (максимум 20).");

            // 6) Service roles: "serviceId: roleName"
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

            // 7) Если есть ошибки — вернуть страницу с ними
            if (!ModelState.IsValid)
            {
                StepToShow = DetermineStepFromErrors(ModelState);
                return Page();
            }

            // 8) Здесь можно собрать DTO для Keycloak Admin API
            // var request = new CreateClientRequest { ... } // из проверенных значений
            // await _kcAdmin.CreateClientAsync(request);

            TempData["Flash"] =
                $"Client '{ClientId}' создан в realm '{Realm}'. " +
                $"Flows: auth={ClientAuth}, std={FlowStandard}, svc={FlowService}. " +
                $"Redirects={redirects.Count}, LocalRoles={locals.Count}, ServiceRoles={svcRaw.Count}";
            return RedirectToPage("/Index");
        }
        public async Task<IActionResult> OnGetClientsSearchAsync(string realm, string q, int first = 0, int max = 20, CancellationToken ct = default)
            => new JsonResult(await _clients.SearchClientsAsync(realm, q ?? "", first, max, ct));

        public async Task<IActionResult> OnGetClientRolesAsync(string realm, string id, int first = 0, int max = 50, string? q = null, CancellationToken ct = default)
            => new JsonResult(await _clients.GetClientRolesAsync(realm, id, first, max, q, ct));
    }
}
