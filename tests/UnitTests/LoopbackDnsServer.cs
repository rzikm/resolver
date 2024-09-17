using System.Net;
using System.Net.Sockets;

namespace UnitTests;

internal class LoopbackDnsServer : IDisposable
{
    readonly Socket _dnsSocket;
    readonly Socket _tcpSocket;

    public IPEndPoint DnsEndPoint => (IPEndPoint)_dnsSocket.LocalEndPoint!;

    public LoopbackDnsServer()
    {
        _dnsSocket = new(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        _dnsSocket.Bind(new IPEndPoint(IPAddress.Loopback, 0));

        _tcpSocket = new(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _tcpSocket.Bind(new IPEndPoint(IPAddress.Loopback, ((IPEndPoint)_dnsSocket.LocalEndPoint!).Port));
        _tcpSocket.Listen();
    }

    public void Dispose()
    {
        _dnsSocket.Dispose();
        _tcpSocket.Dispose();
    }

    public async Task ProcessUdpRequest(Func<LoopbackDnsResponseBuilder, Task> action)
    {
        byte[] buffer = new byte[512];
        EndPoint remoteEndPoint = new IPEndPoint(IPAddress.Any, 0);
        SocketReceiveFromResult result = await _dnsSocket.ReceiveFromAsync(buffer, remoteEndPoint);

        DnsDataReader reader = new DnsDataReader(buffer.AsMemory(0, result.ReceivedBytes));

        if (!reader.TryReadHeader(out DnsMessageHeader header) ||
            !reader.TryReadQuestion(out var name, out var type, out var @class))
        {
            return;
        }

        LoopbackDnsResponseBuilder responseBuilder = new((IPEndPoint)result.RemoteEndPoint!, name, type, @class);
        responseBuilder.TransactionId = header.TransactionId;
        responseBuilder.Flags = header.QueryFlags | QueryFlags.HasResponse;
        responseBuilder.ResponseCode = QueryResponseCode.NoError;

        await action(responseBuilder);

        DnsDataWriter writer = new(new Memory<byte>(buffer));
        if (!writer.TryWriteHeader(new DnsMessageHeader
        {
            TransactionId = responseBuilder.TransactionId,
            QueryFlags = responseBuilder.Flags,
            ResponseCode = responseBuilder.ResponseCode,
            QueryCount = (ushort)responseBuilder.Questions.Count,
            AnswerCount = (ushort)responseBuilder.Answers.Count,
            AuthorityCount = (ushort)responseBuilder.Authorities.Count,
            AdditionalRecordCount = (ushort)responseBuilder.Additionals.Count
        }))
        {
            throw new InvalidOperationException("Failed to write header");
        };

        foreach (var (questionName, questionType, questionClass) in responseBuilder.Questions)
        {
            if (!writer.TryWriteQuestion(questionName, questionType, questionClass))
            {
                throw new InvalidOperationException("Failed to write question");
            }
        }

        foreach (var answer in responseBuilder.Answers)
        {
            if (!writer.TryWriteResourceRecord(answer))
            {
                throw new InvalidOperationException("Failed to write answer");
            }
        }

        foreach (var authority in responseBuilder.Authorities)
        {
            if (!writer.TryWriteResourceRecord(authority))
            {
                throw new InvalidOperationException("Failed to write authority");
            }
        }

        foreach (var additional in responseBuilder.Additionals)
        {
            if (!writer.TryWriteResourceRecord(additional))
            {
                throw new InvalidOperationException("Failed to write additional records");
            }
        }

        await _dnsSocket.SendToAsync(buffer.AsMemory(0, writer.Position), SocketFlags.None, result.RemoteEndPoint);
    }
}

internal class LoopbackDnsResponseBuilder
{
    public LoopbackDnsResponseBuilder(IPEndPoint remoteEndPoint, string name, QueryType type, QueryClass @class)
    {
        RemoteEndPoint = remoteEndPoint;
        Name = name;
        Type = type;
        Class = @class;
        Questions.Add((name, type, @class));
    }

    public IPEndPoint RemoteEndPoint { get; }

    public ushort TransactionId { get; set; }
    public QueryFlags Flags { get; set; }
    public QueryResponseCode ResponseCode { get; set; }

    public string Name { get; }
    public QueryType Type { get; }
    public QueryClass Class { get; }

    public List<(string, QueryType, QueryClass)> Questions { get; } = new List<(string, QueryType, QueryClass)>();
    public List<DnsResourceRecord> Answers { get; } = new List<DnsResourceRecord>();
    public List<DnsResourceRecord> Authorities { get; } = new List<DnsResourceRecord>();
    public List<DnsResourceRecord> Additionals { get; } = new List<DnsResourceRecord>();
}

internal static class LoopbackDnsServerExtensions
{
    public static List<DnsResourceRecord> AddAddress(this List<DnsResourceRecord> records, string name, int ttl, IPAddress address)
    {
        QueryType type = address.AddressFamily == AddressFamily.InterNetwork ? QueryType.A : QueryType.AAAA;
        records.Add(new DnsResourceRecord(name, type, QueryClass.Internet, ttl, address.GetAddressBytes()));
        return records;
    }

    public static List<DnsResourceRecord> AddCname(this List<DnsResourceRecord> records, string name, int ttl, string alias)
    {
        byte[] buff = new byte[256];
        if (!DnsPrimitives.TryWriteQName(buff, alias, out int length))
        {
            throw new InvalidOperationException("Failed to encode domain name");
        }

        records.Add(new DnsResourceRecord(name, QueryType.CNAME, QueryClass.Internet, ttl, buff.AsMemory(0, length)));
        return records;
    }

    public static List<DnsResourceRecord> AddService(this List<DnsResourceRecord> records, string name, int ttl, ushort priority, ushort weight, ushort port, string target)
    {
        byte[] buff = new byte[256];
        if (!DnsPrimitives.TryWriteService(buff, priority, weight, port, target, out int length))
        {
            throw new InvalidOperationException("Failed to encode domain name");
        }

        records.Add(new DnsResourceRecord(name, QueryType.SRV, QueryClass.Internet, ttl, buff.AsMemory(0, length)));
        return records;
    }
}
