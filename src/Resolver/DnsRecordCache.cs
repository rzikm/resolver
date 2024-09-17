using System.Collections.Concurrent;

namespace Resolver;

internal struct DnsCacheRecord
{
    public List<DnsResourceRecord> Answers { get; }
    public List<DnsResourceRecord> Authorities { get; }
    public List<DnsResourceRecord> Additionals { get; }
    public DateTime CreatedAt { get; }
    public DateTime Expiration { get; }

    public DnsCacheRecord(DateTime createdAt, DateTime expiration, List<DnsResourceRecord> answers, List<DnsResourceRecord> authorities, List<DnsResourceRecord> additionals)
    {
        CreatedAt = createdAt;
        Expiration = expiration;
        Answers = answers;
        Authorities = authorities;
        Additionals = additionals;
    }
}

internal class DnsRecordCache
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

    public DnsRecordCache(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
    }

    public bool TryGet(string name, QueryType type, out DnsCacheRecord record)
    {
        var key = new DnsCacheKey(name, type);
        if (_cache.TryGetValue(key, out var r) && r.Expiration > _timeProvider.GetUtcNow().DateTime)
        {
            record = r;
            return true;
        }

        record = default;
        return false;
    }

    public bool TryAdd(string name, QueryType type, DnsCacheRecord record)
    {
        _cache.AddOrUpdate(new DnsCacheKey(name, type), record, (_, _) => record);
        return true;
    }
}
