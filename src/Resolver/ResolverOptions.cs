using System.Net;

namespace Resolver;

public class ResolverOptions
{
    public IPEndPoint[] Servers;
    public string DefaultDomain = string.Empty;
    public string[]? SearchDomains;
    public bool UseHostsFile;

    public ResolverOptions(IPEndPoint[] servers)
    {
        Servers = servers;
    }

    public ResolverOptions(IPEndPoint server)
    {
        Servers = new IPEndPoint[] { server };
    }
}