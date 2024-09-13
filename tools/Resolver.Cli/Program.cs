using System.Net.Sockets;
using System.Collections.Concurrent;
using Resolver;
using System.Threading;
using System.Net;
using System.Threading.Channels;
using System.Linq;
using DnsClient;

LookupClient client = new LookupClient();

// {
//     var res = client.QueryAsync("cdgsrv.com", QueryType.ANY);
//     foreach (var item in res.Result.Answers)
//     {
//         System.Console.WriteLine(item);
//     }
// }

Resolver resolver = new Resolver();
resolver.Timeout = TimeSpan.FromSeconds(5);

var names = File.ReadAllLines("names.txt").Select(x =>
{
    var parts = x.Split(';');
    return (int.Parse(parts[0]), parts[1]);
});

// names = [names.ElementAt(8244)];

Channel<Task<Result>> results = Channel.CreateUnbounded<Task<Result>>();

SemaphoreSlim concurrencyLimiter = new(200);

var readerTask = Task.Run(async () =>
{
    await foreach (var task in results.Reader.ReadAllAsync())
    {
        var res = await task;
        string resolverResult;

        if (res.ResolverResult != null)
        {
            resolverResult = string.Join<IPAddress>(", ", res.ResolverResult);
        }
        else if (res.ResolverException is TimeoutException)
        {
            // resolverResult = "Timeout";
            resolverResult = "";
        }
        else
        {
            resolverResult = $"Error: {res.ResolverException}";
        }

        string dnsClientResult;
        if (res.DnsClientResult != null)
        {
            dnsClientResult = string.Join<IPAddress>(", ", res.DnsClientResult);
        }
        else
        {
            dnsClientResult = $"Error: {res.DnsClientException}";
        }

        if (dnsClientResult == resolverResult)
        {
            // continue;
        }

        if (dnsClientResult != resolverResult)
        {
            Console.ForegroundColor = ConsoleColor.Red;
        }

        Console.WriteLine($"[{res.Line,5}]: {res.Name} - Resolver: {resolverResult} - DNS Client: {dnsClientResult}");

        Console.ResetColor();

        // string runtimeResult;
        // if (res.RuntimeResult != null)
        // {
        //     runtimeResult = string.Join<IPAddress>(", ", res.RuntimeResult);
        // }
        // else if (res.RuntimeException is TimeoutException)
        // {
        //     runtimeResult = "Timeout";
        // }
        // else
        // {
        //     runtimeResult = $"Error: {res.RuntimeException}";
        // }

        // if (runtimeResult == resolverResult)
        // {
        //     continue;
        // }

        // if (runtimeResult != resolverResult)
        // {
        //     Console.ForegroundColor = ConsoleColor.Red;
        // }

        // Console.WriteLine($"[{res.Line,5}]: {res.Name} - Resolver: {resolverResult} - Runtime: {runtimeResult}");

        // Console.ResetColor();
    }
});

var writerTask = Task.Run(async () =>
{
    foreach (var (line, name) in names)
    {
        await concurrencyLimiter.WaitAsync();

        var task = Task.Run(async () =>
        {
            Result result = new Result { Line = line, Name = name };
            try
            {
                try
                {
                    var v4task = resolver.ResolveIPAddressAsync(name, AddressFamily.InterNetwork);
                    var v6task = resolver.ResolveIPAddressAsync(name, AddressFamily.InterNetworkV6);
                    result.ResolverResult = (await v6task).Select(x => x.Address).Concat((await v4task).Select(x => x.Address)).ToArray();
                    Array.Sort(result.ResolverResult, CompareIpAddresses);
                }
                catch (Exception ex)
                {
                    result.ResolverException = ex;
                }

                try
                {
                    var queryv4 = client.QueryAsync(name, QueryType.A);
                    var queryv6 = client.QueryAsync(name, QueryType.AAAA);
                    result.DnsClientResult = (await queryv4).Answers.ARecords().Select(x => x.Address).Concat((await queryv6).Answers.AaaaRecords().Select(x => x.Address)).ToArray();

                    Array.Sort(result.DnsClientResult, CompareIpAddresses);
                }
                catch (Exception ex)
                {
                    result.DnsClientException = ex;
                }

                try
                {
                    result.RuntimeResult = await Dns.GetHostAddressesAsync(name);
                    Array.Sort(result.RuntimeResult, CompareIpAddresses);
                }
                catch (SocketException ex) when (ex.SocketErrorCode == SocketError.NoData || ex.SocketErrorCode == SocketError.HostNotFound)
                {
                    result.RuntimeResult = Array.Empty<IPAddress>();
                }
                catch (Exception ex)
                {
                    result.RuntimeException = ex;
                }
            }
            finally
            {
                concurrencyLimiter.Release();
            }

            return result;
        });
        await results.Writer.WriteAsync(task);
    }

    results.Writer.Complete();
});

await Task.WhenAll(writerTask, readerTask);

static int CompareIpAddresses(IPAddress x, IPAddress y)
{
    byte[] xBytes = x.GetAddressBytes();
    byte[] yBytes = y.GetAddressBytes();

    if (xBytes.Length != yBytes.Length)
    {
        return xBytes.Length.CompareTo(yBytes.Length);
    }

    for (int i = 0; i < xBytes.Length; i++)
    {
        if (xBytes[i] != yBytes[i])
        {
            return xBytes[i].CompareTo(yBytes[i]);
        }
    }

    return 0;
}

struct Result
{
    public int Line { get; set; }
    public string Name { get; set; }
    public Exception? ResolverException { get; set; }
    public IPAddress[]? ResolverResult { get; set; }
    public Exception? DnsClientException { get; set; }
    public IPAddress[]? DnsClientResult { get; set; }
    public Exception? RuntimeException { get; set; }
    public IPAddress[]? RuntimeResult { get; set; }
}