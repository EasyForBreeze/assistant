using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Xunit;

namespace Assistant.Tests;

public class ClientsServiceCachingTests
{
    [Fact]
    public async Task SearchClientsAsync_RefreshesCache_WhenExclusionsChange()
    {
        var clients = new[]
        {
            new { id = "1", clientId = "alpha" },
            new { id = "2", clientId = "beta" }
        };
        var clientsJson = JsonSerializer.Serialize(clients);

        using var handler = new StaticHttpMessageHandler(request =>
        {
            if (request.RequestUri?.Query?.Contains("search=true", StringComparison.OrdinalIgnoreCase) == true)
            {
                return CreateJsonResponse(clientsJson);
            }

            return CreateJsonResponse("[]");
        });

        using var httpClient = new HttpClient(handler);
        var httpClientFactory = new FakeHttpClientFactory(httpClient);
        var options = Options.Create(new AdminApiOptions { BaseUrl = "https://kc.example" });
        var exclusions = new StubServiceRoleExclusionsRepository();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Username=test;Password=test;Database=test"
            })
            .Build();

        var logs = new ApiLogRepository(configuration);
        var httpContextAccessor = new HttpContextAccessor();
        using var memoryCache = new MemoryCache(new MemoryCacheOptions());

        var service = new ClientsService(
            httpClientFactory,
            options,
            exclusions,
            logs,
            httpContextAccessor,
            memoryCache);

        var initial = await service.SearchClientsAsync("master", "client", ct: default);
        Assert.Equal(new[] { "alpha", "beta" }, initial.Select(c => c.ClientId).ToArray());

        var added = await exclusions.AddAsync("beta", default);
        Assert.True(added);

        var updated = await service.SearchClientsAsync("master", "client", ct: default);
        Assert.Equal(new[] { "alpha" }, updated.Select(c => c.ClientId).ToArray());
    }

    private static HttpResponseMessage CreateJsonResponse(string json)
        => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

    private sealed class FakeHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public FakeHttpClientFactory(HttpClient client)
        {
            _client = client;
        }

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class StaticHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public StaticHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(_responder(request));
    }

    private sealed class StubServiceRoleExclusionsRepository : IServiceRoleExclusionsRepository
    {
        private readonly object _sync = new();
        private readonly HashSet<string> _clients = new(StringComparer.OrdinalIgnoreCase);
        private long _version;
        private CancellationTokenSource _cts = new();

        public Task<HashSet<string>> GetAllAsync(CancellationToken ct = default)
        {
            lock (_sync)
            {
                return Task.FromResult(new HashSet<string>(_clients, StringComparer.OrdinalIgnoreCase));
            }
        }

        public Task<bool> IsExcludedAsync(string clientId, CancellationToken ct = default)
        {
            lock (_sync)
            {
                return Task.FromResult(_clients.Contains(clientId));
            }
        }

        public Task<bool> AddAsync(string clientId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Task.FromResult(false);
            }

            lock (_sync)
            {
                if (_clients.Add(clientId))
                {
                    TriggerChange();
                    return Task.FromResult(true);
                }
            }

            return Task.FromResult(false);
        }

        public Task<string?> RemoveAsync(string clientId, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(clientId))
            {
                return Task.FromResult<string?>(null);
            }

            lock (_sync)
            {
                if (_clients.Remove(clientId))
                {
                    TriggerChange();
                    return Task.FromResult<string?>(clientId);
                }
            }

            return Task.FromResult<string?>(null);
        }

        public long GetVersion()
        {
            lock (_sync)
            {
                return _version;
            }
        }

        public IChangeToken CreateChangeToken()
        {
            lock (_sync)
            {
                return new CancellationChangeToken(_cts.Token);
            }
        }

        private void TriggerChange()
        {
            var oldCts = _cts;
            _cts = new CancellationTokenSource();
            _version++;

            oldCts.Cancel();
            oldCts.Dispose();
        }
    }
}
