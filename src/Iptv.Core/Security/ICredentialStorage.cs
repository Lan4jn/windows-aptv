using System.Threading;
using System.Threading.Tasks;

namespace Iptv.Core.Security;

/// <summary>
/// Abstraction for secure credential storage to keep sensitive passwords off plain-text JSON.
/// </summary>
public interface ICredentialStorage
{
    Task SavePasswordAsync(string sourceId, string password, CancellationToken cancellationToken = default);
    Task<string?> GetPasswordAsync(string sourceId, CancellationToken cancellationToken = default);
    Task DeletePasswordAsync(string sourceId, CancellationToken cancellationToken = default);
}
