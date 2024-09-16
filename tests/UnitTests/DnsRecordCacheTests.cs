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
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.Address, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(new() { record }, TimeProvider.GetUtcNow().AddSeconds(3600));

        Assert.True(Cache.TryAdd(record.Name, QueryType.Address, cacheRecord));
        Assert.True(Cache.TryGet(record.Name, QueryType.Address, out DnsCacheRecord outRecord));

        Assert.Equal(cacheRecord.Expiration, outRecord.Expiration);
        Assert.Equal(cacheRecord.Records, outRecord.Records);
    }

    [Fact]
    public void TryGet_KeyNotExists_Fails()
    {
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.Address, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(new() { record }, TimeProvider.GetUtcNow().AddSeconds(3600));

        Assert.True(Cache.TryAdd(record.Name, QueryType.Address, cacheRecord));

        Assert.False(Cache.TryGet(record.Name, QueryType.IP6Address, out _));
        Assert.False(Cache.TryGet("google.com", QueryType.Address, out _));
    }

    [Fact]
    public void TryGet_Expired_Fails()
    {
        DnsResourceRecord record = new DnsResourceRecord("www.example.com", QueryType.Address, QueryClass.Internet, 3600, new byte[4]);
        DnsCacheRecord cacheRecord = new DnsCacheRecord(new() { record }, TimeProvider.GetUtcNow().AddSeconds(3600));

        Assert.True(Cache.TryAdd(record.Name, QueryType.Address, cacheRecord));
        TimeProvider.Advance(TimeSpan.FromSeconds(3601));

        Assert.False(Cache.TryGet(record.Name, QueryType.Address, out _));
    }
}
