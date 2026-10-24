using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using Microsoft.Win32;
using WinExt4.Core;
using WinExt4.Mount;

namespace WinExt4.UI;

public partial class MainWindow : Window
{
    public ObservableCollection<SourceItem> Sources { get; } = new();

    private readonly AppSettings _settings = AppSettings.Load();
    private readonly List<Ext4Source> _imageSources = new();
    private readonly HashSet<string> _knownKeys = new();
    private readonly DispatcherTimer _rescanTimer;
    private readonly TempCopies _temp = new();
    private readonly Dictionary<string, Core.Archives.ArchiveContents> _archives = new(StringComparer.OrdinalIgnoreCase);
    private bool _writeBackAlways;
    private bool _askingWriteBack;
    private readonly PreviewView _preview = new();
    private readonly DispatcherTimer _previewTimer;
    private CancellationTokenSource? _previewCts;

    private Ext4Volume? _vol;
    private SourceItem? _current;
    private MountSession? _mount;
    private string _cwdPath = "/";
    private uint _cwdIno = Ext4Volume.RootIno;
    private List<EntryItem> _entries = new();
    private string _sortColumn = "Nome";
    private bool _sortDescending;
    private CancellationTokenSource? _operation;
    private Ext4Source? _pendingNewDisk;
    private bool _scanning;
    private bool _suppressSelection;
    private bool _firstScan = true;
    private bool _closeRequested;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _trayHintShown;

    public MainWindow()
    {
        InitializeComponent();
        DataContext = this;
        ChkAllPartitions.IsChecked = _settings.ShowAllPartitions;
        _rescanTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _rescanTimer.Tick += (_, _) => { _rescanTimer.Stop(); _ = RescanAsync(); };
        PreviewHost.Child = _preview;
        _previewTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
        _previewTimer.Tick += (_, _) => { _previewTimer.Stop(); _ = LoadPreviewAsync(); };
        _temp.Modified += copy => Dispatcher.BeginInvoke(() => OnTempCopyModified(copy));
        SourceInitialized += OnSourceInitialized;
        Closing += OnClosing;
        UpdateUi();
    }

    // ==================================================================
    // startup, device notifications, drag & drop

    /// <summary>Runs hidden with a tray icon (started at logon) until a disk shows up.</summary>
    public void EnterBackgroundMode(TrayIcon tray)
    {
        _tray = tray;
        new WindowInteropHelper(this).EnsureHandle(); // device notifications need a window
    }

    public void ShowFromBackground()
    {
        if (!IsVisible) Show();
        BringToFront();
    }

    public void ExitFromTray()
    {
        _exiting = true;
        Close();
    }

    private async void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(hwnd)!.AddHook(WndProc);
        RegisterDiskNotifications(hwnd);
        EnableElevatedDrop(hwnd);
        SetStatus($"Pronto · motore {Ext4Volume.Version}");
        await RescanAsync();
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_DEVICECHANGE = 0x0219, WM_DROPFILES = 0x0233;
        const int DBT_DEVICEARRIVAL = 0x8000, DBT_DEVICEREMOVECOMPLETE = 0x8004, DBT_DEVNODES_CHANGED = 0x0007;

        if (msg == WM_DEVICECHANGE)
        {
            int ev = wParam.ToInt32();
            if (ev is DBT_DEVICEARRIVAL or DBT_DEVICEREMOVECOMPLETE or DBT_DEVNODES_CHANGED)
            {
                _rescanTimer.Stop();
                _rescanTimer.Start();
            }
        }
        else if (msg == WM_DROPFILES)
        {
            var files = Interop.DroppedFiles(wParam);
            handled = true;
            Dispatcher.BeginInvoke(() => OnFilesDropped(files));
        }
        return IntPtr.Zero;
    }

    private static void RegisterDiskNotifications(IntPtr hwnd)
    {
        var filter = new Interop.DEV_BROADCAST_DEVICEINTERFACE
        {
            dbcc_size = Marshal.SizeOf<Interop.DEV_BROADCAST_DEVICEINTERFACE>(),
            dbcc_devicetype = 5, // DBT_DEVTYP_DEVICEINTERFACE
            dbcc_classguid = new Guid("53F56307-B6BF-11D0-94F2-00A0C91EFB8B"), // GUID_DEVINTERFACE_DISK
        };
        Interop.RegisterDeviceNotification(hwnd, ref filter, 0);
    }

    /// <summary>
    /// We run elevated, so Explorer (not elevated) cannot use OLE drag &amp; drop
    /// on us. The classic WM_DROPFILES path works once the messages are allowed.
    /// </summary>
    private static void EnableElevatedDrop(IntPtr hwnd)
    {
        Interop.ChangeWindowMessageFilterEx(hwnd, 0x0233, 1, IntPtr.Zero); // WM_DROPFILES
        Interop.ChangeWindowMessageFilterEx(hwnd, 0x004A, 1, IntPtr.Zero); // WM_COPYDATA
        Interop.ChangeWindowMessageFilterEx(hwnd, 0x0049, 1, IntPtr.Zero); // WM_COPYGLOBALDATA
        Interop.DragAcceptFiles(hwnd, true);
    }

    private async void OnFilesDropped(string[] files)
    {
        if (files.Length == 0) return;
        bool allImages = files.All(f => File.Exists(f) && IsImageFile(f));
        if (allImages && (_vol is null || !_vol.ReadWrite))
        {
            foreach (var f in files) OpenImage(f);
            return;
        }
        if (_vol is null) return;
        if (!_vol.ReadWrite)
        {
            SetStatus("Per copiare file nel disco ext sblocca prima la scrittura.");
            return;
        }
        await RunTransferAsync("Importazione", t => t.Import(files, _cwdIno));
    }

    private static bool IsImageFile(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".img" or ".raw" or ".bin" or ".dd" or ".ext4" or ".ext3"
            or ".ext2" or ".iso" or ".gz" or ".tgz" or ".xz" or ".zst" or ".zip" or ".tar";

    // ==================================================================
    // sources

    private async Task RescanAsync()
    {
        if (_scanning) return;
        _scanning = true;
        try
        {
            List<Ext4Source> disks;
            string? error = null;
            try
            {
                disks = await Task.Run(SourceScanner.ScanDisks);
            }
            catch (Ext4Exception ex)
            {
                disks = new();
                error = ex.Code == Errno.EACCES
                    ? "Per leggere i dischi servono i privilegi di amministratore: riavvia WinExt4 come amministratore."
                    : ex.Message;
            }
            catch (DllNotFoundException)
            {
                disks = new();
                error = "ext4core.dll non trovata accanto a WinExt4.exe.";
            }

            // While open read-write the volume is locked and its superblock may
            // not be readable through the disk: keep our own description.
            if (_current is not null && !_current.Source.IsImage)
            {
                int i = disks.FindIndex(d => d.Device == _current.Source.Device);
                if (i >= 0) disks[i] = _current.Source;
                else if (_vol is not null) HandleDeviceRemoved();
            }

            var all = disks.Concat(_imageSources).ToList();
            ApplySources(all);

            var newExternal = all.Where(s => s.IsExt && !s.IsImage && !_knownKeys.Contains(s.Key)).ToList();
            foreach (var s in all) _knownKeys.Add(s.Key);

            if (_firstScan)
            {
                _firstScan = false;
                var first = all.FirstOrDefault(s => s.IsExt && s.IsExternal);
                if (first is not null && _vol is null && _settings.AutoOpenOnInsert && _tray is null)
                    SelectAndOpen(first);
            }
            else if (newExternal.Count > 0)
            {
                OnNewDisk(newExternal[0]);
            }

            if (error is not null) SetStatus(error);
        }
        finally
        {
            _scanning = false;
        }
    }

    private void ApplySources(List<Ext4Source> all)
    {
        string? selectedKey = (SourcesList.SelectedItem as SourceItem)?.Source.Key;
        _suppressSelection = true;
        try
        {
            // Reuse items by key: the open volume keeps a reference to its item
            // (state label), and a rescan may finish while it is being opened.
            var existing = new Dictionary<string, SourceItem>();
            foreach (var i in Sources) existing[i.Source.Key] = i;
            if (_current is not null) existing[_current.Source.Key] = _current;
            Sources.Clear();
            foreach (var s in all.Where(s => s.IsExt || _settings.ShowAllPartitions))
            {
                if (existing.TryGetValue(s.Key, out var item))
                    item.Source = s;
                else
                    item = new SourceItem(s);
                Sources.Add(item);
            }
            foreach (var item in Sources)
                item.Findings = PartitionChecks.Analyze(item.Source, all);
            var sel = Sources.FirstOrDefault(i => i.Source.Key == selectedKey);
            SourcesList.SelectedItem = sel;
        }
        finally
        {
            _suppressSelection = false;
        }
        NoSourcesText.Visibility = Sources.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnNewDisk(Ext4Source s)
    {
        _tray?.Notify("Disco ext collegato", $"{s.Title} · {s.Subtitle}");
        if (_vol is null && _settings.AutoOpenOnInsert)
        {
            ShowFromBackground();
            SelectAndOpen(s);
            return;
        }
        if (!IsVisible) ShowFromBackground();
        _pendingNewDisk = s;
        NewDiskText.Text = $"Nuovo disco ext rilevato: {s.Title} ({s.Subtitle})";
        NewDiskBanner.Visibility = Visibility.Visible;
        BringToFront();
    }

    private void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true;
        Topmost = false;
    }

    private void OnOpenNewDisk(object sender, RoutedEventArgs e)
    {
        NewDiskBanner.Visibility = Visibility.Collapsed;
        if (_pendingNewDisk is { } s) SelectAndOpen(s);
        _pendingNewDisk = null;
    }

    private void OnDismissNewDisk(object sender, RoutedEventArgs e)
    {
        NewDiskBanner.Visibility = Visibility.Collapsed;
        _pendingNewDisk = null;
    }

    private void SelectAndOpen(Ext4Source s)
    {
        var item = Sources.FirstOrDefault(i => i.Source.Key == s.Key);
        if (item is null) return;
        SourcesList.SelectedItem = item; // triggers OnSourceSelected
    }

    private async void OnSourceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (SourcesList.SelectedItem is not SourceItem item) return;
        if (_current is not null && item.Source.Key == _current.Source.Key && _vol is not null) return;
        if (!item.Source.IsExt)
        {
            SetStatus("Questa partizione non contiene un filesystem ext2/3/4.");
            return;
        }
        if (!await ConfirmLeaveCurrentAsync())
        {
            _suppressSelection = true;
            SourcesList.SelectedItem = _current;
            _suppressSelection = false;
            return;
        }
        await OpenSourceAsync(item, readWrite: false);
    }

    private async void OnRescan(object sender, RoutedEventArgs e) => await RescanAsync();

    private void OnShowAllChanged(object sender, RoutedEventArgs e)
    {
        _settings.ShowAllPartitions = ChkAllPartitions.IsChecked == true;
        _settings.Save();
        _ = RescanAsync();
    }

    private void OnOpenImage(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Apri immagine disco",
            Filter = "Immagini e archivi (*.img;*.raw;*.bin;*.dd;*.ext*;*.gz;*.tgz;*.xz;*.zst;*.zip;*.tar)|*.img;*.raw;*.bin;*.dd;*.ext4;*.ext3;*.ext2;*.gz;*.tgz;*.xz;*.zst;*.zip;*.tar|Tutti i file (*.*)|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) == true)
            foreach (var f in dlg.FileNames) OpenImage(f);
    }

    public void OpenImage(string path) => _ = OpenImageAsync(path);

    private async Task OpenImageAsync(string path)
    {
        List<Ext4Source> found;
        try
        {
            if (Core.Archives.ArchiveOpener.IsArchive(path))
            {
                var r = await OpenArchiveAsync(path);
                if (r is null) return;
                found = r;
            }
            else
            {
                found = SourceScanner.ScanImage(path);
            }
        }
        catch (Exception ex) when (ex is Ext4Exception or IOException or InvalidDataException)
        {
            MessageBox.Show(this, $"{Path.GetFileName(path)}\n\n{ex.Message}", "Apri immagine",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _imageSources.RemoveAll(s => string.Equals(s.ImagePath, path, StringComparison.OrdinalIgnoreCase));
        _imageSources.AddRange(found);
        foreach (var s in found) _knownKeys.Add(s.Key);
        _settings.AddRecentImage(path);
        _settings.Save();

        var all = Sources.Select(i => i.Source).Where(s => !s.IsImage).Concat(_imageSources).ToList();
        ApplySources(all);
        var first = found.FirstOrDefault(s => s.IsExt);
        if (first is null)
        {
            SetStatus($"{Path.GetFileName(path)}: nessun filesystem ext trovato.");
            return;
        }
        SelectAndOpen(first);
    }

    /// <summary>Indexes an archive (once; the index is cached) and scans every image inside it.</summary>
    private async Task<List<Ext4Source>?> OpenArchiveAsync(string path)
    {
        // a volume from the previous opening of this archive must be closed first
        if (_current?.Source.IsInArchive == true && string.Equals(_current.Source.ImagePath, path, StringComparison.OrdinalIgnoreCase))
            await CloseVolumeAsync();
        if (_archives.Remove(path, out var old)) old.Dispose();

        var (ok, result) = await RunJobAsync($"Apertura di {Path.GetFileName(path)}", (p, ct) =>
        {
            var contents = Core.Archives.ArchiveOpener.Open(path, p, ct);
            var sources = new List<Ext4Source>();
            try
            {
                foreach (var m in contents.Members)
                {
                    ct.ThrowIfCancellationRequested();
                    p.Report(new JobProgress { Phase = $"analisi di {m.Name}", Done = 0, Total = 0 });
                    sources.AddRange(SourceScanner.ScanVirtual(m.Access, path, m.Name));
                }
            }
            catch
            {
                contents.Dispose();
                throw;
            }
            return (contents, sources);
        });
        if (!ok) return null;
        var (archive, list) = result;
        _archives[path] = archive;
        string note = $"{Path.GetFileName(path)}: archivio {archive.Kind}, {archive.Members.Count} file immagine";
        if (archive.Skipped.Count > 0) note += $" ({archive.Skipped.Count} non supportati)";
        SetStatus(note + ". Le immagini negli archivi si aprono in sola lettura.");
        if (list.Count == 0)
            MessageBox.Show(this, $"Nell'archivio non ci sono immagini con filesystem ext2/3/4.\n\n{string.Join("\n", archive.Skipped)}",
                "Apri archivio", MessageBoxButton.OK, MessageBoxImage.Information);
        return list;
    }

    // ==================================================================
    // open / close / read-write

    private async Task<bool> ConfirmLeaveCurrentAsync()
    {
        if (_vol is null) return true;
        if (_operation is not null)
        {
            MessageBox.Show(this, "Attendi la fine dell'operazione in corso.", "WinExt4");
            return false;
        }
        if (_mount is not null || _vol.ReadWrite)
        {
            var r = MessageBox.Show(this,
                "Il volume attuale è " + (_mount is not null ? $"montato su {_mount.DriveLetter}" : "aperto in scrittura") +
                ".\nVerrà smontato e chiuso (tutti i dati vengono scritti su disco). Continuare?",
                "Chiudere il volume?", MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (r != MessageBoxResult.OK) return false;
        }
        await CloseVolumeAsync();
        return true;
    }

    private async Task OpenSourceAsync(SourceItem item, bool readWrite)
    {
        SetStatus($"Apertura di {item.Source.Title}…");
        try
        {
            var vol = await Task.Run(() => Ext4Volume.Open(item.Source, readWrite));
            _vol = vol;
            _current = item;
            _cwdPath = "/";
            await NavigateAsync("/");
            var info = vol.GetInfo();
            SetStatus(readWrite && info.JournalReplayed
                ? "Aperto in scrittura. Il journal conteneva modifiche non applicate ed è stato riprodotto."
                : $"Aperto {info.Kind} «{(info.Label.Length > 0 ? info.Label : item.Source.Title)}».");
        }
        catch (Ext4Exception ex)
        {
            _vol = null;
            _current = null;
            MessageBox.Show(this, ex.Message, "Impossibile aprire il volume", MessageBoxButton.OK, MessageBoxImage.Warning);
            SetStatus(ex.Message);
        }
        UpdateUi();
    }

    private async Task CloseVolumeAsync()
    {
        await SettleTempCopiesAsync();
        _mount?.Dispose();
        _mount = null;
        var vol = _vol;
        _vol = null;
        if (vol is not null)
        {
            try
            {
                await Task.Run(vol.Dispose);
            }
            catch (Ext4Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Errore in chiusura", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        if (_current is not null) _current.StateText = "";
        _current = null;
        _entries.Clear();
        FileList.ItemsSource = null;
        UpdateUi();
    }

    private async void OnCloseVolume(object sender, RoutedEventArgs e)
    {
        if (_operation is not null) return;
        bool wasRw = _vol?.ReadWrite == true;
        await CloseVolumeAsync();
        _suppressSelection = true;
        SourcesList.SelectedItem = null;
        _suppressSelection = false;
        SetStatus(wasRw ? "Volume chiuso e dati scritti su disco: puoi scollegare il dispositivo in sicurezza."
                        : "Volume chiuso.");
    }

    private void HandleDeviceRemoved()
    {
        bool wasRw = _vol?.ReadWrite == true;
        try { _mount?.Dispose(); } catch (Exception) { }
        _mount = null;
        try { _vol?.Dispose(); } catch (Exception) { }
        _vol = null;
        _current = null;
        _entries.Clear();
        FileList.ItemsSource = null;
        UpdateUi();
        if (wasRw)
            MessageBox.Show(this,
                "Il dispositivo è stato scollegato mentre era aperto in scrittura.\n" +
                "Alcune modifiche potrebbero non essere state salvate: al prossimo utilizzo su Linux esegui e2fsck.",
                "Dispositivo rimosso", MessageBoxButton.OK, MessageBoxImage.Warning);
        else
            SetStatus("Il dispositivo è stato scollegato.");
    }

    private async void OnToggleReadWrite(object sender, RoutedEventArgs e)
    {
        if (_vol is null || _current is null || _operation is not null) return;
        var item = _current;
        string path = _cwdPath;

        if (!_vol.ReadWrite)
        {
            var info = _vol.GetInfo();
            if (info.RwBlocker.Length > 0)
            {
                MessageBox.Show(this,
                    "Questo filesystem non può essere aperto in scrittura in modo sicuro:\n\n" + info.RwBlocker,
                    "Scrittura non disponibile", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var sb = new StringBuilder();
            sb.AppendLine("Sbloccando la scrittura:");
            sb.AppendLine();
            if (!item.Source.IsImage)
                sb.AppendLine("• la partizione viene bloccata per Windows e per gli altri programmi finché non la richiudi;");
            sb.AppendLine("• le modifiche vengono scritte direttamente, senza usare il journal ext3/4: prima di scollegare il disco premi «Chiudi» (o richiudi in sola lettura), altrimenti al rientro su Linux servirà e2fsck;");
            if (info.NeedsRecovery)
                sb.AppendLine("• il journal contiene modifiche non applicate (il disco non è stato smontato bene da Linux): verranno riprodotte ora;");
            if (!info.Clean)
                sb.AppendLine("• il filesystem risulta non smontato correttamente: è consigliato un controllo con e2fsck;");
            sb.AppendLine();
            sb.Append("Continuare?");
            if (MessageBox.Show(this, sb.ToString(), "Sbloccare la scrittura?", MessageBoxButton.OKCancel,
                    MessageBoxImage.Question) != MessageBoxResult.OK)
                return;
        }

        bool wasMounted = _mount is not null;
        string? letter = _mount?.DriveLetter;
        bool toRw = !_vol.ReadWrite;
        await CloseVolumeAsync();
        await OpenSourceAsync(item, toRw);
        if (_vol is null && toRw)
            await OpenSourceAsync(item, false); // fall back to read-only
        if (_vol is not null)
        {
            SourcesList.SelectedItem = item;
            await NavigateAsync(path);
            if (wasMounted) DoMount(letter);
        }
    }

    // ==================================================================
    // mount

    private void OnMount(object sender, RoutedEventArgs e)
    {
        if (_vol is null) return;
        if (_mount is not null)
        {
            string l = _mount.DriveLetter;
            try
            {
                _mount.Dispose();
                _vol.Flush();
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Smontaggio", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            _mount = null;
            SetStatus($"Unità {l} smontata.");
            UpdateUi();
            return;
        }
        if (!MountSession.WinFspInstalled)
        {
            if (MessageBox.Show(this,
                    "Per vedere il disco come unità in Esplora risorse serve WinFsp, un driver open source firmato (gratuito).\n\nAprire la pagina di download?",
                    "WinFsp non installato", MessageBoxButton.YesNo, MessageBoxImage.Information) == MessageBoxResult.Yes)
                Process.Start(new ProcessStartInfo("https://winfsp.dev/rel/") { UseShellExecute = true });
            return;
        }
        var dlg = new OptionsDialog(_settings, forMount: true, readOnly: !_vol.ReadWrite) { Owner = this };
        if (dlg.ShowDialog() != true) return;
        _settings.Save();
        DoMount(dlg.SelectedDriveLetter);
    }

    private void DoMount(string? letter)
    {
        if (_vol is null) return;
        try
        {
            if (letter is not null && !MountSession.FreeDriveLetters().Contains(letter)) letter = null;
            _mount = MountSession.Mount(_vol, _settings, letter);
            SetStatus($"Montato su {_mount.DriveLetter}");
            if (_settings.OpenExplorerAfterMount)
                Process.Start(new ProcessStartInfo("explorer.exe", _mount.DriveLetter + "\\"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DllNotFoundException or FileNotFoundException)
        {
            MessageBox.Show(this, ex.Message, "Montaggio non riuscito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        UpdateUi();
    }

    /// <summary>Sets the partition type to "Linux filesystem" (GPT) / 0x83 (MBR).</summary>
    private async void OnFixPartitionType(object sender, RoutedEventArgs e)
    {
        if (_current is not { } item || _operation is not null) return;
        var s = item.Source;
        string target = s.PartitionStyle == PartitionStyle.Gpt
            ? $"Linux filesystem ({PartitionTypes.LinuxData})" : "Linux (0x83)";
        if (MessageBox.Show(this,
                $"Cambiare il tipo della partizione {s.PartitionNumber} da «{s.PartitionTypeText}» a «{target}»?\n\n" +
                "Viene modificata solo la tabella delle partizioni, non i dati. Windows smetterà di assegnarle una lettera " +
                "e di proporre la formattazione; Linux la monta come prima.\n\nIl volume verrà chiuso e riaperto.",
                "Tipo di partizione", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        bool rw = _vol?.ReadWrite == true;
        await CloseVolumeAsync();
        try
        {
            if (s.PartitionStyle == PartitionStyle.Gpt) PartitionTypes.Edit(s, gptType: PartitionTypes.LinuxData);
            else PartitionTypes.Edit(s, mbrType: PartitionTypes.MbrLinux);
            SetStatus("Tipo di partizione aggiornato.");
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Tipo di partizione", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await RefreshSourcesAfterTableChangeAsync(s, rw);
    }

    /// <summary>Re-reads the partition tables and reopens <paramref name="s"/> if it is still there.</summary>
    private async Task RefreshSourcesAfterTableChangeAsync(Ext4Source s, bool readWrite)
    {
        if (s.IsImage && s.ImagePath is { } img)
        {
            _imageSources.RemoveAll(x => x.ImagePath == img);
            try { _imageSources.AddRange(SourceScanner.ScanImage(img)); } catch (Ext4Exception) { }
        }
        await Task.Delay(s.IsImage ? 0 : 1500); // let Windows re-read the disk layout
        await RescanAsync();
        var again = Sources.FirstOrDefault(i => i.Source.Container == s.Container && i.Source.PartitionNumber == s.PartitionNumber);
        if (again is null || !again.Source.IsExt) return;
        _suppressSelection = true;
        SourcesList.SelectedItem = again;
        _suppressSelection = false;
        await OpenSourceAsync(again, readWrite);
    }

    // ==================================================================
    // hashes, images

    private SourceItem? _contextSource;

    private void OnSourcesRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        // Right-click must not select (= open) the partition, only target the menu.
        _contextSource = (e.OriginalSource as FrameworkElement)?.DataContext as SourceItem;
        e.Handled = true;
        if (_contextSource is not null)
        {
            SourcesList.ContextMenu.PlacementTarget = SourcesList;
            SourcesList.ContextMenu.IsOpen = true;
        }
    }

    private void OnSourcesContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        if (_contextSource is null) { e.Handled = true; return; }
        var s = _contextSource.Source;
        bool disk = !s.IsImage;
        bool busy = _operation is not null;
        ScProperties.IsEnabled = !busy;
        ScHashPartition.IsEnabled = ScExportPartition.IsEnabled = !busy && s.PartitionNumber > 0;
        ScHashDisk.Header = disk ? "Calcola hash dell'intero disco" : "Calcola hash dell'immagine intera";
        ScHashDisk.IsEnabled = !busy;
        ScExportDisk.IsEnabled = !busy && disk;
        ScWritePartition.IsEnabled = !busy && disk && s.PartitionNumber > 0;
        ScZeroFree.IsEnabled = !busy && s.IsExt;
        ScWriteDisk.IsEnabled = !busy && disk;
    }

    private async void OnSourceProperties(object sender, RoutedEventArgs e)
    {
        if (_contextSource is { } item) await ShowPartitionPropertiesAsync(item);
    }

    /// <summary>Properties dialog; applies partition-table edits and refreshes after filesystem edits.</summary>
    private async Task ShowPartitionPropertiesAsync(SourceItem item)
    {
        var s = item.Source;
        bool isCurrent = ReferenceEquals(item, _current) && _vol is not null;
        Ext4Volume? temp = null;
        if (!isCurrent && s.IsExt)
        {
            try { temp = await Task.Run(() => Ext4Volume.Open(s, false)); }
            catch (Ext4Exception) { }
        }
        var dlg = new PartitionPropertiesDialog(s, isCurrent ? _vol : temp) { Owner = this };
        dlg.ShowDialog();
        temp?.Dispose();
        if (dlg.PartitionEdit is null && !dlg.FilesystemChanged) return;

        bool rw = isCurrent && _vol!.ReadWrite;
        string path = _cwdPath;
        bool reopen = isCurrent || (_current is not null && _current.Source.Container == s.Container);
        if (reopen) await CloseVolumeAsync();
        if (dlg.PartitionEdit is { } pe)
        {
            try
            {
                PartitionTypes.Edit(s, pe.GptType, pe.MbrType, pe.GptName, pe.GptAttributes);
                SetStatus("Tabella delle partizioni aggiornata.");
            }
            catch (Ext4Exception ex)
            {
                MessageBox.Show(this, ex.Message, "Partizione", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        if (reopen)
        {
            await RefreshSourcesAfterTableChangeAsync(s, rw);
            if (_vol is not null && path != "/") await NavigateAsync(path);
        }
        else
        {
            if (s.IsImage && s.ImagePath is { } img)
            {
                _imageSources.RemoveAll(x => x.ImagePath == img);
                try { _imageSources.AddRange(SourceScanner.ScanImage(img)); } catch (Ext4Exception) { }
            }
            await RescanAsync();
        }
    }

    /// <summary>Runs a background job with the status-bar progress and cancel button.</summary>
    private async Task<(bool Ok, T? Result)> RunJobAsync<T>(string title, Func<IProgress<JobProgress>, CancellationToken, T> job)
    {
        if (_operation is not null) return (false, default);
        var cts = new CancellationTokenSource();
        _operation = cts;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.Value = 0;
        UpdateUi();
        var started = DateTime.UtcNow;
        var progress = new Progress<JobProgress>(p =>
        {
            Progress.Value = p.Total > 0 ? 1000.0 * p.Done / p.Total : 0;
            double secs = (DateTime.UtcNow - started).TotalSeconds;
            string speed = secs > 1 && p.Total > 1000 ? $" · {Format.Size((ulong)(p.Done / secs))}/s" : "";
            SetStatus(p.Total > 1000
                ? $"{title} – {p.Phase}: {Format.Size((ulong)p.Done)} di {Format.Size((ulong)p.Total)}{speed}"
                : $"{title} – {p.Phase} ({p.Done}/{p.Total})");
        });
        try
        {
            var r = await Task.Run(() => job(progress, cts.Token), cts.Token);
            SetStatus($"{title}: completato.");
            return (true, r);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{title}: annullato.");
        }
        catch (Exception ex) when (ex is Ext4Exception or IOException or UnauthorizedAccessException)
        {
            SetStatus($"{title}: {ex.Message}");
            MessageBox.Show(this, ex.Message, title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            _operation = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateUi();
        }
        return (false, default);
    }

    private static string ImageName(Ext4Source s, bool whole) =>
        $"{(s.Label.Length > 0 ? s.Label : s.IsImage ? Path.GetFileNameWithoutExtension(s.ImagePath) : $"disco{s.DiskNumber}")}" +
        $"{(whole ? "" : $"-p{s.PartitionNumber}")}-{DateTime.Now:yyyyMMdd}.img";

    private async Task HashTargetAsync(RawTarget t, string sidecarName)
    {
        var (ok, r) = await RunJobAsync("Hash", (p, ct) => Core.Imaging.Hash(t, p, ct));
        if (ok && r is not null)
            new HashResultDialog(t.Description, r, sidecarName) { Owner = this }.ShowDialog();
    }

    private async void OnHashPartition(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await HashTargetAsync(RawTarget.Partition(s), ImageName(s, false));
    }

    private async void OnHashDisk(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await HashTargetAsync(RawTarget.WholeDisk(s), ImageName(s, true));
    }

    private async Task ExportAsync(Ext4Source s, RawTarget t, bool whole)
    {
        ulong size = t.Length > 0 ? t.Length : s.DiskSize;
        var d = new ExportImageDialog(t, size, ImageName(s, whole)) { Owner = this };
        if (d.ShowDialog() != true) return;
        var (ok, r) = await RunJobAsync("Esportazione", (p, ct) => Core.Imaging.Export(t, d.ImagePath, d.SelectedFormat, d.Sparse, d.Verify, p, ct));
        if (ok && r is not null)
        {
            string what = $"Immagine creata: {d.ImagePath}";
            if (d.SelectedFormat != ImageFormat.Raw)
                what += $"\nFile: {Format.Size((ulong)r.FileBytes)} ({100.0 * r.FileBytes / Math.Max(1, (double)r.Raw.Bytes):0.#}% dei dati) · SHA-256 del file: {r.FileSha256}\nHash dei dati decompressi:";
            new HashResultDialog(what, r.Raw, null) { Owner = this }.ShowDialog();
            SetStatus($"Immagine salvata in {d.ImagePath} (con .md5 e .sha256).");
        }
    }

    private async void OnExportPartition(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await ExportAsync(s, RawTarget.Partition(s), false);
    }

    private async void OnExportDisk(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await ExportAsync(s, RawTarget.WholeDisk(s), true);
    }

    private async void OnZeroFree(object sender, RoutedEventArgs e)
    {
        if (_contextSource is not { } item) return;
        if (!ReferenceEquals(item, _current) || _vol is not { ReadWrite: true } vol)
        {
            MessageBox.Show(this, "Apri questa partizione e sblocca la scrittura, poi riprova.", "Azzera lo spazio libero",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (MessageBox.Show(this,
                "Scrivere zeri su tutto lo spazio libero?\n\n" +
                "I dati di file già cancellati non saranno più recuperabili e le immagini esportate (soprattutto compresse) " +
                "diventano molto più piccole. I blocchi già a zero vengono solo letti, così chiavette e SD non si consumano. " +
                "Si può annullare in qualsiasi momento.",
                "Azzera lo spazio libero", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        var (ok, r) = await RunJobAsync("Azzeramento", (p, ct) =>
            vol.ZeroFreeSpace(ct, new Progress<double>(f => p.Report(new JobProgress { Phase = "spazio libero", Done = (long)(f * 1000), Total = 1000 }))));
        if (ok)
        {
            uint bs = vol.GetInfo().BlockSize;
            SetStatus($"Spazio libero azzerato: {Format.Size(r.Zeroed * bs)} riscritti su {Format.Size(r.FreeBlocks * bs)} liberi.");
        }
    }

    private async Task WriteImageAsync(Ext4Source s, RawTarget t)
    {
        var d = new WriteImageDialog(t, s.Model.Length > 0 ? s.Model : null) { Owner = this };
        if (d.ShowDialog() != true) return;
        // The open volume, if it is on the target, must be released first.
        if (_current is not null && !_current.Source.IsImage && _current.Source.DiskNumber == s.DiskNumber)
            await CloseVolumeAsync();
        var (ok, r) = await RunJobAsync("Scrittura immagine",
            (p, ct) => Core.Imaging.WriteImage(d.ImagePath, t, d.Expected, d.PreVerify, d.Readback, p, ct));
        if (ok && r is not null)
        {
            string msg = $"Immagine scritta su {t.Description}.\n\nSHA-256: {r.Image.Sha256}\nMD5: {r.Image.Md5}";
            if (r.ExpectedFromSidecar is not null) msg += $"\n\n✔ Corrisponde a {r.ExpectedFromSidecar}";
            if (r.Readback is not null) msg += "\n✔ La rilettura del disco corrisponde all'immagine";
            MessageBox.Show(this, msg, "Scrittura completata", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        await Task.Delay(1500);
        await RescanAsync();
    }

    private async void OnWritePartition(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await WriteImageAsync(s, RawTarget.Partition(s));
    }

    private async void OnWriteDisk(object sender, RoutedEventArgs e)
    {
        if (_contextSource?.Source is { } s) await WriteImageAsync(s, RawTarget.WholeDisk(s));
    }

    private async Task HashFilesAsync(System.Security.Cryptography.HashAlgorithmName algo, string tool)
    {
        if (_vol is not { } vol) return;
        var sel = Selected();
        if (sel.Count == 0) return;
        uint dir = _cwdIno;
        var (ok, list) = await RunJobAsync("Hash dei file",
            (p, ct) => FileHashes.Compute(vol, dir, sel.Select(i => i.Entry), algo, p, ct));
        if (!ok || list is null) return;
        string text = FileHashes.Format(list);
        string ext = tool == "md5sum" ? "md5" : "sha256";
        new TextResultDialog("Hash dei file",
            $"{list.Count} file · formato {tool} (verificabile su Linux con «{tool} -c» dalla cartella {_cwdPath})",
            text, sel.Count == 1 ? $"{sel[0].Entry.Name}.{ext}" : $"{ext.ToUpperInvariant()}SUMS") { Owner = this }.ShowDialog();
    }

    private async void OnHashFilesMd5(object sender, RoutedEventArgs e) =>
        await HashFilesAsync(System.Security.Cryptography.HashAlgorithmName.MD5, "md5sum");

    private async void OnHashFilesSha256(object sender, RoutedEventArgs e) =>
        await HashFilesAsync(System.Security.Cryptography.HashAlgorithmName.SHA256, "sha256sum");

    private async void OnVerifyHashes(object sender, RoutedEventArgs e)
    {
        if (_vol is not { } vol) return;
        string text;
        string source;
        // A list stored inside the ext filesystem can be selected directly.
        if (FileList.SelectedItem is EntryItem { IsDirectoryLike: false } it &&
            System.Text.RegularExpressions.Regex.IsMatch(it.Entry.Name, @"(?i)(\.(md5|sha1|sha256|sha512)|sums?)$"))
        {
            var bytes = new byte[Math.Min(it.Stat.Size, 16UL << 20)];
            vol.Read(it.IsLink ? it.TargetIno : it.Entry.Ino, 0, bytes);
            text = System.Text.Encoding.UTF8.GetString(bytes);
            source = it.Entry.Name;
        }
        else
        {
            var d = new OpenFileDialog { Title = "File md5sum/sha256sum da verificare", Filter = "Liste di hash (*.md5;*.sha256;*SUMS)|*.md5;*.sha1;*.sha256;*.sha512;*SUMS;*sums|Tutti i file|*.*" };
            if (d.ShowDialog(this) != true) return;
            text = File.ReadAllText(d.FileName);
            source = Path.GetFileName(d.FileName);
        }
        string dir = _cwdPath;
        var (ok, results) = await RunJobAsync("Verifica", (p, ct) => FileHashes.Verify(vol, dir, text, p, ct));
        if (!ok || results is null) return;
        int good = results.Count(r => r.Status == "OK");
        string report = string.Join("\n", results.Select(r => $"{r.Status,-9} {r.RelativePath}"));
        new TextResultDialog("Verifica degli hash",
            $"{source}: {good} OK su {results.Count}" + (good == results.Count ? " ✔" : $" · {results.Count - good} con problemi"),
            report, null) { Owner = this }.ShowDialog();
    }

    private void OnOpenInExplorer(object sender, RoutedEventArgs e)
    {
        if (_mount is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", _mount.DriveLetter + "\\"));
    }

    // ==================================================================
    // browsing

    private static string Combine(string dir, string name) => dir == "/" ? "/" + name : dir + "/" + name;

    private static string ParentOf(string path)
    {
        if (path == "/") return "/";
        int i = path.TrimEnd('/').LastIndexOf('/');
        return i <= 0 ? "/" : path[..i];
    }

    private async Task NavigateAsync(string path)
    {
        var vol = _vol;
        if (vol is null) return;
        try
        {
            var (ino, list) = await Task.Run(() =>
            {
                // "/link/." makes namei follow a final symlink to a directory
                uint dirIno = path == "/" ? Ext4Volume.RootIno : vol.Lookup(path + "/.");
                var items = new List<EntryItem>();
                foreach (var e in vol.ReadDir(dirIno))
                {
                    if (!vol.TryStat(e.Ino, out var st)) continue;
                    string? target = null;
                    Ext4Stat? tst = null;
                    uint tino = 0;
                    if (st!.IsSymlink)
                    {
                        try { target = vol.ReadLink(e.Ino); } catch (Ext4Exception) { target = "?"; }
                        tino = vol.Follow(dirIno, e.Ino);
                        if (tino != 0) vol.TryStat(tino, out tst);
                    }
                    items.Add(new EntryItem { Entry = e, Stat = st, LinkTarget = target, TargetStat = tst, TargetIno = tino });
                }
                return (dirIno, items);
            });
            if (!ReferenceEquals(vol, _vol)) return;
            _cwdPath = path;
            _cwdIno = ino;
            _entries = list;
            ApplySort();
            PathBox.Text = path;
            SetStatus($"{list.Count} elementi");
        }
        catch (Ext4Exception ex)
        {
            SetStatus($"{path}: {ex.Message}");
        }
        UpdateUi();
    }

    private void ApplySort()
    {
        IEnumerable<EntryItem> q = _entries;
        Func<EntryItem, object> key = _sortColumn switch
        {
            "Dimensione" => i => i.Stat.Size,
            "Modificato" => i => i.Stat.ModifyTime,
            "Permessi" => i => i.Stat.Mode,
            "Proprietario" => i => ((ulong)i.Stat.Uid << 32) | i.Stat.Gid,
            "Tipo" => i => i.TypeText,
            _ => i => i.Name,
        };
        var cmp = Comparer<object>.Create((a, b) => a is string sa && b is string sb
            ? string.Compare(sa, sb, StringComparison.CurrentCultureIgnoreCase)
            : Comparer<object>.Default.Compare(a, b));
        var ordered = q.OrderByDescending(i => i.IsDirectoryLike);
        ordered = _sortDescending ? ordered.ThenByDescending(key, cmp) : ordered.ThenBy(key, cmp);
        FileList.ItemsSource = ordered.ToList();
    }

    private void OnColumnHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is not GridViewColumnHeader { Column: not null } h) return;
        string col = h.Column.Header as string ?? "Nome";
        _sortDescending = col == _sortColumn && !_sortDescending;
        _sortColumn = col;
        ApplySort();
    }

    private async void OnItemDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FileList.SelectedItem is EntryItem item) await OpenItemAsync(item);
    }

    private async void OnContextOpen(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (sel.Count == 1) await OpenItemAsync(sel[0]);
        else if (sel.Count > 1) await OpenFilesAsync(sel, null);
    }

    private async Task OpenItemAsync(EntryItem item)
    {
        if (_vol is null) return;
        if (item.IsDirectoryLike)
        {
            await NavigateAsync(Combine(_cwdPath, item.Entry.Name));
            return;
        }
        if (item.IsLink && item.TargetStat is null)
        {
            SetStatus($"Il link punta a «{item.LinkTarget}», che non esiste in questo filesystem.");
            return;
        }
        await OpenFilesAsync(new[] { item }, null);
    }

    /// <summary>
    /// Extracts the files to temporary copies and opens them with their
    /// associated program, a chosen <paramref name="handler"/>, or the
    /// system "Open with" dialog when <paramref name="chooser"/> is set.
    /// </summary>
    private async Task OpenFilesAsync(IList<EntryItem> items, OpenWithHandler? handler, bool chooser = false)
    {
        if (_vol is not { } vol) return;
        var files = items.Where(i => !i.IsDirectoryLike && (!i.IsLink || i.TargetStat is not null)).ToList();
        if (files.Count == 0) return;
        string dir = _cwdPath;
        List<TempCopy> copies;
        try
        {
            SetStatus(files.Count == 1 ? $"Apertura di {files[0].Entry.Name}…" : $"Apertura di {files.Count} file…");
            copies = await Task.Run(() => files.Select(f =>
                _temp.Extract(vol, f.IsLink ? f.TargetIno : f.Entry.Ino, Combine(dir, f.Entry.Name), f.Entry.Name)).ToList());
        }
        catch (Exception ex) when (ex is Ext4Exception or IOException or UnauthorizedAccessException)
        {
            SetStatus(ex.Message);
            return;
        }
        foreach (var c in copies)
        {
            try
            {
                if (handler is not null) ShellOpen.Invoke(handler, c.LocalPath);
                else if (chooser) ShellOpen.ShowChooser(new WindowInteropHelper(this).Handle, c.LocalPath);
                else Process.Start(new ProcessStartInfo(c.LocalPath) { UseShellExecute = true });
            }
            catch (Exception ex) when (ex is Win32Exception or COMException)
            {
                SetStatus($"{Path.GetFileName(c.LocalPath)}: {ex.Message}");
            }
        }
        SetStatus(vol.ReadWrite
            ? "Aperta una copia temporanea: quando la salvi ti verrà chiesto se aggiornare il file sul disco ext."
            : "Aperta una copia temporanea in sola lettura: per modificare il file sblocca prima la scrittura.");
    }

    private async void OnTempCopyModified(TempCopy copy)
    {
        if (_vol is not { ReadWrite: true } vol || _askingWriteBack) return;
        if (!_writeBackAlways)
        {
            _askingWriteBack = true;
            if (!IsVisible) ShowFromBackground();
            var d = new WriteBackDialog(copy.ExtPath) { Owner = this };
            d.ShowDialog();
            _askingWriteBack = false;
            if (d.Choice == WriteBackChoice.Ignore) return;
            if (d.Choice == WriteBackChoice.Always) _writeBackAlways = true;
        }
        if (vol.TryStat(copy.Ino, out var st) && st!.ModifyTime != copy.ExtModified &&
            MessageBox.Show(this, $"«{copy.ExtPath}» è stato modificato anche sul disco ext dopo l'apertura.\nSovrascriverlo con la copia salvata?",
                "Conflitto", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
            return;
        try
        {
            await Task.Run(() => TempCopies.WriteBack(vol, copy));
            SetStatus($"«{copy.ExtPath}» aggiornato sul disco ext.");
            if (ParentOf(copy.ExtPath) == _cwdPath) await NavigateAsync(_cwdPath);
        }
        catch (Exception ex) when (ex is Ext4Exception or IOException)
        {
            MessageBox.Show(this, ex.Message, "Aggiornamento non riuscito", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>Before closing: offer to write back saved copies that were not synced yet.</summary>
    private async Task SettleTempCopiesAsync()
    {
        var vol = _vol;
        var pending = _temp.All.Where(c => c.Writable && c.LocallyModified).ToList();
        if (vol is { ReadWrite: true } && pending.Count > 0 &&
            MessageBox.Show(this,
                "Questi file aperti con altri programmi sono stati modificati ma non riportati sul disco ext:\n\n" +
                string.Join("\n", pending.Select(c => "• " + c.ExtPath)) + "\n\nRiportarli adesso?",
                "Modifiche non riportate", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            foreach (var c in pending)
            {
                try
                {
                    await Task.Run(() => TempCopies.WriteBack(vol, c));
                }
                catch (Exception ex) when (ex is Ext4Exception or IOException)
                {
                    MessageBox.Show(this, $"{c.ExtPath}: {ex.Message}", "Aggiornamento non riuscito");
                }
            }
        }
        _temp.Clear();
        _writeBackAlways = false;
    }

    private async void OnOpenWithHandler(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: OpenWithHandler h }) await OpenFilesAsync(Selected(), h);
    }

    private async void OnOpenWithChooser(object sender, RoutedEventArgs e) =>
        await OpenFilesAsync(Selected(), null, chooser: true);

    private void FillOpenWithMenu()
    {
        CmOpenWith.Items.Clear();
        var sel = Selected();
        var files = sel.Where(i => !i.IsDirectoryLike).ToList();
        CmOpenWith.IsEnabled = files.Count > 0 && files.Count == sel.Count;
        if (!CmOpenWith.IsEnabled) return;
        var exts = files.Select(f => Path.GetExtension(f.Entry.Name).ToLowerInvariant()).Distinct().ToList();
        if (exts.Count == 1 && exts[0].Length > 1)
        {
            foreach (var h in ShellOpen.Handlers(exts[0]))
            {
                var mi = new MenuItem { Header = h.Name, Tag = h };
                if (h.Icon is not null) mi.Icon = new Image { Source = h.Icon, Width = 16, Height = 16 };
                mi.Click += OnOpenWithHandler;
                CmOpenWith.Items.Add(mi);
            }
            if (CmOpenWith.Items.Count > 0) CmOpenWith.Items.Add(new Separator());
        }
        var choose = new MenuItem { Header = "Scegli un'altra app…" };
        choose.Click += OnOpenWithChooser;
        CmOpenWith.Items.Add(choose);
    }

    private async void OnUp(object sender, RoutedEventArgs e) => await GoUpAsync();

    private async void OnUpCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (PathBox.IsKeyboardFocusWithin) return;
        await GoUpAsync();
    }

    private async Task GoUpAsync()
    {
        if (_vol is null || _cwdPath == "/") return;
        string from = _cwdPath;
        await NavigateAsync(ParentOf(_cwdPath));
        string name = from[(from.LastIndexOf('/') + 1)..];
        var back = (FileList.ItemsSource as List<EntryItem>)?.FirstOrDefault(i => i.Entry.Name == name);
        if (back is not null)
        {
            FileList.SelectedItem = back;
            FileList.ScrollIntoView(back);
        }
    }

    private async void OnRefresh(object sender, RoutedEventArgs e) => await NavigateAsync(_cwdPath);

    private async void OnRefreshCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (_vol is not null) await NavigateAsync(_cwdPath);
        else await RescanAsync();
    }

    private async void OnPathKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        string p = PathBox.Text.Trim().Replace('\\', '/');
        if (!p.StartsWith('/')) p = "/" + p;
        if (p.Length > 1) p = p.TrimEnd('/');
        await NavigateAsync(p);
    }

    private void OnFileSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateUi();
        if (_settings.ShowPreview)
        {
            _previewTimer.Stop();
            _previewTimer.Start();
        }
    }

    // ==================================================================
    // preview

    private void ApplyPreviewLayout()
    {
        bool show = _settings.ShowPreview && _vol is not null;
        if (!show && PreviewColumn.Width.Value > 0)
        {
            _settings.PreviewWidth = PreviewColumn.ActualWidth;
            _settings.Save();
        }
        PreviewColumn.Width = show ? new GridLength(Math.Max(220, _settings.PreviewWidth)) : new GridLength(0);
        PreviewColumn.MinWidth = show ? 180 : 0;
        PreviewSplitterColumn.Width = new GridLength(show ? 5 : 0);
        PreviewHost.Visibility = PreviewSplitter.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        PreviewIcon.Text = show ? "\uE89F" : "\uE8A1";
    }

    private async void OnTogglePreview(object sender, RoutedEventArgs e)
    {
        if (_vol is null) return;
        _settings.ShowPreview = !_settings.ShowPreview;
        _settings.Save();
        ApplyPreviewLayout();
        if (_settings.ShowPreview) await LoadPreviewAsync();
    }

    private async Task LoadPreviewAsync()
    {
        _previewCts?.Cancel();
        if (_vol is not { } vol || !_settings.ShowPreview) return;
        if (FileList.SelectedItems.Count != 1 || FileList.SelectedItem is not EntryItem item)
        {
            _preview.Show(FileList.SelectedItems.Count > 1
                ? new PreviewData { Kind = PreviewKind.Message, Text = $"{FileList.SelectedItems.Count} elementi selezionati" }
                : null);
            return;
        }
        var cts = new CancellationTokenSource();
        _previewCts = cts;
        _preview.ShowLoading(item.Entry.Name);
        int width = (int)Math.Max(200, PreviewColumn.ActualWidth * VisualTreeHelperDpi());
        try
        {
            var data = await Task.Run(() => PreviewBuilder.Build(vol, item, width, cts.Token), cts.Token);
            if (!cts.IsCancellationRequested) _preview.Show(data);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Ext4Exception ex)
        {
            if (!cts.IsCancellationRequested)
                _preview.Show(new PreviewData { Kind = PreviewKind.Message, Title = item.Entry.Name, Text = ex.Message });
        }
    }

    private double VisualTreeHelperDpi() => System.Windows.Media.VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void OnFileListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Space && FileList.SelectedItem is EntryItem && _vol is not null)
        {
            e.Handled = true;
            ShowQuickLook();
        }
        else if (e.Key == Key.Enter && FileList.SelectedItem is EntryItem item)
        {
            e.Handled = true;
            _ = OpenItemAsync(item);
        }
    }

    private void ShowQuickLook()
    {
        if (_vol is not { } vol || FileList.ItemsSource is not List<EntryItem> items) return;
        int index = FileList.SelectedIndex;
        var w = new QuickLookWindow(items, index,
            (item, width, ct) => Task.Run(() => PreviewBuilder.Build(vol, item, width, ct), ct)) { Owner = this };
        w.IndexChanged += i =>
        {
            FileList.SelectedIndex = i;
            FileList.ScrollIntoView(FileList.SelectedItem);
        };
        w.ShowDialog();
        (FileList.ItemContainerGenerator.ContainerFromIndex(FileList.SelectedIndex) as ListViewItem)?.Focus();
    }

    private List<EntryItem> Selected() => FileList.SelectedItems.Cast<EntryItem>().ToList();

    // ==================================================================
    // file operations

    private async Task<bool> RunTransferAsync(string title, Action<FileTransfer> work, bool refresh = true)
    {
        if (_vol is null || _operation is not null) return false;
        var cts = new CancellationTokenSource();
        _operation = cts;
        ProgressPanel.Visibility = Visibility.Visible;
        Progress.Value = 0;
        UpdateUi();
        var progress = new Progress<TransferProgress>(p =>
        {
            Progress.Value = p.BytesTotal > 0 ? 1000.0 * p.BytesDone / p.BytesTotal : 0;
            SetStatus($"{title}: {p.CurrentItem}  ({Format.Size((ulong)p.BytesDone)} di {Format.Size((ulong)p.BytesTotal)})");
        });
        var transfer = new FileTransfer(_vol, _settings, progress, cts.Token);
        bool overwriteAll = false;
        transfer.OnConflict = name =>
        {
            if (overwriteAll) return ConflictChoice.Overwrite;
            return Dispatcher.Invoke(() =>
            {
                var d = new ConflictDialog(name) { Owner = this };
                d.ShowDialog();
                if (d.ApplyToAll && d.Choice == ConflictChoice.Overwrite) overwriteAll = true;
                return d.Choice;
            });
        };

        bool ok = false;
        try
        {
            await Task.Run(() => work(transfer), cts.Token);
            ok = true;
            var st = transfer.State;
            string msg = $"{title} completata: {st.FilesDone} elementi";
            if (st.Skipped > 0) msg += $", {st.Skipped} saltati";
            SetStatus(msg);
            if (st.Problems.Count > 0)
                MessageBox.Show(this, string.Join("\n", st.Problems.Take(30)), title, MessageBoxButton.OK,
                    MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            SetStatus($"{title} annullata.");
        }
        catch (Exception ex) when (ex is Ext4Exception or IOException or UnauthorizedAccessException)
        {
            SetStatus($"{title}: {ex.Message}");
            MessageBox.Show(this, $"{transfer.State.CurrentItem}\n\n{ex.Message}", title, MessageBoxButton.OK,
                MessageBoxImage.Warning);
        }
        finally
        {
            _operation = null;
            ProgressPanel.Visibility = Visibility.Collapsed;
        }
        if (_closeRequested)
        {
            Close();
            return ok;
        }
        if (refresh) await NavigateAsync(_cwdPath);
        UpdateUi();
        return ok;
    }

    private void OnCancelOperation(object sender, RoutedEventArgs e) => _operation?.Cancel();

    private async void OnExport(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (_vol is null || sel.Count == 0) return;
        var dlg = new OpenFolderDialog { Title = "Cartella di destinazione" };
        if (dlg.ShowDialog(this) != true) return;
        await RunTransferAsync("Estrazione", t => t.Export(_cwdIno, sel.Select(i => i.Entry), dlg.FolderName));
    }

    private async void OnImportFiles(object sender, RoutedEventArgs e)
    {
        if (_vol is not { ReadWrite: true }) return;
        var dlg = new OpenFileDialog { Title = $"Copia file in {_cwdPath}", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        await RunTransferAsync("Importazione", t => t.Import(dlg.FileNames, _cwdIno));
    }

    private async void OnImportFolder(object sender, RoutedEventArgs e)
    {
        if (_vol is not { ReadWrite: true }) return;
        var dlg = new OpenFolderDialog { Title = $"Copia cartelle in {_cwdPath}", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;
        await RunTransferAsync("Importazione", t => t.Import(dlg.FolderNames, _cwdIno));
    }

    private async void OnNewFolder(object sender, RoutedEventArgs e)
    {
        if (_vol is not { ReadWrite: true } vol) return;
        var dlg = new InputDialog("Nuova cartella", "Nome della cartella:", "Nuova cartella") { Owner = this };
        if (dlg.ShowDialog() != true) return;
        try
        {
            var (uid, gid) = _settings.OwnerFor(vol.Stat(_cwdIno));
            vol.CreateDirectory(_cwdIno, Ext4Volume.Encode(dlg.Value), _settings.DirMode, uid, gid);
            vol.Flush();
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Nuova cartella", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await NavigateAsync(_cwdPath);
    }

    private async void OnRename(object sender, RoutedEventArgs e)
    {
        if (_vol is not { ReadWrite: true } vol || FileList.SelectedItem is not EntryItem item) return;
        var dlg = new InputDialog("Rinomina", "Nuovo nome:", item.Entry.Name) { Owner = this };
        if (dlg.ShowDialog() != true || dlg.Value == item.Entry.Name) return;
        try
        {
            vol.Rename(_cwdIno, item.Entry.RawName, _cwdIno, Ext4Volume.Encode(dlg.Value), replace: false);
            vol.Flush();
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Rinomina", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await NavigateAsync(_cwdPath);
    }

    private void OnRenameCommand(object sender, ExecutedRoutedEventArgs e) => OnRename(sender, e);

    private async void OnDelete(object sender, RoutedEventArgs e)
    {
        var sel = Selected();
        if (_vol is not { ReadWrite: true } || sel.Count == 0) return;
        string what = sel.Count == 1 ? $"«{sel[0].Entry.Name}»" : $"{sel.Count} elementi";
        if (MessageBox.Show(this, $"Eliminare definitivamente {what}?\nLe cartelle vengono eliminate con tutto il contenuto.",
                "Elimina", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await RunTransferAsync("Eliminazione", t => t.Delete(_cwdIno, sel.Select(i => i.Entry)));
    }

    private void OnDeleteCommand(object sender, ExecutedRoutedEventArgs e)
    {
        if (!PathBox.IsKeyboardFocusWithin) OnDelete(sender, e);
    }

    private async void OnProperties(object sender, RoutedEventArgs e)
    {
        if (_vol is null) return;
        if (FileList.SelectedItem is not EntryItem item)
        {
            if (_current is not null) await ShowPartitionPropertiesAsync(_current);
            return;
        }
        var dlg = new PropertiesDialog(_vol, item, _cwdPath) { Owner = this };
        if (dlg.ShowDialog() == true) await NavigateAsync(_cwdPath);
    }

    private void OnContextMenuOpened(object sender, RoutedEventArgs e)
    {
        bool rw = _vol?.ReadWrite == true;
        int n = FileList.SelectedItems.Count;
        CmOpen.IsEnabled = n >= 1;
        FillOpenWithMenu();
        CmExport.IsEnabled = n > 0;
        CmHash.IsEnabled = n > 0;
        CmVerify.IsEnabled = _vol is not null;
        CmRename.IsEnabled = rw && n == 1;
        CmDelete.IsEnabled = rw && n > 0;
        CmNewFolder.IsEnabled = rw;
        CmProperties.IsEnabled = _vol is not null;
    }

    private void OnSettings(object sender, RoutedEventArgs e)
    {
        var dlg = new OptionsDialog(_settings, forMount: false, readOnly: false) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            _settings.Save();
            _ = RescanAsync();
        }
    }

    // ==================================================================
    // state

    private void SetStatus(string text) => StatusText.Text = text;

    private void UpdateUi()
    {
        bool open = _vol is not null;
        bool rw = _vol?.ReadWrite == true;
        bool busy = _operation is not null;
        int sel = FileList.SelectedItems.Count;

        BtnReadWrite.IsEnabled = open && !busy && _current?.Source.IsInArchive != true;
        BtnReadWrite.ToolTip = _current?.Source.IsInArchive == true
            ? "Le immagini dentro un archivio sono in sola lettura: estraile o esportale per modificarle"
            : null;
        RwIcon.Text = rw ? "" : "";
        RwText.Text = rw ? " Torna in sola lettura" : " Sblocca scrittura";
        BtnMount.IsEnabled = open && !busy;
        MountIcon.Text = _mount is null ? "" : "";
        MountText.Text = _mount is null ? " Monta su Windows" : $" Smonta {_mount.DriveLetter}";
        BtnExport.IsEnabled = open && sel > 0 && !busy;
        BtnImportFiles.IsEnabled = BtnImportFolder.IsEnabled = BtnNewFolder.IsEnabled = rw && !busy;
        BtnRename.IsEnabled = rw && sel == 1 && !busy;
        BtnDelete.IsEnabled = rw && sel > 0 && !busy;
        BtnProperties.IsEnabled = open;
        BtnPreview.IsEnabled = open;
        ApplyPreviewLayout();
        BtnClose.IsEnabled = open && !busy;
        BtnUp.IsEnabled = open && _cwdPath != "/";
        BtnRefresh.IsEnabled = PathBox.IsEnabled = open;
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        FileList.Visibility = open ? Visibility.Visible : Visibility.Collapsed;

        if (_current is not null)
            _current.StateText = _mount is not null ? $"· {_mount.DriveLetter}" : rw ? "· scrittura" : "· aperto";

        if (!open)
        {
            ModeBanner.Visibility = MountBanner.Visibility = WarnBanner.Visibility = Visibility.Collapsed;
            FsSummary.Text = "";
            Title = "WinExt4";
            return;
        }

        var info = _vol!.GetInfo();
        ModeBanner.Visibility = Visibility.Visible;
        if (rw)
        {
            ModeBanner.Background = (System.Windows.Media.Brush)FindResource("WarnBrush");
            ModeBannerIcon.Foreground = ModeBannerText.Foreground = (System.Windows.Media.Brush)FindResource("WarnTextBrush");
            ModeBannerIcon.Text = "";
            ModeBannerText.Text = "Scrittura attiva" + (_current?.Source.IsImage == true ? "" : " · partizione bloccata per Windows") +
                                  ". Premi «Chiudi» o torna in sola lettura prima di scollegare il disco.";
        }
        else
        {
            ModeBanner.Background = (System.Windows.Media.Brush)FindResource("SafeBrush");
            ModeBannerIcon.Foreground = ModeBannerText.Foreground = (System.Windows.Media.Brush)FindResource("SafeTextBrush");
            ModeBannerIcon.Text = "";
            ModeBannerText.Text = "Sola lettura: niente viene scritto sulla partizione. Puoi scollegare il disco quando vuoi.";
        }

        MountBanner.Visibility = _mount is null ? Visibility.Collapsed : Visibility.Visible;
        if (_mount is not null)
            MountBannerText.Text = $"Montato su {_mount.DriveLetter} " + (rw ? "in lettura/scrittura" : "in sola lettura") +
                                   " · permessi e proprietari Linux non sono tradotti in permessi Windows.";

        var warnings = new List<string>();
        if (info.NeedsRecovery)
            warnings.Add("Il journal contiene modifiche non ancora applicate (il disco non è stato smontato bene): alcuni file potrebbero sembrare vecchi. Sbloccando la scrittura verranno applicate.");
        else if (!info.Clean && !rw)
            warnings.Add("Il filesystem non risulta smontato correttamente: su Linux è consigliato e2fsck.");
        if (info.HasErrors)
            warnings.Add("Il filesystem è segnato con errori: esegui e2fsck su Linux prima di scriverci.");
        if (_current is not null)
            warnings.AddRange(_current.Findings.Select(f => f.Text));
        BtnFixType.Visibility = _current?.Findings.Any(f => f.CanFixType) == true ? Visibility.Visible : Visibility.Collapsed;
        WarnBanner.Visibility = warnings.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        WarnBannerText.Text = string.Join(" ", warnings);

        FsSummary.Text = $"{info.Kind} · {(info.Label.Length > 0 ? info.Label + " · " : "")}" +
                         $"{Format.Size(info.FreeBytes)} liberi di {Format.Size(info.TotalBytes)}";
        Title = $"WinExt4 — {(info.Label.Length > 0 ? info.Label : _current?.Source.Title)}{(rw ? " (scrittura)" : "")}";
    }

    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_operation is not null)
        {
            if (MessageBox.Show(this, "Un'operazione è in corso. Annullarla e uscire?", "WinExt4",
                    MessageBoxButton.OKCancel) != MessageBoxResult.OK)
            {
                e.Cancel = true;
                return;
            }
            // The worker still uses the volume: close once it has stopped.
            _closeRequested = true;
            _operation.Cancel();
            e.Cancel = true;
            return;
        }
        if (_tray is not null && !_exiting)
        {
            // Background mode: closing the window only hides it.
            e.Cancel = true;
            if (_vol is not null) await CloseVolumeAsync();
            Hide();
            if (!_trayHintShown)
            {
                _trayHintShown = true;
                _tray.Notify("WinExt4 è ancora attivo", "Resta nell'area di notifica e si apre quando colleghi un disco ext. Clic destro sull'icona per uscire.");
            }
            return;
        }
        if (_vol is null) return;
        e.Cancel = true;
        await CloseVolumeAsync();
        Closing -= OnClosing;
        Close();
    }
}
