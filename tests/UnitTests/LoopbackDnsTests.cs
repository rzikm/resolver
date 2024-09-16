using System.Net;
using System.Net.Sockets;

namespace UnitTests;

public class LoopbackDnsTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    private readonly LoopbackDnsServer _dnsServer;
    private readonly Resolver.Resolver _resolver;

    public LoopbackDnsTests(ITestOutputHelper output)
    {
        _output = output;
        _dnsServer = new();
        _resolver = new(new[] { _dnsServer.DnsEndPoint });
    }

    public void Dispose()
    {
        _dnsServer.Dispose();
    }

    [Fact]
    public async void Test()
    {
        IPAddress address = IPAddress.Parse("172.213.245.111");
        _ = _dnsServer.ProcessUdpRequest(builder =>
        {
            builder.Answers.AddAddress("www.example.com", 3600, address);
            return Task.CompletedTask;
        });

        AddressResult[] results = await _resolver.ResolveIPAddressAsync("www.example.com", AddressFamily.InterNetwork);

        AddressResult res = Assert.Single(results);
        Assert.Equal(address, res.Address);
        Assert.Equal(3600, res.Ttl);
    }
}