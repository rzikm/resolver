using System.Net;
using System.Net.Sockets;

namespace UnitTests;

public class ResolveServiceTests : LoopbackDnsTestBase
{
    public ResolveServiceTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async void ResolveService_Simple_Success()
    {
        IPAddress address = IPAddress.Parse("172.213.245.111");
        _ = DnsServer.ProcessUdpRequest(builder =>
        {
            builder.Answers.AddService("_s0._tcp.example.com", 3600, 1, 2, 8080, "www.example.com");
            builder.Additionals.AddAddress("www.example.com", 3600, address);
            return Task.CompletedTask;
        });

        ServiceResult[] results = await Resolver.ResolveServiceAsync("_s0._tcp.example.com");

        ServiceResult result = Assert.Single(results);
        Assert.Equal("www.example.com", result.Target);
        Assert.Equal(1, result.Priority);
        Assert.Equal(2, result.Weight);
        Assert.Equal(8080, result.Port);

        AddressResult addressResult = Assert.Single(result.Addresses);
        Assert.Equal(address, addressResult.Address);
    }
}
