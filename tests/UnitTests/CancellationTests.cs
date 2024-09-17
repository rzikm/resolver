using System.Net;
using System.Net.Sockets;

namespace UnitTests;

public class CancellationTests : LoopbackDnsTestBase
{
    public CancellationTests(ITestOutputHelper output) : base(output)
    {
    }

    [Fact]
    public async Task PreCanceledToken_Throws()
    {
        CancellationTokenSource cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await Resolver.ResolveIPAddressesAsync("example.com", AddressFamily.InterNetwork, cts.Token));
    }

    [Fact]
    public async Task Timeout_Throws()
    {
        Resolver.Timeout = TimeSpan.FromSeconds(1);
        await Assert.ThrowsAsync<TimeoutException>(async () => await Resolver.ResolveIPAddressesAsync("example.com", AddressFamily.InterNetwork));
    }
}