using Assistant.KeyCloak;
using Assistant.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace Assistant.Tests;

public sealed class ClientsServiceTests
{
    [Fact]
    public async Task RemoveMissingLocalRolesAsync_RemovesObsoleteRolesAndInvalidatesCaches()
    {
        var handler = new FakeHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var factory = new TestHttpClientFactory(httpClient);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Username=test;Password=test;Database=test"
            })
            .Build();

        using var cache = new MemoryCache(new MemoryCacheOptions());
        using var exclusionsCache = new MemoryCache(new MemoryCacheOptions());

        var exclusions = new ServiceRoleExclusionsRepository(configuration, exclusionsCache);
        var logs = new ApiLogRepository(configuration);
        var service = new ClientsService(
            factory,
            Options.Create(new AdminApiOptions { BaseUrl = "https://kc.local" }),
            exclusions,
            logs,
            new HttpContextAccessor(),
            cache);

        cache.Set("client-roles:test-realm:test-client:50", new object());
        cache.Set("client-role-map:test-realm:test-client", new object());

        var desiredRoles = new[] { "Existing-Role" };

        var method = typeof(ClientsService).GetMethod(
            "RemoveMissingLocalRolesAsync",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(method);

        var task = (Task<List<string>>)method!.Invoke(
            service,
            new object[] { "test-realm", "test-client", desiredRoles, CancellationToken.None })!;

        var existingRoles = await task.ConfigureAwait(false);

        Assert.Contains("existing-role", existingRoles, StringComparer.OrdinalIgnoreCase);
        Assert.Contains("obsolete-role", existingRoles, StringComparer.OrdinalIgnoreCase);

        Assert.Single(handler.DeleteRequests);
        Assert.EndsWith("/obsolete-role", handler.DeleteRequests[0], System.StringComparison.Ordinal);

        Assert.False(cache.TryGetValue("client-roles:test-realm:test-client:50", out _));
        Assert.False(cache.TryGetValue("client-role-map:test-realm:test-client", out _));
    }

    private sealed class TestHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public TestHttpClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        private int _getCount;

        public List<string> DeleteRequests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                _getCount++;
                var content = _getCount == 1
                    ? "[{\"name\":\"existing-role\"},{\"name\":\"obsolete-role\"}]"
                    : "[]";

                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(content, Encoding.UTF8, "application/json")
                });
            }

            if (request.Method == HttpMethod.Delete)
            {
                DeleteRequests.Add(request.RequestUri!.ToString());
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }
}
