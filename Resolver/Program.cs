// See https://aka.ms/new-console-template for more information
using Test.Net;

//var r = new Resolver(IPAddress.Parse("8.8.8.8"));
var r = new Resolver();

var response = r.ResolveIPAddressAsync("test.wft.cz", System.Net.Sockets.AddressFamily.InterNetwork).GetAwaiter().GetResult();
foreach (var entry in response)
{
    Console.WriteLine("Got {0} with {1} TTL", entry.Address, entry.Ttl);
}
response = r.ResolveIPAddressAsync("test.wft.cz", System.Net.Sockets.AddressFamily.InterNetworkV6).GetAwaiter().GetResult();
foreach (var entry in response)
{
    Console.WriteLine("Got {0} with {1} TTL", entry.Address, entry.Ttl);
}

response = r.ResolveIPAddressAsync("ipv6.wft.cz", System.Net.Sockets.AddressFamily.InterNetworkV6).GetAwaiter().GetResult();
Console.WriteLine("Results for ipv6.wft.cz {0}", response.Length);
foreach (var entry in response)
{
    Console.WriteLine("Got {0} with {1} TTL", entry.Address, entry.Ttl);
}

var (services,_) = r.ResolveServiceAsync("_test._tcp.wft.cz").GetAwaiter().GetResult();
Console.WriteLine("Results1 for _test._tcp.wft.cz {0}", services.Length);
foreach (var entry in services)
{
    Console.WriteLine("Got {0}:{1} with {2} TTL", entry.Target, entry.Port, entry.Ttl);
}

(services, _) = r.ResolveServiceAsync("_test._tcp.wft.cz").GetAwaiter().GetResult();
Console.WriteLine("Results2 for _test._tcp.wft.cz {0}", services.Length);
foreach (var entry in services)
{
    Console.WriteLine("Got {0}:{1} with {2} TTL", entry.Target, entry.Port, entry.Ttl);
}



