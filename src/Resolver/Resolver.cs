using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Resolver;

public class Resolver : IDisposable
{
    private const int MaximumNameLength = 253;
    private const int IPv4Length = 4;
    private const int IPv6Length = 16;
    private const int HeaderSize = 12;

    private static readonly TimeSpan s_maxTimeout = TimeSpan.FromMilliseconds(int.MaxValue);

    bool _disposed = false;
    private ResolverOptions _options;
    private TimeSpan _timeout = System.Threading.Timeout.InfiniteTimeSpan;
    private CancellationTokenSource _pendingRequestsCts = new();

    private DnsResultCache _cache = new DnsResultCache(TimeProvider.System);

    private TimeProvider _timeProvider = TimeProvider.System;

    internal void SetTimeProvider(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _cache = new DnsResultCache(timeProvider);
    }

    public Resolver() : this(OperatingSystem.IsWindows() ? NetworkInfo.GetOptions() : ResolvConf.GetOptions())
    {
    }

    public Resolver(ResolverOptions options)
    {
        _options = options;
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

    public async ValueTask<ServiceResult[]> ResolveServiceAsync(string name, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        var queryType = QueryType.SRV;
        if (_cache.TryGet(name, queryType, out ServiceResult[] cached))
        {
            return cached;
        }

        DnsResponse record = await SendQueryAsync(name, QueryType.SRV, cancellationToken);
        if (!ValidateResponse<ServiceResult>(name, QueryType.SRV, record))
        {
            return Array.Empty<ServiceResult>();
        }

        var results = new List<ServiceResult>(record.Answers.Count);

        foreach (var answer in record.Answers)
        {
            if (answer.Type == QueryType.SRV)
            {
                bool success = DnsPrimitives.TryReadService(answer.Data.Span, out ushort priority, out ushort weight, out ushort port, out string? target, out _);
                Debug.Assert(success, "Failed to read SRV");

                List<AddressResult> addresses = new List<AddressResult>();
                foreach (var additional in record.Additionals)
                {
                    if (additional.Name == target && (additional.Type == QueryType.A || additional.Type == QueryType.AAAA))
                    {
                        addresses.Add(new AddressResult(record.CreatedAt.AddSeconds(additional.Ttl), new IPAddress(additional.Data.Span)));
                    }
                }

                results.Add(new ServiceResult(record.CreatedAt.AddSeconds(answer.Ttl), priority, weight, port, target!, addresses.ToArray()));
            }
        }

        var result = results.ToArray();
        _cache.TryAdd(name, queryType, record.Expiration, result);
        return result;
    }

    public async ValueTask<AddressResult[]> ResolveIPAddressesAsync(string name, AddressFamily addressFamily, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        if (addressFamily != AddressFamily.InterNetwork && addressFamily != AddressFamily.InterNetworkV6 && addressFamily != AddressFamily.Unspecified)
        {
            throw new ArgumentOutOfRangeException(nameof(addressFamily), addressFamily, "Invalid address family");
        }

        if (name.Length > MaximumNameLength)
        {
            throw new ArgumentException("Name is too long", nameof(name));
        }

        var queryType = addressFamily == AddressFamily.InterNetwork ? QueryType.A : QueryType.AAAA;
        if (_cache.TryGet(name, queryType, out AddressResult[] cached))
        {
            return cached;
        }

        DnsResponse record = await SendQueryAsync(name, queryType, cancellationToken);
        if (!ValidateResponse<AddressResult>(name, queryType, record))
        {
            return Array.Empty<AddressResult>();
        }

        var results = new List<AddressResult>(record.Answers.Count);

        // servers send back CNAME records together with associated A/AAAA records
        string currentAlias = name;

        foreach (var answer in record.Answers)
        {
            if (answer.Name != currentAlias)
            {
                continue;
            }

            if (answer.Type == QueryType.CNAME)
            {
                bool success = DnsPrimitives.TryReadQName(answer.Data.Span, 0, out currentAlias!, out _);
                Debug.Assert(success, "Failed to read CNAME");
                continue;
            }

            else if (answer.Type == queryType)
            {
                Debug.Assert(answer.Data.Length == IPv4Length || answer.Data.Length == IPv6Length);
                results.Add(new AddressResult(record.CreatedAt.AddSeconds(answer.Ttl), new IPAddress(answer.Data.Span)));
            }
        }

        var result = results.ToArray();
        _cache.TryAdd(name, queryType, record.Expiration, result);
        return result;
    }

    internal bool GetNegativeCacheExpiration(in DnsResponse response, out DateTime expiration)
    {
        //
        // RFC 2308 Section 5 - Caching Negative Answers
        //
        //    Like normal answers negative answers have a time to live (TTL).  As
        //    there is no record in the answer section to which this TTL can be
        //    applied, the TTL must be carried by another method.  This is done by
        //    including the SOA record from the zone in the authority section of
        //    the reply.  When the authoritative server creates this record its TTL
        //    is taken from the minimum of the SOA.MINIMUM field and SOA's TTL.
        //    This TTL decrements in a similar manner to a normal cached answer and
        //    upon reaching zero (0) indicates the cached negative answer MUST NOT
        //    be used again.
        //

        DnsResourceRecord? soa = response.Authorities.FirstOrDefault(r => r.Type == QueryType.SOA);
        if (soa != null && DnsPrimitives.TryReadSoa(soa.Value.Data.Span, out string? mname, out string? rname, out uint serial, out uint refresh, out uint retry, out uint expire, out uint minimum, out _))
        {
            expiration = response.CreatedAt.AddSeconds(Math.Min(minimum, soa.Value.Ttl));
            return true;
        }

        expiration = default;
        return false;
    }

    internal bool ValidateResponse<T>(string name, QueryType queryType, in DnsResponse response)
    {
        if (response.Header.ResponseCode == QueryResponseCode.NoError)
        {
            if (response.Answers.Count > 0)
            {
                return true;
            }
            //
            // RFC 2308 Section 2.2 - No Data
            //
            //    NODATA is indicated by an answer with the RCODE set to NOERROR and no
            //    relevant answers in the answer section.  The authority section will
            //    contain an SOA record, or there will be no NS records there.
            //
            //
            // RFC 2308 Section 5 - Caching Negative Answers
            //
            //    A negative answer that resulted from a no data error (NODATA) should
            //    be cached such that it can be retrieved and returned in response to
            //    another query for the same <QNAME, QTYPE, QCLASS> that resulted in
            //    the cached negative response.
            //
            if (!response.Authorities.Any(r => r.Type == QueryType.NS) && GetNegativeCacheExpiration(response, out DateTime expiration))
            {
                _cache.TryAdd(name, queryType, expiration, Array.Empty<T>());
            }
            return false;
        }

        if (response.Header.ResponseCode == QueryResponseCode.NameError)
        {
            //
            // RFC 2308 Section 5 - Caching Negative Answers
            //
            //    A negative answer that resulted from a name error (NXDOMAIN) should
            //    be cached such that it can be retrieved and returned in response to
            //    another query for the same <QNAME, QCLASS> that resulted in the
            //    cached negative response.
            //
            if (GetNegativeCacheExpiration(response, out DateTime expiration))
            {
                _cache.TryAddNonexistent(name, expiration);
            }

            return false;
        }

        return true;
    }

    internal async ValueTask<(DnsDataReader reader, DnsMessageHeader header)> SendDnsQueryCoreTcpAsync(IPEndPoint serverEndPoint, string name, QueryType queryType, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(8 * 1024);
        try
        {
            // When sending over TCP, the message is prefixed by 2B length
            (ushort transactionId, int length) = EncodeQuestion(buffer.AsMemory(2), name, queryType);
            BinaryPrimitives.WriteUInt16BigEndian(buffer, (ushort)length);

            using var socket = new Socket(serverEndPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            await socket.ConnectAsync(serverEndPoint, cancellationToken);
            await socket.SendAsync(buffer.AsMemory(0, length + 2), SocketFlags.None, cancellationToken);

            int responseLength = -1;
            int bytesRead = 0;
            while (responseLength < 0 || bytesRead < length + 2)
            {
                int read = await socket.ReceiveAsync(buffer.AsMemory(bytesRead), SocketFlags.None);
                bytesRead += read;

                if (responseLength < 0 && bytesRead >= 2)
                {
                    responseLength = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2));

                    if (responseLength > buffer.Length)
                    {
                        var largerBuffer = ArrayPool<byte>.Shared.Rent(responseLength);
                        Array.Copy(buffer, largerBuffer, bytesRead);
                        ArrayPool<byte>.Shared.Return(buffer);
                        buffer = largerBuffer;
                    }
                }
            }

            DnsDataReader responseReader = new DnsDataReader(buffer.AsMemory(2, responseLength), buffer);
            if (!responseReader.TryReadHeader(out DnsMessageHeader header) ||
                header.TransactionId != transactionId ||
                !header.IsResponse)
            {
                throw new Exception("Invalid response: Header mismatch");
            }

            buffer = null!;
            return (responseReader, header);
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    internal async ValueTask<(DnsDataReader reader, DnsMessageHeader header)> SendDnsQueryCoreUdpAsync(IPEndPoint serverEndPoint, string name, QueryType queryType, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(512);
        try
        {

            Memory<byte> memory = buffer;
            (ushort transactionId, int length) = EncodeQuestion(memory, name, queryType);

            using var socket = new Socket(serverEndPoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp);
            await socket.ConnectAsync(serverEndPoint, cancellationToken);

            await socket.SendAsync(memory.Slice(0, length), SocketFlags.None, cancellationToken);

            DnsDataReader responseReader;
            DnsMessageHeader header;

            while (true)
            {
                int readLength = await socket.ReceiveAsync(memory, SocketFlags.None, cancellationToken);

                if (readLength < HeaderSize)
                {
                    continue;
                }

                responseReader = new DnsDataReader(memory.Slice(0, readLength), buffer);
                if (!responseReader.TryReadHeader(out header) ||
                    header.TransactionId != transactionId ||
                    !header.IsResponse)
                {
                    // the message is not a response for our query.
                    // don't dispose reader, we will reuse the buffer
                    continue;
                }

                // ownership of the buffer is transferred to the reader, caller will dispose.
                buffer = null!;
                return (responseReader, header);
            }
        }
        finally
        {
            if (buffer != null)
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    internal async ValueTask<DnsResponse> SendQueryAsync(string name, QueryType queryType, CancellationToken cancellationToken)
    {
        (CancellationTokenSource cts, bool disposeTokenSource, CancellationTokenSource pendingRequestsCts) = PrepareCancellationTokenSource(cancellationToken);

        try
        {
            return await SendQueryAsyncSlow(name, queryType, cts.Token);
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

        async ValueTask<DnsResponse> SendQueryAsyncSlow(string name, QueryType queryType, CancellationToken cancellationToken)
        {
            DnsDataReader responseReader = default;
            DnsMessageHeader header = default;
            DateTime queryStartedTime = default;

            foreach (IPEndPoint serverEndPoint in _options.Servers)
            {
                queryStartedTime = _timeProvider.GetUtcNow().DateTime;
                (responseReader, header) = await SendDnsQueryCoreUdpAsync(serverEndPoint, name, queryType, cancellationToken);

                if (header.IsResultTruncated)
                {
                    responseReader.Dispose();
                    // TCP fallback
                    (responseReader, header) = await SendDnsQueryCoreTcpAsync(serverEndPoint, name, queryType, cancellationToken);
                }

                if (header.QueryCount != 1 ||
                    !responseReader.TryReadQuestion(out var qName, out var qType, out var qClass) ||
                    qName != name || qType != queryType || qClass != QueryClass.Internet)
                {
                    // TODO: do we care?
                    throw new Exception("Invalid response: Query mismatch");
                    // return default;
                }

                // TODO: on which response codes should we retry?
                if (header.ResponseCode == QueryResponseCode.NoError)
                {
                    break;
                }

                responseReader.Dispose();
            }

            int ttl = int.MaxValue;
            List<DnsResourceRecord> answers = ReadRecords(header.AnswerCount, ref ttl, ref responseReader);
            List<DnsResourceRecord> authorities = ReadRecords(header.AuthorityCount, ref ttl, ref responseReader);
            List<DnsResourceRecord> additionals = ReadRecords(header.AdditionalRecordCount, ref ttl, ref responseReader);

            DnsResponse record = new(header, queryStartedTime, queryStartedTime.AddSeconds(ttl), answers, authorities, additionals);
            responseReader.Dispose();

            return record;

            static List<DnsResourceRecord> ReadRecords(int count, ref int ttl, ref DnsDataReader reader)
            {
                List<DnsResourceRecord> records = new(count);

                for (int i = 0; i < count; i++)
                {
                    if (!reader.TryReadResourceRecord(out var record))
                    {
                        // TODO how to handle corrupted responses?
                        throw new Exception("Invalid response: Answer record");
                    }

                    ttl = Math.Min(ttl, record.Ttl);
                    // copy the data to a new array since the underlying array is pooled
                    records.Add(new DnsResourceRecord(record.Name, record.Type, record.Class, record.Ttl, record.Data.ToArray()));
                }

                return records;
            }
        }
    }

    private static (ushort id, int length) EncodeQuestion(Memory<byte> buffer, string name, QueryType queryType)
    {
        DnsMessageHeader header = default;
        header.InitQueryHeader();
        DnsDataWriter writer = new DnsDataWriter(buffer);
        if (!writer.TryWriteHeader(header) ||
            !writer.TryWriteQuestion(name, queryType, QueryClass.Internet))
        {
            throw new Exception("Buffer too small");
        }
        return (header.TransactionId, writer.Position);
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