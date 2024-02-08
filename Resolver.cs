
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Test.Net
{


    public class Resolver
    {
        private const int MaximumNameLength = 253;
        private const int IPv4Length = 4;
        private const int IPv6Length = 16;
        private const int HeaderSize = 12;
        private const int RecordHeaderLength = 10;
        private const byte DotValue = 46;
        private readonly ASCIIEncoding s_ascii = new ASCIIEncoding();

        [Flags]
        private enum QueryFlags : byte
        {
            Recursion = 1
        }

        private enum QueryType
        {
            Address = 1,
            NameServer = 2,
            MailExchange = 15,
            Text = 16,
            IP6Address = 28,
            Service = 33,
            All = 255
        }

        private enum QueryClass
        {
            Internet = 1
        }


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

        private IPAddress _nextServer;
        private Socket _server;
        private Random _rnd = new Random();
        private ResolverOptions _options;

        private ConcurrentDictionary<string, ServiceResultCacheRecord>? _serviceRecordCache;
        public struct AddressResult
        {
            public IPAddress Address;
            public int Ttl;
        }

        public struct ServiceResult
        {
            public int Port;
            public int Priority;
            public string Target;
            public int Ttl;
            public int Weight;
        }

        private struct ServiceResultCacheRecord
        {
            public ServiceResult[] ServiceResult;
            public long Expires;
        }
        public struct Response
        {
            public IPAddress? Address;
            public int Ttl;
            int ErrorCode;
        }

        public Resolver()
        {
            try
            {
                _options = OperatingSystem.IsWindows() ? NetworkInfo.GetOptions() : ResolvConf.GetOptions();
                _nextServer = _options.Servers[0];
                _server = new Socket(_nextServer.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
                _server.Connect(_nextServer, 53);
            }
            catch (Exception ex)
            {
                Console.WriteLine(ex);
            }
        }

        public Resolver(ResolverOptions options)
        {
            _options = options;

            _nextServer = _options.Servers[0];
            _server = new Socket(_nextServer.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            _server.Connect(_nextServer, 53);
        }

        public Resolver(IEnumerable<IPAddress> servers) : this(new ResolverOptions(servers.ToArray<IPAddress>()))
        {
        }

        public Resolver(IPAddress address) : this(new ResolverOptions(address))
        {
        }

        public async ValueTask<AddressResult[]> ResolveIPAddress(string name, AddressFamily addressFamily)
        {
            if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6 && addressFamily != AddressFamily.Unspecified)
            {
                throw new NotSupportedException("IP only");
            }

            // TODO name checks.

            //var response = Cache.Lookup(name, addressFamily);
            var _buffer = ArrayPool<byte>.Shared.Rent(255);
            try
            {

                Memory<byte> buffer = new Memory<byte>(_buffer);
       
                SetQueryHeader(MemoryMarshal.Cast<byte, Header>(buffer.Span));
                QueryType queryType = addressFamily == AddressFamily.InterNetwork ? QueryType.Address : QueryType.IP6Address;
                int size = EncodeQuery(buffer.Span.Slice(HeaderSize), name, queryType);

                // retransmit ????
                await _server.SendAsync(buffer.Slice(0, HeaderSize + size), default);
                int readLength = await _server.ReceiveAsync(buffer);

                Console.WriteLine("Received {0} bytes of data", readLength);

                return (AddressResult[])ProcessResponse(new Span<byte>(_buffer, 0, readLength), queryType);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_buffer);
            }
        }

        public async ValueTask<ServiceResult[]> ResolveService(string name)
        {
            if (_options.CacheResults && _serviceRecordCache?.TryGetValue(name, out ServiceResultCacheRecord record) == true)
            {
                // TBD update expiration or change Ttl to be absolute value.
                return record.ServiceResult;
            } 
            else
            {
                Console.WriteLine("Cache lookup failed");
            } 

            var _buffer = ArrayPool<byte>.Shared.Rent(255);
            try
            {

                Memory<byte> buffer = new Memory<byte>(_buffer);
                SetQueryHeader(MemoryMarshal.Cast<byte, Header>(buffer.Span));

                int size = EncodeQuery(buffer.Span.Slice(HeaderSize), name, QueryType.Service);

                // retransmit ????
                await _server.SendAsync(buffer.Slice(0, HeaderSize + size), default);
                int readLength = await _server.ReceiveAsync(buffer);

                Console.WriteLine("Received {0} bytes of data", readLength);

                ServiceResult[] result = (ServiceResult[])ProcessResponse(new Span<byte>(_buffer, 0, readLength), QueryType.Service);
                if (_options.CacheResults && result.Length > 0)
                {
                    if (_serviceRecordCache == null)
                    {
                        _serviceRecordCache = new ConcurrentDictionary<string, ServiceResultCacheRecord>();
                    }

                    //ServiceResultCacheRecord record;
                    record.ServiceResult = result;
                    // TBD should we cache minimal record? Servers seems to normalize TTL anyway.
                    record.Expires = Environment.TickCount64 + result[0].Ttl * TimeSpan.TicksPerSecond;
                    _serviceRecordCache.AddOrUpdate(name, record, (key, oldValue) => record);
                    Console.WriteLine("adder SRV record to cache!!!");
                }

                return result;
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
            int length = s_ascii.GetBytes(name, buffer.Slice(1));
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

            return length > 0 ? s_ascii.GetString(name.Slice(0, length - 1)) : string.Empty;
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
    }
}