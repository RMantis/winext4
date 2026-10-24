using System.Buffers.Binary;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using Joveler.Compression.XZ;
using ZstdSharp;

namespace WinExt4.Core;

public enum ImageFormat
{
    Raw,     // .img (optionally sparse on NTFS)
    Gzip,    // .img.gz
    Zstd,    // .img.zst
    Xz,      // .img.xz (multi-block: seekable later on)
    Vhd,     // dynamic .vhd: opens directly in Windows / Hyper-V
}

public static class ImageFormats
{
    public static string Extension(ImageFormat f) => f switch
    {
        ImageFormat.Gzip => ".img.gz",
        ImageFormat.Zstd => ".img.zst",
        ImageFormat.Xz => ".img.xz",
        ImageFormat.Vhd => ".vhd",
        _ => ".img",
    };

    public static string Description(ImageFormat f) => f switch
    {
        ImageFormat.Gzip => "gzip (.img.gz) — compatibile ovunque",
        ImageFormat.Zstd => "zstd (.img.zst) — veloce, ottimo rapporto",
        ImageFormat.Xz => "xz (.img.xz) — il più compatto, più lento",
        ImageFormat.Vhd => "VHD dinamico (.vhd) — si apre direttamente in Windows",
        _ => "immagine grezza (.img)",
    };

    /// <summary>From the content (magic numbers), falling back to the extension.</summary>
    public static ImageFormat Detect(string path)
    {
        Span<byte> head = stackalloc byte[8];
        using (var fs = File.OpenRead(path))
        {
            int n = fs.Read(head);
            head = head[..n];
            if (head.StartsWith(stackalloc byte[] { 0x1F, 0x8B })) return ImageFormat.Gzip;
            if (head.StartsWith(stackalloc byte[] { 0x28, 0xB5, 0x2F, 0xFD })) return ImageFormat.Zstd;
            if (head.StartsWith(stackalloc byte[] { 0xFD, (byte)'7', (byte)'z', (byte)'X', (byte)'Z', 0 })) return ImageFormat.Xz;
            if (head.StartsWith("conectix"u8)) return ImageFormat.Vhd; // dynamic VHD: footer copy first
            if (fs.Length >= 512)
            {
                var foot = new byte[8];
                fs.Seek(-512, SeekOrigin.End);
                fs.ReadExactly(foot);
                if (foot.AsSpan().SequenceEqual("conectix"u8)) return ImageFormat.Vhd; // fixed VHD
            }
        }
        return ImageFormat.Raw;
    }

    private static bool _xzReady;

    internal static void EnsureXz()
    {
        if (_xzReady) return;
        // next to WinExt4.dll (also when hosted by tests), or in the NuGet runtimes folder
        string dir = Path.GetDirectoryName(typeof(ImageFormats).Assembly.Location) is { Length: > 0 } d ? d : AppContext.BaseDirectory;
        string? dll = new[] { Path.Combine(dir, "liblzma.dll"), Path.Combine(dir, "runtimes", "win-x64", "native", "liblzma.dll") }
            .FirstOrDefault(File.Exists);
        if (dll is null) throw new IOException("liblzma.dll non trovata: il formato xz non è disponibile.");
        XZInit.GlobalInit(dll);
        _xzReady = true;
    }

    /// <summary>Compressing stream for <paramref name="format"/> (not Raw/Vhd).</summary>
    public static Stream Compressor(Stream output, ImageFormat format) => format switch
    {
        ImageFormat.Gzip => new GZipStream(output, CompressionLevel.Optimal, leaveOpen: true),
        ImageFormat.Zstd => Zstd(output),
        ImageFormat.Xz => Xz(output),
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static Stream Zstd(Stream output)
    {
        var z = new CompressionStream(output, level: 9, leaveOpen: true);
        // content checksum: a damaged file fails to decompress instead of yielding garbage
        z.SetParameter(ZstdSharp.Unsafe.ZSTD_cParameter.ZSTD_c_checksumFlag, 1);
        return z;
    }

    private static Stream Xz(Stream output)
    {
        EnsureXz();
        return new XZStream(output, new XZCompressOptions { Level = LzmaCompLevel.Level6, LeaveOpen = true },
            new XZParallelCompressOptions { Threads = Math.Max(1, Environment.ProcessorCount - 1), BlockSize = 32UL << 20 });
    }

    /// <summary>Decompressing stream over <paramref name="compressed"/> (gzip, zstd or xz), closing it when disposed.</summary>
    public static Stream Decompressor(Stream compressed, ImageFormat format)
    {
        switch (format)
        {
            case ImageFormat.Gzip: return new GZipStream(compressed, CompressionMode.Decompress);
            case ImageFormat.Zstd: return new DecompressionStream(compressed, leaveOpen: false);
            case ImageFormat.Xz:
                EnsureXz();
                return new XZStream(compressed, new XZDecompressOptions { LeaveOpen = false });
            default: throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    /// <summary>
    /// Opens an image for sequential reading of its raw content.
    /// <paramref name="length"/> is known for Raw and VHD, null for compressed streams.
    /// </summary>
    public static Stream OpenRead(string path, out ImageFormat format, out long? length)
    {
        format = Detect(path);
        var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
        switch (format)
        {
            case ImageFormat.Gzip:
                length = null;
                return new GZipStream(fs, CompressionMode.Decompress);
            case ImageFormat.Zstd:
                length = null;
                return new DecompressionStream(fs, leaveOpen: false);
            case ImageFormat.Xz:
                EnsureXz();
                length = null;
                return new XZStream(fs, new XZDecompressOptions { LeaveOpen = false });
            case ImageFormat.Vhd:
                var vhd = new VhdReader(fs);
                length = vhd.Length;
                return vhd;
            default:
                length = fs.Length;
                return fs;
        }
    }
}

/// <summary>Pass-through stream that hashes everything written (hash of the compressed file).</summary>
public sealed class HashingStream : Stream
{
    private readonly Stream _inner;
    private readonly IncrementalHash _md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
    private readonly IncrementalHash _sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
    private readonly bool _onRead;

    public long Count { get; private set; }

    /// <param name="onRead">Hash what is read instead of what is written.</param>
    public HashingStream(Stream inner, bool onRead = false)
    {
        _inner = inner;
        _onRead = onRead;
    }

    public (string Md5, string Sha256) Finish() =>
        (Convert.ToHexString(_md5.GetHashAndReset()).ToLowerInvariant(), Convert.ToHexString(_sha.GetHashAndReset()).ToLowerInvariant());

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (!_onRead) Add(buffer);
        _inner.Write(buffer);
    }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = _inner.Read(buffer);
        if (_onRead && n > 0) Add(buffer[..n]);
        return n;
    }

    private void Add(ReadOnlySpan<byte> b)
    {
        _md5.AppendData(b);
        _sha.AppendData(b);
        Count += b.Length;
    }

    public override bool CanRead => _onRead;
    public override bool CanSeek => false;
    public override bool CanWrite => !_onRead;
    public override long Length => _inner.Length;
    public override long Position { get => _inner.Position; set => throw new NotSupportedException(); }
    public override void Flush() => _inner.Flush();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _md5.Dispose();
            _sha.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Writes a dynamic VHD sequentially: all-zero 2 MB blocks are not stored,
/// so the file only takes the space of the data.
/// </summary>
public sealed class DynamicVhdWriter : IDisposable
{
    private const int BlockSize = 2 << 20;
    private const long HeaderOffset = 512;
    private const long BatOffset = 1536;

    private readonly Stream _out;
    private readonly ulong _size;
    private readonly uint[] _bat;
    private readonly byte[] _block = new byte[BlockSize];
    private int _fill;
    private int _index;
    private long _next;
    private static readonly byte[] Bitmap = Enumerable.Repeat((byte)0xFF, 512).ToArray();

    public DynamicVhdWriter(Stream output, ulong size)
    {
        if (size % 512 != 0) throw new ArgumentException("La dimensione deve essere un multiplo di 512 byte.");
        _out = output;
        _size = size;
        _bat = new uint[(size + BlockSize - 1) / BlockSize];
        Array.Fill(_bat, 0xFFFFFFFFu);
        long batBytes = ((long)_bat.Length * 4 + 511) / 512 * 512;
        _next = BatOffset + batBytes;
        _out.SetLength(_next);
        _out.Position = _next;
    }

    public void Write(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            int n = Math.Min(data.Length, BlockSize - _fill);
            data[..n].CopyTo(_block.AsSpan(_fill));
            _fill += n;
            data = data[n..];
            if (_fill == BlockSize) FlushBlock();
        }
    }

    private void FlushBlock()
    {
        if (_fill == 0) return;
        if (_fill < BlockSize) Array.Clear(_block, _fill, BlockSize - _fill);
        if (_block.AsSpan().IndexOfAnyExcept((byte)0) >= 0)
        {
            _bat[_index] = (uint)(_next / 512);
            _out.Position = _next;
            _out.Write(Bitmap);
            _out.Write(_block);
            _next += 512 + BlockSize;
        }
        _index++;
        _fill = 0;
    }

    /// <summary>Writes BAT, dynamic header and both footers.</summary>
    public void Finish()
    {
        FlushBlock();
        var footer = Footer(_size);
        _out.Position = _next;
        _out.Write(footer);
        _out.Position = 0;
        _out.Write(footer);
        _out.Position = HeaderOffset;
        _out.Write(DynamicHeader());
        _out.Position = BatOffset;
        var bat = new byte[((long)_bat.Length * 4 + 511) / 512 * 512];
        bat.AsSpan().Fill(0xFF);
        for (int i = 0; i < _bat.Length; i++) BinaryPrimitives.WriteUInt32BigEndian(bat.AsSpan(i * 4), _bat[i]);
        _out.Write(bat);
        _out.Flush();
    }

    private byte[] DynamicHeader()
    {
        var h = new byte[1024];
        "cxsparse"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(8), ulong.MaxValue);
        BinaryPrimitives.WriteUInt64BigEndian(h.AsSpan(16), (ulong)BatOffset);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(24), 0x00010000);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(28), (uint)_bat.Length);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(32), BlockSize);
        BinaryPrimitives.WriteUInt32BigEndian(h.AsSpan(36), Checksum(h));
        return h;
    }

    internal static byte[] Footer(ulong size)
    {
        var f = new byte[512];
        "conectix"u8.CopyTo(f);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(8), 2);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(12), 0x00010000);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(16), (ulong)HeaderOffset);
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(24), (uint)(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 946684800));
        "wx4 "u8.CopyTo(f.AsSpan(28));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(32), 0x00010000);
        "Wi2k"u8.CopyTo(f.AsSpan(36));
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(40), size);
        BinaryPrimitives.WriteUInt64BigEndian(f.AsSpan(48), size);
        Geometry(size).CopyTo(f.AsSpan(56));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(60), 3); // dynamic
        Guid.NewGuid().ToByteArray().CopyTo(f.AsSpan(68));
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(64), Checksum(f));
        return f;
    }

    private static uint Checksum(byte[] b)
    {
        uint sum = 0;
        foreach (byte x in b) sum += x;
        return ~sum;
    }

    private static byte[] Geometry(ulong size)
    {
        ulong total = Math.Min(size / 512, 65535UL * 16 * 255);
        ulong spt, heads, cth;
        if (total >= 65535UL * 16 * 63) { spt = 255; heads = 16; cth = total / spt; }
        else
        {
            spt = 17; cth = total / spt;
            heads = Math.Max((cth + 1023) / 1024, 4);
            if (cth >= heads * 1024 || heads > 16) { spt = 31; heads = 16; cth = total / spt; }
            if (cth >= heads * 1024) { spt = 63; heads = 16; cth = total / spt; }
        }
        var g = new byte[4];
        BinaryPrimitives.WriteUInt16BigEndian(g, (ushort)(cth / heads));
        g[2] = (byte)heads;
        g[3] = (byte)spt;
        return g;
    }

    public void Dispose()
    {
    }
}

/// <summary>Sequential reader of fixed and dynamic VHDs (differencing disks are not supported).</summary>
public sealed class VhdReader : Stream
{
    private readonly Stream _fs;
    private readonly bool _dynamic;
    private readonly uint[]? _bat;
    private readonly uint _blockSize;
    private long _pos;

    public VhdReader(Stream fs)
    {
        _fs = fs;
        var footer = new byte[512];
        fs.Seek(-512, SeekOrigin.End);
        fs.ReadExactly(footer);
        if (!footer.AsSpan(0, 8).SequenceEqual("conectix"u8))
        {
            fs.Position = 0;
            fs.ReadExactly(footer);
        }
        Length = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(48));
        uint type = BinaryPrimitives.ReadUInt32BigEndian(footer.AsSpan(60));
        if (type == 4) throw new IOException("I VHD differenziali non sono supportati.");
        _dynamic = type == 3;
        if (_dynamic)
        {
            var h = new byte[1024];
            fs.Position = (long)BinaryPrimitives.ReadUInt64BigEndian(footer.AsSpan(16));
            fs.ReadExactly(h);
            long batOffset = (long)BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(16));
            int entries = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(28));
            _blockSize = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(32));
            var bat = new byte[entries * 4];
            fs.Position = batOffset;
            fs.ReadExactly(bat);
            _bat = new uint[entries];
            for (int i = 0; i < entries; i++) _bat[i] = BinaryPrimitives.ReadUInt32BigEndian(bat.AsSpan(i * 4));
        }
    }

    public override long Length { get; }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (_pos >= Length) return 0;
        int n = (int)Math.Min(buffer.Length, Length - _pos);
        if (!_dynamic)
        {
            _fs.Position = _pos;
            n = _fs.Read(buffer[..n]);
        }
        else
        {
            int block = (int)(_pos / _blockSize);
            int inBlock = (int)(_pos % _blockSize);
            n = Math.Min(n, (int)_blockSize - inBlock);
            uint sector = _bat![block];
            if (sector == 0xFFFFFFFF)
            {
                buffer[..n].Clear();
            }
            else
            {
                // block = sector bitmap (rounded to 512) + data; the bitmap is all
                // ones for blocks we wrote, and Windows sets it for written sectors.
                long bitmapBytes = ((_blockSize / 512 / 8) + 511) / 512 * 512;
                _fs.Position = (long)sector * 512 + bitmapBytes + inBlock;
                _fs.ReadExactly(buffer[..n]);
            }
        }
        _pos += n;
        return n;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Position { get => _pos; set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _fs.Dispose();
        base.Dispose(disposing);
    }
}
