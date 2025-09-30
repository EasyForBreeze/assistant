using Assistant.KeyCloak;
using Assistant.KeyCloak.Models;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assistant.Tests;

public class ClientsServiceTests
{
    [Fact]
    public async Task SearchClientsAsync_BypassesCachedEmptyResults_WhenSkipCacheRequested()
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var handler = new FakeKeycloakHandler();
        var httpClient = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var factory = new FakeHttpClientFactory(httpClient);

        var configValues = new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Username=test;Password=test;Database=test"
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(configValues).Build();

        var exclusions = new ServiceRoleExclusionsRepository(configuration, cache);
        cache.Set("service-role-exclusions", new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        var apiLogs = new ApiLogRepository(configuration);

        var service = new ClientsService(
            factory,
            Options.Create(new AdminApiOptions { BaseUrl = "http://localhost" }),
            exclusions,
            apiLogs,
            new HttpContextAccessor(),
            cache);

        const string realm = "test-realm";
        const string clientId = "sample-client";

        var initial = await service.SearchClientsAsync(realm, clientId, 0, 20, CancellationToken.None);
        Assert.Empty(initial);

        var cacheKey = BuildCacheKey(realm, clientId, 0, 20);
        Assert.False(cache.TryGetValue(cacheKey, out _));

        cache.Set(cacheKey, Array.Empty<ClientShort>(), new MemoryCacheEntryOptions
        {
            AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(5)
        });

        var cached = await service.SearchClientsAsync(realm, clientId, 0, 20, CancellationToken.None);
        Assert.Empty(cached);

        handler.SetClients(new[] { new ClientShort("uuid-123", clientId) });

        var stillCached = await service.SearchClientsAsync(realm, clientId, 0, 20, CancellationToken.None);
        Assert.Empty(stillCached);

        var requestsBeforeBypass = handler.TotalRequests;

        var live = await service.SearchClientsAsync(realm, clientId, 0, 20, CancellationToken.None, skipCache: true);

        Assert.Single(live);
        Assert.Equal("uuid-123", live[0].Id);
        Assert.Equal(clientId, live[0].ClientId);
        Assert.True(handler.TotalRequests > requestsBeforeBypass);
    }

    private static string BuildCacheKey(string realm, string query, int first, int max)
    {
        var method = typeof(ClientsService)
            .GetMethod("BuildClientSearchCacheKey", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        return (string)method!.Invoke(null, new object[] { realm, query, first, max })!;
    }

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeKeycloakHandler : HttpMessageHandler
    {
        private readonly object _sync = new();
        private List<ClientShort> _clients = new();
        private int _totalRequests;

        public int TotalRequests => Volatile.Read(ref _totalRequests);

        public void SetClients(IEnumerable<ClientShort> clients)
        {
            lock (_sync)
            {
                _clients = clients.ToList();
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri is null)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            }

            if (request.Method == HttpMethod.Get && request.RequestUri.AbsolutePath.Contains("/clients", StringComparison.Ordinal))
            {
                Interlocked.Increment(ref _totalRequests);
                var query = QueryHelpers.ParseQuery(request.RequestUri.Query);
                var hasSearch = query.TryGetValue("search", out var search) && search.Any(v => string.Equals(v, "true", StringComparison.OrdinalIgnoreCase));
                var clientId = query.TryGetValue("clientId", out var clientIds) ? clientIds.FirstOrDefault() : null;

                List<ClientShort> snapshot;
                lock (_sync)
                {
                    snapshot = _clients.ToList();
                }

                IEnumerable<ClientShort> matches = snapshot;
                if (!string.IsNullOrEmpty(clientId))
                {
                    matches = hasSearch
                        ? snapshot.Where(c => c.ClientId.Contains(clientId, StringComparison.OrdinalIgnoreCase))
                        : snapshot.Where(c => string.Equals(c.ClientId, clientId, StringComparison.OrdinalIgnoreCase));
                }

                var payload = matches
                    .Select(c => new { id = c.Id, clientId = c.ClientId })
                    .ToList();

                var json = JsonSerializer.Serialize(payload);
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(json, Encoding.UTF8, "application/json")
                };

                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
