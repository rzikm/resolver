using System.Net;
using System.Text;

namespace Resolver;

public record struct AddressResult(DateTime ExpiresAt, IPAddress Address);

public record struct ServiceResult(DateTime ExpiresAt, int Priority, int Weight, int Port, string Target, AddressResult[] Addresses);

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
