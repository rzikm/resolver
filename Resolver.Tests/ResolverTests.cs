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
            Resolver.AddressResult[] addresses = await resolver.ResolveIPAddress("example.com", addressFamily);

            Assert.NotEmpty(addresses);
            foreach (var a in addresses)
            { 
                Assert.Equal(addressFamily, a.Address.AddressFamily);
                _output.WriteLine($"{a.Address}, TTL={a.Ttl}");
            }
        }

        public static TheoryData<string, AddressFamily, (string, int)[]> ResolveIPAddress_Data = new()
        {
            {"address.test", AddressFamily.InterNetwork, [("1.2.3.4", 7)] }
        };

        [Theory]
        [MemberData(nameof(ResolveIPAddress_Data))]
        public async Task ResolveIPAddress(string hostName, AddressFamily addressFamily, (string, int)[] expected)
        {
            Resolver.AddressResult[] addresses = await _resolver.ResolveIPAddress(hostName, addressFamily);
            
            Assert.Equal(expected.Length, addresses.Length);
            
            for (int i = 0; i < expected.Length; i++)
            {
                Resolver.AddressResult a = addresses[i];
                (string expectedAddress, int expectedTtl) = expected[i];
                Assert.Equal(IPAddress.Parse(expectedAddress), a.Address);
                Assert.Equal(expectedTtl, a.Ttl);
            }
        }

        public void Dispose() => _resolver.Dispose();
    }
}
