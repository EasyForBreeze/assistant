using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Http;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;

namespace Assistant.Pages.Admin;

[Authorize(Roles = "assistant-admin")]
public sealed class ServiceRoleExclusionsModel : PageModel
{
    private readonly ServiceRoleExclusionsRepository _repository;
    private readonly ApiLogRepository _logs;
    private readonly RealmsService _realms;
    private readonly ClientsService _clients;
    private readonly ILogger<ServiceRoleExclusionsModel> _logger;

    private const int LookupMinLength = 2;
    private const int LookupMaxResults = 20;
    private const int LookupFetchPerRealm = 25;
    private const int ValidationFetchLimit = 200;

    public ServiceRoleExclusionsModel(
        ServiceRoleExclusionsRepository repository,
        ApiLogRepository logs,
        RealmsService realms,
        ClientsService clients,
        ILogger<ServiceRoleExclusionsModel> logger)
    {
        _repository = repository;
        _logs = logs;
        _realms = realms;
        _clients = clients;
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
        if (await _repository.IsExcludedAsync(storedValue, ct))
        {
            TempData["FlashError"] = $"Клиент '{input}' уже присутствует в списке.";
            return RedirectToPage();
        }

        bool exists;
        try
        {
            exists = await ClientExistsAsync(input, ct);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Недостаточно прав для поиска клиента {ClientId}.", input);
            TempData["FlashError"] = "Недостаточно прав для поиска клиента в Keycloak.";
            return RedirectToPage();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка запроса при поиске клиента {ClientId}.", input);
            TempData["FlashError"] = "Не удалось выполнить запрос к Keycloak. Попробуйте повторить позже.";
            return RedirectToPage();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке существования клиента {ClientId}.", input);
            TempData["FlashError"] = "Не удалось проверить клиента. Попробуйте позже.";
            return RedirectToPage();
        }

        if (!exists)
        {
            TempData["FlashError"] = $"Клиент '{input}' не найден в Keycloak.";
            return RedirectToPage();
        }

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

    public async Task<IActionResult> OnGetClientLookupAsync(string? q, CancellationToken ct)
    {
        var query = (q ?? string.Empty).Trim();
        if (query.Length < LookupMinLength)
        {
            return new JsonResult(Array.Empty<string>());
        }

        try
        {
            var results = await LookupClientsAsync(query, ct);
            return new JsonResult(results);
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Недостаточно прав для поиска клиентов по запросу {Query}.", query);
            return new JsonResult(Array.Empty<string>())
            {
                StatusCode = StatusCodes.Status403Forbidden
            };
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка запроса к Keycloak при поиске клиентов по запросу {Query}.", query);
            return new JsonResult(Array.Empty<string>())
            {
                StatusCode = StatusCodes.Status502BadGateway
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось получить список клиентов для исключений по запросу {Query}.", query);
            return new JsonResult(Array.Empty<string>())
            {
                StatusCode = StatusCodes.Status500InternalServerError
            };
        }
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

    private async Task<bool> ClientExistsAsync(string clientId, CancellationToken ct)
    {
        var query = clientId.Trim();
        var realms = await _realms.GetRealmsAsync(ct);

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Realm))
            {
                continue;
            }

            var hits = await _clients.SearchClientsAsync(realm.Realm!, query, 0, ValidationFetchLimit, ct);
            if (hits.Any(hit => string.Equals(hit.ClientId, query, StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }

    private async Task<List<string>> LookupClientsAsync(string query, CancellationToken ct)
    {
        var realms = await _realms.GetRealmsAsync(ct);
        var collected = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Realm))
            {
                continue;
            }

            var hits = await _clients.SearchClientsAsync(realm.Realm!, query, 0, LookupFetchPerRealm, ct);
            foreach (var hit in hits)
            {
                if (!string.IsNullOrWhiteSpace(hit.ClientId))
                {
                    collected.Add(hit.ClientId);
                }
            }
        }

        return collected
            .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
            .Take(LookupMaxResults)
            .ToList();
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
