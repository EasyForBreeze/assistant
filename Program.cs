using Assistant.Interfaces;
using Assistant.Services;
using Assistant.KeyCloak;
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
builder.Services.AddSingleton<IUserClientsRepository>(sp => sp.GetRequiredService<UserClientsRepository>());
builder.Services.AddSingleton<IServiceRoleExclusionsRepository, ServiceRoleExclusionsRepository>();
builder.Services.AddSingleton<ApiLogRepository>();
builder.Services.AddSingleton<ClientWikiRepository>();
builder.Services.AddScoped<IClientsProvider, DbClientsProvider>();
builder.Services.Configure<EmailOptions>(builder.Configuration.GetSection("Email"));
builder.Services.AddSingleton<EmailClientFactory>();
builder.Services.AddScoped<IAccessRequestEmailSender, AccessRequestEmailSender>();
builder.Services.AddScoped<ClientSecretDistributionService>();
var confluenceLabels = builder.Configuration.GetSection("Confluence").GetSection("Labels").Get<string[]?>();
var confluenceOptions = ConfluenceOptions.FromConnectionString(builder.Configuration.GetConnectionString("ConnectionWiki"), confluenceLabels);
builder.Services.AddSingleton(confluenceOptions);
builder.Services.AddSingleton<ConfluenceTemplateProvider>();
builder.Services.AddSingleton<RealmLinkProvider>();
builder.Services.AddHttpClient("confluence-wiki", client =>
{
    client.Timeout = TimeSpan.FromSeconds(100);
});
builder.Services.AddSingleton<ConfluenceWikiService>();
builder.Services.AddAuthorization();

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
    options.AccessDeniedPath = "/AccessDenied";
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
app.Use(async (context, next) =>
{
    if (context.User?.Identity?.IsAuthenticated == true)
    {
        var path = context.Request.Path;
        if (!path.StartsWithSegments("/AccessDenied", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/signin-oidc", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/signout-callback-oidc", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/Account/Logout", StringComparison.OrdinalIgnoreCase)
            && !path.StartsWithSegments("/api/access-request", StringComparison.OrdinalIgnoreCase))
        {
            var hasAssistantRole = context.User.IsInRole("assistant-user")
                                   || context.User.IsInRole("assistant-admin");
            if (!hasAssistantRole)
            {
                context.Response.Redirect("/AccessDenied");
                return;
            }
        }
    }

    await next();
});
app.UseAuthorization();
app.MapStaticAssets();
app.MapGet("/api/client-secret", async (
    ClaimsPrincipal user,
    string realm,
    string clientId,
    ClientsService clients,
    IUserClientsRepository userClients,
    CancellationToken ct) =>
{
    return await ClientSecretEndpointHandler.HandleAsync(
        user,
        realm,
        clientId,
        userClients,
        cancellationToken => clients.GetClientSecretAsync(realm, clientId, cancellationToken),
        ct);
}).RequireAuthorization();
app.MapPost("/api/client-secret", async (
    ClaimsPrincipal user,
    string realm,
    string clientId,
    ClientsService clients,
    IUserClientsRepository userClients,
    CancellationToken ct) =>
{
    return await ClientSecretEndpointHandler.HandleAsync(
        user,
        realm,
        clientId,
        userClients,
        cancellationToken => clients.RegenerateClientSecretAsync(realm, clientId, cancellationToken),
        ct);
}).RequireAuthorization();
app.MapPost("/api/access-request", async (
    HttpContext context,
    IAccessRequestEmailSender emailSender,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    if (context.User?.Identity?.IsAuthenticated != true)
    {
        return Results.Unauthorized();
    }

    var login = context.User.Identity?.Name;
    if (string.IsNullOrWhiteSpace(login))
    {
        login = context.User.FindFirst("preferred_username")?.Value
                 ?? context.User.FindFirst(ClaimTypes.Name)?.Value
                 ?? context.User.FindFirst(ClaimTypes.Email)?.Value;
    }

    if (string.IsNullOrWhiteSpace(login))
    {
        logger.LogWarning("Unable to determine user login for access request email.");
        return Results.Problem("Не удалось определить логин пользователя.", statusCode: StatusCodes.Status500InternalServerError);
    }

    try
    {
        await emailSender.SendAsync(login.Trim(), ct);
        return Results.Ok();
    }
    catch (InvalidOperationException ex)
    {
        logger.LogError(ex, "Access request email failed due to configuration error.");
        return Results.Problem(ex.Message, statusCode: StatusCodes.Status500InternalServerError);
    }
    catch (Exception ex)
    {
        logger.LogError(ex, "Access request email failed.");
        return Results.Problem("Не удалось отправить заявку. Попробуйте позже.", statusCode: StatusCodes.Status500InternalServerError);
    }
}).AllowAnonymous();
var razorPages = app.MapRazorPages();
razorPages.WithStaticAssets();
razorPages.RequireAuthorization();
app.Run();
