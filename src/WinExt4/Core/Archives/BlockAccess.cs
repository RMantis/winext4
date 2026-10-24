using System.Buffers.Binary;
using System.IO;
using Joveler.Compression.XZ;

namespace WinExt4.Core.Archives;

internal static class Crc32
{
    private static readonly uint[] Table = Enumerable.Range(0, 256).Select(i =>
    {
        uint c = (uint)i;
        for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
        return c;
    }).ToArray();

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint c = 0xFFFFFFFF;
        foreach (byte b in data) c = Table[(c ^ b) & 0xFF] ^ (c >> 8);
        return c ^ 0xFFFFFFFF;
    }
}

/// <summary>
/// Random access to a multi-block .xz: each block is decoded on its own by
/// wrapping it in a minimal one-block xz stream (header + block + index +
/// footer) handed to liblzma. Files made with "xz -T0" or by WinExt4 are
/// multi-block; single-block files fall back to <see cref="CachedAccess"/>.
/// </summary>
public sealed class XzAccess : IRandomAccess
{
    private sealed record Block(long CompOffset, long Unpadded, long OutOffset, long OutSize, byte[] StreamFlags);

    private readonly FileStream _fs;
    private readonly List<Block> _blocks;
    private readonly SegmentCache _cache = new(3);
    private readonly object _lock = new();

    public long Length { get; }
    public int BlockCount => _blocks.Count;

    private XzAccess(FileStream fs, List<Block> blocks)
    {
        _fs = fs;
        _blocks = blocks;
        Length = blocks.Count == 0 ? 0 : blocks[^1].OutOffset + blocks[^1].OutSize;
    }

    private static long Varint(ReadOnlySpan<byte> b, ref int i)
    {
        long v = 0;
        for (int shift = 0; shift < 63; shift += 7)
        {
            byte x = b[i++];
            v |= (long)(x & 0x7F) << shift;
            if ((x & 0x80) == 0) return v;
        }
        throw new InvalidDataException("Indice xz non valido.");
    }

    private static int VarintSize(long v)
    {
        int n = 1;
        while (v >= 0x80) { v >>= 7; n++; }
        return n;
    }

    private static void PutVarint(List<byte> o, long v)
    {
        while (v >= 0x80) { o.Add((byte)(v | 0x80)); v >>= 7; }
        o.Add((byte)v);
    }

    /// <summary>Reads the stream indexes from the end of the file.</summary>
    public static XzAccess Open(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        try
        {
            var streams = new List<List<Block>>();
            long end = fs.Length;
            var foot = new byte[12];
            while (end > 0)
            {
                // stream padding: groups of four zero bytes
                fs.Position = end - 4;
                var four = new byte[4];
                fs.ReadExactly(four);
                if (BinaryPrimitives.ReadUInt32LittleEndian(four) == 0) { end -= 4; continue; }

                fs.Position = end - 12;
                fs.ReadExactly(foot);
                if (foot[10] != (byte)'Y' || foot[11] != (byte)'Z') throw new InvalidDataException("Piede dello stream xz non valido.");
                long indexSize = (BinaryPrimitives.ReadUInt32LittleEndian(foot.AsSpan(4)) + 1L) * 4;
                long indexStart = end - 12 - indexSize;
                var index = new byte[indexSize];
                fs.Position = indexStart;
                fs.ReadExactly(index);
                int i = 1; // skip indicator
                long records = Varint(index, ref i);
                var list = new List<(long Unpadded, long Out)>();
                long blocksBytes = 0;
                for (long r = 0; r < records; r++)
                {
                    long unpadded = Varint(index, ref i), outSize = Varint(index, ref i);
                    list.Add((unpadded, outSize));
                    blocksBytes += (unpadded + 3) & ~3L;
                }
                long streamStart = indexStart - blocksBytes - 12;
                var header = new byte[12];
                fs.Position = streamStart;
                fs.ReadExactly(header);
                if (header[0] != 0xFD || header[1] != (byte)'7') throw new InvalidDataException("Intestazione xz non valida.");
                var flags = header.AsSpan(6, 2).ToArray();
                long comp = streamStart + 12;
                var blocks = new List<Block>();
                foreach (var (unpadded, outSize) in list)
                {
                    blocks.Add(new Block(comp, unpadded, 0, outSize, flags));
                    comp += (unpadded + 3) & ~3L;
                }
                streams.Insert(0, blocks);
                end = streamStart;
            }
            var all = new List<Block>();
            long outPos = 0;
            foreach (var b in streams.SelectMany(s => s))
            {
                all.Add(b with { OutOffset = outPos });
                outPos += b.OutSize;
            }
            return new XzAccess(fs, all);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    public void Read(long pos, Span<byte> dst)
    {
        int n = (int)Math.Clamp(Length - pos, 0, dst.Length);
        dst[n..].Clear();
        lock (_lock)
        {
            int done = 0;
            while (done < n)
            {
                long p = pos + done;
                int bi = _blocks.FindLastIndex(b => b.OutOffset <= p);
                var data = Decode(bi);
                int off = (int)(p - _blocks[bi].OutOffset);
                int take = Math.Min(n - done, data.Length - off);
                data.AsSpan(off, take).CopyTo(dst[done..]);
                done += take;
            }
        }
    }

    private byte[] Decode(int bi)
    {
        if (_cache.Get(bi) is { } hit) return hit;
        var b = _blocks[bi];
        long padded = (b.Unpadded + 3) & ~3L;
        var s = new List<byte>((int)padded + 64);
        // stream header
        s.AddRange(new byte[] { 0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0 });
        s.AddRange(b.StreamFlags);
        s.AddRange(BitConverter.GetBytes(Crc32.Compute(b.StreamFlags)));
        // the block as stored
        var block = new byte[padded];
        _fs.Position = b.CompOffset;
        _fs.ReadExactly(block);
        s.AddRange(block);
        // one-record index
        var index = new List<byte> { 0 };
        PutVarint(index, 1);
        PutVarint(index, b.Unpadded);
        PutVarint(index, b.OutSize);
        while (index.Count % 4 != 0) index.Add(0);
        index.AddRange(BitConverter.GetBytes(Crc32.Compute(index.ToArray())));
        s.AddRange(index);
        // footer
        var tail = new byte[6];
        BinaryPrimitives.WriteUInt32LittleEndian(tail, (uint)(index.Count / 4 - 1));
        b.StreamFlags.CopyTo(tail, 4);
        s.AddRange(BitConverter.GetBytes(Crc32.Compute(tail)));
        s.AddRange(tail);
        s.Add((byte)'Y');
        s.Add((byte)'Z');

        ImageFormats.EnsureXz();
        var data = new byte[b.OutSize];
        using (var xz = new XZStream(new MemoryStream(s.ToArray()), new XZDecompressOptions()))
            xz.ReadExactly(data);
        _cache.Put(bi, data);
        return data;
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>
/// Random access to a .zst made of several frames that declare their content
/// size (seekable zstd, "zstd -T0 --long" outputs, ...): one frame decoded at
/// a time. Anything else falls back to <see cref="CachedAccess"/>.
/// </summary>
public sealed class ZstdAccess : IRandomAccess
{
    private sealed record Frame(long CompOffset, long CompSize, long OutOffset, long OutSize);

    private readonly FileStream _fs;
    private readonly List<Frame> _frames;
    private readonly SegmentCache _cache = new(3);
    private readonly object _lock = new();

    public long Length { get; }

    private ZstdAccess(FileStream fs, List<Frame> frames)
    {
        _fs = fs;
        _frames = frames;
        Length = frames.Count == 0 ? 0 : frames[^1].OutOffset + frames[^1].OutSize;
    }

    /// <summary>Null when the file cannot be accessed frame by frame.</summary>
    public static ZstdAccess? TryOpen(string path)
    {
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
        var frames = new List<Frame>();
        long pos = 0, outPos = 0;
        var h = new byte[18];
        try
        {
            while (pos < fs.Length)
            {
                fs.Position = pos;
                fs.ReadExactly(h.AsSpan(0, 4));
                uint magic = BinaryPrimitives.ReadUInt32LittleEndian(h);
                if ((magic & 0xFFFFFFF0) == 0x184D2A50)
                {
                    // skippable frame (e.g. the seek table of the seekable format)
                    fs.ReadExactly(h.AsSpan(4, 4));
                    pos += 8 + BinaryPrimitives.ReadUInt32LittleEndian(h.AsSpan(4));
                    continue;
                }
                if (magic != 0xFD2FB528) { fs.Dispose(); return null; }
                int fhd = fs.ReadByte();
                int fcsFlag = fhd >> 6, single = (fhd >> 5) & 1, checksum = (fhd >> 2) & 1, did = fhd & 3;
                int fcsSize = fcsFlag switch { 0 => single, 1 => 2, 2 => 4, _ => 8 };
                if (fcsSize == 0) { fs.Dispose(); return null; } // size unknown: no random access
                int headerSize = 4 + 1 + (single == 0 ? 1 : 0) + (did == 0 ? 0 : did == 1 ? 1 : did == 2 ? 2 : 4) + fcsSize;
                fs.Position = pos + headerSize - fcsSize;
                fs.ReadExactly(h.AsSpan(0, fcsSize));
                long content = fcsSize switch
                {
                    1 => h[0],
                    2 => BinaryPrimitives.ReadUInt16LittleEndian(h) + 256,
                    4 => BinaryPrimitives.ReadUInt32LittleEndian(h),
                    _ => (long)BinaryPrimitives.ReadUInt64LittleEndian(h),
                };
                long p = pos + headerSize;
                while (true)
                {
                    fs.Position = p;
                    fs.ReadExactly(h.AsSpan(0, 3));
                    int bh = h[0] | h[1] << 8 | h[2] << 16;
                    bool last = (bh & 1) != 0;
                    int type = (bh >> 1) & 3, size = bh >> 3;
                    p += 3 + (type == 1 ? 1 : size);
                    if (last) break;
                }
                if (checksum == 1) p += 4;
                frames.Add(new Frame(pos, p - pos, outPos, content));
                outPos += content;
                pos = p;
            }
        }
        catch (EndOfStreamException)
        {
            fs.Dispose();
            return null;
        }
        if (frames.Count < 2) { fs.Dispose(); return null; }
        return new ZstdAccess(fs, frames);
    }

    public void Read(long pos, Span<byte> dst)
    {
        int n = (int)Math.Clamp(Length - pos, 0, dst.Length);
        dst[n..].Clear();
        lock (_lock)
        {
            int done = 0;
            while (done < n)
            {
                long p = pos + done;
                int fi = _frames.FindLastIndex(f => f.OutOffset <= p);
                var data = Decode(fi);
                int off = (int)(p - _frames[fi].OutOffset);
                int take = Math.Min(n - done, data.Length - off);
                data.AsSpan(off, take).CopyTo(dst[done..]);
                done += take;
            }
        }
    }

    private byte[] Decode(int fi)
    {
        if (_cache.Get(fi) is { } hit) return hit;
        var f = _frames[fi];
        var comp = new byte[f.CompSize];
        _fs.Position = f.CompOffset;
        _fs.ReadExactly(comp);
        using var d = new ZstdSharp.Decompressor();
        var data = d.Unwrap(comp).ToArray();
        if (data.Length != f.OutSize) throw new InvalidDataException("Frame zstd di dimensione inattesa.");
        _cache.Put(fi, data);
        return data;
    }

    public void Dispose() => _fs.Dispose();
}

/// <summary>
/// Fallback for streams without random access (single-block xz, single-frame
/// zstd): decompressed once into a sparse cache file, reused next time.
/// </summary>
public static class CachedAccess
{
    public static IRandomAccess Create(string path, string key, IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        string dir = ArchiveStore.Dir("cache");
        string data = Path.Combine(dir, key + ".raw"), done = data + ".ok";
        if (File.Exists(data) && File.Exists(done))
        {
            File.SetLastAccessTimeUtc(data, DateTime.UtcNow);
            return new FileSliceAccess(data, 0, new FileInfo(data).Length);
        }
        File.Delete(done);
        var state = new JobProgress { Phase = "Decompressione nella cache", Total = new FileInfo(path).Length };
        using (var compressed = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
        using (var input = ImageFormats.Decompressor(compressed, ImageFormats.Detect(path)))
        using (var output = new FileStream(data, FileMode.Create, FileAccess.Write))
        {
            Imaging.MakeSparse(output);
            var buf = new byte[4 << 20];
            int n;
            while ((n = input.ReadAtLeast(buf, buf.Length, throwOnEndOfStream: false)) > 0)
            {
                cancel.ThrowIfCancellationRequested();
                if (buf.AsSpan(0, n).IndexOfAnyExcept((byte)0) < 0) output.Seek(n, SeekOrigin.Current);
                else output.Write(buf, 0, n);
                state.Done = compressed.Position;
                progress?.Report(state);
            }
            output.SetLength(output.Position);
        }
        File.WriteAllText(done, "");
        return new FileSliceAccess(data, 0, new FileInfo(data).Length);
    }
}
