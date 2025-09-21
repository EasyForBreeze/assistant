using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages;

[AllowAnonymous]
public class AccessDeniedModel : PageModel
{
    public IReadOnlyList<string> RequiredRoles { get; } = new[]
    {
        "assistant-user",
        "assistant-admin"
    };
}
