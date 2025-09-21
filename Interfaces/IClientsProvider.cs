
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
    )
    {
        public string DisplayName => string.IsNullOrWhiteSpace(Name) ? ClientId : Name;

        public static ClientSummary ForLookup(string realm, string clientId, string? name = null)
            => new(name ?? clientId, clientId, realm, Enabled: true, FlowStandard: false, FlowService: false);
    }

    public interface IClientsProvider
    {
        Task<IReadOnlyList<ClientSummary>> GetClientsForUser(
            ClaimsPrincipal user,
            CancellationToken ct = default);
    }
}
