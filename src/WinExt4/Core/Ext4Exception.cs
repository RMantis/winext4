using System.Runtime.InteropServices;
using WinExt4.Native;

namespace WinExt4.Core;

/// <summary>errno values as defined by the MSVC CRT (what ext4core returns).</summary>
public static class Errno
{
    public const int ENOENT = 2;
    public const int EIO = 5;
    public const int ENOMEM = 12;
    public const int EACCES = 13;
    public const int EBUSY = 16;
    public const int EEXIST = 17;
    public const int ENODEV = 19;
    public const int ENOTDIR = 20;
    public const int EISDIR = 21;
    public const int EINVAL = 22;
    public const int EFBIG = 27;
    public const int ENOSPC = 28;
    public const int EROFS = 30;
    public const int ENAMETOOLONG = 38;
    public const int ENOTEMPTY = 41;
    public const int ELOOP = 114;

    // ext4core specific
    public const int EVOLUME_LOCK = 900;
    public const int ENOTEXT = 901;
    public const int EUNSUPP_RW = 902;
    public const int ENEEDS_FSCK = 903;
    public const int EDRAFT_MISMATCH = 904;
    public const int EDRAFT_BAD = 905;
    public const int EDRAFT_APPLIED = 906;
    public const int EPART_NOT_FOUND = 907;
    public const int EPART_STYLE = 908;
}

public sealed class Ext4Exception : Exception
{
    public int Code { get; }

    public Ext4Exception(int code, string? operation = null)
        : base(Describe(code, operation))
    {
        Code = code;
    }

    public static string Describe(int code, string? operation = null)
    {
        string text = code switch
        {
            Errno.ENOENT => "File o cartella non trovati",
            Errno.EIO => "Errore di I/O sul disco",
            Errno.ENOMEM => "Memoria insufficiente",
            Errno.EACCES => "Accesso negato (servono i privilegi di amministratore?)",
            Errno.EBUSY => "Risorsa occupata",
            Errno.EEXIST => "Esiste già un elemento con questo nome",
            Errno.ENODEV => "Dispositivo non pronto o scollegato",
            Errno.ENOTDIR => "Non è una cartella",
            Errno.EISDIR => "È una cartella",
            Errno.EINVAL => "Parametro o nome non valido",
            Errno.EFBIG => "File troppo grande per questo filesystem",
            Errno.ENOSPC => "Spazio esaurito sul filesystem",
            Errno.EROFS => "Il filesystem è aperto in sola lettura",
            Errno.ENAMETOOLONG => "Nome troppo lungo (max 255 byte)",
            Errno.ENOTEMPTY => "La cartella non è vuota",
            Errno.ELOOP => "Troppi link simbolici annidati",
            Errno.EVOLUME_LOCK => "Il volume è in uso da un altro programma e non può essere bloccato per la scrittura",
            Errno.ENOTEXT => "Nessun filesystem ext2/ext3/ext4 trovato",
            Errno.EUNSUPP_RW => "Il filesystem usa funzionalità che non possono essere scritte in sicurezza",
            Errno.ENEEDS_FSCK => "Il filesystem contiene errori: controllalo e riparalo («Controlla e ripara») prima di scrivere",
            Errno.EDRAFT_MISMATCH => "La bozza è stata creata da un'altra sorgente, oppure la sorgente è cambiata dopo",
            Errno.EDRAFT_BAD => "Il file non è una bozza valida",
            Errno.EDRAFT_APPLIED => "La bozza è già stata applicata",
            Errno.EPART_NOT_FOUND => "Partizione non trovata nella tabella delle partizioni",
            Errno.EPART_STYLE => "Modifica non possibile con questo tipo di tabella delle partizioni (MBR/GPT)",
            _ => NativeMessage(code),
        };
        return operation is null ? text : $"{operation}: {text}";
    }

    private static string NativeMessage(int code)
    {
        try
        {
            IntPtr p = Ext4Native.e4_strerror(code);
            return (p == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(p)) ?? $"Errore {code}";
        }
        catch (DllNotFoundException)
        {
            return $"Errore {code}";
        }
    }

    public static void Check(int code, string? operation = null)
    {
        if (code != 0) throw new Ext4Exception(code, operation);
    }
}
