using Assistant.Interfaces;
using Assistant.Services;
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
var authority = $"{kcBase}/realms/{realm}";
var metadata = $"{authority}/.well-known/openid-configuration";



// после builder.Services.AddRazorPages();
builder.Services.Configure<Assistant.KeyCloak.AdminApiOptions>(
    builder.Configuration.GetSection("KeycloakAdmin"));

builder.Services.AddMemoryCache();

// Провайдер токена сервис-клиента
builder.Services.AddSingleton<Assistant.KeyCloak.IAdminTokenProvider, Assistant.KeyCloak.AdminTokenProvider>();

// Хэндлер, который подставляет Bearer к админским запросам
builder.Services.AddTransient<Assistant.KeyCloak.AdminBearerHandler>();

// Именованный HttpClient для Admin API
builder.Services.AddHttpClient("kc-admin", (sp, c) =>
{
    var opt = sp.GetRequiredService<
        Microsoft.Extensions.Options.IOptions<Assistant.KeyCloak.AdminApiOptions>>().Value;
    // BaseAddress можно не ставить; используем абсолютные URL в сервисе.
    c.Timeout = TimeSpan.FromSeconds(100);
})
.AddHttpMessageHandler<Assistant.KeyCloak.AdminBearerHandler>();

// Сервис реалмов
builder.Services.AddScoped<Assistant.KeyCloak.RealmsService, Assistant.KeyCloak.RealmsService>();
builder.Services.AddScoped<Assistant.KeyCloak.ClientsService, Assistant.KeyCloak.ClientsService>();

builder.Services.AddAuthentication(options =>
  {
      options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
      options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
  })
  .AddCookie(options =>
  {
      options.Cookie.Name = ".KeycloakShell.Auth";
      options.SlidingExpiration = true;
  })
  .AddOpenIdConnect(options =>
  {
     //options.Authority = authority;
      options.MetadataAddress = metadata;

      options.ClientId = clientId;
      options.ClientSecret = clientSecret;
      options.ResponseType = "code";

      options.SaveTokens = true;                   // сохраним access/refresh/id токены в куки
      options.GetClaimsFromUserInfoEndpoint = true;
      options.RequireHttpsMetadata = false;         // в DEV можно поставить false, если без https

      // Callback пути по умолчанию (совпадают с тем, что добавили в Keycloak)
      options.CallbackPath = "/signin-oidc";
      options.SignedOutCallbackPath = "/signout-callback-oidc";

      // Запрашиваемые scope'ы
      options.Scope.Clear();
      options.Scope.Add("openid");
      options.Scope.Add("profile");
      options.Scope.Add("email");
      options.Scope.Add("roles"); // благодаря mapper'у в Keycloak

      // Какой claim считать именем и ролью
      options.TokenValidationParameters = new TokenValidationParameters
      {
          NameClaimType = "preferred_username",
          RoleClaimType = "roles"
      };

      // Корректный логаут через Keycloak end-session
      options.Events = new OpenIdConnectEvents
      {
          OnTokenValidated = ctx =>
          {
              var identity = (ClaimsIdentity)ctx.Principal!.Identity!;
              var roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

              void Add(string? r) { if (!string.IsNullOrWhiteSpace(r)) roles.Add(r!); }

              // 1) realm_access.roles
              var realmJson = ctx.Principal.FindFirst("realm_access")?.Value;
              if (!string.IsNullOrEmpty(realmJson))
              {
                  using var doc = JsonDocument.Parse(realmJson);
                  if (doc.RootElement.TryGetProperty("roles", out var arr))
                      foreach (var el in arr.EnumerateArray()) Add(el.GetString());
              }
              foreach (var r in roles)
                  identity.AddClaim(new Claim(identity.RoleClaimType, r));

              return Task.CompletedTask;
          }
      };
  });
builder.Services.AddScoped<IClientsProvider, FakeClientsProvider>();
builder.Services.AddAuthorization();
builder.Services.Configure<ForwardedHeadersOptions>(opt =>
{
    opt.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // opt.KnownProxies.Add(IPAddress.Parse("X.X.X.X")); // при необходимости зафиксировать
});


var app = builder.Build();

app.UseForwardedHeaders();
app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();


app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapRazorPages().WithStaticAssets();
app.MapRazorPages().RequireAuthorization();

app.Run();
