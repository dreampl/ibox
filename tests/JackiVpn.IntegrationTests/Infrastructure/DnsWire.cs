using System.Buffers.Binary;
using System.Net;

namespace JackiVpn.IntegrationTests.Infrastructure;

/// <summary>Minimal DNS message encoding/decoding for tests that send real queries to a core.</summary>
internal static class DnsWire
{
    public const ushort TypeA = 1;
    public const ushort TypeAaaa = 28;

    public static byte[] Query(ushort id, string name, ushort type)
    {
        var query = new List<byte>();
        var header = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(header, id);
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(2), 0x0100); // RD
        BinaryPrimitives.WriteUInt16BigEndian(header.AsSpan(4), 1);      // QDCOUNT
        query.AddRange(header);
        foreach (var label in name.Split('.'))
        {
            query.Add((byte)label.Length);
            query.AddRange(System.Text.Encoding.ASCII.GetBytes(label));
        }

        query.Add(0);
        query.AddRange([(byte)(type >> 8), (byte)type, 0, 1]);
        return query.ToArray();
    }

    public static DnsResponse Parse(ReadOnlySpan<byte> m)
    {
        var id = BinaryPrimitives.ReadUInt16BigEndian(m);
        var rcode = BinaryPrimitives.ReadUInt16BigEndian(m[2..]) & 0xF;
        var qd = BinaryPrimitives.ReadUInt16BigEndian(m[4..]);
        var an = BinaryPrimitives.ReadUInt16BigEndian(m[6..]);
        var pos = 12;
        for (var i = 0; i < qd; i++)
        {
            pos = SkipName(m, pos) + 4;
        }

        var answers = new List<(ushort Type, string Data)>();
        for (var i = 0; i < an; i++)
        {
            pos = SkipName(m, pos);
            var type = BinaryPrimitives.ReadUInt16BigEndian(m[pos..]);
            var len = BinaryPrimitives.ReadUInt16BigEndian(m[(pos + 8)..]);
            var data = m.Slice(pos + 10, len);
            answers.Add((type, type is TypeA or TypeAaaa ? new IPAddress(data).ToString() : Convert.ToHexString(data)));
            pos += 10 + len;
        }

        return new DnsResponse(id, rcode, answers);
    }

    private static int SkipName(ReadOnlySpan<byte> m, int pos)
    {
        while (true)
        {
            var len = m[pos];
            if (len == 0)
            {
                return pos + 1;
            }

            if ((len & 0xC0) == 0xC0)
            {
                return pos + 2;
            }

            pos += len + 1;
        }
    }
}

internal sealed record DnsResponse(ushort Id, int Rcode, List<(ushort Type, string Data)> Answers);
