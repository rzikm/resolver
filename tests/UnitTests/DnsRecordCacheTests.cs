namespace UnitTests;

public class DnsRecordCacheTests
{
    private readonly DnsRecordCache Cache;
    private readonly TestTimeProvider TimeProvider;

    public DnsRecordCacheTests()
    {
        TimeProvider = new TestTimeProvider();
        Cache = new DnsRecordCache(TimeProvider);
    }

    [Fact]
    public void TryGet_Success()
    {
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.A, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), new() { record }, new(), new());

        Assert.True(Cache.TryAdd(record.Name, QueryType.A, cacheRecord));
        Assert.True(Cache.TryGet(record.Name, QueryType.A, out DnsCacheRecord outRecord));

        Assert.Equal(cacheRecord.Expiration, outRecord.Expiration);
        Assert.Equal(cacheRecord.Answers, outRecord.Answers);
        Assert.Equal(cacheRecord.Authorities, outRecord.Authorities);
        Assert.Equal(cacheRecord.Additionals, outRecord.Additionals);
    }

    [Fact]
    public void TryGet_KeyNotExists_Fails()
    {
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.A, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), new() { record }, new(), new());

        Assert.True(Cache.TryAdd(record.Name, QueryType.A, cacheRecord));

        Assert.False(Cache.TryGet(record.Name, QueryType.AAAA, out _));
        Assert.False(Cache.TryGet("google.com", QueryType.A, out _));
    }

    [Fact]
    public void TryGet_Expired_Fails()
    {
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.A, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(TimeProvider.GetUtcNow().DateTime, TimeProvider.GetUtcNow().DateTime.AddSeconds(3600), new() { record }, new(), new());

        Assert.True(Cache.TryAdd(record.Name, QueryType.A, cacheRecord));
        TimeProvider.Advance(TimeSpan.FromSeconds(3601));

        Assert.False(Cache.TryGet(record.Name, QueryType.A, out _));
    }
}
