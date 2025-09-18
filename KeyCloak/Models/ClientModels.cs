namespace Assistant.KeyCloak.Models;

public sealed record ClientShort(string Id, string ClientId);

public sealed record ClientDetails(
    string Id,
    string ClientId,
    bool Enabled,
    string? Description,
    bool ClientAuth,
    bool StandardFlow,
    bool ServiceAccount,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> LocalRoles,
    IReadOnlyList<(string ClientId, string Role)> ServiceRoles,
    IReadOnlyList<string> DefaultScopes
);

public sealed record RoleHit(string ClientUuid, string ClientId, string Role);

public sealed record NewClientSpec(
    string Realm,
    string ClientId,
    string? Description,
    bool ClientAuthentication,
    bool StandardFlow,
    bool ServiceAccount,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> LocalRoles,
    IReadOnlyList<(string ClientId, string Role)> ServiceRoles
);

public sealed record UpdateClientSpec(
    string Realm,
    string CurrentClientId,
    string ClientId,
    bool Enabled,
    string? Description,
    bool ClientAuth,
    bool StandardFlow,
    bool ServiceAccount,
    IReadOnlyList<string> RedirectUris,
    IReadOnlyList<string> LocalRoles,
    IReadOnlyList<(string ClientId, string Role)> ServiceRoles
);
