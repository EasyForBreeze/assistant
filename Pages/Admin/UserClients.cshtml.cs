using System;
using Assistant.Interfaces;
using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Logging;
using System.Security.Claims;

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
    private readonly ApiLogRepository _logs;
    private readonly ILogger<UserClientsModel> _logger;

    public UserClientsModel(
        RealmsService realms,
        ClientsService clients,
        UsersService users,
        UserClientsRepository repo,
        ApiLogRepository logs,
        ILogger<UserClientsModel> logger)
    {
        _realms = realms;
        _clients = clients;
        _users = users;
        _repo = repo;
        _logs = logs;
        _logger = logger;
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
    [BindProperty(SupportsGet = true)] public int AssignmentPage { get; set; } = 1;

    public List<ClientSummary> ClientResults { get; private set; } = [];
    public List<UserSearchResult> UserResults { get; private set; } = [];
    public List<ClientSummary> Assignments { get; private set; } = [];
    public List<ClientSummary> AssignmentPageItems { get; private set; } = [];

    public int ClientTotalPages { get; private set; }
    public int UserTotalPages { get; private set; }
    public int AssignmentTotalPages { get; private set; }

    public bool ClientHasNextPage { get; private set; }
    public bool UserHasNextPage { get; private set; }
    public bool AssignmentHasNextPage { get; private set; }

    public string PrimaryRealm => _users.PrimaryRealm;

    public bool HasClientSelection =>
        !string.IsNullOrWhiteSpace(SelectedClientId) && !string.IsNullOrWhiteSpace(SelectedClientRealm);

    public bool HasUserSelection => !string.IsNullOrWhiteSpace(SelectedUsername);

    public bool CanGrant => HasClientSelection && HasUserSelection;

    public bool ClientHasPreviousPage => ClientPage > 1;
    public bool UserHasPreviousPage => UserPage > 1;
    public bool AssignmentHasPreviousPage => AssignmentPage > 1;

    public int MinimumQueryLength => MinimumQueryLengthValue;

    public bool ClientQueryTooShort => IsQueryTooShort(ClientQuery);
    public bool UserQueryTooShort => IsQueryTooShort(UserQuery);

    public async Task OnGetAsync(CancellationToken ct)
    {
        ClientPage = NormalizePage(ClientPage);
        UserPage = NormalizePage(UserPage);
        AssignmentPage = NormalizePage(AssignmentPage);
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
            list.AddRange(hits.Select(hit => ClientSummary.ForLookup(realm.Realm!, hit.ClientId)));
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
            AssignmentPageItems = [];
            AssignmentTotalPages = 0;
            AssignmentHasNextPage = false;
            return;
        }

        var assignments = await _repo.GetForUserAsync(SelectedUsername!, isAdmin: false, ct);
        if (assignments.Count == 0)
        {
            Assignments = [];
            AssignmentPageItems = [];
            AssignmentTotalPages = 0;
            AssignmentHasNextPage = false;
            return;
        }

        var ordered = assignments
            .OrderBy(c => c.Realm, StringComparer.OrdinalIgnoreCase)
            .ThenBy(c => c.ClientId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var pageSize = PageSize;
        var totalPages = (int)Math.Ceiling(ordered.Count / (double)pageSize);
        if (totalPages > 0 && AssignmentPage > totalPages)
        {
            AssignmentPage = totalPages;
        }

        var skip = (AssignmentPage - 1) * pageSize;

        Assignments = ordered;
        AssignmentTotalPages = totalPages;
        AssignmentHasNextPage = ordered.Count > skip + pageSize;
        AssignmentPageItems = ordered
            .Skip(skip)
            .Take(pageSize)
            .ToList();
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
        int? assignmentPage,
        CancellationToken ct)
    {
        var normalizedAssignmentPage = NormalizePage(assignmentPage ?? 1);

        if (string.IsNullOrWhiteSpace(realm) || string.IsNullOrWhiteSpace(clientId) || string.IsNullOrWhiteSpace(username))
        {
            TempData["FlashError"] = "Выберите клиента и пользователя, прежде чем назначать доступ.";
            return RedirectToPage(new
            {
                clientQuery,
                userQuery,
                clientPage,
                userPage,
                assignmentPage = normalizedAssignmentPage,
                selectedUsername = username,
                selectedUserDisplay = userDisplay,
                selectedClientId = clientId,
                selectedClientRealm = realm,
                selectedClientName = clientName
            });
        }

        var summary = ClientSummary.ForLookup(realm, clientId, string.IsNullOrWhiteSpace(clientName) ? null : clientName);

        var existingAssignments = await _repo.GetForUserAsync(username, isAdmin: false, ct);
        var alreadyAssigned = existingAssignments.Any(a =>
            string.Equals(a.ClientId, clientId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Realm, realm, StringComparison.OrdinalIgnoreCase));

        if (alreadyAssigned)
        {
            TempData["FlashError"] = $"Клиент '{clientId}' ({realm}) уже назначен пользователю {username}.";

            return RedirectToPage(new
            {
                clientQuery,
                userQuery,
                clientPage,
                userPage,
                assignmentPage = normalizedAssignmentPage,
                selectedUsername = username,
                selectedUserDisplay = string.IsNullOrWhiteSpace(userDisplay) ? username : userDisplay,
                selectedClientId = clientId,
                selectedClientRealm = realm,
                selectedClientName = summary.Name
            });
        }

        await _repo.AddAsync(username, summary, ct);

        var actor = GetCurrentActorLogin();
        await AuditAssignmentChangeAsync(
            "user-client:grant",
            actor,
            realm,
            clientId,
            username,
            summary.Name,
            userDisplay,
            ct);
        _logger.LogInformation("{Actor} granted client {ClientId} ({Realm}) to {TargetUser}.", actor, clientId, realm, username);

        TempData["FlashOk"] = $"Клиент '{clientId}' ({realm}) назначен пользователю {username}.";

        return RedirectToPage(new
        {
            clientQuery,
            userQuery,
            clientPage,
            userPage,
            assignmentPage = normalizedAssignmentPage,
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
        int? assignmentPage,
        CancellationToken ct)
    {
        var normalizedAssignmentPage = NormalizePage(assignmentPage ?? 1);

        if (!string.IsNullOrWhiteSpace(realm)
            && !string.IsNullOrWhiteSpace(clientId)
            && !string.IsNullOrWhiteSpace(username))
        {
            await _repo.RemoveForUserAsync(username, clientId, realm, ct);
            var actor = GetCurrentActorLogin();
            await AuditAssignmentChangeAsync(
                "user-client:revoke",
                actor,
                realm,
                clientId,
                username,
                clientName: null,
                userDisplay,
                ct);
            _logger.LogInformation("{Actor} revoked client {ClientId} ({Realm}) from {TargetUser}.", actor, clientId, realm, username);
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
            assignmentPage = normalizedAssignmentPage,
            selectedUsername = username,
            selectedUserDisplay = string.IsNullOrWhiteSpace(userDisplay) ? username : userDisplay
        });
    }

    private static bool IsQueryTooShort(string? query)
    {
        var trimmed = query?.Trim();
        return !string.IsNullOrEmpty(trimmed) && trimmed.Length < MinimumQueryLengthValue;
    }

    private string GetCurrentActorLogin()
    {
        var user = User ?? HttpContext.User;

        return user?.Identity?.Name
            ?? user?.FindFirst("preferred_username")?.Value
            ?? user?.FindFirst(ClaimTypes.Name)?.Value
            ?? user?.FindFirst(ClaimTypes.Email)?.Value
            ?? "unknown";
    }

    private Task AuditAssignmentChangeAsync(
        string operationType,
        string actor,
        string realm,
        string clientId,
        string targetUser,
        string? clientName,
        string? targetDisplay,
        CancellationToken ct)
    {
        var normalizedRealm = string.IsNullOrWhiteSpace(realm) ? "-" : realm;
        var normalizedClientId = string.IsNullOrWhiteSpace(clientId) ? "-" : clientId;
        var normalizedTargetUser = string.IsNullOrWhiteSpace(targetUser) ? "-" : targetUser;

        var detailsParts = new List<string>();

        if (!string.IsNullOrWhiteSpace(clientName)
            && !string.Equals(clientName, normalizedClientId, StringComparison.Ordinal))
        {
            detailsParts.Add($"clientName={clientName}");
        }

        if (!string.IsNullOrWhiteSpace(targetDisplay)
            && !string.Equals(targetDisplay, normalizedTargetUser, StringComparison.OrdinalIgnoreCase))
        {
            detailsParts.Add($"targetDisplay={targetDisplay}");
        }

        var details = detailsParts.Count == 0 ? null : string.Join("; ", detailsParts);

        return _logs.LogAsync(
            operationType,
            actor,
            normalizedRealm,
            $"{normalizedClientId}:{normalizedTargetUser}",
            details,
            ct);
    }
}
