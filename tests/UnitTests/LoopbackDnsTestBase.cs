using System.Net;
using System.Net.Sockets;
using System.Runtime.CompilerServices;

namespace UnitTests;

public abstract class LoopbackDnsTestBase : IDisposable
{
    protected readonly ITestOutputHelper Output;

    internal readonly LoopbackDnsServer DnsServer;
    protected readonly Resolver.Resolver Resolver;
    private readonly TestTimeProvider _timeProvider;

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "SetTimeProvider")]
    extern static void MockTimeProvider(Resolver.Resolver instance, TimeProvider provider);

    public LoopbackDnsTestBase(ITestOutputHelper output)
    {
        Output = output;
        DnsServer = new();
        Resolver = new([DnsServer.DnsEndPoint]);
        Resolver.Timeout = TimeSpan.FromSeconds(5);
        _timeProvider = new();
        MockTimeProvider(Resolver, _timeProvider);
    }

    public void Dispose()
    {
        DnsServer.Dispose();
    }
}
