using Assistant.Interfaces;
using Microsoft.AspNetCore.Http;
using System;
using System.Linq;
using System.Security.Claims;

namespace Assistant.Services;

public static class ClientSecretEndpointHandler
{
    public static async Task<IResult> HandleAsync(
        ClaimsPrincipal user,
        string realm,
        string clientId,
        IUserClientsRepository userClients,
        Func<CancellationToken, Task<string?>> secretFactory,
        CancellationToken ct)
    {
        if (!await HasAccessAsync(user, realm, clientId, userClients, ct).ConfigureAwait(false))
        {
            return Results.Forbid();
        }

        var secret = await secretFactory(ct).ConfigureAwait(false);
        return secret is not null ? Results.Ok(new { secret }) : Results.NotFound();
    }

    internal static async Task<bool> HasAccessAsync(
        ClaimsPrincipal user,
        string realm,
        string clientId,
        IUserClientsRepository userClients,
        CancellationToken ct)
    {
        if (user.IsInRole("assistant-admin"))
        {
            return true;
        }

        var username = GetUserName(user);
        if (string.IsNullOrWhiteSpace(username))
        {
            return false;
        }

        return await userClients.HasAccessAsync(username, realm, clientId, ct).ConfigureAwait(false);
    }

    private static string? GetUserName(ClaimsPrincipal user)
    {
        return user.Identity?.Name
               ?? user.FindFirst("preferred_username")?.Value
               ?? user.FindFirst(ClaimTypes.Name)?.Value
               ?? user.FindFirst(ClaimTypes.Email)?.Value;
    }
}
