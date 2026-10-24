using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using WinExt4.Core;

namespace WinExt4.UI;

internal static class DialogUi
{
    public static Brush Muted => (Brush)Application.Current.FindResource("MutedBrush");

    public static TextBlock Label(string text, bool bold = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        Margin = new Thickness(0, 2, 0, 2),
    };

    public static TextBox Mono(string text) => new()
    {
        Text = text,
        IsReadOnly = true,
        FontFamily = new FontFamily("Cascadia Mono, Consolas"),
        BorderThickness = new Thickness(0),
        Background = Brushes.Transparent,
        TextWrapping = TextWrapping.Wrap,
    };
}

/// <summary>MD5/SHA-256 of a disk, partition or image, with copy, compare and save.</summary>
public sealed class HashResultDialog : SimpleDialog
{
    public HashResultDialog(string what, HashResult r, string? defaultSidecarName) : base("Hash", 620)
    {
        Body.Children.Add(DialogUi.Label(what, bold: true));
        Body.Children.Add(new TextBlock { Text = $"{Format.Size(r.Bytes)} ({r.Bytes:N0} byte)", Foreground = DialogUi.Muted, Margin = new Thickness(0, 0, 0, 8) });
        AddHash("MD5", r.Md5);
        AddHash("SHA-256", r.Sha256);

        Body.Children.Add(DialogUi.Label("Confronta con un hash atteso (incolla qui):"));
        var expected = new TextBox { FontFamily = new FontFamily("Cascadia Mono, Consolas"), Padding = new Thickness(4, 2, 4, 2) };
        var verdict = new TextBlock { Margin = new Thickness(0, 4, 0, 0), FontWeight = FontWeights.SemiBold };
        expected.TextChanged += (_, _) =>
        {
            var (h, _) = Sidecar.ParseLine(expected.Text);
            h = h?.ToLowerInvariant();
            verdict.Text = string.IsNullOrEmpty(h) ? "" : h == r.Md5 || h == r.Sha256 ? "✔ Corrisponde" : "✘ Non corrisponde";
            verdict.Foreground = verdict.Text.StartsWith('✔') ? Brushes.DarkGreen : Brushes.DarkRed;
        };
        Body.Children.Add(expected);
        Body.Children.Add(verdict);

        if (defaultSidecarName is not null)
            AddButton("Salva .md5 e .sha256…", false, (_, _) =>
            {
                var d = new SaveFileDialog { FileName = defaultSidecarName, Title = "Nome dell'immagine a cui si riferiscono gli hash", Filter = "Immagine (*.img)|*.img|Tutti i file|*.*" };
                if (d.ShowDialog(this) == true) Sidecar.Write(d.FileName, r);
            });
        AddButton("Chiudi", true, (_, _) => DialogResult = true).IsCancel = true;
    }

    private void AddHash(string name, string value)
    {
        var row = new DockPanel { Margin = new Thickness(0, 2, 0, 6) };
        var copy = new Button { Content = "Copia", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(8, 0, 0, 0) };
        copy.Click += (_, _) => Clipboard.SetText(value);
        DockPanel.SetDock(copy, Dock.Right);
        row.Children.Add(copy);
        row.Children.Add(new TextBlock { Text = name, Width = 70, Foreground = DialogUi.Muted, VerticalAlignment = VerticalAlignment.Center });
        row.Children.Add(DialogUi.Mono(value));
        Body.Children.Add(row);
    }
}

/// <summary>Shows a block of text (md5sum list, verification report) with copy and save.</summary>
public sealed class TextResultDialog : SimpleDialog
{
    public TextResultDialog(string title, string header, string text, string? saveName) : base(title, 720)
    {
        Body.Children.Add(DialogUi.Label(header, bold: true));
        var box = DialogUi.Mono(text);
        box.TextWrapping = TextWrapping.NoWrap;
        box.HorizontalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
        box.MaxHeight = 420;
        box.Margin = new Thickness(0, 6, 0, 0);
        Body.Children.Add(box);
        AddButton("Copia", false, (_, _) => Clipboard.SetText(text));
        if (saveName is not null)
            AddButton("Salva…", false, (_, _) =>
            {
                var d = new SaveFileDialog { FileName = saveName, Filter = "Tutti i file|*.*" };
                if (d.ShowDialog(this) == true) File.WriteAllText(d.FileName, text.Replace("\r\n", "\n"));
            });
        AddButton("Chiudi", true, (_, _) => DialogResult = true).IsCancel = true;
    }
}

/// <summary>Disk/partition → image options.</summary>
public sealed class ExportImageDialog : SimpleDialog
{
    private readonly TextBox _path;
    private readonly CheckBox _sparse, _verify;
    private readonly ComboBox _format;

    public string ImagePath => _path.Text.Trim();
    public bool Sparse => _sparse.IsChecked == true;
    public bool Verify => _verify.IsChecked == true;
    public ImageFormat SelectedFormat => (ImageFormat)_format.SelectedIndex;

    public ExportImageDialog(RawTarget t, ulong size, string defaultName) : base("Esporta come immagine", 600)
    {
        Body.Children.Add(DialogUi.Label(t.Description, bold: true));
        Body.Children.Add(new TextBlock { Text = Format.Size(size), Foreground = DialogUi.Muted, Margin = new Thickness(0, 0, 0, 8) });
        Body.Children.Add(DialogUi.Label("File immagine:"));
        var row = new DockPanel();
        var browse = new Button { Content = "Sfoglia…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        _path = new TextBox
        {
            Text = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), defaultName),
            Padding = new Thickness(4, 2, 4, 2),
        };
        browse.Click += (_, _) =>
        {
            var d = new SaveFileDialog { FileName = Path.GetFileName(_path.Text), InitialDirectory = Path.GetDirectoryName(_path.Text), Filter = "Immagine disco (*.img)|*.img|Tutti i file|*.*" };
            if (d.ShowDialog(this) == true) _path.Text = d.FileName;
        };
        row.Children.Add(browse);
        row.Children.Add(_path);
        Body.Children.Add(row);
        Body.Children.Add(DialogUi.Label("Formato:"));
        _format = new ComboBox { Margin = new Thickness(0, 0, 0, 4) };
        foreach (ImageFormat f in Enum.GetValues<ImageFormat>()) _format.Items.Add(ImageFormats.Description(f));
        _format.SelectedIndex = 0;
        _format.SelectionChanged += (_, _) =>
        {
            // swap the extension to match the format
            string p = _path.Text.Trim();
            foreach (var ext in new[] { ".img.gz", ".img.zst", ".img.xz", ".vhd", ".img" })
                if (p.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) { p = p[..^ext.Length]; break; }
            _path.Text = p + ImageFormats.Extension(SelectedFormat);
            _sparse!.IsEnabled = SelectedFormat == ImageFormat.Raw;
        };
        Body.Children.Add(_format);
        _sparse = new CheckBox { Content = DialogUi.Label("File sparso: le zone a zero non occupano spazio su NTFS"), IsChecked = true, Margin = new Thickness(0, 8, 0, 2) };
        _verify = new CheckBox { Content = DialogUi.Label("Verifica rileggendo l'immagine dopo averla scritta (più lento)"), Margin = new Thickness(0, 2, 0, 2) };
        Body.Children.Add(_sparse);
        Body.Children.Add(_verify);
        Body.Children.Add(new TextBlock
        {
            Text = "Accanto all'immagine vengono creati i file .md5 e .sha256 (formato md5sum/sha256sum, verificabili anche su Linux). " +
                   "Per i formati compressi ce ne sono due coppie: una per il file compresso e una (disco.img.sha256) per i dati decompressi. " +
                   "Suggerimento: «Azzera lo spazio libero» prima dell'esportazione rende le immagini compresse molto più piccole.",
            TextWrapping = TextWrapping.Wrap,
            Foreground = DialogUi.Muted,
            Margin = new Thickness(0, 8, 0, 0),
        });
        AddButton("Esporta", true, (_, _) =>
        {
            if (ImagePath.Length == 0) return;
            if (File.Exists(ImagePath) && MessageBox.Show(this, $"{ImagePath} esiste già. Sovrascriverlo?", Title,
                    MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
                return;
            DialogResult = true;
        });
        AddButton("Annulla", false, (_, _) => DialogResult = false).IsCancel = true;
    }
}

/// <summary>Image → disk/partition, with every safety check spelled out.</summary>
public sealed class WriteImageDialog : SimpleDialog
{
    private readonly RawTarget _target;
    private readonly TextBox _path;
    private readonly TextBlock _checks;
    private readonly CheckBox _preVerify, _readback, _confirm;
    private readonly Button _write;
    private readonly bool _systemDisk;

    public string ImagePath => _path.Text.Trim();
    public Sidecar.Entry? Expected { get; private set; }
    public bool PreVerify => _preVerify.IsChecked == true;
    public bool Readback => _readback.IsChecked == true;

    public WriteImageDialog(RawTarget target, string? model) : base("Scrivi immagine", 640)
    {
        _target = target;
        _systemDisk = target.DiskNumber >= 0 && Imaging.SystemDisks().Contains(target.DiskNumber);
        Body.Children.Add(DialogUi.Label("Destinazione: " + target.Description, bold: true));
        Body.Children.Add(new TextBlock { Text = $"{Format.Size(target.Length)}{(model is null ? "" : " · " + model)}", Foreground = DialogUi.Muted, Margin = new Thickness(0, 0, 0, 8) });

        Body.Children.Add(DialogUi.Label("Immagine da scrivere:"));
        var row = new DockPanel();
        var browse = new Button { Content = "Sfoglia…", Padding = new Thickness(8, 2, 8, 2), Margin = new Thickness(6, 0, 0, 0) };
        DockPanel.SetDock(browse, Dock.Right);
        _path = new TextBox { Padding = new Thickness(4, 2, 4, 2) };
        browse.Click += (_, _) =>
        {
            var d = new OpenFileDialog { Filter = "Immagini disco (*.img;*.raw;*.bin;*.dd;*.iso;*.gz;*.zst;*.xz;*.vhd)|*.img;*.raw;*.bin;*.dd;*.iso;*.gz;*.zst;*.xz;*.vhd|Tutti i file|*.*" };
            if (d.ShowDialog(this) == true) _path.Text = d.FileName;
        };
        _path.TextChanged += (_, _) => Evaluate();
        row.Children.Add(browse);
        row.Children.Add(_path);
        Body.Children.Add(row);

        _checks = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 4) };
        Body.Children.Add(_checks);
        _preVerify = new CheckBox { Content = DialogUi.Label("Verifica l'immagine con il file .md5/.sha256 prima di scrivere"), IsChecked = true };
        _readback = new CheckBox { Content = DialogUi.Label("Dopo la scrittura rileggi il disco e confronta"), IsChecked = true };
        _confirm = new CheckBox { Margin = new Thickness(0, 10, 0, 0) };
        _confirm.Checked += (_, _) => Evaluate();
        _confirm.Unchecked += (_, _) => Evaluate();
        Body.Children.Add(_preVerify);
        Body.Children.Add(_readback);
        Body.Children.Add(_confirm);

        _write = AddButton("Scrivi", true, (_, _) => DialogResult = true);
        AddButton("Annulla", false, (_, _) => DialogResult = false).IsCancel = true;
        Evaluate();
    }

    private void Evaluate()
    {
        var lines = new List<string>();
        bool ok = true;
        if (_systemDisk)
        {
            lines.Add("✘ È il disco da cui gira Windows: la scrittura non è consentita.");
            ok = false;
        }
        Expected = null;
        if (File.Exists(ImagePath) && ImageFormats.Detect(ImagePath) is var fmt && fmt != ImageFormat.Raw)
        {
            lines.Add($"• Immagine {ImageFormats.Description(fmt).Split(' ')[0]}: prima di scrivere viene decompressa una volta per verificarne dimensione" +
                      " e hash; se non entra nella destinazione non viene scritto niente.");
            Expected = Sidecar.Find(ImagePath);
            lines.Add(Expected is null
                ? "• Nessun file .md5/.sha256 trovato."
                : $"✔ Trovato {Expected.File} ({(Expected.AppliesToRaw ? "dati decompressi" : "file compresso")}): verrà verificato.");
        }
        else if (File.Exists(ImagePath))
        {
            ulong size = (ulong)new FileInfo(ImagePath).Length;
            if (_target.Length > 0 && size > _target.Length)
            {
                lines.Add($"✘ L'immagine ({Format.Size(size)}) è più grande della destinazione ({Format.Size(_target.Length)}).");
                ok = false;
            }
            else
            {
                lines.Add($"✔ Dimensione immagine: {Format.Size(size)}" + (_target.Length > size ? $" (lo spazio restante, {Format.Size(_target.Length - size)}, non viene toccato)" : ""));
            }
            Expected = Sidecar.Find(ImagePath);
            lines.Add(Expected is null
                ? "• Nessun file .md5/.sha256 accanto all'immagine: verrà solo confrontata la rilettura."
                : $"✔ Trovato {Expected.File}: l'immagine verrà verificata ({Expected.Algorithm.ToUpperInvariant()}).");
        }
        else
        {
            ok = false;
        }
        if (_target.DiskNumber >= 0 && _target.Offset == 0 && _target.Path.Contains("PhysicalDrive", StringComparison.OrdinalIgnoreCase))
        {
            var letters = Imaging.LettersOnDisk(_target.DiskNumber);
            if (letters.Count > 0)
                lines.Add($"• Le unità {string.Join(", ", letters)} di questo disco verranno smontate.");
            lines.Add("• Tutte le partizioni del disco verranno sostituite da quelle dell'immagine.");
        }
        _checks.Text = string.Join("\n", lines);
        _preVerify.IsEnabled = Expected is not null;
        _confirm.Content = DialogUi.Label($"Ho capito che il contenuto di «{_target.Description}» verrà sovrascritto e non si potrà recuperare.");
        _write.IsEnabled = ok && _confirm.IsChecked == true;
    }
}
