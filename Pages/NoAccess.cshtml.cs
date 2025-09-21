using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Configuration;

namespace Assistant.Pages;

[AllowAnonymous]
public class NoAccessModel : PageModel
{
    private readonly IConfiguration _configuration;

    public NoAccessModel(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public string? SupportEmail { get; private set; }

    public void OnGet()
    {
        SupportEmail = _configuration["App:SupportEmail"];
    }
}
