using System.Collections.Concurrent;

namespace Resolver;

internal struct DnsResponse
{
    public DnsMessageHeader Header { get; }
    public List<DnsResourceRecord> Answers { get; }
    public List<DnsResourceRecord> Authorities { get; }
    public List<DnsResourceRecord> Additionals { get; }
    public DateTime CreatedAt { get; }
    public DateTime Expiration { get; }

    public DnsResponse(DnsMessageHeader header, DateTime createdAt, DateTime expiration, List<DnsResourceRecord> answers, List<DnsResourceRecord> authorities, List<DnsResourceRecord> additionals)
    {
        Header = header;
        CreatedAt = createdAt;
        Expiration = expiration;
        Answers = answers;
        Authorities = authorities;
        Additionals = additionals;
    }
}

internal struct DnsCacheRecord
{
    public DateTime CreatedAt { get; }
    public DateTime Expiration { get; }
    public object Result { get; }

    public DnsCacheRecord(DateTime createdAt, DateTime expiration, object result)
    {
        CreatedAt = createdAt;
        Expiration = expiration;
        Result = result;
    }
}

internal class DnsResultCache
{
    private struct DnsCacheKey : IEquatable<DnsCacheKey>
    {
        public string Name { get; }
        public QueryType Type { get; }

        public DnsCacheKey(string name, QueryType type)
        {
            Name = name;
            Type = type;
        }

        public bool Equals(DnsCacheKey other)
        {
            return Name.Equals(other.Name, StringComparison.Ordinal) && Type == other.Type;
        }

        public override bool Equals(object? obj)
        {
            return obj is DnsCacheKey other && Equals(other);
        }

        public override int GetHashCode()
        {
            return HashCode.Combine(Name, (int)Type);
        }
    }

    private readonly TimeProvider _timeProvider;

    private readonly ConcurrentDictionary<DnsCacheKey, DnsCacheRecord> _cache = new();
    private readonly ConcurrentDictionary<string, DateTime> _negativeCache = new();

    public DnsResultCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryGet<T>(string name, QueryType type, out T[] result)
    {
        if (_negativeCache.TryGetValue(name, out var expiration) && expiration > _timeProvider.GetUtcNow().DateTime)
        {
            result = Array.Empty<T>();
            return true;
        }

        var key = new DnsCacheKey(name, type);
        if (_cache.TryGetValue(key, out var r) && r.Expiration > _timeProvider.GetUtcNow().DateTime)
        {
            result = (T[])r.Result;
            return true;
        }

        result = default;
        return false;
    }

    public bool TryAdd<T>(string name, QueryType type, DateTime expiration, T[] result)
    {
        DnsCacheRecord record = new DnsCacheRecord(_timeProvider.GetUtcNow().DateTime, expiration, result);
        _cache.AddOrUpdate(new DnsCacheKey(name, type), record, (_, _) => record);
        return true;
    }

    public bool TryAddNonexistent(string name, DateTime expiration)
    {
        _negativeCache.AddOrUpdate(name, expiration, (_, _) => expiration);
        return true;
    }
}
