using Assistant.Interfaces;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using System.Collections.Generic;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assistant.Tests;

public class ClientSecretEndpointHandlerTests
{
    [Fact]
    public async Task AssistantUserCannotGetSecretForForeignClient()
    {
        var repo = new FakeUserClientsRepository();
        var user = CreateUser("alice", "assistant-user");
        var wasCalled = false;

        var result = await ClientSecretEndpointHandler.HandleAsync(
            user,
            realm: "test",
            clientId: "client-a",
            repo,
            ct =>
            {
                wasCalled = true;
                return Task.FromResult<string?>("secret");
            },
            CancellationToken.None);

        Assert.IsType<ForbidHttpResult>(result);
        Assert.False(wasCalled);
        Assert.Single(repo.HasAccessCalls);
        Assert.Equal(("alice", "test", "client-a"), repo.HasAccessCalls[0]);
    }

    [Fact]
    public async Task AssistantUserCannotRegenerateSecretForForeignClient()
    {
        var repo = new FakeUserClientsRepository();
        var user = CreateUser("bob", "assistant-user");
        var wasCalled = false;

        var result = await ClientSecretEndpointHandler.HandleAsync(
            user,
            realm: "test",
            clientId: "client-b",
            repo,
            ct =>
            {
                wasCalled = true;
                return Task.FromResult<string?>("new-secret");
            },
            CancellationToken.None);

        Assert.IsType<ForbidHttpResult>(result);
        Assert.False(wasCalled);
        Assert.Single(repo.HasAccessCalls);
        Assert.Equal(("bob", "test", "client-b"), repo.HasAccessCalls[0]);
    }

    [Fact]
    public async Task AssistantAdminCanAccessAnyClient()
    {
        var repo = new FakeUserClientsRepository();
        var user = CreateUser("carol", "assistant-admin");
        var wasCalled = false;

        var result = await ClientSecretEndpointHandler.HandleAsync(
            user,
            realm: "test",
            clientId: "client-c",
            repo,
            ct =>
            {
                wasCalled = true;
                return Task.FromResult<string?>("secret");
            },
            CancellationToken.None);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        Assert.True(wasCalled);
        Assert.Empty(repo.HasAccessCalls);

        var ok = Assert.IsType<Ok<object>>(result);
        var secretProperty = ok.Value?.GetType().GetProperty("secret");
        Assert.NotNull(secretProperty);
        Assert.Equal("secret", secretProperty!.GetValue(ok.Value));
    }

    [Fact]
    public async Task AssistantUserCanAccessOwnClient()
    {
        var repo = new FakeUserClientsRepository
        {
            HasAccessResult = true
        };
        var user = CreateUser("dave", "assistant-user");

        var result = await ClientSecretEndpointHandler.HandleAsync(
            user,
            realm: "alpha",
            clientId: "client-d",
            repo,
            ct => Task.FromResult<string?>("owned-secret"),
            CancellationToken.None);

        var status = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        Assert.Equal(StatusCodes.Status200OK, status.StatusCode);
        Assert.Single(repo.HasAccessCalls);
        Assert.Equal(("dave", "alpha", "client-d"), repo.HasAccessCalls[0]);

        var ok = Assert.IsType<Ok<object>>(result);
        var secretProperty = ok.Value?.GetType().GetProperty("secret");
        Assert.NotNull(secretProperty);
        Assert.Equal("owned-secret", secretProperty!.GetValue(ok.Value));
    }

    private static ClaimsPrincipal CreateUser(string username, params string[] roles)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, username)
        };

        foreach (var role in roles)
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        var identity = new ClaimsIdentity(claims, authenticationType: "test");
        return new ClaimsPrincipal(identity);
    }

    private sealed class FakeUserClientsRepository : IUserClientsRepository
    {
        public List<(string Username, string Realm, string ClientId)> HasAccessCalls { get; } = new();

        public bool HasAccessResult { get; set; }

        public Task<List<ClientSummary>> GetForUserAsync(string username, bool isAdmin, CancellationToken ct = default)
            => Task.FromResult(new List<ClientSummary>());

        public Task<bool> HasAccessAsync(string username, string realm, string clientId, CancellationToken ct = default)
        {
            HasAccessCalls.Add((username, realm, clientId));
            return Task.FromResult(HasAccessResult);
        }
    }
}
