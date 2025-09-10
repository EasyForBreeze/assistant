
using System.Security.Claims;

namespace Assistant.Interfaces
{
    public sealed record ClientSummary(
        string Name,
        string ClientId,
        string Realm,
        bool Enabled,
        bool FlowStandard,   // Authorization Code (openid)
        bool FlowService     // Client Credentials (rest-api)
    );

    public interface IClientsProvider
    {
        Task<IReadOnlyList<ClientSummary>> GetClientsForUser(
            ClaimsPrincipal user,
            CancellationToken ct = default);
    }
}
