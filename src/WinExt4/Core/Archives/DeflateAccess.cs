using System.IO;

namespace WinExt4.Core.Archives;

/// <summary>
/// Random access to a gzip file (possibly multi-member) or to a raw deflate
/// stream inside a zip, through checkpoints taken every ~4 MB of output.
/// The index is built in one sequential pass and saved under %LOCALAPPDATA%\WinExt4\index for the next time.
/// </summary>
public sealed class DeflateAccess : IRandomAccess
{
    private const long Spacing = 4L << 20;
    private const uint IndexMagic = 0x58444957; // "WIDX"

    private readonly string _path;
    private readonly bool _gzip;
    private readonly List<Checkpoint> _points;
    private readonly FileStream _fs;
    private readonly SegmentCache _cache = new(8);
    private readonly object _lock = new();

    public long Length { get; }

    private DeflateAccess(string path, bool gzip, List<Checkpoint> points, long length)
    {
        _path = path;
        _gzip = gzip;
        _points = points;
        Length = length;
        _fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.RandomAccess);
    }

    // ------------------------------------------------------------------
    // building

    /// <param name="start">Byte offset of the gzip header (gzip) or of the deflate data (zip).</param>
    public static DeflateAccess Build(string path, long start, bool gzip, string indexKey,
        IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        string idx = Path.Combine(ArchiveStore.Dir("index"), indexKey + ".idx");
        if (TryLoad(idx, path, gzip) is { } cached) return cached;

        var points = new List<Checkpoint>();
        long total = new FileInfo(path).Length;
        var state = new JobProgress { Phase = "Indicizzazione dell'archivio", Total = total };
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, FileOptions.SequentialScan);
        var inf = new Inflater(fs);
        inf.Start(start * 8, 0, ReadOnlySpan<byte>.Empty);
        long lastPoint = -Spacing;
        inf.OnBlockBoundary = (outPos, inBit) =>
        {
            if (outPos - lastPoint < Spacing) return;
            points.Add(new Checkpoint { OutPos = outPos, InBit = inBit, Window = inf.WindowSnapshot() });
            lastPoint = outPos;
        };
        if (gzip) SkipGzipHeader(inf, first: true);
        byte[] buf = new byte[1 << 20];
        while (true)
        {
            cancel.ThrowIfCancellationRequested();
            int n = inf.Read(buf);
            if (n > 0)
            {
                state.Done = inf.BitPosition / 8;
                progress?.Report(state);
                continue;
            }
            if (!gzip || !NextGzipMember(inf)) break;
        }
        var access = new DeflateAccess(path, gzip, points, inf.OutPos);
        if (points.Count == 0) points.Add(new Checkpoint { OutPos = 0, InBit = start * 8 });
        access.Save(idx);
        return access;
    }

    private static void SkipGzipHeader(Inflater inf, bool first)
    {
        int id1 = inf.ReadByteAligned(), id2 = inf.ReadByteAligned();
        if (id1 != 0x1F || id2 != 0x8B) throw new InvalidDataException("Intestazione gzip non valida.");
        if (inf.ReadByteAligned() != 8) throw new InvalidDataException("Metodo di compressione gzip non supportato.");
        int flg = inf.ReadByteAligned();
        for (int i = 0; i < 6; i++) inf.ReadByteAligned(); // mtime, xfl, os
        if ((flg & 4) != 0)
        {
            int xlen = inf.ReadByteAligned() | inf.ReadByteAligned() << 8;
            for (int i = 0; i < xlen; i++) inf.ReadByteAligned();
        }
        if ((flg & 8) != 0) while (inf.ReadByteAligned() > 0) { }
        if ((flg & 16) != 0) while (inf.ReadByteAligned() > 0) { }
        if ((flg & 2) != 0) { inf.ReadByteAligned(); inf.ReadByteAligned(); }
        if (!first) inf.Restart();
    }

    /// <summary>After a member: skip the trailer; true if another member follows.</summary>
    private static bool NextGzipMember(Inflater inf)
    {
        for (int i = 0; i < 8; i++)
            if (inf.ReadByteAligned() < 0) return false;
        // peek: a following member starts with 1F 8B; anything else (e.g. zero padding) ends the file
        int b = inf.ReadByteAligned();
        if (b != 0x1F) return false;
        int c = inf.ReadByteAligned();
        if (c != 0x8B) return false;
        if (inf.ReadByteAligned() != 8) throw new InvalidDataException("Metodo di compressione gzip non supportato.");
        int flg = inf.ReadByteAligned();
        for (int i = 0; i < 6; i++) inf.ReadByteAligned();
        if ((flg & 4) != 0)
        {
            int xlen = inf.ReadByteAligned() | inf.ReadByteAligned() << 8;
            for (int i = 0; i < xlen; i++) inf.ReadByteAligned();
        }
        if ((flg & 8) != 0) while (inf.ReadByteAligned() > 0) { }
        if ((flg & 16) != 0) while (inf.ReadByteAligned() > 0) { }
        if ((flg & 2) != 0) { inf.ReadByteAligned(); inf.ReadByteAligned(); }
        inf.Restart();
        return true;
    }

    // ------------------------------------------------------------------
    // persistence

    private void Save(string file)
    {
        try
        {
            using var w = new BinaryWriter(File.Create(file));
            w.Write(IndexMagic);
            w.Write(_gzip);
            w.Write(Length);
            w.Write(_points.Count);
            foreach (var p in _points)
            {
                w.Write(p.OutPos);
                w.Write(p.InBit);
                w.Write(p.Window.Length);
                w.Write(p.Window);
            }
        }
        catch (IOException)
        {
        }
    }

    private static DeflateAccess? TryLoad(string file, string path, bool gzip)
    {
        try
        {
            if (!File.Exists(file)) return null;
            using var r = new BinaryReader(File.OpenRead(file));
            if (r.ReadUInt32() != IndexMagic || r.ReadBoolean() != gzip) return null;
            long length = r.ReadInt64();
            int count = r.ReadInt32();
            var points = new List<Checkpoint>(count);
            for (int i = 0; i < count; i++)
                points.Add(new Checkpoint { OutPos = r.ReadInt64(), InBit = r.ReadInt64(), Window = r.ReadBytes(r.ReadInt32()) });
            File.SetLastAccessTimeUtc(file, DateTime.UtcNow);
            return new DeflateAccess(path, gzip, points, length);
        }
        catch (Exception e) when (e is IOException or EndOfStreamException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------
    // reading

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
                int i = FindSegment(p);
                byte[] seg = Segment(i);
                long segStart = _points[i].OutPos;
                int off = (int)(p - segStart);
                int take = Math.Min(n - done, seg.Length - off);
                if (take <= 0) throw new InvalidDataException("Indice dell'archivio non coerente.");
                seg.AsSpan(off, take).CopyTo(dst[done..]);
                done += take;
            }
        }
    }

    private int FindSegment(long pos)
    {
        int lo = 0, hi = _points.Count - 1;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (_points[mid].OutPos <= pos) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    private byte[] Segment(int i)
    {
        if (_cache.Get(i) is { } hit) return hit;
        var cp = _points[i];
        long end = i + 1 < _points.Count ? _points[i + 1].OutPos : Length;
        var data = new byte[end - cp.OutPos];
        var inf = new Inflater(_fs);
        inf.Start(cp.InBit, cp.OutPos, cp.Window);
        int done = 0;
        while (done < data.Length)
        {
            int r = inf.Read(data.AsSpan(done));
            if (r > 0) { done += r; continue; }
            if (!_gzip || !NextGzipMember(inf)) break;
        }
        if (done != data.Length) throw new InvalidDataException("Archivio troncato o modificato dopo l'indicizzazione.");
        _cache.Put(i, data);
        return data;
    }

    public void Dispose() => _fs.Dispose();
}
