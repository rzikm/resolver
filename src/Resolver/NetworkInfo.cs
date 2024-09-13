using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.NetworkInformation;

namespace Resolver;

static internal class NetworkInfo
{
    // basic option to get DNS serves via NetworkInfo. We may get it directly later via proper APIs. 
    public static ResolverOptions GetOptions()
    {
        IPGlobalProperties computerProperties = IPGlobalProperties.GetIPGlobalProperties();
        List<IPEndPoint> servers = new List<IPEndPoint>();

        foreach (NetworkInterface nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties = nic.GetIPProperties();
            // avoid loopback, VPN etc. Should be re-visited.

            if (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet && nic.OperationalStatus == OperationalStatus.Up)
            {
                foreach (IPAddress server in properties.DnsAddresses)
                {
                    IPEndPoint ep = new IPEndPoint(server, 53);
                    if (!servers.Contains(ep))
                    {
                        servers.Add(ep);
                    }
                }
            }
        }

        return new ResolverOptions(servers!.ToArray());
    }
}