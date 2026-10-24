using System.IO;
using System.Runtime.CompilerServices;

namespace WinExt4.Core.Archives;

/// <summary>Where decoding can restart: a deflate block boundary.</summary>
public sealed class Checkpoint
{
    public long OutPos;     // decompressed offset
    public long InBit;      // bit offset in the compressed file
    public byte[] Window = Array.Empty<byte>(); // last up to 32 KB of output before OutPos
}

/// <summary>
/// A raw deflate (RFC 1951) decoder that can start at any block boundary given
/// the preceding 32 KB of output — what zlib's zran example does with
/// inflatePrime/inflateSetDictionary, which .NET's DeflateStream cannot.
/// It reports block boundaries so callers can build an access index.
/// </summary>
public sealed class Inflater
{
    private const int WindowSize = 32768;
    private const int MaxBits = 15;

    private readonly Stream _in;
    private readonly byte[] _inBuf = new byte[1 << 16];
    private int _inLen, _inPos;
    private long _inBase;          // file offset of _inBuf[0]
    private ulong _bits;
    private int _bitCount;

    private readonly byte[] _window = new byte[WindowSize];
    private int _wpos;             // next write position in the circular window
    private long _out;             // total bytes produced so far (absolute)
    private int _filled;           // valid bytes in the window (up to 32 KB)

    private bool _final;           // the current block is the last one
    private bool _inBlock;
    private int _blockType;
    private int _storedLeft;
    private int[] _litTable = Array.Empty<int>(), _distTable = Array.Empty<int>();
    private int _litBits, _distBits;
    private int _pendingLen, _pendingDist;

    public bool Finished { get; private set; }
    public long OutPos => _out;

    /// <summary>Called at each block start with the restart state.</summary>
    public Action<long, long>? OnBlockBoundary; // (outPos, inBit)

    public Inflater(Stream input)
    {
        _in = input;
    }

    // ------------------------------------------------------------------
    // positioning

    public void Start(long inBit, long outPos, ReadOnlySpan<byte> window)
    {
        _in.Position = inBit >> 3;
        _inBase = _in.Position;
        _inLen = _inPos = 0;
        _bits = 0;
        _bitCount = 0;
        int skip = (int)(inBit & 7);
        if (skip > 0) { Need(skip); Drop(skip); }
        _out = outPos;
        _filled = Math.Min(window.Length, WindowSize);
        window[^_filled..].CopyTo(_window);
        _wpos = _filled % WindowSize;
        _final = false;
        _inBlock = false;
        Finished = false;
        _pendingLen = 0;
    }

    public long BitPosition => (_inBase + _inPos) * 8 - _bitCount;

    /// <summary>Copy of the last 32 KB of output (for a checkpoint).</summary>
    public byte[] WindowSnapshot()
    {
        var w = new byte[_filled];
        if (_filled < WindowSize) Array.Copy(_window, 0, w, 0, _filled);
        else
        {
            int tail = WindowSize - _wpos;
            Array.Copy(_window, _wpos, w, 0, tail);
            Array.Copy(_window, 0, w, tail, _wpos);
        }
        return w;
    }

    // ------------------------------------------------------------------
    // bits

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Need(int n)
    {
        while (_bitCount < n)
        {
            if (_inPos == _inLen)
            {
                _inBase += _inLen;
                _inLen = _in.Read(_inBuf, 0, _inBuf.Length);
                _inPos = 0;
                if (_inLen <= 0) throw new InvalidDataException("Dati compressi troncati.");
            }
            _bits |= (ulong)_inBuf[_inPos++] << _bitCount;
            _bitCount += 8;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Drop(int n)
    {
        _bits >>= n;
        _bitCount -= n;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Take(int n)
    {
        if (n == 0) return 0;
        Need(n);
        int v = (int)(_bits & ((1UL << n) - 1));
        Drop(n);
        return v;
    }

    private void AlignToByte() => Drop(_bitCount & 7);

    /// <summary>Raw byte reads after the deflate stream (gzip trailer/header), byte aligned.</summary>
    public int ReadByteAligned()
    {
        AlignToByte();
        if (_bitCount >= 8) return Take(8);
        if (_inPos == _inLen)
        {
            _inBase += _inLen;
            _inLen = _in.Read(_inBuf, 0, _inBuf.Length);
            _inPos = 0;
            if (_inLen <= 0) return -1;
        }
        return _inBuf[_inPos++];
    }

    /// <summary>Starts a new deflate stream at the current (byte aligned) position, keeping output position.</summary>
    public void Restart()
    {
        AlignToByte();
        Finished = false;
        _final = false;
        _inBlock = false;
        _filled = 0;
        _wpos = 0;
    }

    // ------------------------------------------------------------------
    // Huffman tables: index = next maxBits input bits (LSB first), value = (len << 16) | symbol

    private static int[] Build(ReadOnlySpan<byte> lengths, out int maxBits)
    {
        Span<int> count = stackalloc int[MaxBits + 1];
        maxBits = 0;
        foreach (byte l in lengths) { count[l]++; if (l > maxBits) maxBits = l; }
        count[0] = 0;
        if (maxBits == 0) { maxBits = 1; return new int[2]; }
        Span<int> next = stackalloc int[MaxBits + 2];
        int code = 0;
        for (int b = 1; b <= MaxBits; b++)
        {
            code = (code + count[b - 1]) << 1;
            next[b] = code;
        }
        var table = new int[1 << maxBits];
        for (int sym = 0; sym < lengths.Length; sym++)
        {
            int len = lengths[sym];
            if (len == 0) continue;
            int c = next[len]++;
            int rev = 0;
            for (int i = 0; i < len; i++) { rev = (rev << 1) | (c & 1); c >>= 1; }
            int entry = (len << 16) | sym;
            for (int f = rev; f < table.Length; f += 1 << len) table[f] = entry;
        }
        return table;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Decode(int[] table, int bits)
    {
        if (_bitCount < bits)
        {
            // near the end of input there may be fewer bits than the table width
            try { Need(bits); }
            catch (InvalidDataException) when (_bitCount > 0) { }
        }
        int e = table[(int)(_bits & (ulong)(table.Length - 1))];
        int len = e >> 16;
        if (len == 0 || len > _bitCount) throw new InvalidDataException("Codice Huffman non valido.");
        Drop(len);
        return e & 0xFFFF;
    }

    private static readonly byte[] FixedLit = FixedLengths(288, i => i < 144 ? 8 : i < 256 ? 9 : i < 280 ? 7 : 8);
    private static readonly byte[] FixedDist = FixedLengths(30, _ => 5);
    private static byte[] FixedLengths(int n, Func<int, int> f) => Enumerable.Range(0, n).Select(i => (byte)f(i)).ToArray();

    private static readonly ushort[] LenBase = { 3, 4, 5, 6, 7, 8, 9, 10, 11, 13, 15, 17, 19, 23, 27, 31, 35, 43, 51, 59, 67, 83, 99, 115, 131, 163, 195, 227, 258 };
    private static readonly byte[] LenExtra = { 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 2, 2, 3, 3, 3, 3, 4, 4, 4, 4, 5, 5, 5, 5, 0 };
    private static readonly ushort[] DistBase = { 1, 2, 3, 4, 5, 7, 9, 13, 17, 25, 33, 49, 65, 97, 129, 193, 257, 385, 513, 769, 1025, 1537, 2049, 3073, 4097, 6145, 8193, 12289, 16385, 24577 };
    private static readonly byte[] DistExtra = { 0, 0, 0, 0, 1, 1, 2, 2, 3, 3, 4, 4, 5, 5, 6, 6, 7, 7, 8, 8, 9, 9, 10, 10, 11, 11, 12, 12, 13, 13 };
    private static readonly byte[] CodeOrder = { 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15 };

    private bool BeginBlock()
    {
        if (_final) { Finished = true; return false; }
        if (OnBlockBoundary is not null) OnBlockBoundary(_out, BitPosition);
        _final = Take(1) == 1;
        _blockType = Take(2);
        switch (_blockType)
        {
            case 0:
                AlignToByte();
                int len = Take(16), nlen = Take(16);
                if ((len ^ 0xFFFF) != nlen) throw new InvalidDataException("Blocco non compresso corrotto.");
                _storedLeft = len;
                break;
            case 1:
                _litTable = Build(FixedLit, out _litBits);
                _distTable = Build(FixedDist, out _distBits);
                break;
            case 2:
                ReadDynamicTables();
                break;
            default:
                throw new InvalidDataException("Tipo di blocco deflate non valido.");
        }
        _inBlock = true;
        return true;
    }

    private void ReadDynamicTables()
    {
        int hlit = Take(5) + 257, hdist = Take(5) + 1, hclen = Take(4) + 4;
        Span<byte> clen = stackalloc byte[19];
        clen.Clear();
        for (int i = 0; i < hclen; i++) clen[CodeOrder[i]] = (byte)Take(3);
        var cl = Build(clen, out int clBits);
        var lengths = new byte[hlit + hdist];
        for (int i = 0; i < lengths.Length;)
        {
            int sym = Decode(cl, clBits);
            if (sym < 16) { lengths[i++] = (byte)sym; continue; }
            int rep;
            byte val = 0;
            if (sym == 16)
            {
                if (i == 0) throw new InvalidDataException("Ripetizione senza lunghezza precedente.");
                val = lengths[i - 1];
                rep = 3 + Take(2);
            }
            else rep = sym == 17 ? 3 + Take(3) : 11 + Take(7);
            if (i + rep > lengths.Length) throw new InvalidDataException("Troppe lunghezze di codice.");
            while (rep-- > 0) lengths[i++] = val;
        }
        _litTable = Build(lengths.AsSpan(0, hlit), out _litBits);
        _distTable = Build(lengths.AsSpan(hlit), out _distBits);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private void Put(byte b)
    {
        _window[_wpos] = b;
        _wpos = (_wpos + 1) & (WindowSize - 1);
        if (_filled < WindowSize) _filled++;
        _out++;
    }

    /// <summary>
    /// Decompresses into <paramref name="dst"/>; returns bytes produced (0 at
    /// the end of the deflate stream).
    /// </summary>
    public int Read(Span<byte> dst)
    {
        int n = 0;
        while (n < dst.Length)
        {
            if (_pendingLen > 0)
            {
                // finish a back-reference interrupted by a full output buffer
                while (_pendingLen > 0 && n < dst.Length)
                {
                    byte b = _window[(_wpos - _pendingDist) & (WindowSize - 1)];
                    Put(b);
                    dst[n++] = b;
                    _pendingLen--;
                }
                continue;
            }
            if (!_inBlock && !BeginBlock()) break;
            if (_blockType == 0)
            {
                if (_storedLeft == 0) { _inBlock = false; continue; }
                byte b = (byte)Take(8);
                Put(b);
                dst[n++] = b;
                _storedLeft--;
                continue;
            }
            int sym = Decode(_litTable, _litBits);
            if (sym < 256)
            {
                byte b = (byte)sym;
                Put(b);
                dst[n++] = b;
            }
            else if (sym == 256)
            {
                _inBlock = false;
            }
            else
            {
                sym -= 257;
                if (sym >= 29) throw new InvalidDataException("Lunghezza non valida.");
                int len = LenBase[sym] + Take(LenExtra[sym]);
                int ds = Decode(_distTable, _distBits);
                if (ds >= 30) throw new InvalidDataException("Distanza non valida.");
                int dist = DistBase[ds] + Take(DistExtra[ds]);
                if (dist > _filled) throw new InvalidDataException("Distanza oltre la finestra.");
                _pendingLen = len;
                _pendingDist = dist;
            }
        }
        return n;
    }
}
