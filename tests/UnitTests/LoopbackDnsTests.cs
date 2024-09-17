using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace UnitTests;

public class LoopbackDnsTests : LoopbackDnsTestBase
{
    public LoopbackDnsTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async void ResolveIPv4_Simple_Success()
    {
        IPAddress address = IPAddress.Parse("172.213.245.111");
        _ = DnsServer.ProcessUdpRequest(builder =>
        {
            builder.Answers.AddAddress("www.example.com", 3600, address);
            return Task.CompletedTask;
        });

        AddressResult[] results = await Resolver.ResolveIPAddressesAsync("www.example.com", AddressFamily.InterNetwork);

        AddressResult res = Assert.Single(results);
        Assert.Equal(address, res.Address);
        Assert.Equal(3600, res.Ttl);
    }


    [Fact]
    public async void ResolveIPv4_Aliases_Success()
    {
        IPAddress address = IPAddress.Parse("172.213.245.111");
        _ = DnsServer.ProcessUdpRequest(builder =>
        {
            builder.Answers.AddCname("www.example.com", 3600, "www.example2.com");
            builder.Answers.AddCname("www.example2.com", 3600, "www.example3.com");
            builder.Answers.AddAddress("www.example3.com", 3600, address);
            return Task.CompletedTask;
        });

        AddressResult[] results = await Resolver.ResolveIPAddressesAsync("www.example.com", AddressFamily.InterNetwork);

        AddressResult res = Assert.Single(results);
        Assert.Equal(address, res.Address);
        Assert.Equal(3600, res.Ttl);
    }

    [Fact]
    public async void ResolveIPv4_Aliases_NotFound_Success()
    {
        IPAddress address = IPAddress.Parse("172.213.245.111");
        _ = DnsServer.ProcessUdpRequest(builder =>
        {
            builder.Answers.AddCname("www.example.com", 3600, "www.example2.com");
            builder.Answers.AddCname("www.example2.com", 3600, "www.example3.com");

            // extra address in the answer not connected to the above
            builder.Answers.AddAddress("www.example4.com", 3600, address);
            return Task.CompletedTask;
        });

        AddressResult[] results = await Resolver.ResolveIPAddressesAsync("www.example.com", AddressFamily.InterNetwork);

        Assert.Empty(results);
    }
}