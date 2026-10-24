using System.Text;
using WinExt4.Native;

namespace WinExt4.Core;

public enum PartitionStyle { None = 0, Mbr = 1, Gpt = 2 }

[Flags]
public enum GptStatus : uint
{
    None = 0,
    PrimaryOk = 0x01,
    BackupOk = 0x02,
    CopiesMatch = 0x04,
    Checked = 0x80,
}

/// <summary>Well-known partition types and their names.</summary>
public static class PartitionTypes
{
    public const string LinuxData = "0FC63DAF-8483-4772-8E79-3D69D8477DE4";
    public const int MbrLinux = 0x83;

    private static readonly Dictionary<string, string> Gpt = new(StringComparer.OrdinalIgnoreCase)
    {
        [LinuxData] = "Linux filesystem",
        ["4F68BCE3-E8CD-4DB1-96E7-FBCAF984B709"] = "Linux root (x86-64)",
        ["44479540-F297-41B2-9AF7-D131D5F0458A"] = "Linux root (x86)",
        ["B921B045-1DF0-41C3-AF44-4C6F280D3FAE"] = "Linux root (ARM64)",
        ["933AC7E1-2EB4-4F13-B844-0E14E2AEF915"] = "Linux /home",
        ["3B8F8425-20E0-4F3B-907F-1A25A76F98E8"] = "Linux /srv",
        ["BC13C2FF-59E6-4262-A352-B275FD6F7172"] = "Linux /boot (XBOOTLDR)",
        ["0657FD6D-A4AB-43C4-84E5-0933C84B4F4F"] = "Linux swap",
        ["E6D6D379-F507-44C2-A23C-238F2A3DF928"] = "Linux LVM",
        ["A19D880F-05FC-4D3B-A006-743F0F84911E"] = "Linux RAID",
        ["CA7D7CCB-63ED-4C53-861C-1742536059CC"] = "Linux LUKS",
        ["C12A7328-F81F-11D2-BA4B-00A0C93EC93B"] = "EFI System",
        ["21686148-6449-6E6F-744E-656564454649"] = "BIOS boot",
        ["EBD0A0A2-B9E5-4433-87C0-68B6B72699C7"] = "Microsoft basic data",
        ["E3C9E316-0B5C-4DB8-817D-F92DF00215AE"] = "Microsoft reserved",
        ["DE94BBA4-06D1-4D40-A16A-BFD50179D6AC"] = "Windows recovery",
        ["5808C8AA-7E8F-42E0-85D2-E1E90434CFB3"] = "Windows LDM metadata",
        ["AF9B60A0-1431-4F62-BC68-3311714A69AD"] = "Windows LDM data",
        ["48465300-0000-11AA-AA11-00306543ECAC"] = "Apple HFS+",
        ["7C3457EF-0000-11AA-AA11-00306543ECAC"] = "Apple APFS",
        ["516E7CB6-6ECF-11D6-8FF8-00022D09712B"] = "FreeBSD",
        ["00000000-0000-0000-0000-000000000000"] = "inutilizzata",
    };

    private static readonly Dictionary<int, string> Mbr = new()
    {
        [0x83] = "Linux",
        [0x82] = "Linux swap",
        [0x8E] = "Linux LVM",
        [0xFD] = "Linux RAID",
        [0x07] = "NTFS / exFAT",
        [0x0B] = "FAT32",
        [0x0C] = "FAT32 (LBA)",
        [0x06] = "FAT16",
        [0x0E] = "FAT16 (LBA)",
        [0x01] = "FAT12",
        [0xEF] = "EFI System",
        [0xEE] = "GPT protettiva",
        [0x05] = "Estesa",
        [0x0F] = "Estesa (LBA)",
        [0xA5] = "FreeBSD",
        [0x27] = "Windows recovery",
    };

    public static string GptName(string guid) =>
        Gpt.TryGetValue(guid, out var n) ? n : $"sconosciuto ({guid})";

    public static string MbrName(int type) =>
        Mbr.TryGetValue(type, out var n) ? $"{n} (0x{type:X2})" : $"0x{type:X2}";

    public static IReadOnlyDictionary<string, string> KnownGpt => Gpt;

    /// <summary>Types under which an ext filesystem is expected.</summary>
    public static bool IsLinuxFsType(Ext4Source s) => s.PartitionStyle switch
    {
        PartitionStyle.Gpt => Gpt.TryGetValue(s.GptType, out var n) && n.StartsWith("Linux", StringComparison.Ordinal)
                              && n is not ("Linux swap" or "Linux LVM" or "Linux RAID" or "Linux LUKS"),
        PartitionStyle.Mbr => s.MbrType == MbrLinux,
        _ => true,
    };

    /// <summary>GPT attribute bits with names (bit 63 etc. are the Microsoft basic data ones).</summary>
    public static IEnumerable<string> DescribeAttributes(ulong a)
    {
        if ((a & 1) != 0) yield return "obbligatoria (platform required)";
        if ((a & 2) != 0) yield return "ignorata dal firmware EFI";
        if ((a & 4) != 0) yield return "avviabile (legacy BIOS)";
        if ((a & (1UL << 60)) != 0) yield return "sola lettura";
        if ((a & (1UL << 61)) != 0) yield return "shadow copy";
        if ((a & (1UL << 62)) != 0) yield return "nascosta";
        if ((a & (1UL << 63)) != 0) yield return "nessuna lettera di unità (no automount)";
    }

    public const ulong AttrNoAutomount = 1UL << 63;
    public const ulong AttrHidden = 1UL << 62;
    public const ulong AttrReadOnly = 1UL << 60;

    /// <summary>Applies a change to the partition table (type, name, attributes).</summary>
    public static unsafe void Edit(Ext4Source s, string? gptType = null, int? mbrType = null, string? gptName = null,
        ulong? gptAttributes = null)
    {
        var e = new Ext4Native.PartEdit();
        if (!string.IsNullOrEmpty(gptType)) { e.mask |= Ext4Native.EditGptType; Copy(gptType, e.gpt_type, 40); }
        if (mbrType is { } t) { e.mask |= Ext4Native.EditMbrType; e.mbr_type = t; }
        if (gptName is not null) { e.mask |= Ext4Native.EditGptName; Copy(gptName, e.gpt_name, 80); }
        if (gptAttributes is { } a) { e.mask |= Ext4Native.EditGptAttributes; e.gpt_attributes = a; }
        Ext4Exception.Check(Ext4Native.e4_edit_partition(s.Container, s.PartitionNumber, in e), "Modifica della partizione");
    }

    private static unsafe void Copy(string text, byte* dst, int size)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        if (bytes.Length >= size) throw new Ext4Exception(Errno.ENAMETOOLONG);
        for (int i = 0; i < bytes.Length; i++) dst[i] = bytes[i];
        dst[bytes.Length] = 0;
    }
}

/// <summary>Consistency checks between partition table and filesystem.</summary>
public static class PartitionChecks
{
    public sealed record Finding(string Text, bool CanFixType);

    public static List<Finding> Analyze(Ext4Source s, IReadOnlyCollection<Ext4Source> all)
    {
        var list = new List<Finding>();
        if (s.IsExt && s.PartitionStyle != PartitionStyle.None && !PartitionTypes.IsLinuxFsType(s))
            list.Add(new Finding(
                $"La partizione contiene ext{s.ExtVersion} ma è di tipo «{s.PartitionTypeText}»: Windows potrebbe assegnarle una lettera o proporre di formattarla.",
                CanFixType: s.PartitionNumber > 0));
        if (!s.IsExt && s.PartitionStyle == PartitionStyle.Gpt &&
            string.Equals(s.GptType, PartitionTypes.LinuxData, StringComparison.OrdinalIgnoreCase))
            list.Add(new Finding("Partizione di tipo Linux che non contiene ext (forse btrfs, XFS o vuota).", false));
        if (!s.IsExt && s.PartitionStyle == PartitionStyle.Mbr && s.MbrType == PartitionTypes.MbrLinux)
            list.Add(new Finding("Partizione di tipo Linux (0x83) che non contiene ext (forse btrfs, XFS o vuota).", false));

        if (s.PartitionStyle == PartitionStyle.Gpt && s.GptStatus.HasFlag(GptStatus.Checked))
        {
            if (!s.GptStatus.HasFlag(GptStatus.PrimaryOk))
                list.Add(new Finding("La tabella GPT principale è danneggiata (CRC non valido): Windows e Linux usano la copia di backup.", false));
            else if (!s.GptStatus.HasFlag(GptStatus.BackupOk))
                list.Add(new Finding("La copia di backup della tabella GPT (in fondo al disco) manca o è danneggiata.", false));
            else if (!s.GptStatus.HasFlag(GptStatus.CopiesMatch))
                list.Add(new Finding("Le due copie della tabella GPT non coincidono.", false));
        }

        if (s.IsExt && s.Uuid.Length > 0 &&
            all.Any(o => !ReferenceEquals(o, s) && o.IsExt && o.Uuid == s.Uuid && o.Key != s.Key && !SameDisk(o, s)))
            list.Add(new Finding($"Un altro filesystem collegato ha lo stesso UUID ({s.Uuid}): è probabilmente una copia. Su Linux può causare montaggi sbagliati.", false));
        if (s.GptId.Length > 0 && all.Any(o => !ReferenceEquals(o, s) && o.GptId == s.GptId && o.Key != s.Key))
            list.Add(new Finding($"Un'altra partizione collegata ha lo stesso PARTUUID ({s.GptId}).", false));
        return list;
    }

    private static bool SameDisk(Ext4Source a, Ext4Source b) => a.Container == b.Container && a.Offset == b.Offset && a.PartitionNumber == b.PartitionNumber;
}
