using System.Net;

namespace UnitTests;

public class DnsResultCacheTests
{
    private readonly DnsResultCache Cache;
    private readonly TestTimeProvider TimeProvider;

    public DnsResultCacheTests()
    {
        TimeProvider = new TestTimeProvider();
        Cache = new DnsResultCache(TimeProvider);
    }

    [Fact]
    public void TryGet_Success()
    {
        string name = "www.example.com";

        IPAddress[] addresses = [IPAddress.Parse("127.0.0.1")];
        DateTime expires = TimeProvider.GetUtcNow().DateTime.AddSeconds(3600);

        Assert.True(Cache.TryAdd(name, QueryType.A, expires, addresses));
        Assert.True(Cache.TryGet(name, QueryType.A, out IPAddress[] outRecord));

        Assert.Equal(addresses, outRecord);
    }

    [Fact]
    public void TryGet_KeyNotExists_Fails()
    {
        string name = "www.example.com";

        IPAddress[] addresses = [IPAddress.Parse("127.0.0.1")];
        DateTime expires = TimeProvider.GetUtcNow().DateTime.AddSeconds(3600);

        Assert.True(Cache.TryAdd(name, QueryType.A, expires, addresses));

        Assert.False(Cache.TryGet<IPAddress>(name, QueryType.AAAA, out _));
        Assert.False(Cache.TryGet<IPAddress>("google.com", QueryType.A, out _));
    }

    [Fact]
    public void TryGet_Expired_Fails()
    {
        string name = "www.example.com";

        IPAddress[] addresses = [IPAddress.Parse("127.0.0.1")];
        DateTime expires = TimeProvider.GetUtcNow().DateTime.AddSeconds(3600);

        Assert.True(Cache.TryAdd(name, QueryType.A, expires, addresses));

        TimeProvider.Advance(TimeSpan.FromSeconds(3601));
        Assert.False(Cache.TryGet(name, QueryType.A, out IPAddress[] _));
    }

    [Fact]
    public void TryAdd_Overwrite_Expired()
    {
        string name = "www.example.com";

        IPAddress[] addresses = [IPAddress.Parse("127.0.0.1")];
        DateTime expires = TimeProvider.GetUtcNow().DateTime.AddSeconds(3600);

        Assert.True(Cache.TryAdd(name, QueryType.A, expires, addresses));
        Assert.True(Cache.TryGet(name, QueryType.A, out IPAddress[] outRecord));

        TimeProvider.Advance(TimeSpan.FromSeconds(3601));

        IPAddress[] addresses2 = [IPAddress.Parse("227.0.0.1")];
        DateTime expires2 = TimeProvider.GetUtcNow().DateTime.AddSeconds(3600);
        Assert.True(Cache.TryAdd(name, QueryType.A, expires2, addresses2));
        Assert.True(Cache.TryGet(name, QueryType.A, out outRecord));

        Assert.Equal(addresses2, outRecord);
    }
}
