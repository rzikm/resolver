
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Xml.Linq;

namespace Test.Net
{
    [Flags]
    internal enum QueryFlags : ushort
    {
        IsCheckingDisabled = 0x0010,
        IsAuthenticData = 0x0020,
        RecursionAvailable = 0x0080,
        RecursionDesired = 0x0100,
        ResultTruncated = 0x0200,
        HasAuthorityAnswer = 0x0400,
        HasQuery = 0x8000,
    }

    internal enum QueryType
    {
        Address = 1,
        NameServer = 2,
        MailExchange = 15,
        Text = 16,
        IP6Address = 28,
        Service = 33,
        All = 255
    }

    internal enum QueryClass
    {
        Internet = 1
    }

    public record struct AddressResult(int Ttl, IPAddress Address);

    public record struct ServiceResult(int Ttl, int Priority, int Weight, int Port, string Target);

    public record struct TxtResult(int Ttl, byte[] Data)
    {
        public IEnumerable<string> GetText() => GetText(Encoding.ASCII);

        public IEnumerable<string> GetText(Encoding encoding)
        {
            for (int i = 0; i < Data.Length;)
            {
                int length = Data[i];
                yield return encoding.GetString(Data, i + 1, length);
                i += length + 1;
            }
        }
    }

    public class Resolver : IDisposable
    {
        private const int MaximumNameLength = 253;
        private const int IPv4Length = 4;
        private const int IPv6Length = 16;
        private const int HeaderSize = 12;
        private const int RecordHeaderLength = 10;
        private const byte DotValue = 46;

        // RFC 1035 4.1.1. Header section format
        private struct Header
        {
            private static readonly Random s_rnd = new Random();

            internal ushort TransactionId;
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

            internal void InitQueryHeader()
            {
                this = default;
                TransactionId = (ushort)s_rnd.Next(ushort.MaxValue);
                RecursionDesired = true;
                QueryCount = 1;
            }
        }

        private IPEndPoint _serverEndPoint;
        private Socket _socket;
        private ResolverOptions _options;

        public Resolver() : this(OperatingSystem.IsWindows() ? NetworkInfo.GetOptions() : ResolvConf.GetOptions())
        {
        }

        public Resolver(ResolverOptions options)
        {
            _options = options;

            _serverEndPoint = _options.Servers[0];
            _socket = new Socket(_serverEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _socket.Connect(_serverEndPoint);
        }

        public Resolver(IEnumerable<IPEndPoint> servers) : this(new ResolverOptions(servers.ToArray()))
        {
        }

        public Resolver(IPEndPoint server) : this(new ResolverOptions(server))
        {
        }

        private delegate TResult ParseResponseDataDelegate<TResult>(Span<byte> buffer, ref Header header);

        private async ValueTask<TResult> SendQueryAsync<TResult>(string name, QueryType queryType, ParseResponseDataDelegate<TResult> parseResponseBody, CancellationToken cancellationToken)
        {
            byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
            byte[]? tcpBuffer = null;
            try
            {
                // TODO: Implement UDP retries.
                Memory<byte> memory = new Memory<byte>(buffer);
                int questionSize = EncodeQuestion(buffer, name, queryType);
                await _socket.SendAsync(memory.Slice(0, questionSize), cancellationToken);
                int readLength = await _socket.ReceiveAsync(memory, cancellationToken);

                TResult? result = DoParseResponse(buffer.AsSpan(0, readLength), parseResponseBody);

                if (result is null)
                {
                    using Socket tcpSocket = new Socket(_serverEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                    await tcpSocket.ConnectAsync(_serverEndPoint);

                    // The TCP message is prefixed with the 2-byte length
                    questionSize = EncodeQuestion(buffer.AsSpan(2), name, queryType);
                    BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)questionSize);

                    await tcpSocket.SendAsync(memory.Slice(0, questionSize + 2), cancellationToken);

                    readLength = await tcpSocket.ReceiveAsync(memory.Slice(0, 2), cancellationToken);
                    Assert(readLength == 2); // TODO: implement robust reading

                    // The TCP message is prefixed with the 2-byte length
                    int responseSize = BinaryPrimitives.ReadUInt16BigEndian(buffer);
                    tcpBuffer = ArrayPool<byte>.Shared.Rent(responseSize);
                    memory = new Memory<byte>(tcpBuffer);

                    for (int offset = 0; offset < responseSize; offset += readLength)
                    {
                        readLength = await tcpSocket.ReceiveAsync(memory.Slice(offset), cancellationToken);
                    }

                    result = DoParseResponse(tcpBuffer.AsSpan(0, responseSize), parseResponseBody);
                }

                return result is null ? throw new Exception("Invalid response: Truncated TCP!") : result;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);

                if (tcpBuffer != null)
                {
                    ArrayPool<byte>.Shared.Return(tcpBuffer);
                }
            }


            static TResult? DoParseResponse(Span<byte> buffer, ParseResponseDataDelegate<TResult> parseResponseBody)
            {
                ref Header header = ref MemoryMarshal.AsRef<Header>(buffer);
                Log($"T:{header.TransactionId} questions {header.QueryCount} Answers {header.AnswerCount} length {buffer.Length}");

                return header.ResultTruncated ? default : parseResponseBody(buffer, ref header);
            }
        }

        public ValueTask<AddressResult[]> ResolveIPAddressAsync(string name, AddressFamily addressFamily, CancellationToken cancellationToken = default)
        {
            if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6 && addressFamily != AddressFamily.Unspecified)
            {
                throw new NotSupportedException("IP only");
            }

            // TODO name checks.
            QueryType queryType = addressFamily == AddressFamily.InterNetwork ? QueryType.Address : QueryType.IP6Address;

            return SendQueryAsync(name, queryType, ParseResponse, cancellationToken); 

            static AddressResult[] ParseResponse(Span<byte> buffer, ref Header header)
            {
                int offset = SkipResponseQuestionSection(buffer, header.QueryCount);
                var result = new AddressResult[header.AnswerCount];
                int actualCount = ParseAddressRecords(buffer, ref offset, result);

                if (actualCount != result.Length)
                {
                    throw new Exception($"Invalid response: expected {result.Length} Address records, got {actualCount}.");
                }

                return result;
            }
        }

        public ValueTask<TxtResult[]> ResolveTextAsync(string name, CancellationToken cancellationToken = default)
        {
            return SendQueryAsync(name, QueryType.Text, ParseResponse, cancellationToken);

            static TxtResult[] ParseResponse(Span<byte> buffer, ref Header header)
            {
                int offset = SkipResponseQuestionSection(buffer, header.QueryCount);
                var result = new TxtResult[header.AnswerCount];
                int actualCount = ParseTxtRecords(buffer, ref offset, result);

                if (actualCount != result.Length)
                {
                    throw new Exception($"Invalid response: expected {result.Length} TXT records, got {actualCount}.");
                }

                return result;
            }
        }

        public async ValueTask<ServiceResult[]> ResolveServiceAsync(string name, CancellationToken cancellationToken = default)
            => (await ResolveServiceAsync(name, false, cancellationToken)).Services;

        // https://www.rfc-editor.org/rfc/rfc2782.html: "Implementors are urged, but not required, to return the address record(s) in the Additional Data section."
        // If no matching addresses are found, an empty array is being returned.
        public async ValueTask<(ServiceResult[] Services, AddressResult[] Addresses)> ResolveServiceAndAddressesAsync(string name, CancellationToken cancellationToken = default)
        {
            var result = await ResolveServiceAsync(name, true, cancellationToken);
            Assert(result.Addresses != null);
            return (result.Services, result.Addresses);
        }

        private ValueTask<(ServiceResult[] Services, AddressResult[]? Addresses)> ResolveServiceAsync(string name, bool includeAddresses, CancellationToken cancellationToken)
        {
            return SendQueryAsync(name, QueryType.Service, ParseResponse, cancellationToken);

            (ServiceResult[], AddressResult[]?) ParseResponse(Span<byte> buffer, ref Header header)
            {
                int offset = SkipResponseQuestionSection(buffer, header.QueryCount);
                var result = new ServiceResult[header.AnswerCount];
                int actualCount = ParseServiceRecords(buffer, ref offset, result);
                if (actualCount != result.Length)
                {
                    throw new Exception($"Invalid response: expected {result.Length} SRV records, got {actualCount}.");
                }

                if (includeAddresses)
                {
                    if (header.AdditionalRecordCount == 0)
                    {
                        return (result, Array.Empty<AddressResult>());
                    }

                    SkipRecords(buffer, ref offset, header.AuthorityCount);

                    AddressResult[] addresses = new AddressResult[header.AdditionalRecordCount];
                    actualCount = ParseAddressRecords(buffer, ref offset, addresses);
                    
                    // If there were non A/AAAA records in the additional section, shrink the array.
                    if (actualCount < addresses.Length)
                    {
                        Array.Resize(ref addresses, actualCount);
                    }

                    return (result, addresses);
                }
                else
                {
                    return (result, null);
                }
            }
        }

        private static int EncodeName(Span<byte> buffer, string name)
        {
            int length = Encoding.ASCII.GetBytes(name, buffer.Slice(1));
            buffer[length + 1] = 0; // last label
            Span<byte> nameBuffer = buffer.Slice(0, length + 1);
            while (true)
            {
                int index = nameBuffer.Slice(1).IndexOf<byte>(DotValue);
                if (index == -1)
                {
                    nameBuffer[0] = (byte)(nameBuffer.Length - 1);
                    // this is last label
                    break;
                }
                else
                {
                    nameBuffer[0] = (byte)index;
                    nameBuffer = nameBuffer.Slice(index + 1);
                }
            }

            return length + 2;
        }
        private static int EncodeQuestion(Span<byte> buffer, string name, QueryType queryType)
        {
            MemoryMarshal.AsRef<Header>(buffer).InitQueryHeader();
            buffer = buffer.Slice(HeaderSize);
            int size = EncodeName(buffer, name);
            BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(size), (short)queryType);
            BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(size + 2), (short)QueryClass.Internet);
            return size + 4 + HeaderSize;
        }

        private static int SkipResponseQuestionSection(Span<byte> buffer, int count)
        {
            int offset = HeaderSize;

            // TBD should ve verify the answer is what we asked for? 
            while (count > 0)
            {
                //DecodeName(buffer, offset);
                int len = SkipName(buffer, offset);
                int queryType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + len, 2));
                int queryClass = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + len + 2, 2));

                Log($"SkipResponseQueries: type={queryType} class={queryClass} len={len}");
                offset += len + 4;
                count--;
            }

            return offset;
        }

        private static (QueryType, uint, int) ReadRecordHeader(Span<byte> buffer, ref int offset)
        {
            int nameLength = SkipName(buffer, offset);
            offset += nameLength;

            QueryType queryType = (QueryType)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 4, 4));
            int dataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 8, 2));
            offset += RecordHeaderLength;
            
            Log($"Type {queryType} ttl = {ttl} data {dataLength}");

            return (queryType, ttl, dataLength);
        }
        
        private static int ParseServiceRecords(Span<byte> buffer, ref int offset, Span<ServiceResult> result)
        {
            int index = 0;
            int count = result.Length;
            while (count > 0)
            {
                (QueryType queryType, uint ttl, int dataLength) = ReadRecordHeader(buffer, ref offset);

                ref ServiceResult r = ref result[index];

                if (queryType == QueryType.Service)
                {
                    r.Priority = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
                    r.Weight = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
                    r.Port = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 4, 2));
                    r.Ttl = (int)ttl;
                    r.Target = DecodeName(buffer, offset + 6);

                    index++;
                }
                else
                {
                    // TBD logging?
                }

                offset += dataLength;
                count--;
            }

            return index;
        }

        private static void SkipRecords(Span<byte> buffer, ref int offset, int count)
        {
            while (count > 0)
            {
                (_, _, int dataLength) = ReadRecordHeader(buffer, ref offset);
                offset += dataLength;
                count--;
            }
        }

        private static int ParseAddressRecords(Span<byte> buffer, ref int offset, Span<AddressResult> result)
        {
            var index = 0;
            int count = result.Length;
            while (count > 0)
            {
                Log($"Processing answer {count} of {result.Length}");

                (QueryType queryType, uint ttl, int dataLength) = ReadRecordHeader(buffer, ref offset);

                ref AddressResult r = ref result[index];

                if (queryType is QueryType.Address or QueryType.IP6Address)
                {
                    Assert(queryType == QueryType.Address ? dataLength == 4 : dataLength == IPv6Length);
                    r.Address = new IPAddress(buffer.Slice(offset, dataLength));
                    r.Ttl = (int)ttl;

                    index++;
                }

                offset += dataLength;
                count--;
            }

            return index;
        }

        private static int ParseTxtRecords(Span<byte> buffer, ref int offset, TxtResult[] result)
        {
            int index = 0;
            int count = result.Length;
            while (count > 0)
            {
                (QueryType queryType, uint ttl, int dataLength) = ReadRecordHeader(buffer, ref offset);

                ref TxtResult r = ref result[index];

                if (queryType is QueryType.Text)
                {
                    r.Ttl = (int)ttl;
                    r.Data = new byte[dataLength];
                    buffer.Slice(offset, dataLength).CopyTo(r.Data);
                    index++;
                }

                offset += dataLength;
                count--;
            }

            return index;
        }

        private static string DecodeName(Span<byte> buffer, int offset)
        {
            Span<byte> name = stackalloc byte[MaximumNameLength + 1];
            int length = 0;
            bool jumpOffset = false;

            int index = offset;
            while (true)
            {
                if (buffer[index] == 0)
                {
                    index++;
                    break;
                }
                else if ((buffer[index] & (byte)0xc0) == 0xc0)
                {
                    if (jumpOffset)
                    {
                        throw new Exception("ONLY ONE POINTER ALLOWED");
                    }
                    jumpOffset = true;
                    index = (buffer[index] & (byte)0x3f) + buffer[index + 1];
                    continue;
                }
                else
                {
                    var label = buffer.Slice(index + 1, buffer[index]);
                    label.CopyTo(name.Slice(length));
                    length += buffer[index];
                    name[length] = DotValue;
                    length++;
                    index += buffer[index] + 1;
                }
            }

            return length > 0 ? Encoding.ASCII.GetString(name.Slice(0, length - 1)) : string.Empty;
        }

        private static int SkipName(Span<byte> buffer, int offset)
        {
            int index = offset;
            while (true)
            {
                if (buffer[index] == 0)
                {
                    index++;
                    break;
                }
                else if ((buffer[index] & (byte)0xc0) == 0xc0)
                {
                    index += 2;
                    break;
                }
                else
                {
                    index += buffer[index] + 1;
                }
            }

            return index - offset;
        }

        public void Dispose() => _socket?.Dispose();

        private static void Log(FormattableString str)
        {
            Console.WriteLine(str);
        }

        private static void Assert(bool condition)
        {
            if (!condition)
            {
                throw new Exception("Assertion failed");
            }

            // Debug.Assert(condition);
        }

        private static ushort ReverseByteOrder(ushort value) => BitConverter.IsLittleEndian ? BinaryPrimitives.ReverseEndianness(value) : value;
    }
}