using Assistant.Interfaces;
using Assistant.Services;
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

var kcBase = builder.Configuration["Keycloak:BaseUrl"]!;
var realm = builder.Configuration["Keycloak:Realm"]!;
var clientId = builder.Configuration["Keycloak:ClientId"]!;
var clientSecret = builder.Configuration["Keycloak:ClientSecret"]!;
var metadata = $"{kcBase}/realms/{realm}/.well-known/openid-configuration";

builder.Services.Configure<Assistant.KeyCloak.AdminApiOptions>(
    builder.Configuration.GetSection("KeycloakAdmin"));

builder.Services.AddMemoryCache();
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<Assistant.KeyCloak.IAdminTokenProvider, Assistant.KeyCloak.AdminTokenProvider>();
builder.Services.AddTransient<Assistant.KeyCloak.AdminBearerHandler>();

builder.Services.AddHttpClient("kc-admin", (sp, c) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Assistant.KeyCloak.AdminApiOptions>>().Value;
    c.Timeout = TimeSpan.FromSeconds(100);
}).AddHttpMessageHandler<Assistant.KeyCloak.AdminBearerHandler>();

builder.Services.AddScoped<Assistant.KeyCloak.RealmsService>();
builder.Services.AddScoped<Assistant.KeyCloak.ClientsService>();
builder.Services.AddScoped<Assistant.KeyCloak.UsersService>();
builder.Services.AddScoped<Assistant.KeyCloak.EventsService>();
builder.Services.AddSingleton<UserClientsRepository>();
builder.Services.AddSingleton<ServiceRoleExclusionsRepository>();
builder.Services.AddSingleton<ApiLogRepository>();
builder.Services.AddScoped<IClientsProvider, DbClientsProvider>();
var confluenceOptions = ConfluenceOptions.FromConnectionString(builder.Configuration.GetConnectionString("ConnectionWiki"));
builder.Services.AddSingleton(confluenceOptions);
builder.Services.AddSingleton<ConfluenceTemplateProvider>();
builder.Services.AddHttpClient("confluence-wiki", client =>
{
    client.Timeout = TimeSpan.FromSeconds(100);
});
builder.Services.AddSingleton<ConfluenceWikiService>();
builder.Services.AddAuthorization(options =>
{
    options.FallbackPolicy = new AuthorizationPolicyBuilder()
        .RequireAuthenticatedUser()
        .RequireRole("assistant-admin", "assistant-user")
        .Build();
});

builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = ".KeycloakShell.Auth";
    options.SlidingExpiration = true;
    options.AccessDeniedPath = "/NoAccess";
})
.AddOpenIdConnect(options =>
{
    options.MetadataAddress = metadata;
    options.ClientId = clientId;
    options.ClientSecret = clientSecret;
    options.ResponseType = "code";

    options.SaveTokens = true;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.RequireHttpsMetadata = false;

    options.CallbackPath = "/signin-oidc";
    options.SignedOutCallbackPath = "/signout-callback-oidc";

    options.Scope.Clear();
    options.Scope.Add("openid");
    options.Scope.Add("profile");
    options.Scope.Add("email");
    options.Scope.Add("roles");

    options.TokenValidationParameters = new TokenValidationParameters
    {
        NameClaimType = "preferred_username",
        RoleClaimType = "roles"
    };

    options.Events = new OpenIdConnectEvents
    {
        OnTokenValidated = ctx =>
        {
            var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
            var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            void Add(string? r)
            {
                if (!string.IsNullOrWhiteSpace(r))
                    roles.Add(r);
            }

            var realmJson = ctx.Principal.FindFirst("realm_access")?.Value;
            if (!string.IsNullOrEmpty(realmJson))
            {
                using var doc = JsonDocument.Parse(realmJson);
                if (doc.RootElement.TryGetProperty("roles", out var arr))
                    foreach (var el in arr.EnumerateArray())
                        Add(el.GetString());
            }

            foreach (var r in roles)
                identity.AddClaim(new Claim(identity.RoleClaimType, r));

            return Task.CompletedTask;
        }
    };
});

var app = builder.Build();

app.UseExceptionHandler("/Error");
app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapStaticAssets();
app.MapGet("/api/client-secret", async (
    string realm,
    string clientId,
    ClientsService clients,
    CancellationToken ct) =>
{
    var secret = await clients.GetClientSecretAsync(realm, clientId, ct);
    return secret is not null ? Results.Ok(new { secret }) : Results.NotFound();
}).RequireAuthorization();
app.MapPost("/api/client-secret", async (
    string realm,
    string clientId,
    ClientsService clients,
    CancellationToken ct) =>
{
    var secret = await clients.RegenerateClientSecretAsync(realm, clientId, ct);
    return secret is not null ? Results.Ok(new { secret }) : Results.NotFound();
}).RequireAuthorization();
app.MapRazorPages().WithStaticAssets();
app.MapRazorPages().RequireAuthorization();
app.Run();
