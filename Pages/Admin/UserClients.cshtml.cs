using Assistant.Interfaces;
using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Assistant.Pages.Admin;

[Authorize(Roles = "assistant-admin")]
public sealed class UserClientsModel : PageModel
{
    private const int PageSize = 5;
    private const int MinimumQueryLengthValue = 3;
    private const int SearchFetchLimit = 200;

    private readonly RealmsService _realms;
    private readonly ClientsService _clients;
    private readonly UsersService _users;
    private readonly UserClientsRepository _repo;

    public UserClientsModel(
        RealmsService realms,
        ClientsService clients,
        UsersService users,
        UserClientsRepository repo)
    {
        _realms = realms;
        _clients = clients;
        _users = users;
        _repo = repo;
    }

    [BindProperty(SupportsGet = true)] public string? ClientQuery { get; set; }
    [BindProperty(SupportsGet = true)] public string? UserQuery { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedClientId { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedClientRealm { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedClientName { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedUsername { get; set; }
    [BindProperty(SupportsGet = true)] public string? SelectedUserDisplay { get; set; }
    [BindProperty(SupportsGet = true)] public int ClientPage { get; set; } = 1;
    [BindProperty(SupportsGet = true)] public int UserPage { get; set; } = 1;

    public List<ClientSummary> ClientResults { get; private set; } = [];
    public List<UserSearchResult> UserResults { get; private set; } = [];
    public List<ClientSummary> Assignments { get; private set; } = [];

    public int ClientTotalPages { get; private set; }
    public int UserTotalPages { get; private set; }

    public bool ClientHasNextPage { get; private set; }
    public bool UserHasNextPage { get; private set; }

    public string PrimaryRealm => _users.PrimaryRealm;

    public bool HasClientSelection =>
        !string.IsNullOrWhiteSpace(SelectedClientId) && !string.IsNullOrWhiteSpace(SelectedClientRealm);

    public bool HasUserSelection => !string.IsNullOrWhiteSpace(SelectedUsername);

    public bool CanGrant => HasClientSelection && HasUserSelection;

    public bool ClientHasPreviousPage => ClientPage > 1;
    public bool UserHasPreviousPage => UserPage > 1;

    public int MinimumQueryLength => MinimumQueryLengthValue;

    public bool ClientQueryTooShort => IsQueryTooShort(ClientQuery);
    public bool UserQueryTooShort => IsQueryTooShort(UserQuery);

    public async Task OnGetAsync(CancellationToken ct)
    {
        ClientPage = NormalizePage(ClientPage);
        UserPage = NormalizePage(UserPage);
        await LoadClientResultsAsync(ct);
        await LoadUserResultsAsync(ct);
        await LoadAssignmentsAsync(ct);
        NormalizeSelection();
    }

    private static int NormalizePage(int page) => page <= 0 ? 1 : page;

    private void NormalizeSelection()
    {
        if (HasClientSelection && string.IsNullOrWhiteSpace(SelectedClientName))
        {
            SelectedClientName = SelectedClientId;
        }

        if (HasUserSelection && string.IsNullOrWhiteSpace(SelectedUserDisplay))
        {
            SelectedUserDisplay = SelectedUsername;
        }
    }

    private async Task LoadClientResultsAsync(CancellationToken ct)
    {
        ClientPage = NormalizePage(ClientPage);

        if (string.IsNullOrWhiteSpace(ClientQuery))
        {
            ClientResults = [];
            ClientHasNextPage = false;
            ClientTotalPages = 0;
            return;
        }

        var query = ClientQuery!.Trim();
        if (query.Length < MinimumQueryLengthValue)
        {
            ClientResults = [];
            ClientHasNextPage = false;
            ClientTotalPages = 0;
            return;
        }

        var realms = await _realms.GetRealmsAsync(ct);
        var list = new List<ClientSummary>();

        var pageSize = PageSize;
        var skip = (ClientPage - 1) * pageSize;
        var fetchLimit = SearchFetchLimit;

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Realm))
            {
                continue;
            }

            var hits = await _clients.SearchClientsAsync(realm.Realm!, query, 0, fetchLimit, ct);
            foreach (var hit in hits)
            {
                list.Add(new ClientSummary(
                    Name: hit.ClientId,
                    ClientId: hit.ClientId,
                    Realm: realm.Realm!,
                    Enabled: true,
                    FlowStandard: false,
                    FlowService: false));
            }
        }

        var ordered = list
            .OrderBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (ordered.Count == 0)
        {
            ClientResults = [];
            ClientHasNextPage = false;
            ClientTotalPages = 0;
            return;
        }

        var totalPages = (int)Math.Ceiling(ordered.Count / (double)pageSize);
        if (totalPages > 0 && ClientPage > totalPages)
        {
            ClientPage = totalPages;
            skip = (ClientPage - 1) * pageSize;
        }

        ClientTotalPages = totalPages;
        ClientHasNextPage = ordered.Count > skip + pageSize;
        ClientResults = ordered
            .Skip(skip)
            .Take(pageSize)
            .ToList();
    }

    private async Task LoadUserResultsAsync(CancellationToken ct)
    {
        UserPage = NormalizePage(UserPage);

        if (string.IsNullOrWhiteSpace(UserQuery))
        {
            UserResults = [];
            UserHasNextPage = false;
            UserTotalPages = 0;
            return;
        }

        var query = UserQuery!.Trim();
        if (query.Length < MinimumQueryLengthValue)
        {
            UserResults = [];
            UserHasNextPage = false;
            UserTotalPages = 0;
            return;
        }

        var pageSize = PageSize;
        var skip = (UserPage - 1) * pageSize;
        var fetchLimit = SearchFetchLimit;

        var results = await _users.SearchUsersAsync(query, 0, fetchLimit, ct);

        if (results.Count == 0)
        {
            UserResults = [];
            UserHasNextPage = false;
            UserTotalPages = 0;
            return;
        }

        var totalPages = (int)Math.Ceiling(results.Count / (double)pageSize);
        if (totalPages > 0 && UserPage > totalPages)
        {
            UserPage = totalPages;
            skip = (UserPage - 1) * pageSize;
        }

        UserTotalPages = totalPages;
        UserHasNextPage = results.Count > skip + pageSize;
        UserResults = results
            .Skip(skip)
            .Take(pageSize)
            .ToList();
    }

    private async Task LoadAssignmentsAsync(CancellationToken ct)
    {
        if (!HasUserSelection)
        {
            Assignments = [];
            return;
        }

        Assignments = await _repo.GetForUserAsync(SelectedUsername!, isAdmin: false, ct);
    }

    public async Task<IActionResult> OnPostGrantAsync(
        string realm,
        string clientId,
        string? clientName,
        string username,
        string? userDisplay,
        string? clientQuery,
        string? userQuery,
        int? clientPage,
        int? userPage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(username))
        {
            TempData["FlashError"] = "Выберите клиента и пользователя, прежде чем назначать доступ.";
            return RedirectToPage(new
            {
                clientQuery,
                userQuery,
                clientPage,
                userPage,
                selectedUsername = username,
                selectedUserDisplay = userDisplay,
                selectedClientId = clientId,
                selectedClientRealm = realm,
                selectedClientName = clientName
            });
        }

        var summary = new ClientSummary(
            Name: string.IsNullOrWhiteSpace(clientName) ? clientId : clientName!,
            ClientId: clientId,
            Realm: realm,
            Enabled: true,
            FlowStandard: false,
            FlowService: false);

        await _repo.AddAsync(username, summary, ct);

        TempData["FlashOk"] = $"Клиент '{clientId}' ({realm}) назначен пользователю {username}.";

        return RedirectToPage(new
        {
            clientQuery,
            userQuery,
            clientPage,
            userPage,
            selectedUsername = username,
            selectedUserDisplay = string.IsNullOrWhiteSpace(userDisplay) ? username : userDisplay,
            selectedClientId = clientId,
            selectedClientRealm = realm,
            selectedClientName = summary.Name
        });
    }

    public async Task<IActionResult> OnPostRevokeAsync(
        string realm,
        string clientId,
        string username,
        string? userDisplay,
        string? clientQuery,
        string? userQuery,
        int? clientPage,
        int? userPage,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(realm)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(username))
        {
            await _repo.RemoveForUserAsync(username, clientId, realm, ct);
            TempData["FlashOk"] = $"Доступ к клиенту '{clientId}' ({realm}) для пользователя {username} удалён.";
        }
        else
        {
            TempData["FlashError"] = "Не удалось определить запись для удаления.";
        }

        return RedirectToPage(new
        {
            clientQuery,
            userQuery,
            clientPage,
            userPage,
            selectedUsername = username,
            selectedUserDisplay = string.IsNullOrWhiteSpace(userDisplay) ? username : userDisplay
        });
    }

    private static bool IsQueryTooShort(string? query)
    {
        var trimmed = query?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length < MinimumQueryLengthValue;
    }
}
