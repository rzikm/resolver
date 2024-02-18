using System.Net;
using System.Net.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace Test.Net
{
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

        //; _service._proto ttl IN SRV priority weight port target
        //_s0._tcp		0 IN SRV 0 0 1000 a0.srv.test.
        //_s1._udp		1 IN SRV 0 0 1001 a1.srv.test.
        //_s2._tcp		2 IN SRV 0 0 1002 a2.srv.test.
        //_s2._tcp		2 IN SRV 1 0 1002 xx.srv.test.
        //_s3._tcp		3 IN SRV 0 0 1003 xx.srv.test.
        //_s3._tcp		3 IN SRV 1 1 1003 yy.srv.test.
        //_s3._tcp		3 IN SRV 1 2 1003 zz.srv.test.

        public static TheoryData<string, ServiceResult[]> ResolveServiceAsync_Basic_Data = new()
        {
            { "_s0._tcp.srv.test", [new(0,0,0,1000, "a0.srv.test")] },
            { "_s1._udp.srv.test", [new(1,0,0,1001, "a1.srv.test")] },
            { "_s2._tcp.srv.test", [new(2,0,0,1002, "a2.srv.test"), new(2,1,0,1002,"xx.srv.test")] },
            { "_s3._tcp.srv.test", [new(3,0,0,1003, "xx.srv.test"), new(3,1,1,1003,"yy.srv.test"), new(3,1,2,1003,"zz.srv.test") ] },
        };

        [Theory]
        [MemberData(nameof(ResolveServiceAsync_Basic_Data))]
        public async Task ResolveServiceAsync_Basic(string name, ServiceResult[] expected)
        {
            ServiceResult[] result = await _resolver.ResolveServiceAsync(name);
            Assert.Equal(expected, result);
        }

        public void Dispose() => _resolver.Dispose();
    }
}
