using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace Resolver;

internal static class DnsPrimitives
{
    internal static bool TryWriteQName(Span<byte> destination, string name, out int written)
    {
        //
        // RFC 1035 4.1.2. 
        //
        //     a domain name represented as a sequence of labels, where
        //     each label consists of a length octet followed by that
        //     number of octets.  The domain name terminates with the
        //     zero length octet for the null label of the root.  Note
        //     that this field may be an odd number of octets; no
        //     padding is used.
        //

        if (!Encoding.ASCII.TryGetBytes(name, destination.IsEmpty ? destination : destination.Slice(1), out int length) || destination.Length < length + 2)
        {
            // buffer too small
            written = 0;
            return false;
        }

        destination[1 + length] = 0; // last label (root)

        Span<byte> nameBuffer = destination.Slice(0, 1 + length);
        while (true)
        {
            // figure out the next label and prepend the length
            int index = nameBuffer.Slice(1).IndexOf<byte>((byte)'.');
            int labelLen = index == -1 ? nameBuffer.Length - 1 : index;

            if (labelLen > 63)
            {
                throw new ArgumentException("Label is too long");
            }

            nameBuffer[0] = (byte)labelLen;
            if (index == -1)
            {
                // this was the last label
                break;
            }

            nameBuffer = nameBuffer.Slice(index + 1);
        }

        written = length + 2;
        return true;
    }

    private static bool TryReadQNameCore(StringBuilder sb, ReadOnlySpan<byte> messageBuffer, int offset, out int bytesRead)
    {
        bytesRead = 1;

        if (offset < 0 || offset >= messageBuffer.Length)
        {
            return false;
        }

        int currentOffset = offset;

        while (true)
        {
            byte length = messageBuffer[currentOffset];

            if ((length & 0xC0) == 0x00)
            {
                // length followed by the label

                if (length == 0)
                {
                    // end of name
                    bytesRead = currentOffset - offset + 1;
                    return true;
                }
                if (currentOffset + 1 + length < messageBuffer.Length)
                {
                    if (sb.Length > 0)
                    {
                        sb.Append('.');
                    }
                    sb.Append(Encoding.ASCII.GetString(messageBuffer.Slice(currentOffset + 1, length)));
                    currentOffset += 1 + length;
                    bytesRead += 1 + length;
                }
                else
                {
                    // truncated data
                    break;
                }
            }
            else if ((length & 0xC0) == 0xC0)
            {
                // pointer, together with next byte gives the offset of the true label
                if (currentOffset + 1 < messageBuffer.Length)
                {
                    int pointer = ((length & 0x3F) << 8) | messageBuffer[currentOffset + 1];

                    // we prohibit self-references and forward pointers to avoid infinite loops, we do this
                    // by truncating the messagebuffer at the offset where we started reading the name
                    return TryReadQNameCore(sb, messageBuffer.Slice(0, offset), pointer, out int _);
                }
                else
                {
                    // truncated data
                    break;
                }
            }
            else
            {
                // top two bits are reserved, this means invalid data
                break;
            }
        }

        return false;

    }

    internal static bool TryReadQName(ReadOnlySpan<byte> messageBuffer, int offset, [NotNullWhen(true)] out string? name, out int bytesRead)
    {
        StringBuilder sb = new StringBuilder();

        if (TryReadQNameCore(sb, messageBuffer, offset, out bytesRead))
        {
            name = sb.ToString();
            return true;
        }
        else
        {
            bytesRead = 0;
            name = null;
            return false;
        }
    }
}