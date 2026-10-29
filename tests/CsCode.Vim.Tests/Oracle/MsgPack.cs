using System.Buffers.Binary;
using System.Text;

namespace CsCode.Vim.Tests.Oracle;

/// <summary>
/// Minimal MessagePack codec — just enough for Neovim's msgpack-RPC (nil, bool, ints, floats, str, bin, array, map, ext).
/// Decoded shapes: null, bool, long, double, string, byte[], List&lt;object?&gt;, Dictionary&lt;string, object?&gt;.
/// </summary>
internal static class MsgPack
{
    public static void Write(Stream s, object? v)
    {
        switch (v)
        {
            case null: s.WriteByte(0xc0); break;
            case bool b: s.WriteByte(b ? (byte)0xc3 : (byte)0xc2); break;
            case int i: WriteInt(s, i); break;
            case long l: WriteInt(s, l); break;
            case string str:
            {
                var bytes = Encoding.UTF8.GetBytes(str);
                if (bytes.Length < 32) s.WriteByte((byte)(0xa0 | bytes.Length));
                else if (bytes.Length < 256) { s.WriteByte(0xd9); s.WriteByte((byte)bytes.Length); }
                else if (bytes.Length < 65536) { s.WriteByte(0xda); Span<byte> t = stackalloc byte[2]; BinaryPrimitives.WriteUInt16BigEndian(t, (ushort)bytes.Length); s.Write(t); }
                else { s.WriteByte(0xdb); Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, (uint)bytes.Length); s.Write(t); }
                s.Write(bytes);
                break;
            }
            case IReadOnlyList<object?> list:
            {
                if (list.Count < 16) s.WriteByte((byte)(0x90 | list.Count));
                else { s.WriteByte(0xdd); Span<byte> t = stackalloc byte[4]; BinaryPrimitives.WriteUInt32BigEndian(t, (uint)list.Count); s.Write(t); }
                foreach (var e in list) Write(s, e);
                break;
            }
            default: throw new NotSupportedException($"msgpack: {v.GetType()}");
        }
    }

    private static void WriteInt(Stream s, long l)
    {
        if (l >= 0 && l < 128) s.WriteByte((byte)l);
        else if (l < 0 && l >= -32) s.WriteByte((byte)l);
        else { s.WriteByte(0xd3); Span<byte> t = stackalloc byte[8]; BinaryPrimitives.WriteInt64BigEndian(t, l); s.Write(t); }
    }

    public static object? Read(Stream s)
    {
        int b = s.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        if (b < 0x80) return (long)b;
        if (b >= 0xe0) return (long)(sbyte)b;
        if (b is >= 0xa0 and <= 0xbf) return ReadStr(s, b & 0x1f);
        if (b is >= 0x90 and <= 0x9f) return ReadArray(s, b & 0x0f);
        if (b is >= 0x80 and <= 0x8f) return ReadMap(s, b & 0x0f);
        switch (b)
        {
            case 0xc0: return null;
            case 0xc2: return false;
            case 0xc3: return true;
            case 0xc4: return ReadBytes(s, s.ReadByte());
            case 0xc5: return ReadBytes(s, U16(s));
            case 0xc6: return ReadBytes(s, (int)U32(s));
            case 0xc7: { var n = s.ReadByte(); s.ReadByte(); return ReadBytes(s, n); }
            case 0xc8: { var n = U16(s); s.ReadByte(); return ReadBytes(s, n); }
            case 0xc9: { var n = (int)U32(s); s.ReadByte(); return ReadBytes(s, n); }
            case 0xca: { var t = Take(s, 4); return (double)BinaryPrimitives.ReadSingleBigEndian(t); }
            case 0xcb: { var t = Take(s, 8); return BinaryPrimitives.ReadDoubleBigEndian(t); }
            case 0xcc: return (long)s.ReadByte();
            case 0xcd: return (long)U16(s);
            case 0xce: return (long)U32(s);
            case 0xcf: return (long)BinaryPrimitives.ReadUInt64BigEndian(Take(s, 8));
            case 0xd0: return (long)(sbyte)s.ReadByte();
            case 0xd1: return (long)BinaryPrimitives.ReadInt16BigEndian(Take(s, 2));
            case 0xd2: return (long)BinaryPrimitives.ReadInt32BigEndian(Take(s, 4));
            case 0xd3: return BinaryPrimitives.ReadInt64BigEndian(Take(s, 8));
            case 0xd4: s.ReadByte(); return ReadBytes(s, 1);
            case 0xd5: s.ReadByte(); return ReadBytes(s, 2);
            case 0xd6: s.ReadByte(); return ReadBytes(s, 4);
            case 0xd7: s.ReadByte(); return ReadBytes(s, 8);
            case 0xd8: s.ReadByte(); return ReadBytes(s, 16);
            case 0xd9: return ReadStr(s, s.ReadByte());
            case 0xda: return ReadStr(s, U16(s));
            case 0xdb: return ReadStr(s, (int)U32(s));
            case 0xdc: return ReadArray(s, U16(s));
            case 0xdd: return ReadArray(s, (int)U32(s));
            case 0xde: return ReadMap(s, U16(s));
            case 0xdf: return ReadMap(s, (int)U32(s));
            default: throw new InvalidDataException($"msgpack: unsupported type byte 0x{b:x2}");
        }
    }

    private static byte[] Take(Stream s, int n)
    {
        var buf = new byte[n];
        var off = 0;
        while (off < n)
        {
            var r = s.Read(buf, off, n - off);
            if (r <= 0) throw new EndOfStreamException();
            off += r;
        }
        return buf;
    }

    private static int U16(Stream s) => BinaryPrimitives.ReadUInt16BigEndian(Take(s, 2));
    private static uint U32(Stream s) => BinaryPrimitives.ReadUInt32BigEndian(Take(s, 4));
    private static byte[] ReadBytes(Stream s, int n) => Take(s, n);
    private static string ReadStr(Stream s, int n) => Encoding.UTF8.GetString(Take(s, n));

    private static List<object?> ReadArray(Stream s, int n)
    {
        var l = new List<object?>(n);
        for (var i = 0; i < n; i++) l.Add(Read(s));
        return l;
    }

    private static Dictionary<string, object?> ReadMap(Stream s, int n)
    {
        var d = new Dictionary<string, object?>();
        for (var i = 0; i < n; i++)
        {
            var k = Read(s);
            d[k is byte[] kb ? Encoding.UTF8.GetString(kb) : k?.ToString() ?? ""] = Read(s);
        }
        return d;
    }
}
