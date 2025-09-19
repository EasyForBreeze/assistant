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

    private const int PageSize = 5;

    public List<ClientSummary> ClientResults { get; private set; } = [];
    public List<UserSearchResult> UserResults { get; private set; } = [];
    public List<ClientSummary> Assignments { get; private set; } = [];
    public IReadOnlyList<ClientSummary> ClientPageResults { get; private set; } = Array.Empty<ClientSummary>();
    public IReadOnlyList<UserSearchResult> UserPageResults { get; private set; } = Array.Empty<UserSearchResult>();
    public int ClientTotalPages { get; private set; }
    public int UserTotalPages { get; private set; }

    public string PrimaryRealm => _users.PrimaryRealm;

    public bool HasClientSelection =>
        !string.IsNullOrWhiteSpace(SelectedClientId) && !string.IsNullOrWhiteSpace(SelectedClientRealm);

    public bool HasUserSelection => !string.IsNullOrWhiteSpace(SelectedUsername);

    public bool CanGrant => HasClientSelection && HasUserSelection;

    public async Task OnGetAsync(CancellationToken ct)
    {
        await LoadClientResultsAsync(ct);
        await LoadUserResultsAsync(ct);
        await LoadAssignmentsAsync(ct);
        NormalizeSelection();
    }

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
        if (string.IsNullOrWhiteSpace(ClientQuery))
        {
            ClientResults = [];
            ClientPageResults = Array.Empty<ClientSummary>();
            ClientTotalPages = 0;
            ClientPage = 1;
            return;
        }

        var query = ClientQuery!.Trim();
        if (query.Length == 0)
        {
            ClientResults = [];
            ClientPageResults = Array.Empty<ClientSummary>();
            ClientTotalPages = 0;
            ClientPage = 1;
            return;
        }

        var realms = await _realms.GetRealmsAsync(ct);
        var list = new List<ClientSummary>();

        foreach (var realm in realms)
        {
            if (string.IsNullOrWhiteSpace(realm.Realm))
            {
                continue;
            }

            var hits = await _clients.SearchClientsAsync(realm.Realm!, query, 0, 5, ct);
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

        ClientResults = list
            .OrderBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        ApplyClientPagination();
    }

    private async Task LoadUserResultsAsync(CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(UserQuery))
        {
            UserResults = [];
            UserPageResults = Array.Empty<UserSearchResult>();
            UserTotalPages = 0;
            UserPage = 1;
            return;
        }

        var query = UserQuery!.Trim();
        if (query.Length == 0)
        {
            UserResults = [];
            UserPageResults = Array.Empty<UserSearchResult>();
            UserTotalPages = 0;
            UserPage = 1;
            return;
        }

        UserResults = await _users.SearchUsersAsync(query, 0, 20, ct);
        ApplyUserPagination();
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
        int clientPage,
        int userPage,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(username))
        {
            TempData["FlashError"] = "Выберите клиента и пользователя, прежде чем назначать доступ.";
            return RedirectToPage(new
            {
                clientQuery,
                userQuery,
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
        int clientPage,
        int userPage,
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

    private void ApplyClientPagination()
    {
        if (ClientResults.Count == 0)
        {
            ClientTotalPages = 0;
            ClientPage = 1;
            ClientPageResults = Array.Empty<ClientSummary>();
            return;
        }

        ClientTotalPages = (int)Math.Ceiling(ClientResults.Count / (double)PageSize);
        if (ClientPage < 1)
        {
            ClientPage = 1;
        }
        else if (ClientPage > ClientTotalPages)
        {
            ClientPage = ClientTotalPages;
        }

        var skip = (ClientPage - 1) * PageSize;
        ClientPageResults = ClientResults.Skip(skip).Take(PageSize).ToList();
    }

    private void ApplyUserPagination()
    {
        if (UserResults.Count == 0)
        {
            UserTotalPages = 0;
            UserPage = 1;
            UserPageResults = Array.Empty<UserSearchResult>();
            return;
        }

        UserTotalPages = (int)Math.Ceiling(UserResults.Count / (double)PageSize);
        if (UserPage < 1)
        {
            UserPage = 1;
        }
        else if (UserPage > UserTotalPages)
        {
            UserPage = UserTotalPages;
        }

        var skip = (UserPage - 1) * PageSize;
        UserPageResults = UserResults.Skip(skip).Take(PageSize).ToList();
    }
}
