using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Account
{
    [Authorize]
    public class LogoutModel : PageModel
    {
        private readonly IConfiguration _cfg;
        public LogoutModel(IConfiguration cfg) => _cfg = cfg;

        public async Task<IActionResult> OnPostAsync()
        {
            await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

            var baseUrl = _cfg["Keycloak:BaseUrl"]!.TrimEnd('/');
            var realm = _cfg["Keycloak:Realm"]!;
            var clientId = _cfg["Keycloak:ClientId"]!;
            var back = $"{Request.Scheme}://{Request.Host}/"; // куда вернёт после логина
            var authUrl =
                $"{baseUrl}/realms/{realm}/protocol/openid-connect/auth" +
                $"?client_id={Uri.EscapeDataString(clientId)}" +
                $"&redirect_uri={Uri.EscapeDataString(back)}" +
                $"&response_type=code&scope=openid&prompt=login";

            return Redirect(authUrl);
        }
    }
}
