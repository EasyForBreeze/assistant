using System.Collections.Generic;
using System.Threading.Tasks;
using Assistant.Services;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Assistant.Tests;

public sealed class ApiLogRepositoryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgresContainer;
    private ApiLogRepository _repository = default!;

    public ApiLogRepositoryTests()
    {
        _postgresContainer = new PostgreSqlBuilder()
            .WithImage("postgres:16-alpine")
            .WithDatabase("api_log_tests")
            .WithUsername("postgres")
            .WithPassword("postgres")
            .Build();
    }

    public async Task InitializeAsync()
    {
        await _postgresContainer.StartAsync();

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:DefaultConnection"] = _postgresContainer.GetConnectionString(),
            })
            .Build();

        _repository = new ApiLogRepository(configuration);
    }

    public Task DisposeAsync() => _postgresContainer.DisposeAsync().AsTask();

    [Fact]
    public async Task GetLogsAsync_ReturnsEntry_WhenFilteredByPrefixedOperationType()
    {
        const string operationType = "PREFIX:suffix";
        const string username = "test-user";
        const string realm = "test-realm";
        const string targetId = "target-123";

        await _repository.LogAsync(operationType, username, realm, targetId);

        var result = await _repository.GetLogsAsync(operationType: operationType);

        var log = Assert.Single(result);
        Assert.Equal(operationType, log.OperationType);
        Assert.Equal(username, log.Username);
        Assert.Equal(realm, log.Realm);
        Assert.Equal(targetId, log.TargetId);
    }
}
