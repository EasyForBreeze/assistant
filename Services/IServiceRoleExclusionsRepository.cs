using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Primitives;

namespace Assistant.Services;

public interface IServiceRoleExclusionsRepository
{
    Task<HashSet<string>> GetAllAsync(CancellationToken ct = default);

    Task<bool> IsExcludedAsync(string clientId, CancellationToken ct = default);

    Task<bool> AddAsync(string clientId, CancellationToken ct = default);

    Task<string?> RemoveAsync(string clientId, CancellationToken ct = default);

    long GetVersion();

    IChangeToken CreateChangeToken();
}
