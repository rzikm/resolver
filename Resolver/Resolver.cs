
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Test.Net
{
    [Flags]
    internal enum QueryFlags : byte
    {
        Recursion = 1
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
            internal ushort TransactionId;
            private byte _lowFlags;
            private byte _highFlags;
            internal short _queryCount;
            internal short _answerCount;
            internal ushort NameServerCount;
            internal ushort AdditionalRecordCount;

            internal short QueryCount
            {
                get
                {
                    return IPAddress.NetworkToHostOrder(_queryCount);
                }
                set
                {
                    _queryCount = IPAddress.HostToNetworkOrder(value);
                }
            }
            internal short AnswerCount
            {
                get
                {
                    return IPAddress.NetworkToHostOrder(_answerCount);
                }
                set
                {
                    _answerCount = IPAddress.HostToNetworkOrder(value);
                }
            }
            internal bool Recursion
            {
                get
                {
                    return (_lowFlags & (byte)QueryFlags.Recursion) != 0;
                }
                set
                {
                    if (value)
                    {
                        _lowFlags |= (byte)QueryFlags.Recursion;
                    }
                    else
                    {
                        _lowFlags &= (byte)~QueryFlags.Recursion;
                    }
                }
            }
        }

        private IPEndPoint _serverEndPoint;
        private Socket _socket;
        private Random _rnd = new Random();
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

        public async ValueTask<AddressResult[]> ResolveIPAddressAsync(string name, AddressFamily addressFamily)
        {
            if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6 && addressFamily != AddressFamily.Unspecified)
            {
                throw new NotSupportedException("IP only");
            }

            // TODO name checks.

            byte[] _buffer = ArrayPool<byte>.Shared.Rent(255);
            try
            {

                Memory<byte> buffer = new Memory<byte>(_buffer);
       
                SetQueryHeader(MemoryMarshal.Cast<byte, Header>(buffer.Span));
                QueryType queryType = addressFamily == AddressFamily.InterNetwork ? QueryType.Address : QueryType.IP6Address;
                int size = EncodeQuery(buffer.Span.Slice(HeaderSize), name, queryType);

                // retransmit ????
                await _socket.SendAsync(buffer.Slice(0, HeaderSize + size), default);
                int readLength = await _socket.ReceiveAsync(buffer);

                Console.WriteLine("Received {0} bytes of data", readLength);

                return (AddressResult[])ProcessResponse(new Span<byte>(_buffer, 0, readLength), queryType);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }

        public async ValueTask<(ServiceResult[], AddressResult[]?)> ResolveServiceAsync(string name, bool includeAddresses = false)
        {
            byte[] _buffer = ArrayPool<byte>.Shared.Rent(255);
            try
            {

                Memory<byte> buffer = new Memory<byte>(_buffer);
                SetQueryHeader(MemoryMarshal.Cast<byte, Header>(buffer.Span));

                int size = EncodeQuery(buffer.Span.Slice(HeaderSize), name, QueryType.Service);

                // retransmit ????
                await _socket.SendAsync(buffer.Slice(0, HeaderSize + size), default);
                int readLength = await _socket.ReceiveAsync(buffer);

                Console.WriteLine("Received {0} bytes of data", readLength);

                ServiceResult[] result = (ServiceResult[])ProcessResponse(new Span<byte>(_buffer, 0, readLength), QueryType.Service);

                return (result, null);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }

        private int SetQueryHeader(Span<Header> header)
        {
            header.Clear();
            header[0].TransactionId = (ushort)_rnd.Next(ushort.MaxValue);
            header[0].Recursion = true;
            header[0].QueryCount = 1;

            return 12;
        }

        private int EncodeName(Span<byte> buffer, string name)
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
        private int EncodeQuery(Span<byte> buffer, string name, QueryType queryType)
        {
            int size = EncodeName(buffer, name);
            BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(size), (short)queryType);
            BinaryPrimitives.WriteInt16BigEndian(buffer.Slice(size + 2), (short)QueryClass.Internet);
            return size + 4;
        }

        private int ProcessResponseQueries(Span<byte> buffer, int count)
        {
            int offset = HeaderSize;

            // TBD should ve verify the answer is what we asked for? 
            while (count > 0)
            {
                //DecodeName(buffer, offset);
                int len = SkipName(buffer, offset);
                int queryType = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + len, 2));
                int queryClass = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + len + 2, 2));

                //Console.WriteLine("Type = {0} class = {1}", queryType, queryClass);
                offset += len + 4;
                count--;
            }

            return offset;
        }

        private object ProcessServiceRecord(Span<byte> buffer, int offset, int count)
        {
            var response = new ServiceResult[count];
            int index = 0;
            while (count > 0)
            {
                int nameLength = SkipName(buffer, offset);
                offset += nameLength;

                QueryType queryType = (QueryType)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
                int queryClass = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + 4, 4));
                int dataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 8, 2));
                offset += RecordHeaderLength;

                if (queryType == QueryType.Service)
                {
                    response[index].Priority = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset, 2));
                    response[index].Weight = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 2, 2));
                    response[index].Port = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + 4, 2));
                    response[index].Ttl = (int)ttl;
                    response[index].Target = DecodeName(buffer, offset + 6);

                    index++;
                }
                else
                {
                    // TBD logging?
                }

                offset += dataLength;
                count--;
            }

            return response;
        }

        private object ProcessResponse(Span<byte> buffer, QueryType _queryType)
        {
            Span<Header> header = MemoryMarshal.Cast<byte, Header>(buffer);


            Console.WriteLine(header[0].TransactionId);
            Console.WriteLine("questions {0} Answers {1} length {2}", header[0].QueryCount, header[0].AnswerCount, buffer.Length);

            int offset = ProcessResponseQueries(buffer, header[0].QueryCount);

            if (_queryType == QueryType.Service)
            {
                return ProcessServiceRecord(buffer, offset, header[0].AnswerCount);
            }
            int count = header[0].AnswerCount;

            var response = new AddressResult[count];
            var index = 0;
            while (count > 0)
            {
                Console.WriteLine("Processing answer {0} ot of {1}", count, header[0].AnswerCount);
                int nameLength = SkipName(buffer, offset);
                Console.WriteLine("Question name length = {0}", nameLength);
                QueryType queryType = (QueryType)BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + nameLength, 2));
                int queryClass = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + nameLength + 2, 2));
                uint ttl = BinaryPrimitives.ReadUInt32BigEndian(buffer.Slice(offset + nameLength + 4, 4)); 
                int dataLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.Slice(offset + nameLength + 8, 2));

                Console.WriteLine("Type {0} class {1} ttl = {2} data {3}", queryType, queryClass, ttl, dataLength);

                switch (queryType)
                {
                    case QueryType.Address:
                        Debug.Assert(dataLength == 4);
                        response[index].Address = new IPAddress(buffer.Slice(offset + nameLength + RecordHeaderLength, IPv4Length));
                        response[index].Ttl = (int)ttl;

                        index++;
                        offset += nameLength + RecordHeaderLength + IPv4Length;
                        count--;
                        continue;
                    //break;
                    case QueryType.IP6Address:
                        Debug.Assert(dataLength == IPv6Length);
                        response[index].Address = new IPAddress(buffer.Slice(offset + nameLength + RecordHeaderLength, IPv6Length));
                        response[index].Ttl = (int)ttl;

                        index++;
                        offset += nameLength + RecordHeaderLength + IPv6Length;
                        count--;
                        continue;
                    case QueryType.Service:
                    case QueryType.MailExchange:
                    default:
                        offset += nameLength + RecordHeaderLength + dataLength;
                        break;
                }
                
                count--;
            }

            return response;
        }

        private string DecodeName(Span<byte> buffer, int offset)
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

        private int SkipName(Span<byte> buffer, int offset)
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
    }
}