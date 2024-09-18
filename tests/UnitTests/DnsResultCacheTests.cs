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

        string result = "result";
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), result);

        Assert.True(Cache.TryAdd(name, QueryType.A, cacheRecord));
        Assert.True(Cache.TryGet(name, QueryType.A, out DnsCacheRecord outRecord));

        Assert.Equal(cacheRecord.Expiration, outRecord.Expiration);
        Assert.Equal(cacheRecord.Result, outRecord.Result);
    }

    [Fact]
    public void TryGet_KeyNotExists_Fails()
    {
        string name = "www.example.com";

        string result = "result";
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), result);

        Assert.True(Cache.TryAdd(name, QueryType.A, cacheRecord));

        Assert.False(Cache.TryGet(name, QueryType.AAAA, out _));
        Assert.False(Cache.TryGet("google.com", QueryType.A, out _));
    }

    [Fact]
    public void TryGet_Expired_Fails()
    {
        string name = "www.example.com";

        string result = "result";
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), result);
        Assert.True(Cache.TryAdd(name, QueryType.A, cacheRecord));
        Assert.True(Cache.TryGet(name, QueryType.A, out _));

        TimeProvider.Advance(TimeSpan.FromSeconds(3601));
        Assert.False(Cache.TryGet(name, QueryType.A, out _));
    }
}
