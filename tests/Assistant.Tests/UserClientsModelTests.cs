using System.Reflection;
using System.Runtime.Serialization;
using Assistant.Pages.Admin;
using Assistant.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Assistant.Tests;

public sealed class UserClientsModelTests
{
    [Fact]
    public void FormatAssignmentChangeDetails_ForGrant_ReturnsExpectedMessage()
    {
        var model = CreateModel();
        var result = InvokeFormatAssignmentChangeDetails(model, "GRANT", "test-user", "test-client");

        Assert.Equal("Пользователю test-user присвоено: test-client", result);
    }

    [Fact]
    public void FormatAssignmentChangeDetails_ForRevoke_ReturnsExpectedMessage()
    {
        var model = CreateModel();
        var result = InvokeFormatAssignmentChangeDetails(model, "REVOKE", "another-user", "client-display");

        Assert.Equal("Пользователю another-user удалены клиенты: client-display", result);
    }

    private static UserClientsModel CreateModel()
    {
        var uninitializedRepository = (ApiLogRepository)FormatterServices.GetUninitializedObject(typeof(ApiLogRepository));

        return new UserClientsModel(
            realms: null!,
            clients: null!,
            users: null!,
            repo: null!,
            logs: uninitializedRepository,
            logger: NullLogger<UserClientsModel>.Instance);
    }

    private static string InvokeFormatAssignmentChangeDetails(
        UserClientsModel model,
        string actionSuffix,
        string normalizedTargetUser,
        string clientDisplay)
    {
        var method = typeof(UserClientsModel).GetMethod(
            name: "FormatAssignmentChangeDetails",
            BindingFlags.Instance | BindingFlags.NonPublic);

        Assert.NotNull(method);
        return (string)method!.Invoke(model, new object[] { actionSuffix, normalizedTargetUser, clientDisplay })!;
    }
}
