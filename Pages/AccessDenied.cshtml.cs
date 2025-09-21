using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;

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

    public AccessDeniedModel(IOptions<EmailOptions> options)
    {
        SupportEmail = options.Value.SupportRecipient;
    }
}
