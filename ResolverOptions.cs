using System.Net;

namespace Test.Net
{
    public class ResolverOptions
    {
        public IPAddress[] Servers;
        public string DefaultDomain = string.Empty;
        public string[]? SearchDomains;
        public bool CacheResults = true;
        public bool UseHostsFile;

        public ResolverOptions(IPAddress[] servers)
        {
            Servers = servers;
        }

        public ResolverOptions(IPAddress server)
        {
            Servers = new IPAddress[]{ server };
        }
    }
}
