using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Net.Sockets;
using System.Net;

namespace Resolver;

internal static class ResolvConf
{
    public static ResolverOptions GetOptions()
    {
        int serverCount = 0;
        int domainCount = 0;

        string[] lines = File.ReadAllLines("/etc/resolv.conf");
        foreach (string line in lines)
        {
            if (line.StartsWith("nameserver"))
            {
                serverCount++;
            }
            else if (line.StartsWith("search"))
            {
                domainCount++;
            }
        }

        if (serverCount == 0)
        {
            throw new SocketException((int)SocketError.AddressNotAvailable);
        }

        IPEndPoint[] serverList = new IPEndPoint[serverCount];
        var options = new ResolverOptions(serverList);
        if (domainCount > 0)
        {
            options.SearchDomains = new string[domainCount];
        }

        serverCount = 0;
        domainCount = 0;
        foreach (string line in lines)
        {
            string[] tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (tokens[0].Equals("nameserver"))
            {
                options.Servers[serverCount] = new IPEndPoint(IPAddress.Parse(tokens[1]), 53);
                serverCount++;
            }
            else if (tokens[0].Equals("search"))
            {
                options.SearchDomains![domainCount] = tokens[1];
                domainCount++;
            }
            else if (tokens[0].Equals("domain"))
            {
                options.DefaultDomain = tokens[1];
            }
        }

        return options;
    }
}
