using System.IO;
using System.Windows;
using Microsoft.Win32;
using WinExt4.Core;

namespace WinExt4.UI;

/// <summary>Draft mode: edits go to a .wx4draft file, applied or exported later.</summary>
public partial class MainWindow
{
    private bool IsDraft => _vol?.DraftPath is not null;

    private async void OnDraft(object sender, RoutedEventArgs e)
    {
        if (_vol is null || _current is null || _operation is not null) return;
        var item = _current;
        string path = _cwdPath;

        if (_vol.DraftPath is { } open)
        {
            bool wasMounted = _mount is not null;
            await CloseVolumeAsync();
            await OpenSourceAsync(item, false);
            if (_vol is not null)
            {
                SourcesList.SelectedItem = item;
                await NavigateAsync(path);
            }
            SetStatus($"Bozza chiusa: le modifiche restano in {open} e puoi riprenderle quando vuoi." +
                      (wasMounted ? " L'unità è stata smontata." : ""));
            return;
        }

        var info = _vol.GetInfo();
        if (info.RwBlocker.Length > 0)
        {
            MessageBox.Show(this, "Questo filesystem non può essere modificato in modo sicuro:\n\n" + info.RwBlocker,
                "Bozza non disponibile", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var existing = await Task.Run(() => Drafts.FindFor(item.Source));
        string? delta = null;
        if (existing.Count > 0)
        {
            var d = existing[0];
            var r = MessageBox.Show(this,
                $"Per questo filesystem esiste già una bozza non applicata:\n\n{Path.GetFileName(d.Path)}\n" +
                $"creata il {d.Created:g}, {Format.Size(d.ChangedBytes)} di modifiche.\n\n" +
                "Sì: riprendi questa bozza\nNo: crea una nuova bozza",
                "Modifica come bozza", MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
            if (r == MessageBoxResult.Cancel) return;
            if (r == MessageBoxResult.Yes) delta = d.Path;
        }
        if (delta is null)
        {
            delta = Drafts.NewPath(item.Source);
            if (MessageBox.Show(this,
                    "In modalità bozza la sorgente non viene mai scritta: ogni modifica viene salvata in un file a parte\n\n" +
                    $"{delta}\n\n" +
                    "Puoi chiudere, riavviare o copiare la bozza su un altro PC e riprenderla quando vuoi. " +
                    "Alla fine la applichi alla sorgente o la esporti come nuova immagine.\n\nContinuare?",
                    "Modifica come bozza", MessageBoxButton.OKCancel, MessageBoxImage.Information) != MessageBoxResult.OK)
                return;
        }
        await OpenDraftAsync(item, delta, path);
    }

    private async Task OpenDraftAsync(SourceItem item, string delta, string path = "/")
    {
        bool wasMounted = _mount is not null;
        string? letter = _mount?.DriveLetter;
        await CloseVolumeAsync();
        SetStatus($"Apertura della bozza {Path.GetFileName(delta)}…");
        try
        {
            _vol = await Task.Run(() => Ext4Volume.OpenDraft(item.Source, delta, Drafts.Describe(item.Source)));
            _current = item;
            _cwdPath = "/";
            _suppressSelection = true;
            SourcesList.SelectedItem = item;
            _suppressSelection = false;
            await NavigateAsync(path);
            var info = _vol.GetInfo();
            SetStatus($"Bozza aperta: le modifiche vanno in {delta}" +
                      (info.JournalReplayed ? " (il journal della sorgente è stato riprodotto nella bozza)." : "."));
            UpdateUi();
            if (wasMounted) DoMount(letter);
        }
        catch (Ext4Exception ex)
        {
            _vol = null;
            _current = null;
            MessageBox.Show(this, ex.Message, "Impossibile aprire la bozza", MessageBoxButton.OK, MessageBoxImage.Warning);
            await OpenSourceAsync(item, false);
        }
    }

    private async void OnDraftChanges(object sender, RoutedEventArgs e)
    {
        if (_vol is not { DraftPath: not null } vol || _current is not { } item || _operation is not null) return;
        vol.Flush();
        var (ok, list) = await RunJobAsync("Confronto con l'originale", (p, ct) =>
        {
            using var orig = Ext4Volume.Open(item.Source, false);
            return DraftDiff.Compare(orig, vol, p, ct);
        });
        if (ok && list is not null)
            new DraftChangesDialog(list, Drafts.Info(vol.DraftPath!)) { Owner = this }.ShowDialog();
    }

    private async void OnDraftExport(object sender, RoutedEventArgs e)
    {
        if (_vol?.DraftPath is not { } delta || _current is not { } item || _operation is not null) return;
        if (_mount is not null &&
            MessageBox.Show(this, $"Per esportare, l'unità {_mount.DriveLetter} viene smontata. Continuare?", "Esporta bozza",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        string path = _cwdPath;
        await CloseVolumeAsync();
        Ext4Volume? view = null;
        try
        {
            // a read-only view, so the exported filesystem is marked clean
            view = await Task.Run(() => Ext4Volume.OpenDraft(item.Source, delta, "", view: true));
            var t = Drafts.ExportTarget(view);
            await ExportAsync(item.Source, t, false, Path.GetFileNameWithoutExtension(ImageName(item.Source, false)) + "-bozza.img");
        }
        catch (Ext4Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Esporta bozza", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            view?.Dispose();
        }
        await OpenDraftAsync(item, delta, path);
    }

    private async void OnDraftApply(object sender, RoutedEventArgs e)
    {
        if (_vol?.DraftPath is not { } delta || _current is not { } item || _operation is not null) return;
        var s = item.Source;
        if (s.IsInArchive)
        {
            MessageBox.Show(this,
                "Un'immagine dentro un archivio non si può modificare sul posto.\nUsa «Esporta immagine…» per salvare il risultato in un nuovo file.",
                "Applica bozza", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _vol.Flush();
        var info = Drafts.Info(delta);
        if (info is null) return;
        string where = s.IsImage ? $"nell'immagine {s.ImagePath}" : $"sulla partizione {s.PartitionNumber} del disco {s.DiskNumber} ({s.Model})";
        if (MessageBox.Show(this,
                $"Scrivere le modifiche della bozza {where}?\n\n" +
                $"Dati da scrivere: {Format.Size(info.ChangedBytes)}.\n" +
                (s.IsImage ? "" : "Durante la scrittura la partizione è bloccata per Windows e per gli altri programmi.\n") +
                "Se l'operazione si interrompe, la bozza resta valida e puoi ripeterla.\n\nContinuare?",
                "Applica bozza", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;
        await ApplyDraftAsync(item, delta, _cwdPath);
    }

    private async Task ApplyDraftAsync(SourceItem item, string delta, string path)
    {
        await CloseVolumeAsync();
        while (true)
        {
            var (ok, _) = await RunJobAsync("Applicazione della bozza", (p, ct) =>
            {
                Drafts.Apply(delta, item.Source, p, ct);
                return true;
            });
            if (ok)
            {
                if (string.Equals(Path.GetDirectoryName(Path.GetFullPath(delta)), Drafts.Dir, StringComparison.OrdinalIgnoreCase))
                    try { File.Delete(delta); } catch (IOException) { }
                await OpenSourceAsync(item, false);
                if (_vol is not null)
                {
                    SourcesList.SelectedItem = item;
                    await NavigateAsync(path);
                }
                SetStatus("Bozza applicata: le modifiche sono state scritte sulla sorgente.");
                return;
            }
            var after = Drafts.Info(delta);
            if (after?.ApplyState == 1)
            {
                // the source is half written: it is consistent only once the draft is fully applied
                if (MessageBox.Show(this,
                        "L'applicazione si è interrotta a metà: la sorgente sarà coerente solo dopo averla completata.\n\nRiprovare ora?",
                        "Applica bozza", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                    continue;
                SetStatus($"Applicazione incompleta: riapri {delta} per completarla.");
                return;
            }
            await OpenDraftAsync(item, delta, path);
            return;
        }
    }

    private async void OnDraftDiscard(object sender, RoutedEventArgs e)
    {
        if (_vol?.DraftPath is not { } delta || _current is not { } item || _operation is not null) return;
        _vol.Flush();
        var info = Drafts.Info(delta);
        if (MessageBox.Show(this,
                $"Eliminare la bozza {Path.GetFileName(delta)}?\n\n{Format.Size(info?.ChangedBytes ?? 0)} di modifiche andranno persi. " +
                "La sorgente non è mai stata modificata.",
                "Scarta bozza", MessageBoxButton.OKCancel, MessageBoxImage.Warning) != MessageBoxResult.OK)
            return;
        await CloseVolumeAsync();
        try
        {
            File.Delete(delta);
            SetStatus("Bozza eliminata.");
        }
        catch (IOException ex)
        {
            MessageBox.Show(this, ex.Message, "Scarta bozza", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        await OpenSourceAsync(item, false);
        if (_vol is not null) SourcesList.SelectedItem = item;
    }

    /// <summary>Opens a .wx4draft: finds its source among the listed ones, then the original image, then asks.</summary>
    private async Task OpenDraftFileAsync(string delta)
    {
        var info = Drafts.Info(delta);
        if (info is null)
        {
            MessageBox.Show(this, $"{Path.GetFileName(delta)} non è un file bozza valido.", "Apri bozza",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (info.ApplyState == 2)
        {
            MessageBox.Show(this, "Questa bozza è già stata applicata alla sua sorgente.", "Apri bozza",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var item = await FindDraftSourceAsync(delta, info, Sources.Select(i => i.Source).ToList());
        if (item is null)
        {
            // the description of an image source starts with its full path
            string desc = info.SourceDescription;
            int cut = desc.IndexOf(" › ", StringComparison.Ordinal);
            if (cut < 0) cut = desc.IndexOf(" (partizione", StringComparison.Ordinal);
            string orig = cut > 0 ? desc[..cut] : desc;
            if (Path.IsPathFullyQualified(orig) && File.Exists(orig))
                item = await FindDraftSourceAsync(delta, info, await LoadImageAsync(orig) ?? new());
        }
        if (item is null)
        {
            var dlg = new OpenFileDialog
            {
                Title = $"Scegli l'immagine o l'archivio della bozza ({info.SourceDescription})",
                Filter = "Immagini e archivi|*.img;*.raw;*.bin;*.dd;*.ext4;*.ext3;*.ext2;*.gz;*.tgz;*.xz;*.zst;*.zip;*.tar;*.vhd|Tutti i file (*.*)|*.*",
            };
            if (MessageBox.Show(this,
                    $"La sorgente di questa bozza non è tra quelle elencate:\n\n{info.SourceDescription}\n" +
                    (info.Label.Length > 0 ? $"etichetta «{info.Label}», " : "") + $"UUID {info.Uuid}\n\n" +
                    "Se è un disco, collegalo e riprova. Se è un'immagine, vuoi indicarla ora?",
                    "Apri bozza", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes ||
                dlg.ShowDialog(this) != true)
                return;
            item = await FindDraftSourceAsync(delta, info, await LoadImageAsync(dlg.FileName) ?? new());
            if (item is null)
            {
                MessageBox.Show(this,
                    "La bozza non corrisponde a questo file: è stata creata da un'altra sorgente, oppure la sorgente è stata modificata dopo.",
                    "Apri bozza", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
        }
        if (!await ConfirmLeaveCurrentAsync()) return;
        if (info.ApplyState == 1)
        {
            if (MessageBox.Show(this,
                    $"L'applicazione di questa bozza a {item.Source.Title} si era interrotta a metà.\n\nCompletarla ora?",
                    "Apri bozza", MessageBoxButton.YesNo, MessageBoxImage.Warning) == MessageBoxResult.Yes)
                await ApplyDraftAsync(item, delta, "/");
            return;
        }
        await OpenDraftAsync(item, delta);
    }

    private async Task<SourceItem?> FindDraftSourceAsync(string delta, DraftInfo info, List<Ext4Source> candidates)
    {
        var match = await Task.Run(() => candidates.FirstOrDefault(s =>
            s.IsExt &&
            (info.Uuid.Length == 0 || s.Uuid.Length == 0 || string.Equals(s.Uuid, info.Uuid, StringComparison.OrdinalIgnoreCase)) &&
            // an interrupted apply changed the source, so only the identity can be checked
            (info.ApplyState == 1 ? s.Uuid.Length > 0 : Drafts.Matches(delta, s))));
        return match is null ? null : Sources.FirstOrDefault(i => i.Source.Key == match.Key);
    }

    /// <summary>Hints at an unapplied draft after opening a source normally.</summary>
    private async Task HintDraftsAsync(SourceItem item)
    {
        try
        {
            var list = await Task.Run(() => Drafts.FindFor(item.Source));
            if (list.Count > 0 && _current == item && !IsDraft)
                SetStatus($"Per questo filesystem c'è una bozza non applicata ({Format.Size(list[0].ChangedBytes)}, {list[0].Created:g}): " +
                          "«Modifica come bozza» per riprenderla.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
