using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;

namespace Resolver;

public class Resolver : IDisposable
{
    private const int MaximumNameLength = 253;
    private const int IPv4Length = 4;
    private const int IPv6Length = 16;
    private const int HeaderSize = 12;
    private const int RecordHeaderLength = 10;
    private const byte DotValue = 46;

    private static readonly TimeSpan s_maxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    bool _disposed = false;
    private IPEndPoint _serverEndPoint;
    private ResolverOptions _options;
    private TimeSpan _timeout;
    private CancellationTokenSource _pendingRequestsCts = new();

    public Resolver() : this(OperatingSystem.IsWindows() ? NetworkInfo.GetOptions() : ResolvConf.GetOptions())
    {
    }

    public Resolver(ResolverOptions options)
    {
        _options = options;
        _serverEndPoint = _options.Servers[0];
    }

    public Resolver(IEnumerable<IPEndPoint> servers) : this(new ResolverOptions(servers.ToArray()))
    {
    }

    public Resolver(IPEndPoint server) : this(new ResolverOptions(server))
    {
    }

    public TimeSpan Timeout
    {
        get => _timeout;
        set
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (value != System.Threading.Timeout.InfiniteTimeSpan)
            {
                ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(value, TimeSpan.Zero);
                ArgumentOutOfRangeException.ThrowIfGreaterThan(value, s_maxTimeout);
            }
            _timeout = value;
        }
    }

    private delegate TResult ParseResponseDataDelegate<TResult>(Span<byte> buffer, ref Header header);
    private async ValueTask<TResult> SendQueryAsync<TResult>(string name, QueryType queryType, ParseResponseDataDelegate<TResult> parseResponseBody, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        (CancellationTokenSource cts, bool disposeTokenSource, CancellationTokenSource pendingRequestsCts) = PrepareCancellationTokenSource(cancellationToken);

        try
        {
            return await SendQueryAsyncCore(name, queryType, parseResponseBody, cts.Token);
        }
        catch (OperationCanceledException oce) when (
            !cancellationToken.IsCancellationRequested && // not cancelled by the caller
            !pendingRequestsCts.IsCancellationRequested) // not cancelled by the global token (dispose)
                                                         // the only remaining token that could cancel this is the linked cts from the timeout.
        {
            Debug.Assert(cts.Token.IsCancellationRequested);
            throw new TimeoutException("The operation has timed out.", oce);
        }
        finally
        {
            if (disposeTokenSource)
            {
                cts.Dispose();
            }
        }
    }

    private async ValueTask<int> ReceiveResponseAsync(ushort queryId, Socket socket, Memory<byte> memory, CancellationToken cancellationToken)
    {
        do
        {
            int readLength = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);

            if (readLength < HeaderSize)
            {
                continue;
            }

            Header header = MemoryMarshal.AsRef<Header>(memory.Span);
            if (header.TransactionId != queryId)
            {
                // possibly a response to a previous query which timed out and the socket was reused.
                continue;
            }

            if (!header.IsResponse)
            {
                // this is a query, not a response.
                continue;
            }

            return readLength;
        } while (true);
    }

    private async ValueTask<TResult> SendQueryAsyncCore<TResult>(string name, QueryType queryType, ParseResponseDataDelegate<TResult> parseResponseBody, CancellationToken cancellationToken)
    {
        byte[] buffer = ArrayPool<byte>.Shared.Rent(512);
        byte[]? tcpBuffer = null;
        try
        {
            // TODO: Implement UDP retries.
            Memory<byte> memory = new Memory<byte>(buffer);
            int questionSize = EncodeQuestion(buffer, name, queryType);
            ushort queryId = MemoryMarshal.AsRef<Header>(memory.Span).TransactionId;

            using Socket udpSocket = new(_serverEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            await udpSocket.ConnectAsync(_serverEndPoint);
            await udpSocket.SendAsync(memory.Slice(0, questionSize), cancellationToken);

            int readLength = await ReceiveResponseAsync(queryId, udpSocket, memory, cancellationToken);

            TResult? result = DoParseResponse(name, queryId, buffer.AsSpan(0, readLength), parseResponseBody);

            if (result is null)
            {
                using Socket tcpSocket = new Socket(_serverEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
                await tcpSocket.ConnectAsync(_serverEndPoint);

                // The TCP message is prefixed with the 2-byte length
                questionSize = EncodeQuestion(memory.Span[2..], name, queryType);
                queryId = MemoryMarshal.AsRef<Header>(memory.Span[2..]).TransactionId;
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

                result = DoParseResponse(name, queryId, tcpBuffer.AsSpan(0, responseSize), parseResponseBody);
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

        static TResult? DoParseResponse(string name, ushort queryId, Span<byte> buffer, ParseResponseDataDelegate<TResult> parseResponseBody)
        {
            ref Header header = ref MemoryMarshal.AsRef<Header>(buffer);
            Log($"T:{header.TransactionId} questions {header.QueryCount} Answers {header.AnswerCount} length {buffer.Length}");
            if (header.TransactionId != queryId)
            {
                var responseFor = DecodeName(buffer, HeaderSize);
                Console.WriteLine($"[{name}] T:{header.TransactionId} mismatch; questions {header.QueryCount} ({responseFor}) Answers {header.AnswerCount} length {buffer.Length}");
                throw new Exception("Invalid response: TransactionId mismatch!");
            }

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
                // throw new Exception($"Invalid response: expected {result.Length} Address records, got {actualCount}.");
                return result.AsSpan(0, actualCount).ToArray();
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
        return (result.Services, result.Addresses!);
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
            else if ((buffer[index] & 0xc0) == 0xc0)
            {
                if (jumpOffset)
                {
                    throw new Exception("ONLY ONE POINTER ALLOWED");
                }
                jumpOffset = true;
                index = (buffer[index] & 0x3f) + buffer[index + 1];
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

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            // Cancel all pending requests (if any). Note that we don't call CancelPendingRequests() but cancel
            // the CTS directly. The reason is that CancelPendingRequests() would cancel the current CTS and create
            // a new CTS. We don't want a new CTS in this case.
            _pendingRequestsCts.Cancel();
            _pendingRequestsCts.Dispose();
        }
    }

    private static void Log(FormattableString str)
    {
        // Console.WriteLine(str);
    }

    private static void Assert(bool condition)
    {
        if (!condition)
        {
            throw new Exception("Assertion failed");
        }

        // Debug.Assert(condition);
    }

    private (CancellationTokenSource TokenSource, bool DisposeTokenSource, CancellationTokenSource PendingRequestsCts) PrepareCancellationTokenSource(CancellationToken cancellationToken)
    {
        // We need a CancellationTokenSource to use with the request.  We always have the global
        // _pendingRequestsCts to use, plus we may have a token provided by the caller, and we may
        // have a timeout.  If we have a timeout or a caller-provided token, we need to create a new
        // CTS (we can't, for example, timeout the pending requests CTS, as that could cancel other
        // unrelated operations).  Otherwise, we can use the pending requests CTS directly.

        // Snapshot the current pending requests cancellation source. It can change concurrently due to cancellation being requested
        // and it being replaced, and we need a stable view of it: if cancellation occurs and the caller's token hasn't been canceled,
        // it's either due to this source or due to the timeout, and checking whether this source is the culprit is reliable whereas
        // it's more approximate checking elapsed time.
        CancellationTokenSource pendingRequestsCts = _pendingRequestsCts;

        bool hasTimeout = _timeout != System.Threading.Timeout.InfiniteTimeSpan;
        if (hasTimeout || cancellationToken.CanBeCanceled)
        {
            CancellationTokenSource cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, pendingRequestsCts.Token);
            if (hasTimeout)
            {
                cts.CancelAfter(_timeout);
            }

            return (cts, DisposeTokenSource: true, pendingRequestsCts);
        }

        return (pendingRequestsCts, DisposeTokenSource: false, pendingRequestsCts);
    }

    private void CancelPendingRequests()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // With every request we link this cancellation token source.
        CancellationTokenSource currentCts = Interlocked.Exchange(ref _pendingRequestsCts, new CancellationTokenSource());

        currentCts.Cancel();
        currentCts.Dispose();
    }
}