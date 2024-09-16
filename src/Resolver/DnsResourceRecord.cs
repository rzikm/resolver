namespace Resolver;

internal struct DnsResourceRecord
{
    public string Name { get; }
    public QueryType Type { get; }
    public QueryClass Class { get; }
    public int Ttl { get; }
    public ReadOnlyMemory<byte> Data { get; }

    public DnsResourceRecord(string name, QueryType type, QueryClass @class, int ttl, ReadOnlyMemory<byte> data)
    {
        Name = name;
        Type = type;
        Class = @class;
        Ttl = ttl;
        Data = data;
    }
}