using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages.Admin;

[Authorize(Roles = "assistant-admin")]
public sealed class ServiceRoleExclusionsModel : PageModel
{
    private readonly ServiceRoleExclusionsRepository _repository;
    private readonly ApiLogRepository _logs;
    private readonly ILogger<ServiceRoleExclusionsModel> _logger;

    public ServiceRoleExclusionsModel(
        ServiceRoleExclusionsRepository repository,
        ApiLogRepository logs,
        ILogger<ServiceRoleExclusionsModel> logger)
    {
        _repository = repository;
        _logs = logs;
        _logger = logger;
    }

    public List<string> Exclusions { get; private set; } = [];

    [BindProperty]
    public string? ClientId { get; set; }

    public async Task OnGetAsync(CancellationToken ct)
    {
        var set = await _repository.GetAllAsync(ct);
        Exclusions = set
            .OrderBy(clientId => clientId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var input = ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            TempData["FlashError"] = "Укажите clientId для добавления в список исключений.";
            return RedirectToPage();
        }

        var storedValue = input.ToLowerInvariant();
        var added = await _repository.AddAsync(input, ct);
        if (!added)
        {
            TempData["FlashError"] = $"Клиент '{input}' уже присутствует в списке.";
            return RedirectToPage();
        }

        var actor = ResolveActor();
        await _logs.LogAsync("service-role-exclusion:add", actor, "-", storedValue, details: input, ct: ct);
        _logger.LogInformation("{Actor} added client {ClientId} to service role exclusions.", actor, storedValue);
        TempData["FlashOk"] = $"Клиент '{input}' добавлен в список исключений.";
        return RedirectToPage();
    }

    public async Task<IActionResult> OnPostRemoveAsync(string clientId, CancellationToken ct)
    {
        var normalized = clientId?.Trim();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            TempData["FlashError"] = "Не удалось определить clientId для удаления.";
            return RedirectToPage();
        }

        var removed = await _repository.RemoveAsync(normalized, ct);
        if (removed is null)
        {
            TempData["FlashError"] = $"Клиент '{normalized}' не найден в списке исключений.";
            return RedirectToPage();
        }

        var actor = ResolveActor();
        await _logs.LogAsync("service-role-exclusion:remove", actor, "-", removed, details: normalized, ct: ct);
        _logger.LogInformation("{Actor} removed client {ClientId} from service role exclusions.", actor, removed);
        TempData["FlashOk"] = $"Клиент '{normalized}' удалён из списка исключений.";
        return RedirectToPage();
    }

    private string ResolveActor()
    {
        var login = User?.Identity?.Name;
        if (!string.IsNullOrWhiteSpace(login))
        {
            return login;
        }

        login = HttpContext.User?.Identity?.Name;
        return string.IsNullOrWhiteSpace(login) ? "unknown" : login;
    }
}
