﻿using System.Net;
using System.Net.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace Resolver.Tests;

public class ResolverTests : IDisposable
{
    private readonly ITestOutputHelper _output;

    public ResolverTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private readonly Resolver _resolver = new Resolver(new IPEndPoint(IPAddress.Loopback, 1053));

    [Theory]
    [InlineData(AddressFamily.InterNetwork)]
    [InlineData(AddressFamily.InterNetworkV6)]
    public async Task ResolveIPAddress_External(AddressFamily addressFamily)
    {
        using Resolver resolver = new Resolver();
        AddressResult[] addresses = await resolver.ResolveIPAddressAsync("example.com", addressFamily);

        Assert.NotEmpty(addresses);
        foreach (var a in addresses)
        {
            Assert.Equal(addressFamily, a.Address.AddressFamily);
            _output.WriteLine($"{a.Address}, TTL={a.Ttl}");
        }
    }

    public static TheoryData<string, AddressFamily, AddressResult[]> ResolveIPAddress_Data = new()
        {
            {"address.test", AddressFamily.InterNetwork, [new(7, IPAddress.Parse("1.2.3.4"))] },
            {"address.test", AddressFamily.InterNetworkV6, [new(42, IPAddress.Parse("abcd::1234"))] },
            {"www.address.test", AddressFamily.InterNetwork, [
                new(0, IPAddress.Parse("10.20.30.40")),
                new(1, IPAddress.Parse("10.20.30.41")),
                new(2, IPAddress.Parse("10.20.30.42")),
                new(3, IPAddress.Parse("10.20.30.43")),] },
        };

    [Theory]
    [MemberData(nameof(ResolveIPAddress_Data))]
    public async Task ResolveIPAddressAsync(string name, AddressFamily addressFamily, AddressResult[] expected)
    {
        AddressResult[] result = await _resolver.ResolveIPAddressAsync(name, addressFamily);

        Assert.Equal(expected, result);
    }


    [Fact]
    public async Task ResolveIPAddressAsync_Parallel()
    {
        List<(Task<AddressResult[]> Task, IPAddress Expected)> workers = new();

        for (int i = 0; i < 10; i++)
        {
            for (int j = 0; j < 10; j++)
            {
                string hostName = $"a{j:D2}.address.test";
                IPAddress expected = IPAddress.Parse($"40.30.20.{j + 10}");
                Task<AddressResult[]> task = Task.Run(() => _resolver.ResolveIPAddressAsync(hostName, AddressFamily.InterNetwork).AsTask());
                workers.Add((task, expected));
            }
        }

        await Task.WhenAll(workers.Select(w => w.Task));

        AddressResult[][] results = workers.Select(w => w.Task.Result).ToArray();

        foreach (var (task, expected) in workers)
        {
            AddressResult[] result = await task;
            Assert.Equal(expected, result.Single().Address);
        }
    }

    //; _service._proto ttl IN SRV priority weight port target
    //_s0._tcp		0 IN SRV 0 0 1000 a0.srv.test.
    //_s1._udp		1 IN SRV 0 0 1001 a1.srv.test.
    //_s2._tcp		2 IN SRV 0 0 1002 a2.srv.test.
    //_s2._tcp		2 IN SRV 1 0 1002 xx.srv.test.
    //_s3._tcp		3 IN SRV 0 0 1003 xx.srv.test.
    //_s3._tcp		3 IN SRV 1 1 1003 yy.srv.test.
    //_s3._tcp		3 IN SRV 1 2 1003 zz.srv.test.

    public static TheoryData<string, ServiceResult[]> ResolveServiceAsync_Data = new()
        {
            { "_s0._tcp.srv.test", [new(0,0,0,1000, "a0.srv.test")] },
            { "_s1._udp.srv.test", [new(1,0,0,1001, "a1.srv.test")] },
            { "_s2._tcp.srv.test", [new(2,0,0,1002, "a2.srv.test"), new(2,1,0,1002,"xx.srv.test")] },
            { "_s3._tcp.srv.test", [new(3,0,0,1003, "xx.srv.test"), new(3,1,1,6666,"xx.srv.test"), new(3,1,2,1003,"yy.srv.test") ] },
        };

    //a1	1   IN A     192.168.1.1
    //a1	2   IN A     192.168.1.2
    //a2	3   IN A     192.168.1.3
    public static TheoryData<string, ServiceResult[], AddressResult[]> ResolveServiceAsync_WithAddresses_Data = new()
        {
            { "_s0._tcp.srv.test", [new(0,0,0,1000, "a0.srv.test")], [] },
            { "_s1._udp.srv.test", [new(1,0,0,1001, "a1.srv.test")], [new(1, IPAddress.Parse("192.168.1.1")), new(2, IPAddress.Parse("192.168.1.2"))] },
            { "_s2._tcp.srv.test", [new(2,0,0,1002, "a2.srv.test"), new(2,1,0,1002,"xx.srv.test")], [new(3, IPAddress.Parse("192.168.1.3"))] },
        };

    [Theory]
    [MemberData(nameof(ResolveServiceAsync_Data))]
    public async Task ResolveServiceAsync(string name, ServiceResult[] expected)
    {
        ServiceResult[] result = await _resolver.ResolveServiceAsync(name);
        Assert.Equal(expected, result);
    }

    [Theory]
    [MemberData(nameof(ResolveServiceAsync_WithAddresses_Data))]
    public async Task ResolveServiceAsync_WithAddresses(string name, ServiceResult[] expectedSrv, AddressResult[] expectedAddr)
    {
        (ServiceResult[] srv, AddressResult[]? addr) = await _resolver.ResolveServiceAndAddressesAsync(name);
        Assert.Equal(expectedSrv, srv);
        Assert.Equal(expectedAddr, addr);
    }

    [Fact]
    public async Task ResolveIPAddressAsync_ResultSizeOver512()
    {
        AddressResult[] expected = [
            new (0 , IPAddress.Parse("abcd::0000")),
                new (1 , IPAddress.Parse("abcd::0001")),
                new (2 , IPAddress.Parse("abcd::0002")),
                new (3 , IPAddress.Parse("abcd::0003")),
                new (4 , IPAddress.Parse("abcd::0004")),
                new (5 , IPAddress.Parse("abcd::0005")),
                new (6 , IPAddress.Parse("abcd::0006")),
                new (7 , IPAddress.Parse("abcd::0007")),
                new (8 , IPAddress.Parse("abcd::0008")),
                new (9 , IPAddress.Parse("abcd::0009")),
                new (10, IPAddress.Parse("abcd::0010")),
                new (11, IPAddress.Parse("abcd::0011")),
                new (12, IPAddress.Parse("abcd::0012")),
                new (13, IPAddress.Parse("abcd::0013")),
                new (14, IPAddress.Parse("abcd::0014")),
                new (15, IPAddress.Parse("abcd::0015")),
                new (16, IPAddress.Parse("abcd::0016")),
                new (17, IPAddress.Parse("abcd::0017")),
                new (18, IPAddress.Parse("abcd::0018")),
                new (19, IPAddress.Parse("abcd::0019")),
                ];
        AddressResult[] actual = await _resolver.ResolveIPAddressAsync("x.trunc.test", AddressFamily.InterNetworkV6);
        Assert.Equal(expected, actual);
    }

    public static TheoryData<string, (int Ttl, string[] Text)[]> ResolveTextAsync_Data = new()
        {
            { "t1.txt.test", [(1, ["test A"])] },
            { "t2.txt.test", [(2, ["test B"]), (3, ["test C"])] },
            { "multi1.txt.test", [(4, ["multi A", "multi B"])] },
            { "multi2.txt.test", [(5, ["multi C", "multi D"]), (6, ["multi E", "multi F", "multi G"])] },
        };

    [Theory]
    [MemberData(nameof(ResolveTextAsync_Data))]
    public async Task ResolveTextAsync(string name, (int Ttl, string[] Text)[] expected)
    {
        TxtResult[] result = await _resolver.ResolveTextAsync(name);
        Assert.Equal(expected.Length, result.Length);

        for (int i = 0; i < expected.Length; i++)
        {
            Assert.Equal(expected[i].Ttl, result[i].Ttl);
            string[] actualText = result[i].GetText().ToArray();
            Assert.Equal(expected[i].Text, actualText);
        }
    }

    [Fact]
    public async Task ResolveTextAsync_Large()
    {
        TxtResult[] result = await _resolver.ResolveTextAsync("large.txt.test");
        Assert.Equal(220, result.Length);
        foreach (TxtResult r in result)
        {
            Assert.Equal(256, r.Data.Length);
        }
    }

    [Fact]
    public async Task ResolveTextAsync_TcpTruncated()
    {
        Exception ex = await Assert.ThrowsAsync<Exception>(async () => { await _resolver.ResolveTextAsync("trunc-tcp.txt.test"); });
        _output.WriteLine(ex.Message);
    }

    public void Dispose() => _resolver.Dispose();
}