using Assistant.Interfaces;
using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using System;
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
    private const int SearchResultsPageSize = 10;
    private const int SearchFetchPerRealm = 200;

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

    [BindProperty(SupportsGet = true)]
    public string? SearchTerm { get; set; }

    [BindProperty(SupportsGet = true)]
    public int SearchPage { get; set; } = 1;

    public List<ClientSummary> SearchResults { get; private set; } = [];

    public int TotalSearchResults { get; private set; }

    public int TotalSearchPages { get; private set; }

    public bool SearchPerformed { get; private set; }

    public bool SearchTermTooShort { get; private set; }

    public bool SearchHasNextPage { get; private set; }

    public bool SearchHasPreviousPage => SearchPage > 1 && TotalSearchPages > 0;

    public string? SearchError { get; private set; }

    public int SearchMinLength => LookupMinLength;

    public bool HasSearchResults => SearchResults.Count > 0;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadExclusionsAsync(ct);
        await LoadSearchResultsAsync(ct);
    }

    public async Task<IActionResult> OnPostAddAsync(CancellationToken ct)
    {
        var input = ClientId?.Trim();
        if (string.IsNullOrWhiteSpace(input))
        {
            TempData["FlashError"] = "Укажите clientId для добавления в список исключений.";
            return RedirectToSelf();
        }

        var storedValue = input.ToLowerInvariant();
        if (await _repository.IsExcludedAsync(storedValue, ct))
        {
            TempData["FlashError"] = $"Клиент '{input}' уже присутствует в списке.";
            return RedirectToSelf();
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
            return RedirectToSelf();
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка запроса при поиске клиента {ClientId}.", input);
            TempData["FlashError"] = "Не удалось выполнить запрос к Keycloak. Попробуйте повторить позже.";
            return RedirectToSelf();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Ошибка при проверке существования клиента {ClientId}.", input);
            TempData["FlashError"] = "Не удалось проверить клиента. Попробуйте позже.";
            return RedirectToSelf();
        }

        if (!exists)
        {
            TempData["FlashError"] = $"Клиент '{input}' не найден в Keycloak.";
            return RedirectToSelf();
        }

        var added = await _repository.AddAsync(input, ct);
        if (!added)
        {
            TempData["FlashError"] = $"Клиент '{input}' уже присутствует в списке.";
            return RedirectToSelf();
        }

        var actor = ResolveActor();
        await _logs.LogAsync("client_ex:add", actor, "-", storedValue, details: "Добавлен клиент в исключения - "+input, ct: ct);
        _logger.LogInformation("{Actor} added client {ClientId} to service role exclusions.", actor, storedValue);
        TempData["FlashOk"] = $"Клиент '{input}' добавлен в список исключений.";
        return RedirectToSelf();
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
            return RedirectToSelf();
        }

        var removed = await _repository.RemoveAsync(normalized, ct);
        if (removed is null)
        {
            TempData["FlashError"] = $"Клиент '{normalized}' не найден в списке исключений.";
            return RedirectToSelf();
        }

        var actor = ResolveActor();
        await _logs.LogAsync("client_ex:remove", actor, "-", removed, details: "Удален клиент из исключений - "+normalized, ct: ct);
        _logger.LogInformation("{Actor} removed client {ClientId} from service role exclusions.", actor, removed);
        TempData["FlashOk"] = $"Клиент '{normalized}' удалён из списка исключений.";
        return RedirectToSelf();
    }

    private async Task LoadExclusionsAsync(CancellationToken ct)
    {
        var set = await _repository.GetAllAsync(ct);
        Exclusions = set
            .OrderBy(clientId => clientId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task LoadSearchResultsAsync(CancellationToken ct)
    {
        SearchPage = NormalizePage(SearchPage);
        SearchError = null;
        SearchHasNextPage = false;
        TotalSearchPages = 0;
        TotalSearchResults = 0;
        SearchResults = [];
        SearchTermTooShort = false;
        SearchPerformed = false;

        if (string.IsNullOrWhiteSpace(SearchTerm))
        {
            SearchTerm = null;
            return;
        }

        var query = SearchTerm.Trim();
        SearchTerm = query;

        if (query.Length < LookupMinLength)
        {
            SearchTermTooShort = true;
            return;
        }

        SearchPerformed = true;

        try
        {
            var matches = await CollectSearchMatchesAsync(query, ct);
            TotalSearchResults = matches.Count;

            if (TotalSearchResults == 0)
            {
                SearchHasNextPage = false;
                TotalSearchPages = 0;
                SearchResults = [];
                return;
            }

            TotalSearchPages = (int)Math.Ceiling(TotalSearchResults / (double)SearchResultsPageSize);
            if (TotalSearchPages > 0 && SearchPage > TotalSearchPages)
            {
                SearchPage = TotalSearchPages;
            }

            var skip = (SearchPage - 1) * SearchResultsPageSize;
            SearchResults = matches
                .Skip(skip)
                .Take(SearchResultsPageSize)
                .ToList();

            SearchHasNextPage = matches.Count > skip + SearchResultsPageSize;
        }
        catch (UnauthorizedAccessException ex)
        {
            _logger.LogError(ex, "Недостаточно прав для поиска клиентов по запросу {Query}.", query);
            SearchError = "Недостаточно прав для поиска клиентов в Keycloak.";
            SearchPerformed = false;
            SearchResults = [];
            SearchHasNextPage = false;
            TotalSearchPages = 0;
            TotalSearchResults = 0;
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Ошибка запроса к Keycloak при поиске клиентов по запросу {Query}.", query);
            SearchError = "Не удалось выполнить запрос к Keycloak. Попробуйте повторить позже.";
            SearchPerformed = false;
            SearchResults = [];
            SearchHasNextPage = false;
            TotalSearchPages = 0;
            TotalSearchResults = 0;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось получить список клиентов для исключений по запросу {Query}.", query);
            SearchError = "Не удалось выполнить поиск клиентов. Попробуйте позже.";
            SearchPerformed = false;
            SearchResults = [];
            SearchHasNextPage = false;
            TotalSearchPages = 0;
            TotalSearchResults = 0;
        }
    }

    private async Task<List<ClientSummary>> CollectSearchMatchesAsync(string query, CancellationToken ct)
    {
        var realms = await _realms.GetRealmsAsync(ct);
        var list = new List<ClientSummary>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Realm))
            {
                continue;
            }

            var hits = await _clients.SearchClientsAsync(realm.Realm!, query, 0, SearchFetchPerRealm, ct);
            foreach (var hit in hits)
            {
                if (string.IsNullOrWhiteSpace(hit.ClientId))
                {
                    continue;
                }

                var key = $"{realm.Realm}|{hit.ClientId}";
                if (seen.Add(key))
                {
                    list.Add(ClientSummary.ForLookup(realm.Realm!, hit.ClientId));
                }
            }
        }

        return list
            .OrderBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private RedirectToPageResult RedirectToSelf()
    {
        var normalizedPage = NormalizePage(SearchPage);
        var query = string.IsNullOrWhiteSpace(SearchTerm) ? null : SearchTerm.Trim();
        return RedirectToPage(new { SearchTerm = query, SearchPage = normalizedPage });
    }

    private static int NormalizePage(int page) => page <= 0 ? 1 : page;

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
