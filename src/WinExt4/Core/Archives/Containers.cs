using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace WinExt4.Core.Archives;

public sealed record ContainerEntry(string Name, long Offset, long Size, int Method = 0, long CompressedSize = 0);

/// <summary>tar (ustar, GNU long names, PAX path/size) read through random access.</summary>
public static class Tar
{
    public static bool Looks(IRandomAccess a)
    {
        if (a.Length < 512) return false;
        Span<byte> h = stackalloc byte[512];
        a.Read(0, h);
        return h.Slice(257, 5).SequenceEqual("ustar"u8) || ChecksumOk(h);
    }

    private static bool ChecksumOk(ReadOnlySpan<byte> h)
    {
        long stored = Octal(h.Slice(148, 8));
        long sum = 0;
        for (int i = 0; i < 512; i++) sum += i is >= 148 and < 156 ? 32 : h[i];
        return stored > 0 && stored == sum;
    }

    private static long Octal(ReadOnlySpan<byte> f)
    {
        if ((f[0] & 0x80) != 0)
        {
            // GNU base-256 for sizes over 8 GB
            long v = f[0] & 0x7F;
            for (int i = 1; i < f.Length; i++) v = (v << 8) | f[i];
            return v;
        }
        long r = 0;
        foreach (byte b in f)
        {
            if (b is 0 or (byte)' ') { if (r != 0) break; continue; }
            if (b < '0' || b > '7') break;
            r = r * 8 + (b - '0');
        }
        return r;
    }

    private static string Str(ReadOnlySpan<byte> f)
    {
        int n = f.IndexOf((byte)0);
        return Encoding.UTF8.GetString(n < 0 ? f : f[..n]);
    }

    public static List<ContainerEntry> List(IRandomAccess a, CancellationToken cancel)
    {
        var list = new List<ContainerEntry>();
        var h = new byte[512];
        long pos = 0;
        string? longName = null;
        long? paxSize = null;
        while (pos + 512 <= a.Length)
        {
            cancel.ThrowIfCancellationRequested();
            a.Read(pos, h);
            if (h.All(b => b == 0)) break;
            long size = Octal(h.AsSpan(124, 12));
            byte type = h[156];
            string name = Str(h.AsSpan(0, 100));
            if (h.AsSpan(257, 5).SequenceEqual("ustar"u8) && h[345] != 0)
                name = Str(h.AsSpan(345, 155)) + "/" + name;
            long data = pos + 512;
            if (type is (byte)'L' or (byte)'x' or (byte)'g')
            {
                var body = new byte[Math.Min(size, 1 << 20)];
                a.Read(data, body);
                if (type == 'L') longName = Str(body);
                else if (type == 'x')
                {
                    // PAX records: "<len> key=value\n"
                    foreach (var rec in Encoding.UTF8.GetString(body).Split('\n'))
                    {
                        int sp = rec.IndexOf(' '), eq = rec.IndexOf('=');
                        if (sp < 0 || eq < sp) continue;
                        string key = rec[(sp + 1)..eq], val = rec[(eq + 1)..];
                        if (key == "path") longName = val;
                        else if (key == "size" && long.TryParse(val, out long ps)) paxSize = ps;
                    }
                }
            }
            else
            {
                if (paxSize is { } s2) size = s2;
                if (type is (byte)'0' or 0 or (byte)'7')
                    list.Add(new ContainerEntry(longName ?? name, data, size));
                longName = null;
                paxSize = null;
            }
            pos = data + ((size + 511) & ~511L);
        }
        return list;
    }
}

/// <summary>zip central directory (zip64 aware); members stored or deflated.</summary>
public static class Zip
{
    public static bool Looks(string path)
    {
        using var fs = File.OpenRead(path);
        Span<byte> b = stackalloc byte[4];
        return fs.Read(b) == 4 && BinaryPrimitives.ReadUInt32LittleEndian(b) == 0x04034B50;
    }

    public static List<ContainerEntry> List(string path)
    {
        using var fs = File.OpenRead(path);
        int tailLen = (int)Math.Min(fs.Length, 65557);
        var tail = new byte[tailLen];
        fs.Seek(-tailLen, SeekOrigin.End);
        fs.ReadExactly(tail);
        int eocd = -1;
        for (int i = tailLen - 22; i >= 0; i--)
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) == 0x06054B50) { eocd = i; break; }
        if (eocd < 0) throw new InvalidDataException("Archivio zip senza directory centrale.");
        long cdOffset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd + 16));
        long count = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(eocd + 10));
        // zip64 end of central directory
        if (eocd >= 20 && BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(eocd - 20)) == 0x07064B50)
        {
            long z64 = (long)BinaryPrimitives.ReadUInt64LittleEndian(tail.AsSpan(eocd - 20 + 8));
            var rec = new byte[56];
            fs.Position = z64;
            fs.ReadExactly(rec);
            if (BinaryPrimitives.ReadUInt32LittleEndian(rec) == 0x06064B50)
            {
                count = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(32));
                cdOffset = (long)BinaryPrimitives.ReadUInt64LittleEndian(rec.AsSpan(48));
            }
        }
        var list = new List<ContainerEntry>();
        fs.Position = cdOffset;
        var hdr = new byte[46];
        for (long e = 0; e < count; e++)
        {
            fs.ReadExactly(hdr);
            if (BinaryPrimitives.ReadUInt32LittleEndian(hdr) != 0x02014B50) break;
            int method = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(10));
            long comp = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(20));
            long size = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(24));
            int nameLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(28));
            int extraLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(30));
            int commentLen = BinaryPrimitives.ReadUInt16LittleEndian(hdr.AsSpan(32));
            long local = BinaryPrimitives.ReadUInt32LittleEndian(hdr.AsSpan(42));
            var name = new byte[nameLen];
            fs.ReadExactly(name);
            var extra = new byte[extraLen];
            fs.ReadExactly(extra);
            fs.Seek(commentLen, SeekOrigin.Current);
            for (int x = 0; x + 4 <= extra.Length;)
            {
                int id = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(x));
                int len = BinaryPrimitives.ReadUInt16LittleEndian(extra.AsSpan(x + 2));
                if (id == 1)
                {
                    int q = x + 4;
                    if (size == 0xFFFFFFFF) { size = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(q)); q += 8; }
                    if (comp == 0xFFFFFFFF) { comp = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(q)); q += 8; }
                    if (local == 0xFFFFFFFF) { local = (long)BinaryPrimitives.ReadUInt64LittleEndian(extra.AsSpan(q)); }
                }
                x += 4 + len;
            }
            string n = Encoding.UTF8.GetString(name);
            if (n.EndsWith('/')) continue;
            // the data starts after the local header, whose extra field may differ
            long save = fs.Position;
            var lh = new byte[30];
            fs.Position = local;
            fs.ReadExactly(lh);
            long data = local + 30 + BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(26)) + BinaryPrimitives.ReadUInt16LittleEndian(lh.AsSpan(28));
            fs.Position = save;
            list.Add(new ContainerEntry(n, data, size, method, comp));
        }
        return list;
    }
}
