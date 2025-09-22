using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using System.Security.Claims;

namespace Assistant.Pages;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public IReadOnlyList<string> RequiredRoles { get; } = new[]
    {
        "assistant-user",
        "assistant-admin"
    };

    public string? SupportEmail { get; }
    public string? UserLogin { get; private set; }

    public AccessDeniedModel(IOptions<EmailOptions> options)
    {
        SupportEmail = options.Value.SupportRecipient;
    }

    public void OnGet()
    {
        var user = User ?? HttpContext.User;

        UserLogin = user?.Identity?.Name
            ?? user?.FindFirst("preferred_username")?.Value
            ?? user?.FindFirst(ClaimTypes.Name)?.Value
            ?? user?.FindFirst(ClaimTypes.Email)?.Value;
    }
}
