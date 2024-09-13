using System.Net;
using System.Net.Sockets;
using Xunit;
using Xunit.Abstractions;

namespace Test.Net
{
    public class CancellationTests
    {
        private readonly ITestOutputHelper _output;

        public CancellationTests(ITestOutputHelper output)
        {
            _output = output;
        }

        [Fact]
        public async Task PreCanceledToken_Throws()
        {
            using Resolver resolver = new Resolver();
            CancellationTokenSource cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await resolver.ResolveIPAddressAsync("example.com", AddressFamily.InterNetwork, cts.Token));
        }

        [Fact]
        public async Task Timeout_Throws()
        {
            using Socket serverSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            serverSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

            using Resolver resolver = new Resolver((IPEndPoint)serverSocket.LocalEndPoint!);
            resolver.Timeout = TimeSpan.FromSeconds(1);

            await Assert.ThrowsAsync<TimeoutException>(async () => await resolver.ResolveIPAddressAsync("example.com", AddressFamily.InterNetwork));
        }
    }
}