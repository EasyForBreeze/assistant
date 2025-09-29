using Assistant.Interfaces;
using Assistant.Services;
using Assistant.KeyCloak;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.IdentityModel.Tokens;
using Polly;
using Polly.Extensions.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks.Npgsql;
using System.Threading.RateLimiting;
using System.Security.Claims;
using System.Text.Json;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddRazorPages();

// Response compression
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;
    options.Providers.Add<BrotliCompressionProvider>();
    options.Providers.Add<GzipCompressionProvider>();
});

// Rate limiting
builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User?.Identity?.Name ?? context.Connection.RemoteIpAddress?.ToString() ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 10
            }));
    
    options.AddPolicy("ClientSecretPolicy", context =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: context.User?.Identity?.Name ?? "anonymous",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                QueueLimit = 2
            }));
});

// Health checks
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("DefaultConnection")!)
    .AddCheck("keycloak", () =>
    {
        try
        {
            var httpClient = new HttpClient();
            var baseUrl = builder.Configuration["Keycloak:BaseUrl"]?.TrimEnd('/');
            var realm = builder.Configuration["Keycloak:Realm"];
            var url = $"{baseUrl}/realms/{realm}/.well-known/openid-configuration";
            var response = httpClient.GetAsync(url).GetAwaiter().GetResult();
            return response.IsSuccessStatusCode ? HealthCheckResult.Healthy() : HealthCheckResult.Unhealthy();
        }
        catch
        {
            return HealthCheckResult.Unhealthy();
        }
    }, tags: new[] { "external" });
var isDevelopment = builder.Environment.IsDevelopment();

var kcBase = builder.Configuration["Keycloak:BaseUrl"]!;
var realm = builder.Configuration["Keycloak:Realm"]!;
var clientId = builder.Configuration["Keycloak:ClientId"]!;
var clientSecret = builder.Configuration["Keycloak:ClientSecret"]!;
var metadata = $"{kcBase}/realms/{realm}/.well-known/openid-configuration";

builder.Services.Configure<Assistant.KeyCloak.AdminApiOptions>(
    builder.Configuration.GetSection("KeycloakAdmin"));

builder.Services.AddMemoryCache(options =>
{
    options.SizeLimit = 1000; // Limit cache size
});
builder.Services.AddHttpContextAccessor();

builder.Services.AddSingleton<Assistant.KeyCloak.IAdminTokenProvider, Assistant.KeyCloak.AdminTokenProvider>();
builder.Services.AddTransient<Assistant.KeyCloak.AdminBearerHandler>();

builder.Services.AddHttpClient("kc-admin", (sp, c) =>
{
    var opt = sp.GetRequiredService<Microsoft.Extensions.Options.IOptions<Assistant.KeyCloak.AdminApiOptions>>().Value;
    c.Timeout = TimeSpan.FromSeconds(30);
    c.DefaultRequestHeaders.Add("User-Agent", "Assistant/1.0");
})
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 10,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    })
    .AddHttpMessageHandler<Assistant.KeyCloak.AdminBearerHandler>()
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(GetCircuitBreakerPolicy())
    .AddPolicyHandler(GetTimeoutPolicy());

builder.Services.AddScoped<Assistant.KeyCloak.RealmsService>();
builder.Services.AddScoped<Assistant.KeyCloak.ClientsService>();
builder.Services.AddScoped<Assistant.KeyCloak.UsersService>();
builder.Services.AddScoped<Assistant.KeyCloak.EventsService>();
builder.Services.AddSingleton<UserClientsRepository>();
builder.Services.AddSingleton<IUserClientsRepository>(sp => sp.GetRequiredService<UserClientsRepository>());
builder.Services.AddSingleton<ServiceRoleExclusionsRepository>();
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
    client.Timeout = TimeSpan.FromSeconds(30);
    client.DefaultRequestHeaders.Add("User-Agent", "Assistant/1.0");
})
    .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler
    {
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        MaxConnectionsPerServer = 5,
        AutomaticDecompression = System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate
    })
    .AddPolicyHandler(GetRetryPolicy())
    .AddPolicyHandler(GetCircuitBreakerPolicy())
    .AddPolicyHandler(GetTimeoutPolicy());
builder.Services.AddSingleton<ConfluenceWikiService>();
builder.Services.AddAuthorization();

builder.Services.AddAntiforgery(options =>
{
    options.HeaderName = "X-CSRF-TOKEN";
});

builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    opt.ForwardLimit = 2;
});

builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    options.Cookie.Name = ".KeycloakShell.Auth";
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    options.Cookie.SameSite = SameSiteMode.Lax;
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
    options.RequireHttpsMetadata = !isDevelopment;

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
app.UseResponseCompression();
app.UseRateLimiter();
if (!isDevelopment)
{
    app.UseHsts();
}
// Basic security headers
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-XSS-Protection"] = "0";
    // Conservative CSP, may require relaxations if something breaks
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; img-src 'self' data:; style-src 'self' 'unsafe-inline'; script-src 'self'";
    await next();
});
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
    ILogger<Program> logger,
    HttpContext http,
    CancellationToken ct) =>
{
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Pragma"] = "no-cache";
    var result = await ClientSecretEndpointHandler.HandleAsync(
        user,
        realm,
        clientId,
        userClients,
        cancellationToken => clients.GetClientSecretAsync(realm, clientId, cancellationToken),
        ct);
    if (result is IResult)
    {
        var username = user?.Identity?.Name ?? "unknown";
        logger.LogInformation("Client secret requested by {User} for {ClientId} in {Realm}", username, clientId, realm);
    }
    return result;
}).RequireAuthorization().RequireRateLimiting("ClientSecretPolicy");
app.MapPost("/api/client-secret", async (
    ClaimsPrincipal user,
    string realm,
    string clientId,
    ClientsService clients,
    IUserClientsRepository userClients,
    IAntiforgery antiforgery,
    ILogger<Program> logger,
    HttpContext http,
    CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(http);
    http.Response.Headers["Cache-Control"] = "no-store";
    http.Response.Headers["Pragma"] = "no-cache";
    var result = await ClientSecretEndpointHandler.HandleAsync(
        user,
        realm,
        clientId,
        userClients,
        cancellationToken => clients.RegenerateClientSecretAsync(realm, clientId, cancellationToken),
        ct);
    var username = user?.Identity?.Name ?? "unknown";
    logger.LogInformation("Client secret regeneration by {User} for {ClientId} in {Realm}", username, clientId, realm);
    return result;
}).RequireAuthorization().RequireRateLimiting("ClientSecretPolicy");
app.MapPost("/api/access-request", async (
    HttpContext context,
    IAccessRequestEmailSender emailSender,
    ILogger<Program> logger,
    IAntiforgery antiforgery,
    CancellationToken ct) =>
{
    await antiforgery.ValidateRequestAsync(context);
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
}).RequireAuthorization();

// Health check endpoint
app.MapHealthChecks("/healthz");
var razorPages = app.MapRazorPages();
razorPages.WithStaticAssets();
razorPages.RequireAuthorization();
app.Run();

static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .OrResult(msg => (int)msg.StatusCode == 429)
        .WaitAndRetryAsync(3, attempt => TimeSpan.FromMilliseconds(200 * Math.Pow(2, attempt)) + TimeSpan.FromMilliseconds(Random.Shared.Next(0, 150)));
}

static IAsyncPolicy<HttpResponseMessage> GetCircuitBreakerPolicy()
{
    return HttpPolicyExtensions
        .HandleTransientHttpError()
        .CircuitBreakerAsync(
            handledEventsAllowedBeforeBreaking: 5,
            durationOfBreak: TimeSpan.FromSeconds(60),
            onBreak: (exception, duration) => Console.WriteLine($"Circuit breaker opened for {duration}"),
            onReset: () => Console.WriteLine("Circuit breaker reset"));
}

static IAsyncPolicy<HttpResponseMessage> GetTimeoutPolicy()
{
    return Policy.TimeoutAsync<HttpResponseMessage>(TimeSpan.FromSeconds(30));
}
