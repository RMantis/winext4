using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using WinExt4.Native;

namespace WinExt4.Core;

/// <summary>A partition (on a physical disk or inside an image file) that may hold ext2/3/4.</summary>
public sealed record Ext4Source
{
    public required string Device { get; init; }
    public ulong Offset { get; init; }
    public ulong Length { get; init; }
    public int DiskNumber { get; init; }
    public int PartitionNumber { get; init; }
    public int BusType { get; init; }
    public bool Removable { get; init; }
    public bool IsExt { get; init; }
    public int ExtVersion { get; init; }
    public string Label { get; init; } = "";
    public string Uuid { get; init; } = "";
    public string Model { get; init; } = "";
    public ulong DiskSize { get; init; }
    public string? ImagePath { get; init; }

    /// <summary>\.\PhysicalDriveN or the image file: what partition edits operate on.</summary>
    public string Container { get; init; } = "";
    public PartitionStyle PartitionStyle { get; init; }
    public int MbrType { get; init; }
    public string GptType { get; init; } = "";
    public string GptId { get; init; } = "";
    public string GptName { get; init; } = "";
    public ulong GptAttributes { get; init; }
    public GptStatus GptStatus { get; init; }
    public string DiskId { get; init; } = "";
    public uint SectorSize { get; init; }

    public string PartitionTypeText => PartitionStyle switch
    {
        PartitionStyle.Gpt => PartitionTypes.GptName(GptType),
        PartitionStyle.Mbr => PartitionTypes.MbrName(MbrType),
        _ => "nessuna tabella delle partizioni",
    };

    public bool IsImage => ImagePath is not null;
    public bool IsUsb => BusType == 7;     // BusTypeUsb
    public bool IsSd => BusType == 12;     // BusTypeSd
    public bool IsExternal => IsUsb || IsSd || Removable;

    /// <summary>Stable identity across rescans.</summary>
    public string Key => IsExt && Uuid.Length > 0 ? $"{Device}|{Offset}|{Uuid}" : $"{Device}|{Offset}";

    public string KindText => IsExt ? $"ext{ExtVersion}" : "altro";

    public string Title
    {
        get
        {
            string name = Label.Length > 0 ? Label : IsExt ? $"ext{ExtVersion} senza etichetta" : "Partizione";
            return name;
        }
    }

    public string Subtitle
    {
        get
        {
            string where = IsImage
                ? $"{System.IO.Path.GetFileName(ImagePath)}" + (PartitionNumber > 0 ? $" · partizione {PartitionNumber}" : "")
                : $"Disco {DiskNumber}" + (PartitionNumber > 0 ? $" · partizione {PartitionNumber}" : "");
            return $"{where} · {Format.Size(Length)} · {KindText}";
        }
    }

    public string GroupTitle
    {
        get
        {
            if (IsImage) return $"Immagine: {System.IO.Path.GetFileName(ImagePath)}";
            string bus = IsUsb ? "USB" : IsSd ? "SD" : Removable ? "Rimovibile" : "Interno";
            string model = Model.Length > 0 ? Model : $"Disco {DiskNumber}";
            return $"{model} ({bus}, {Format.Size(DiskSize)})";
        }
    }

    public string Icon => IsImage ? "" : IsUsb ? "" : IsSd ? "" : "";
}

public static unsafe class SourceScanner
{
    private sealed class Collector
    {
        public readonly List<Ext4Source> Items = new();
        public string? ImagePath;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnPart(IntPtr ctx, Ext4Native.Part* p)
    {
        var c = (Collector)GCHandle.FromIntPtr(ctx).Target!;
        c.Items.Add(new Ext4Source
        {
            Device = Ext4Native.Wide(p->device, 96),
            Offset = p->offset,
            Length = p->length,
            DiskNumber = p->disk_number,
            PartitionNumber = p->partition_number,
            BusType = p->bus_type,
            Removable = p->removable != 0,
            IsExt = p->is_ext != 0,
            ExtVersion = p->ext_version,
            Label = Ext4Native.Utf8(p->label, 20),
            Uuid = Ext4Native.Utf8(p->uuid, 40),
            Model = Ext4Native.Utf8(p->model, 128),
            DiskSize = p->disk_size,
            ImagePath = c.ImagePath,
            Container = Ext4Native.Wide(p->container, 96),
            PartitionStyle = (PartitionStyle)p->part_style,
            MbrType = p->mbr_type,
            GptType = Ext4Native.Utf8(p->gpt_type, 40),
            GptId = Ext4Native.Utf8(p->gpt_id, 40),
            GptName = Ext4Native.Utf8(p->gpt_name, 80),
            GptAttributes = p->gpt_attributes,
            GptStatus = (GptStatus)p->gpt_status,
            DiskId = Ext4Native.Utf8(p->disk_id, 40),
            SectorSize = p->sector_size,
        });
        return 0;
    }

    /// <summary>All partitions of all physical disks (requires administrator rights).</summary>
    public static List<Ext4Source> ScanDisks()
    {
        var c = new Collector();
        var h = GCHandle.Alloc(c);
        try
        {
            Ext4Exception.Check(Ext4Native.e4_enum_disks(&OnPart, GCHandle.ToIntPtr(h)), "Scansione dischi");
        }
        finally
        {
            h.Free();
        }
        return c.Items;
    }

    /// <summary>Filesystems inside an image file (bare filesystem, MBR or GPT disk image).</summary>
    public static List<Ext4Source> ScanImage(string path)
    {
        var c = new Collector { ImagePath = path };
        var h = GCHandle.Alloc(c);
        try
        {
            Ext4Exception.Check(Ext4Native.e4_enum_image(path, &OnPart, GCHandle.ToIntPtr(h)), "Analisi immagine");
        }
        finally
        {
            h.Free();
        }
        return c.Items;
    }
}

public static class Format
{
    public static string Size(ulong bytes)
    {
        string[] u = { "B", "KB", "MB", "GB", "TB", "PB" };
        double v = bytes;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return i == 0 ? $"{bytes} B" : $"{v:0.#} {u[i]}";
    }
}
