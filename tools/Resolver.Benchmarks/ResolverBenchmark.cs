[MemoryDiagnoser]
public class ResolverVsOsBenchmark
{
    private const string Host = "google.com";

    public ResolverVsOsBenchmark()
    {
        _resolver = new Resolver();
        _resolver.Timeout = TimeSpan.FromSeconds(5);

        _dnsClient = new LookupClient();
    }

    private readonly Resolver _resolver;
    private readonly LookupClient _dnsClient;

    [Benchmark]
    public async Task<AddressResult[]> Resolver()
    {
        return await _resolver.ResolveIPAddressAsync(Host, AddressFamily.InterNetwork);
    }

    [Benchmark(Baseline = true)]
    public async Task<IPAddress[]> OsDns()
    {
        return await Dns.GetHostAddressesAsync(Host, AddressFamily.InterNetwork);
    }

    [Benchmark]
    public async Task<IDnsQueryResponse> DnsClient()
    {
        return await _dnsClient.QueryAsync(Host, QueryType.A);
    }
}