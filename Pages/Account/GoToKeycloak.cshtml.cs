using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages.Account
{
    [AllowAnonymous]
    public class GoToKeycloakModel : PageModel
    {
        private readonly IConfiguration _cfg;
        public GoToKeycloakModel(IConfiguration cfg) => _cfg = cfg;

        public string TargetUrl { get; private set; } = "/";

        public void OnGet(string? returnUrl = "/")
        {
            var baseUrl = _cfg["Keycloak:BaseUrl"]!.TrimEnd('/');   // напр. http://localhost:8080
            var realm = _cfg["Keycloak:Realm"]!;
            var clientId = _cfg["Keycloak:ClientId"]!;
            var back = $"{Request.Scheme}://{Request.Host}{(string.IsNullOrEmpty(returnUrl) ? "/" : returnUrl)}";

            TargetUrl =
                $"{baseUrl}/realms/{realm}/protocol/openid-connect/auth" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(back)}" +
                $"&response_type=code&scope=openid&prompt=login";
        }
    }
}
