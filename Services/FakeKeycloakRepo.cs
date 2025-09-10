using Assistant.Interfaces;
using System.Security.Claims;

namespace Assistant.Services
{
    public sealed class FakeClientsProvider : IClientsProvider
    {
        private static readonly IReadOnlyList<ClientSummary> All = new[]
        {
            new ClientSummary("Admin Console","admin-console","master",true,  true,false),
            new ClientSummary("Employee Portal","employee-portal","company",true,true,false),
            new ClientSummary("Service Client","svc-api","prod",false,false,true),
            new ClientSummary("Mobile App","mobile-app","openid",true,true,false),
            new ClientSummary("My Application","my-app","master",true,true,false),
            new ClientSummary("Web Client","web-client","prod",true,true,false),
            new ClientSummary("Payments","payments","cicd",true,true,true),
            new ClientSummary("CI/CD","cicd","cicd",true,true,true),
            new ClientSummary("Client App","client-app","custom",true,true,false),
        };

        public Task<IReadOnlyList<ClientSummary>> GetClientsForUser(ClaimsPrincipal user, CancellationToken ct = default)
            => Task.FromResult(All); // потом заменим на Keycloak Admin API
    }
}
