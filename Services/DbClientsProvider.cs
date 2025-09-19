using Assistant.Interfaces;
using System.Security.Claims;

namespace Assistant.Services;

/// <summary>
/// Поставщик клиентов, использующий PostgreSQL-хранилище.
/// </summary>
public sealed class DbClientsProvider : IClientsProvider
{
    private readonly UserClientsRepository _repo;

    public DbClientsProvider(UserClientsRepository repo) => _repo = repo;

    public async Task<IReadOnlyList<ClientSummary>> GetClientsForUser(ClaimsPrincipal user, CancellationToken ct = default)
    {
        var username = user.Identity?.Name ?? string.Empty;
        if (string.IsNullOrEmpty(username)) return Array.Empty<ClientSummary>();

        if (user.IsInRole("assistant-admin"))
        {
            return Array.Empty<ClientSummary>();
        }

        return await _repo.GetForUserAsync(username, isAdmin: false, ct);
    }
}

