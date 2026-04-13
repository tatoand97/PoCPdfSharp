using System.Net;

namespace PoCPdfSharp.Services;

public interface IHostAddressResolver
{
    Task<IReadOnlyCollection<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken);
}
