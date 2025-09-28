using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assistant.Interfaces;

public interface IUserClientsRepository
{
    Task<List<ClientSummary>> GetForUserAsync(string username, bool isAdmin, CancellationToken ct = default);
}
