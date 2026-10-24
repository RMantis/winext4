using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using WinExt4.Core;

namespace WinExt4.UI;

/// <summary>Requested change of the partition table entry (applied by the main window after closing).</summary>
public sealed record PartitionEditRequest(string? GptType, int? MbrType, string? GptName, ulong? GptAttributes);

/// <summary>
/// Everything about a partition and its filesystem; tune2fs-style fields are
/// editable when the volume is open read-write, partition entries always (the
/// main window closes and reopens the volume to apply them).
/// </summary>
public sealed class PartitionPropertiesDialog : Window
{
    private readonly Ext4Source _source;
    private readonly Ext4Volume? _vol;
    private readonly TabControl _tabs = new() { Margin = new Thickness(12, 12, 12, 0) };
    private bool _fsChanged;

    public PartitionEditRequest? PartitionEdit { get; private set; }
    public bool FilesystemChanged => _fsChanged;

    private static Brush Muted => (Brush)Application.Current.FindResource("MutedBrush");

    public PartitionPropertiesDialog(Ext4Source source, Ext4Volume? vol)
    {
        _source = source;
        _vol = vol;
        Title = $"Proprietà — {source.Title}";
        Width = 720;
        Height = 640;
        MinWidth = 560;
        MinHeight = 420;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        FontFamily = new FontFamily("Segoe UI");
        FontSize = 13;

        var root = new DockPanel();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(12) };
        var close = new Button { Content = "Chiudi", MinWidth = 90, IsCancel = true, Style = (Style)Application.Current.FindResource("PrimaryButton") };
        close.Click += (_, _) => DialogResult = _fsChanged || PartitionEdit is not null;
        buttons.Children.Add(close);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(buttons);
        root.Children.Add(_tabs);
        Content = root;
        Build();
    }

    private void Build()
    {
        int selected = Math.Max(0, _tabs.SelectedIndex);
        _tabs.Items.Clear();
        var info = _vol?.GetInfo();
        var sup = _vol?.GetSuper();
        _tabs.Items.Add(new TabItem { Header = "Generale", Content = Scroll(GeneralTab(info, sup)) });
        if (_source.PartitionStyle != PartitionStyle.None && _source.PartitionNumber > 0)
            _tabs.Items.Add(new TabItem { Header = "Partizione", Content = Scroll(PartitionTab()) });
        if (info is not null && sup is not null)
        {
            _tabs.Items.Add(new TabItem { Header = "Filesystem", Content = Scroll(FilesystemTab(info, sup)) });
            _tabs.Items.Add(new TabItem { Header = "Avanzate", Content = AdvancedTab(info, sup) });
        }
        _tabs.SelectedIndex = Math.Min(selected, _tabs.Items.Count - 1);
    }

    private static ScrollViewer Scroll(UIElement e) =>
        new() { Content = e, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Padding = new Thickness(12) };

    // ------------------------------------------------------------------
    // helpers

    private sealed class Rows
    {
        public readonly Grid Grid = new() { Margin = new Thickness(0, 4, 0, 8) };

        public Rows(double labelWidth = 190)
        {
            Grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(labelWidth) });
            Grid.ColumnDefinitions.Add(new ColumnDefinition());
        }

        public void Add(string label, UIElement value, string? tooltip = null)
        {
            int r = Grid.RowDefinitions.Count;
            Grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = new TextBlock { Text = label, Foreground = Muted, Margin = new Thickness(0, 4, 8, 4), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            if (tooltip is not null) l.ToolTip = tooltip;
            Grid.SetRow(l, r);
            Grid.SetRow(value, r);
            Grid.SetColumn(value, 1);
            if (value is FrameworkElement fe && fe.Margin == default) fe.Margin = new Thickness(0, 3, 0, 3);
            Grid.Children.Add(l);
            Grid.Children.Add(value);
        }

        public void Text(string label, string value, string? tooltip = null) =>
            Add(label, new TextBox { Text = value, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, TextWrapping = TextWrapping.Wrap }, tooltip);
    }

    private static TextBlock Header(string text) =>
        new() { Text = text, FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 10, 0, 2) };

    private static TextBlock Note(string text) =>
        new() { Text = text, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 2, 0, 6) };

    private static string Pct(ulong part, ulong total) => total == 0 ? "" : $" ({100.0 * part / total:0.#}%)";

    private static string Date(DateTime? d) => d?.ToString("g") ?? "mai";

    // ------------------------------------------------------------------
    // Generale

    private UIElement GeneralTab(Ext4VolumeInfo? info, SuperInfo? sup)
    {
        var p = new StackPanel();
        p.Children.Add(new TextBlock { Text = info is null ? _source.Title : (info.Label.Length > 0 ? info.Label : "(senza etichetta)"), FontSize = 20, FontWeight = FontWeights.SemiBold });
        p.Children.Add(Note($"{(info?.Kind ?? _source.KindText)} · {_source.Subtitle}"));
        if (info is null || sup is null)
        {
            p.Children.Add(Note("Questa partizione non contiene un filesystem ext2/3/4: sono disponibili solo le informazioni della partizione."));
            return p;
        }

        ulong total = info.TotalBytes, free = info.FreeBytes, reserved = Math.Min(info.ReservedBytes, total);
        ulong rawFree = info.FreeBytes + reserved; // FreeBytes is what non-root users can use
        ulong used = total > rawFree ? total - rawFree : 0;
        var bar = new Grid { Height = 22, Margin = new Thickness(0, 10, 0, 6) };
        void Seg(ulong v, Color c)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(Math.Max(v, 1), GridUnitType.Star) });
            var rect = new Rectangle { Fill = new SolidColorBrush(c) };
            Grid.SetColumn(rect, bar.ColumnDefinitions.Count - 1);
            bar.Children.Add(rect);
        }
        Seg(used, Color.FromRgb(0x2B, 0x6C, 0xB0));
        Seg(reserved, Color.FromRgb(0xD9, 0x9A, 0x1E));
        Seg(free, Color.FromRgb(0xE2, 0xE4, 0xE8));
        p.Children.Add(new Border { Child = bar, CornerRadius = new CornerRadius(3), ClipToBounds = true });

        var rows = new Rows();
        rows.Text("Occupato", $"{Format.Size(used)}{Pct(used, total)}");
        rows.Text("Libero (utilizzabile)", $"{Format.Size(free)}{Pct(free, total)}");
        rows.Text("Riservato a root", $"{Format.Size(reserved)}{Pct(reserved, total)}");
        rows.Text("Capacità", $"{Format.Size(total)} ({total:N0} byte)");
        ulong inodesUsed = info.InodesCount - info.FreeInodes;
        rows.Text("Inode", $"{inodesUsed:N0} usati su {info.InodesCount:N0}{Pct(inodesUsed, info.InodesCount)} · {info.FreeInodes:N0} liberi");
        rows.Text("Cartelle", $"{sup.DirectoryCount:N0}");
        var countText = new TextBlock { Text = "—", VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
        var countBtn = new Button { Content = "Conta file", Padding = new Thickness(10, 3, 10, 3), Margin = new Thickness(0, 0, 10, 0) };
        countBtn.Click += async (_, _) =>
        {
            countBtn.IsEnabled = false;
            countText.Text = "Conteggio in corso…";
            var progress = new Progress<double>(f => countText.Text = $"Conteggio in corso… {f:P0}");
            try
            {
                var vol = _vol!;
                var c = await Task.Run(() => vol.CountInodes(CancellationToken.None, progress));
                countText.Text = $"{c.Files:N0} file ({Format.Size(c.FileBytes)}) · {c.Directories:N0} cartelle · {c.Symlinks:N0} link simbolici" +
                                 (c.Special > 0 ? $" · {c.Special:N0} speciali" : "");
            }
            catch (Ext4Exception ex)
            {
                countText.Text = ex.Message;
            }
            countBtn.IsEnabled = true;
        };
        var countRow = new StackPanel { Orientation = Orientation.Horizontal };
        countRow.Children.Add(countBtn);
        countRow.Children.Add(countText);
        AutomationProperties.SetAutomationId(countText, "CountResult");
        rows.Add("Numero di file", countRow);
        rows.Text("Stato", info.HasErrors ? "con errori (eseguire e2fsck)" : info.NeedsRecovery ? "journal da riprodurre"
            : info.ReadWrite ? "aperto in scrittura (viene segnato pulito alla chiusura)" : info.Clean ? "pulito" : "non smontato correttamente");
        rows.Text("Creato", Date(sup.Created));
        rows.Text("Ultimo montaggio", Date(info.LastMount) + (info.LastMountedOn.Length > 0 ? $" su {info.LastMountedOn}" : ""));
        rows.Text("Modalità", info.ReadWrite ? "lettura/scrittura" : "sola lettura");
        p.Children.Add(rows.Grid);
        return p;
    }

    // ------------------------------------------------------------------
    // Partizione

    private UIElement PartitionTab()
    {
        var s = _source;
        var p = new StackPanel();
        var rows = new Rows();
        rows.Text("Disco", s.IsImage ? s.ImagePath! : $"Disco {s.DiskNumber} · {s.Model} · {Format.Size(s.DiskSize)}");
        rows.Text("Tabella", s.PartitionStyle == PartitionStyle.Gpt ? "GPT" : "MBR");
        rows.Text(s.PartitionStyle == PartitionStyle.Gpt ? "GUID del disco" : "Firma MBR", s.DiskId);
        rows.Text("Numero partizione", s.PartitionNumber.ToString());
        if (s.IsImage) rows.Text("Inizio", $"{s.Offset:N0} byte (settore {s.Offset / Math.Max(1u, s.SectorSize):N0})");
        rows.Text("Dimensione", $"{Format.Size(s.Length)} ({s.Length:N0} byte)");
        rows.Text("Dimensione settore", $"{s.SectorSize} byte");
        rows.Text("Tipo attuale", s.PartitionTypeText);
        if (s.PartitionStyle == PartitionStyle.Gpt)
        {
            rows.Text("PARTUUID", s.GptId);
            string status = !s.GptStatus.HasFlag(GptStatus.Checked) ? "non verificata"
                : s.GptStatus.HasFlag(GptStatus.PrimaryOk | GptStatus.BackupOk | GptStatus.CopiesMatch) ? "integra (principale e backup coincidono)"
                : !s.GptStatus.HasFlag(GptStatus.PrimaryOk) ? "tabella principale danneggiata"
                : !s.GptStatus.HasFlag(GptStatus.BackupOk) ? "backup mancante o danneggiato"
                : "le due copie non coincidono";
            rows.Text("Stato GPT", status);
        }
        p.Children.Add(rows.Grid);

        p.Children.Add(Header("Modifica"));
        var edit = new Rows();
        TextBox? name = null;
        ComboBox type;
        var attrBoxes = new List<(CheckBox Box, ulong Bit)>();
        if (s.PartitionStyle == PartitionStyle.Gpt)
        {
            name = new TextBox { Text = s.GptName, MaxLength = 36, Padding = new Thickness(4, 2, 4, 2) };
            AutomationProperties.SetAutomationId(name, "PartName");
            edit.Add("Nome partizione", name);
            type = new ComboBox { IsEditable = true };
            foreach (var kv in PartitionTypes.KnownGpt.Where(k => k.Value != "inutilizzata").OrderBy(k => k.Value))
                type.Items.Add($"{kv.Value} — {kv.Key}");
            type.Text = $"{PartitionTypes.GptName(s.GptType)} — {s.GptType}";
            edit.Add("Tipo (GUID)", type);
            var attrs = new StackPanel();
            foreach (var (bit, label) in new[]
                     {
                         (PartitionTypes.AttrNoAutomount, "Nessuna lettera di unità in Windows (no automount)"),
                         (PartitionTypes.AttrHidden, "Nascosta per Windows"),
                         (PartitionTypes.AttrReadOnly, "Sola lettura per Windows"),
                         (1UL, "Obbligatoria per la piattaforma"),
                     })
            {
                var cb = new CheckBox { Content = label, IsChecked = (s.GptAttributes & bit) != 0, Margin = new Thickness(0, 2, 0, 2) };
                attrBoxes.Add((cb, bit));
                attrs.Children.Add(cb);
            }
            edit.Add("Attributi", attrs);
        }
        else
        {
            type = new ComboBox { IsEditable = true };
            foreach (int t in new[] { 0x83, 0x82, 0x8E, 0xFD, 0x07, 0x0C, 0x0B, 0xEF, 0xA5 })
                type.Items.Add(PartitionTypes.MbrName(t));
            type.Text = PartitionTypes.MbrName(s.MbrType);
            edit.Add("Tipo", type);
        }
        p.Children.Add(edit.Grid);
        p.Children.Add(Note("Si modifica solo la tabella delle partizioni, non i dati. " +
                            (s.IsImage ? "Le due copie della GPT e i CRC vengono riscritti." : "Su un disco Windows aggiorna entrambe le copie della GPT. Serve l'amministratore.") +
                            " Il volume verrà chiuso e riaperto."));
        var apply = new Button { Content = "Applica alla partizione", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = !s.IsInArchive };
        if (s.IsInArchive) p.Children.Add(Note("L'immagine è dentro un archivio: la tabella delle partizioni non si può modificare."));
        apply.Click += (_, _) =>
        {
            string? gptType = null, gptName = null;
            int? mbrType = null;
            ulong? attrs = null;
            if (s.PartitionStyle == PartitionStyle.Gpt)
            {
                var m = System.Text.RegularExpressions.Regex.Match(type.Text, "[0-9A-Fa-f]{8}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{4}-[0-9A-Fa-f]{12}");
                if (!m.Success) { MessageBox.Show(this, "Il tipo deve contenere un GUID (es. 0FC63DAF-8483-4772-8E79-3D69D8477DE4).", Title); return; }
                if (!string.Equals(m.Value, s.GptType, StringComparison.OrdinalIgnoreCase)) gptType = m.Value.ToUpperInvariant();
                if (name!.Text != s.GptName) gptName = name.Text;
                ulong a = s.GptAttributes;
                foreach (var (box, bit) in attrBoxes) a = box.IsChecked == true ? a | bit : a & ~bit;
                if (a != s.GptAttributes) attrs = a;
            }
            else
            {
                var m = System.Text.RegularExpressions.Regex.Match(type.Text, @"0x([0-9A-Fa-f]{1,2})");
                if (!m.Success) { MessageBox.Show(this, "Il tipo deve contenere un valore esadecimale (es. 0x83).", Title); return; }
                int t = Convert.ToInt32(m.Groups[1].Value, 16);
                if (t != s.MbrType) mbrType = t;
            }
            if (gptType is null && gptName is null && mbrType is null && attrs is null) return;
            var sb = new StringBuilder("Applicare queste modifiche alla tabella delle partizioni?\n\n");
            if (gptType is not null) sb.AppendLine($"• Tipo: {PartitionTypes.GptName(s.GptType)} → {PartitionTypes.GptName(gptType)}");
            if (mbrType is not null) sb.AppendLine($"• Tipo: {PartitionTypes.MbrName(s.MbrType)} → {PartitionTypes.MbrName(mbrType.Value)}");
            if (gptName is not null) sb.AppendLine($"• Nome: «{s.GptName}» → «{gptName}»");
            if (attrs is not null) sb.AppendLine($"• Attributi: {string.Join(", ", PartitionTypes.DescribeAttributes(attrs.Value).DefaultIfEmpty("nessuno"))}");
            if (MessageBox.Show(this, sb.ToString(), "Partizione", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            PartitionEdit = new PartitionEditRequest(gptType, mbrType, gptName, attrs);
            DialogResult = true;
        };
        p.Children.Add(apply);
        return p;
    }

    // ------------------------------------------------------------------
    // Filesystem

    private UIElement FilesystemTab(Ext4VolumeInfo info, SuperInfo sup)
    {
        var p = new StackPanel();
        var rows = new Rows();
        rows.Text("Tipo", $"{info.Kind} · revisione {sup.Revision} · creato da {sup.CreatorOsText}");
        rows.Text("Blocchi", $"{info.TotalBytes / info.BlockSize:N0} da {info.BlockSize} byte · {sup.GroupCount:N0} gruppi da {sup.BlocksPerGroup:N0} blocchi" +
                             (sup.FlexGroupSize > 0 ? $" · flex_bg {sup.FlexGroupSize}" : ""));
        rows.Text("Inode per gruppo", $"{sup.InodesPerGroup:N0}");
        rows.Text("Journal", info.HasJournal ? $"inode {sup.JournalInode} · {Format.Size(sup.JournalSize)}" + (info.NeedsRecovery ? " · da riprodurre" : "") : "assente");
        var features = new WrapPanel();
        foreach (var (name, text) in Ext4Features.Describe(info.FeatureCompat, info.FeatureIncompat, info.FeatureRoCompat))
            features.Children.Add(new Border
            {
                Background = (Brush)Application.Current.FindResource("PanelBrush"),
                CornerRadius = new CornerRadius(3),
                Padding = new Thickness(6, 1, 6, 1),
                Margin = new Thickness(0, 2, 4, 2),
                Child = new TextBlock { Text = name, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 },
                ToolTip = text,
            });
        rows.Add("Funzionalità", features);
        rows.Text("Montaggi", $"{sup.MountCount}" + (sup.MaxMountCount > 0 ? $" su un massimo di {sup.MaxMountCount} prima del controllo" : " (nessun limite)"));
        rows.Text("Ultimo controllo", Date(sup.LastCheck) + (sup.CheckIntervalSeconds > 0 ? $" · ogni {sup.CheckIntervalSeconds / 86400} giorni" : ""));
        rows.Text("Dati scritti in totale", sup.KilobytesWritten > 0 ? Format.Size(sup.KilobytesWritten * 1024) : "—");
        if (sup.ErrorCount > 0)
        {
            rows.Text("Errori registrati", sup.ErrorCount.ToString());
            if (sup.FirstError is not null) rows.Text("Primo errore", sup.FirstError);
            if (sup.LastError is not null) rows.Text("Ultimo errore", sup.LastError);
        }
        p.Children.Add(rows.Grid);

        p.Children.Add(Header("Modifica"));
        bool rw = info.ReadWrite;
        if (!rw) p.Children.Add(Note("Sblocca la scrittura nella finestra principale per modificare questi valori."));

        var edit = new Rows();
        var label = new TextBox { Text = info.Label, MaxLength = 16, IsEnabled = rw, Padding = new Thickness(4, 2, 4, 2) };
        AutomationProperties.SetAutomationId(label, "FsLabel");
        edit.Add("Etichetta", label, "e2label: massimo 16 byte");

        var uuidPanel = new DockPanel();
        var uuid = new TextBox { Text = info.Uuid, IsEnabled = rw && sup.UuidChange == UuidChange.Allowed, FontFamily = new FontFamily("Cascadia Mono, Consolas"), Padding = new Thickness(4, 2, 4, 2) };
        var gen = new Button { Content = "Genera nuovo", Padding = new Thickness(8, 1, 8, 1), Margin = new Thickness(6, 0, 0, 0), IsEnabled = uuid.IsEnabled };
        gen.Click += (_, _) => uuid.Text = Guid.NewGuid().ToString();
        DockPanel.SetDock(gen, Dock.Right);
        uuidPanel.Children.Add(gen);
        AutomationProperties.SetAutomationId(uuid, "FsUuid");
        uuidPanel.Children.Add(uuid);
        edit.Add("UUID", uuidPanel, "tune2fs -U");
        if (sup.UuidChange == UuidChange.NeedsCsumSeed)
        {
            var seedRow = new StackPanel();
            seedRow.Children.Add(Note("Con metadata_csum, cambiare l'UUID richiederebbe di riscrivere tutti i checksum. " +
                                      "Attivando metadata_csum_seed (kernel Linux 4.4 o successivi) diventa una modifica del solo superblocco."));
            var seed = new Button { Content = new TextBlock { Text = "Attiva metadata_csum_seed" }, Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = rw };
            seed.Click += (_, _) =>
            {
                if (MessageBox.Show(this, "Attivare metadata_csum_seed?\n\nIl filesystem non potrà più essere montato da kernel Linux più vecchi della 4.4 (2016).",
                        "Filesystem", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
                Try(() => _vol!.SetSuper(SuperField.EnableCsumSeed));
            };
            seedRow.Children.Add(seed);
            edit.Add("", seedRow);
        }
        else if (sup.UuidChange == UuidChange.Impossible)
        {
            edit.Add("", Note("L'UUID non si può cambiare: il filesystem usa stable_inodes."));
        }

        double reservedPct = info.TotalBytes == 0 ? 0 : 100.0 * info.ReservedBytes / info.TotalBytes;
        var reserved = new TextBox { Text = reservedPct.ToString("0.##"), Width = 70, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = rw };
        AutomationProperties.SetAutomationId(reserved, "FsReserved");
        edit.Add("Spazio riservato a root (%)", reserved, "tune2fs -m: su dischi di soli dati si può mettere 0");
        var resIds = new StackPanel { Orientation = Orientation.Horizontal };
        var resUid = new TextBox { Text = sup.ReservedUid.ToString(), Width = 70, IsEnabled = rw };
        var resGid = new TextBox { Text = sup.ReservedGid.ToString(), Width = 70, IsEnabled = rw };
        resIds.Children.Add(new TextBlock { Text = "uid ", VerticalAlignment = VerticalAlignment.Center });
        resIds.Children.Add(resUid);
        resIds.Children.Add(new TextBlock { Text = "   gid ", VerticalAlignment = VerticalAlignment.Center });
        resIds.Children.Add(resGid);
        edit.Add("Chi può usare il riservato", resIds, "tune2fs -u / -g");
        var maxMount = new TextBox { Text = sup.MaxMountCount.ToString(), Width = 70, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = rw };
        edit.Add("Montaggi prima del controllo", maxMount, "tune2fs -c: -1 o 0 = nessun controllo");
        var interval = new TextBox { Text = (sup.CheckIntervalSeconds / 86400).ToString(), Width = 70, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = rw };
        edit.Add("Giorni tra i controlli", interval, "tune2fs -i: 0 = nessun controllo periodico");
        var resetCount = new CheckBox { Content = $"Azzera il contatore dei montaggi (ora {sup.MountCount})", IsEnabled = rw };
        edit.Add("", resetCount);
        var errors = new ComboBox { IsEnabled = rw, Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        errors.Items.Add("continua");
        errors.Items.Add("rimonta in sola lettura");
        errors.Items.Add("panic");
        errors.SelectedIndex = (int)Math.Clamp(sup.ErrorsBehavior, 1, 3) - 1;
        edit.Add("In caso di errore", errors, "tune2fs -e");
        var opts = new StackPanel();
        var optBoxes = new List<(CheckBox, uint)>();
        foreach (var (bit, name, text) in SuperInfo.MountOptionBits)
        {
            var cb = new CheckBox { Content = new TextBlock { Text = $"{name} — {text}" }, IsChecked = (sup.DefaultMountOptions & bit) != 0, IsEnabled = rw, Margin = new Thickness(0, 1, 0, 1) };
            optBoxes.Add((cb, bit));
            opts.Children.Add(cb);
        }
        edit.Add("Opzioni di montaggio predefinite", opts, "tune2fs -o");
        var clearMounted = new CheckBox { Content = "Cancella l'ultimo punto di montaggio registrato", IsEnabled = rw && info.LastMountedOn.Length > 0 };
        edit.Add("", clearMounted);
        p.Children.Add(edit.Grid);

        var apply = new Button { Content = "Applica al filesystem", Padding = new Thickness(12, 4, 12, 4), HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = rw };
        apply.Click += (_, _) =>
        {
            var changes = new List<(string Text, Action Do)>();
            if (label.Text != info.Label)
            {
                if (Encoding.UTF8.GetByteCount(label.Text) > 16) { MessageBox.Show(this, "L'etichetta può avere al massimo 16 byte.", Title); return; }
                changes.Add(($"Etichetta: «{info.Label}» → «{label.Text}»", () => _vol!.SetSuper(SuperField.Label, text: label.Text)));
            }
            if (uuid.IsEnabled && !string.Equals(uuid.Text.Trim(), info.Uuid, StringComparison.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(uuid.Text.Trim(), out _)) { MessageBox.Show(this, "UUID non valido.", Title); return; }
                changes.Add(($"UUID: {info.Uuid} → {uuid.Text.Trim().ToLowerInvariant()} (aggiorna eventuali riferimenti in /etc/fstab, GRUB, initramfs)",
                    () => _vol!.SetSuper(SuperField.Uuid, text: uuid.Text.Trim().ToLowerInvariant())));
            }
            if (!double.TryParse(reserved.Text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double pct) || pct is < 0 or > 50)
            { MessageBox.Show(this, "La percentuale riservata deve essere tra 0 e 50.", Title); return; }
            if (Math.Abs(pct - reservedPct) > 0.005)
            {
                ulong blocks = (ulong)(info.TotalBytes / info.BlockSize * pct / 100.0);
                changes.Add(($"Spazio riservato: {reservedPct:0.##}% → {pct:0.##}% ({blocks:N0} blocchi)", () => _vol!.SetSuper(SuperField.ReservedBlocks, blocks)));
            }
            if (!uint.TryParse(resUid.Text, out uint ru) || !uint.TryParse(resGid.Text, out uint rg) || ru > 65535 || rg > 65535)
            { MessageBox.Show(this, "uid/gid riservati: numeri tra 0 e 65535.", Title); return; }
            if (ru != sup.ReservedUid) changes.Add(($"uid riservato: {sup.ReservedUid} → {ru}", () => _vol!.SetSuper(SuperField.ReservedUid, ru)));
            if (rg != sup.ReservedGid) changes.Add(($"gid riservato: {sup.ReservedGid} → {rg}", () => _vol!.SetSuper(SuperField.ReservedGid, rg)));
            if (!int.TryParse(maxMount.Text, out int mm) || mm < -1 || mm > 16000) { MessageBox.Show(this, "Montaggi massimi: da -1 a 16000.", Title); return; }
            if (mm != sup.MaxMountCount) changes.Add(($"Montaggi prima del controllo: {sup.MaxMountCount} → {mm}", () => _vol!.SetSuper(SuperField.MaxMountCount, unchecked((ulong)(uint)mm))));
            if (!uint.TryParse(interval.Text, out uint days) || days > 36500) { MessageBox.Show(this, "Giorni tra i controlli: numero non valido.", Title); return; }
            if (days * 86400 != sup.CheckIntervalSeconds) changes.Add(($"Giorni tra i controlli: {sup.CheckIntervalSeconds / 86400} → {days}", () => _vol!.SetSuper(SuperField.CheckInterval, days * 86400UL)));
            if (resetCount.IsChecked == true) changes.Add(("Azzera il contatore dei montaggi", () => _vol!.SetSuper(SuperField.MountCount, 0)));
            if (errors.SelectedIndex + 1 != sup.ErrorsBehavior) changes.Add(($"In caso di errore: {sup.ErrorsText} → {errors.SelectedItem}", () => _vol!.SetSuper(SuperField.Errors, (ulong)(errors.SelectedIndex + 1))));
            uint newOpts = sup.DefaultMountOptions;
            foreach (var (cb, bit) in optBoxes) newOpts = cb.IsChecked == true ? newOpts | bit : newOpts & ~bit;
            if (newOpts != sup.DefaultMountOptions) changes.Add(("Opzioni di montaggio predefinite", () => _vol!.SetSuper(SuperField.DefaultMountOptions, newOpts)));
            if (clearMounted.IsChecked == true) changes.Add(("Cancella l'ultimo punto di montaggio", () => _vol!.SetSuper(SuperField.LastMounted, text: "")));
            if (changes.Count == 0) return;
            if (MessageBox.Show(this, "Applicare queste modifiche al superblocco?\n\n" + string.Join("\n", changes.Select(c => "• " + c.Text)),
                    "Filesystem", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
            Try(() => { foreach (var c in changes) c.Do(); });
        };
        p.Children.Add(apply);
        return p;
    }

    private void Try(Action a)
    {
        try
        {
            a();
            _fsChanged = true;
            Build();
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Filesystem", MessageBoxButton.OK, MessageBoxImage.Warning);
            Build();
        }
    }

    // ------------------------------------------------------------------
    // Avanzate

    private UIElement AdvancedTab(Ext4VolumeInfo info, SuperInfo sup)
    {
        var sb = new StringBuilder();
        void L(string k, object? v) => sb.Append(k.PadRight(32)).Append(v).Append('\n');
        L("Filesystem volume name:", info.Label.Length > 0 ? info.Label : "<none>");
        L("Last mounted on:", info.LastMountedOn.Length > 0 ? info.LastMountedOn : "<not available>");
        L("Filesystem UUID:", info.Uuid);
        L("Filesystem revision #:", sup.Revision);
        L("Filesystem features:", string.Join(' ', Ext4Features.Describe(info.FeatureCompat, info.FeatureIncompat, info.FeatureRoCompat).Select(f => f.Name)));
        L("Default mount options:", string.Join(' ', SuperInfo.MountOptionBits.Where(b => (sup.DefaultMountOptions & b.Bit) != 0).Select(b => b.Name).DefaultIfEmpty("(none)")));
        if (sup.MountOptions.Length > 0) L("Mount options:", sup.MountOptions);
        L("Filesystem state:", info.HasErrors ? "with errors" : info.Clean ? "clean" : "not clean");
        L("Errors behavior:", sup.ErrorsText);
        L("Filesystem OS type:", sup.CreatorOsText);
        L("Inode count:", info.InodesCount);
        L("Block count:", info.TotalBytes / info.BlockSize);
        L("Reserved block count:", info.ReservedBytes / info.BlockSize);
        L("Free blocks (non-root):", info.FreeBytes / info.BlockSize);
        L("Free inodes:", info.FreeInodes);
        L("Block size:", info.BlockSize);
        L("Blocks per group:", sup.BlocksPerGroup);
        L("Inodes per group:", sup.InodesPerGroup);
        L("Group count:", sup.GroupCount);
        if (sup.FlexGroupSize > 0) L("Flex block group size:", sup.FlexGroupSize);
        L("Filesystem created:", Date(sup.Created));
        L("Last mount time:", Date(info.LastMount));
        L("Last write time:", Date(info.LastWrite));
        L("Mount count:", sup.MountCount);
        L("Maximum mount count:", sup.MaxMountCount);
        L("Last checked:", Date(sup.LastCheck));
        L("Check interval:", $"{sup.CheckIntervalSeconds} ({sup.CheckIntervalSeconds / 86400} days)");
        if (sup.KilobytesWritten > 0) L("Lifetime writes:", Format.Size(sup.KilobytesWritten * 1024));
        L("Reserved blocks uid:", sup.ReservedUid);
        L("Reserved blocks gid:", sup.ReservedGid);
        L("Directories:", sup.DirectoryCount);
        L("Journal inode:", sup.JournalInode);
        L("Journal size:", Format.Size(sup.JournalSize));
        L("Default directory hash seed:", sup.HashSeed);
        L("Checksum seed feature:", sup.HasCsumSeed ? "yes" : "no");
        if (sup.ErrorCount > 0)
        {
            L("FS Error count:", sup.ErrorCount);
            L("First error:", sup.FirstError);
            L("Last error:", sup.LastError);
        }
        if (_source.PartitionStyle != PartitionStyle.None)
        {
            sb.Append('\n');
            L("Partition table:", _source.PartitionStyle);
            L("Partition number:", _source.PartitionNumber);
            L("Partition type:", _source.PartitionTypeText);
            if (_source.GptId.Length > 0) L("PARTUUID:", _source.GptId);
            if (_source.GptName.Length > 0) L("Partition name:", _source.GptName);
            L("Partition attributes:", $"0x{_source.GptAttributes:X16}");
            L("Disk identifier:", _source.DiskId);
        }
        string text = sb.ToString();
        var panel = new DockPanel { Margin = new Thickness(12) };
        var copy = new Button { Content = "Copia tutto", Padding = new Thickness(10, 3, 10, 3), HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
        copy.Click += (_, _) => Clipboard.SetText(text);
        DockPanel.SetDock(copy, Dock.Bottom);
        panel.Children.Add(copy);
        panel.Children.Add(new TextBox
        {
            Text = text,
            IsReadOnly = true,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        });
        return panel;
    }
}
