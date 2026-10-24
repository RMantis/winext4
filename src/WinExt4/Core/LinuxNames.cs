using System.Text;

namespace WinExt4.Core;

/// <summary>
/// Maps Linux file names to Windows-legal names and back, using the same
/// convention as WSL and Cygwin: characters Windows forbids (\ : * ? " &lt; &gt; |,
/// control characters, trailing dots/spaces) are shifted into the private use
/// area at U+F000 + c, so they display as boxes but round-trip exactly.
/// </summary>
public static class LinuxNames
{
    private const int Base = 0xF000;

    private static bool Forbidden(char c) =>
        c < 0x20 || c is '\\' or ':' or '*' or '?' or '"' or '<' or '>' or '|';

    public static string ToWindows(string linux)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < linux.Length; i++)
        {
            char c = linux[i];
            bool last = i == linux.Length - 1;
            if (Forbidden(c) || (last && (c == '.' || c == ' ') && linux != "." && linux != ".."))
            {
                sb ??= new StringBuilder(linux, 0, i, linux.Length);
                sb.Append((char)(Base + c));
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? linux;
    }

    public static string ToLinux(string windows)
    {
        StringBuilder? sb = null;
        for (int i = 0; i < windows.Length; i++)
        {
            char c = windows[i];
            if (c >= Base && c < Base + 0x80)
            {
                sb ??= new StringBuilder(windows, 0, i, windows.Length);
                sb.Append((char)(c - Base));
            }
            else
            {
                sb?.Append(c);
            }
        }
        return sb?.ToString() ?? windows;
    }

    /// <summary>Name suitable for a file extracted to an NTFS folder.</summary>
    public static string ForExport(string linux) => ToWindows(linux);
}
