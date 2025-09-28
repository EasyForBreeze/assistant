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
        Assert.Single(repo.GetForUserCalls);
        Assert.Equal(("alice", false), repo.GetForUserCalls[0]);
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
        Assert.Single(repo.GetForUserCalls);
        Assert.Equal(("bob", false), repo.GetForUserCalls[0]);
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
        Assert.Empty(repo.GetForUserCalls);

        var ok = Assert.IsType<Ok<object>>(result);
        var secretProperty = ok.Value?.GetType().GetProperty("secret");
        Assert.NotNull(secretProperty);
        Assert.Equal("secret", secretProperty!.GetValue(ok.Value));
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
        public List<(string Username, bool IsAdmin)> GetForUserCalls { get; } = new();

        public List<ClientSummary> Clients { get; init; } = new();

        public Task<List<ClientSummary>> GetForUserAsync(string username, bool isAdmin, CancellationToken ct = default)
        {
            GetForUserCalls.Add((username, isAdmin));
            return Task.FromResult(Clients);
        }
    }
}
