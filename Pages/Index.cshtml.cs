using Assistant.Interfaces;
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages
{
    [Authorize]
    public class IndexModel : PageModel
    {
        private readonly IClientsProvider _provider;
        private readonly RealmsService _realms;
        private readonly ClientsService _clients;

        public IndexModel(IClientsProvider provider, RealmsService realms, ClientsService clients)
        {
            _provider = provider;
            _realms = realms;
            _clients = clients;
        }

        private const int PageSize = 20;

        private static readonly Dictionary<string, string> Descriptions = new(StringComparer.OrdinalIgnoreCase)
        {
            ["admin-console"] = "Keycloak administration console.",
            ["employee-portal"] = "Internal portal for employees.",
            ["svc-api"] = "Technical service client (machine to machine).",
            ["mobile-app"] = "Mobile application OIDC client.",
            ["my-app"] = "Sample web application.",
            ["web-client"] = "Public web client.",
            ["payments"] = "Payments subsystem client.",
            ["cicd"] = "CI/CD integration client.",
            ["client-app"] = "Generic application client."
        };

        public List<ClientSummary> Clients { get; private set; } = [];
        public string? Q { get; private set; }
        public bool ShowEmptyMessage { get; private set; }
        public int PageNumber { get; private set; }
        public int TotalPages { get; private set; }
        public bool HasPreviousPage => PageNumber > 1;
        public bool HasNextPage => PageNumber < TotalPages;

        public string MapEnv(string? v) => (v ?? "").Trim().ToLowerInvariant() switch
        {
            "prod" or "production" => "PROD",
            "stage" or "staging" => "STAGE",
            "test" => "TEST",
            "dev" or "development" => "TEST",
            _ => (v ?? "").ToUpperInvariant()
        };

        public IEnumerable<string> Envs(string? raw)
            => string.IsNullOrWhiteSpace(raw)
                ? Enumerable.Empty<string>()
                : raw.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries)
                     .Select(MapEnv)
                     .Distinct(StringComparer.OrdinalIgnoreCase);

        public string EnvBarGradient(string? env) => (env ?? "").ToUpperInvariant() switch
        {
            "PROD" => "from-fuchsia-500/70 to-pink-500/70",
            "STAGE" => "from-amber-400/80 to-orange-500/70",
            "TEST" => "from-emerald-400/80 to-teal-500/70",
            _ => "from-slate-500/60 to-slate-400/60"
        };

        public string DescFor(string? clientId)
            => Descriptions.TryGetValue(clientId ?? "", out var d) ? d : "Description from Keycloak (stub)";

        public async Task OnGetAsync(string? q, int pageNumber = 1)
        {
            Q = q?.Trim();
            var isAdmin = User.IsInRole("assistant-admin");

            var list = new List<ClientSummary>();

            if (isAdmin)
            {
                if (!string.IsNullOrEmpty(Q))
                {
                    var realms = await _realms.GetRealmsAsync();
                    foreach (var r in realms)
                    {
                        if (string.IsNullOrWhiteSpace(r.Realm)) continue;
                        var hits = await _clients.SearchClientsAsync(r.Realm!, Q);
                        foreach (var c in hits)
                        {
                            list.Add(new ClientSummary(
                                Name: c.ClientId,
                                ClientId: c.ClientId,
                                Realm: r.Realm!,
                                Enabled: true,
                                FlowStandard: false,
                                FlowService: false));
                        }
                    }
                }

                ShowEmptyMessage = !string.IsNullOrEmpty(Q);
            }
            else
            {
                list = (await _provider.GetClientsForUser(User)).ToList();
                ShowEmptyMessage = true;
            }

            TotalPages = Math.Max(1, (int)Math.Ceiling(list.Count / (double)PageSize));
            PageNumber = Math.Clamp(pageNumber, 1, TotalPages);
            Clients = list.Skip((PageNumber - 1) * PageSize).Take(PageSize).ToList();
        }

        public async Task<PartialViewResult> OnGetClientsAsync(string? q, int pageNumber = 1)
        {
            await OnGetAsync(q, pageNumber);
            return Partial("_ClientsList", this);
        }
    }
}
