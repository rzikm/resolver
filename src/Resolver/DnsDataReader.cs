using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace Resolver;

internal struct DnsDataReader
{
    private ReadOnlyMemory<byte> _buffer;
    private int _position;

    public DnsDataReader(ReadOnlyMemory<byte> buffer)
    {
        _buffer = buffer;
        _position = 0;
    }

    public bool TryReadHeader(out DnsMessageHeader header)
    {
        if (_buffer.Length - _position < DnsMessageHeader.HeaderLength)
        {
            header = default;
            return false;
        }

        _position += DnsMessageHeader.HeaderLength;
        header = MemoryMarshal.AsRef<DnsMessageHeader>(_buffer.Span);
        return true;
    }

    internal bool TryReadQuestion([NotNullWhen(true)] out string? name, out QueryType type, out QueryClass @class)
    {
        if (!TryReadDomainName(out name) ||
            !TryReadUInt16(out ushort typeAsInt) ||
            !TryReadUInt16(out ushort classAsInt))
        {
            type = 0;
            @class = 0;
            return false;
        }

        type = (QueryType)typeAsInt;
        @class = (QueryClass)classAsInt;
        return true;
    }

    public bool TryReadUInt16(out ushort value)
    {
        if (_buffer.Length - _position < 2)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt16BigEndian(_buffer.Span.Slice(_position));
        _position += 2;
        return true;
    }


    public bool TryReadUInt32(out uint value)
    {
        if (_buffer.Length - _position < 4)
        {
            value = 0;
            return false;
        }

        value = BinaryPrimitives.ReadUInt32BigEndian(_buffer.Span.Slice(_position));
        _position += 4;
        return true;
    }

    public bool TryReadResourceRecord(out DnsResourceRecord record)
    {
        if (!TryReadDomainName(out string? name) ||
            !TryReadUInt16(out ushort type) ||
            !TryReadUInt16(out ushort @class) ||
            !TryReadUInt32(out uint ttl) ||
            !TryReadUInt16(out ushort dataLength) ||
            _buffer.Length - _position < dataLength)
        {
            record = default;
            return false;
        }

        ReadOnlyMemory<byte> data = _buffer.Slice(_position, dataLength);
        _position += dataLength;

        record = new DnsResourceRecord(name, (QueryType)type, (QueryClass)@class, (int)ttl, data);
        return true;
    }

    public bool TryReadDomainName([NotNullWhen(true)] out string? name)
    {
        if (DnsPrimitives.TryReadQName(_buffer.Span, _position, out name, out int bytesRead))
        {
            _position += bytesRead;
            return true;
        }

        return false;
    }
}