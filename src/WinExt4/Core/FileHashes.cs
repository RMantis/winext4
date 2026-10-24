using System.Buffers;
using System.Security.Cryptography;
using System.Text;

namespace WinExt4.Core;

/// <summary>md5sum / sha256sum for files inside the ext filesystem, and verification of such lists.</summary>
public static class FileHashes
{
    public sealed record FileHash(string RelativePath, string Hash, ulong Size);

    public sealed record CheckResult(string RelativePath, string Status); // OK, DIVERSO, MANCANTE, ERRORE: ...

    private const int Chunk = 1 << 20;

    /// <summary>Hashes the entries recursively; paths are relative to the directory they are in.</summary>
    public static List<FileHash> Compute(Ext4Volume vol, uint dir, IEnumerable<Ext4DirEntry> entries, HashAlgorithmName algo,
        IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var files = new List<(string Path, uint Ino, ulong Size)>();
        foreach (var e in entries) Collect(vol, dir, e, e.Name, files, 0);
        var state = new JobProgress { Phase = "Calcolo hash", Total = (long)files.Sum(f => (decimal)f.Size) };
        var result = new List<FileHash>();
        long done = 0;
        foreach (var f in files.OrderBy(f => f.Path, StringComparer.Ordinal))
        {
            cancel.ThrowIfCancellationRequested();
            state.Phase = f.Path;
            result.Add(new FileHash(f.Path, HashOne(vol, f.Ino, f.Size, algo, cancel, n =>
            {
                done += n;
                state.Done = done;
                progress?.Report(state);
            }), f.Size));
        }
        return result;
    }

    private static void Collect(Ext4Volume vol, uint dir, Ext4DirEntry e, string path,
        List<(string, uint, ulong)> files, int depth)
    {
        if (depth > 64 || !vol.TryStat(e.Ino, out var st)) return;
        uint ino = e.Ino;
        if (st!.IsSymlink)
        {
            uint t = vol.Follow(dir, ino);
            if (t == 0 || !vol.TryStat(t, out var ts) || !ts!.IsRegular) return; // like md5sum: skip dirs/dangling
            ino = t;
            st = ts;
        }
        if (st.IsRegular)
        {
            files.Add((path, ino, st.Size));
        }
        else if (st.IsDirectory)
        {
            foreach (var c in vol.ReadDir(ino))
                Collect(vol, ino, c, path + "/" + c.Name, files, depth + 1);
        }
    }

    public static string HashOne(Ext4Volume vol, uint ino, ulong size, HashAlgorithmName algo, CancellationToken cancel,
        Action<int>? onBytes = null)
    {
        using var h = IncrementalHash.CreateHash(algo);
        byte[] buf = ArrayPool<byte>.Shared.Rent(Chunk);
        try
        {
            for (ulong pos = 0; pos < size;)
            {
                cancel.ThrowIfCancellationRequested();
                int n = vol.Read(ino, pos, buf.AsSpan(0, (int)Math.Min((ulong)Chunk, size - pos)));
                if (n <= 0) break;
                h.AppendData(buf, 0, n);
                pos += (ulong)n;
                onBytes?.Invoke(n);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buf);
        }
        return Convert.ToHexString(h.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>The md5sum/sha256sum text ("hash  path" lines, LF).</summary>
    public static string Format(IEnumerable<FileHash> hashes)
    {
        var sb = new StringBuilder();
        foreach (var h in hashes) sb.Append(h.Hash).Append("  ").Append(h.RelativePath).Append('\n');
        return sb.ToString();
    }

    /// <summary>Checks an md5sum/sha256sum list against files below <paramref name="baseDir"/> (an ext path).</summary>
    public static List<CheckResult> Verify(Ext4Volume vol, string baseDir, string listText,
        IProgress<JobProgress>? progress, CancellationToken cancel)
    {
        var results = new List<CheckResult>();
        var lines = listText.Split('\n').Select(Sidecar.ParseLine).Where(l => l.Hash is not null && l.File is not null).ToList();
        var state = new JobProgress { Phase = "Verifica", Total = lines.Count };
        int i = 0;
        foreach (var (hash, file) in lines)
        {
            cancel.ThrowIfCancellationRequested();
            state.Phase = file!;
            state.Done = i++;
            progress?.Report(state);
            string rel = file!.Replace('\\', '/');
            if (rel.StartsWith("./")) rel = rel[2..];
            string path = rel.StartsWith('/') ? rel : (baseDir == "/" ? "/" : baseDir + "/") + rel;
            var algo = hash!.Length switch
            {
                32 => HashAlgorithmName.MD5,
                40 => HashAlgorithmName.SHA1,
                64 => HashAlgorithmName.SHA256,
                128 => HashAlgorithmName.SHA512,
                _ => default,
            };
            if (algo == default)
            {
                results.Add(new CheckResult(rel, "ERRORE: lunghezza dell'hash non riconosciuta"));
                continue;
            }
            // "/x/." would make namei follow a final symlink, as md5sum does
            if (!vol.TryLookup(path, out uint ino))
            {
                results.Add(new CheckResult(rel, "MANCANTE"));
                continue;
            }
            try
            {
                var st = vol.Stat(ino);
                if (st.IsSymlink)
                {
                    uint t = vol.Follow(vol.Lookup(ParentOf(path)), ino);
                    if (t == 0) { results.Add(new CheckResult(rel, "MANCANTE (link non valido)")); continue; }
                    ino = t;
                    st = vol.Stat(t);
                }
                if (!st.IsRegular) { results.Add(new CheckResult(rel, "ERRORE: non è un file")); continue; }
                string actual = HashOne(vol, ino, st.Size, algo, cancel);
                results.Add(new CheckResult(rel, string.Equals(actual, hash, StringComparison.OrdinalIgnoreCase) ? "OK" : "DIVERSO"));
            }
            catch (Ext4Exception ex)
            {
                results.Add(new CheckResult(rel, "ERRORE: " + ex.Message));
            }
        }
        return results;
    }

    private static string ParentOf(string p)
    {
        int i = p.LastIndexOf('/');
        return i <= 0 ? "/" : p[..i];
    }
}
