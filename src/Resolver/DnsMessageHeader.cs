using System.Buffers.Binary;

namespace Resolver;

// RFC 1035 4.1.1. Header section format
internal struct DnsMessageHeader
{
    internal static const HeaderLength = 12;

    private ushort _transactionId;
    private ushort _flags;

    private ushort _queryCount;
    private ushort _answerCount;
    private ushort _authorityCount;
    private ushort _additionalRecordCount;

    internal ushort QueryCount
    {
        get => ReverseByteOrder(_queryCount);
        set => _queryCount = ReverseByteOrder(value);
    }

    internal ushort AnswerCount
    {
        get => ReverseByteOrder(_answerCount);
        set => _answerCount = ReverseByteOrder(value);
    }

    internal ushort AuthorityCount
    {
        get => ReverseByteOrder(_authorityCount);
        set => _authorityCount = ReverseByteOrder(value);
    }

    internal ushort AdditionalRecordCount
    {
        get => ReverseByteOrder(_additionalRecordCount);
        set => _additionalRecordCount = ReverseByteOrder(value);
    }

    internal ushort TransactionId
    {
        get => ReverseByteOrder(_transactionId);
        set => _transactionId = ReverseByteOrder(value);
    }

    internal QueryFlags QueryFlags
    {
        get => (QueryFlags)ReverseByteOrder(_flags);
        set => _flags = ReverseByteOrder((ushort)value);
    }

    internal bool RecursionDesired
    {
        get => (QueryFlags & QueryFlags.RecursionDesired) != 0;
        set
        {
            if (value)
            {
                QueryFlags |= QueryFlags.RecursionDesired;
            }
            else
            {
                QueryFlags &= ~QueryFlags.RecursionDesired;
            }
        }
    }

    internal bool ResultTruncated => (QueryFlags & QueryFlags.ResultTruncated) != 0;

    internal bool IsResponse => (QueryFlags & QueryFlags.HasResponse) != 0;

    internal void InitQueryHeader()
    {
        this = default;
        TransactionId = (ushort)Random.Shared.Next(ushort.MaxValue);
        RecursionDesired = true;
        QueryCount = 1;
    }

    private static ushort ReverseByteOrder(ushort value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
}