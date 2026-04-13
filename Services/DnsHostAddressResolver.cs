using System.Net;

namespace PoCPdfSharp.Services;

public sealed class DnsHostAddressResolver : IHostAddressResolver
{
    public async Task<IReadOnlyCollection<IPAddress>> ResolveAsync(string host, CancellationToken cancellationToken)
    {
        var addresses = await Dns.GetHostAddressesAsync(host, cancellationToken);
        return addresses;
    }
}
