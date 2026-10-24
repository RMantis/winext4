# WinExt4

Legge e scrive filesystem **ext2 / ext3 / ext4** su Windows 10/11, da chiavette USB, dischi, schede SD e immagini `.img`, **senza driver non firmati**.

## Perché non serve un driver non firmato

Progetti come Ext2Fsd/Ext4Fsd usavano un proprio driver kernel. Non essendo firmato, oggi richiede la modalità test-signing, e con l'Integrità della memoria (HVCI) attiva viene bloccato comunque. WinExt4 lavora interamente in user mode:

| Componente | Cosa fa | Driver |
|---|---|---|
| `ext4core.dll` | **libext2fs** (il codice di `e2fsck`, `debugfs` e `fuse2fs`) compilata per Windows, più un livello di I/O che accede alla partizione grezza (`\\.\HarddiskNPartitionM`) o al file immagine | nessuno |
| `WinExt4.exe` | GUI WPF: elenco dei dischi, browser dei file, copia da/verso Windows, permessi | nessuno |
| Montaggio come unità | Filesystem in user mode servito tramite **[WinFsp](https://winfsp.dev)** | driver di WinFsp, **firmato** e compatibile con HVCI |

WinFsp serve solo per il pulsante «Monta su Windows». Tutto il resto funziona anche senza.

## Uso

1. Avvia `WinExt4.exe` e accetta il prompt UAC: per leggere i dischi grezzi servono i privilegi di amministratore.
2. **Collega la chiavetta**: la partizione ext viene trovata e aperta subito **in sola lettura**. In questa modalità WinExt4 non scrive niente e la puoi scollegare quando vuoi.
3. Puoi:
   - **sfogliare** i file, con permessi `rwx`, uid:gid e link simbolici;
   - **Estrai** per copiare file o cartelle in Windows;
   - fare doppio clic su un file per aprirne una copia temporanea.
4. **Sblocca scrittura** riapre la partizione in lettura/scrittura:
   - la partizione viene bloccata e smontata per Windows, così nessun altro programma la tocca;
   - se il journal contiene transazioni non applicate (disco staccato da Linux senza smontarlo), vengono riprodotte prima di scrivere;
   - a questo punto puoi importare file e cartelle (anche trascinandoli nella finestra), rinominare, eliminare, creare cartelle e cambiare permessi e proprietario.
5. **Monta su Windows** assegna una lettera di unità e il disco si usa da Esplora risorse e da qualsiasi programma. Prima del montaggio una finestra spiega come vengono trattati permessi e attributi e chiede proprietario e permessi da dare ai nuovi file.
6. **Chiudi** (oppure torna in sola lettura) **prima di scollegare** un disco aperto in scrittura.

Le immagini si aprono in tre modi: con «Apri immagine», trascinando il file nella finestra, oppure con «Apri con → WinExt4». Sono supportati:
- filesystem «nudi», creati con `mkfs.ext4 file.img`;
- immagini disco con tabella MBR (anche partizioni logiche);
- immagini disco con tabella GPT.

### Avvio con Windows

In *Impostazioni → Avvia con Windows* viene creata un'attività pianificata con privilegi elevati: nessun prompt UAC a ogni accesso. WinExt4 resta nell'area di notifica e si apre da solo quando colleghi un disco ext. Chiudere la finestra la nasconde soltanto; per uscire usa il clic destro sull'icona → Esci.

## Permessi e attributi quando il disco è montato

- **Permessi Windows.** I permessi Linux (`rwx`, uid/gid) **non** diventano ACL di Windows: chiunque su questo PC ha accesso all'unità. Permessi e proprietario si vedono e si modificano dalla finestra di WinExt4 (Proprietà).
- **Attributo «Sola lettura».** Corrisponde al bit di scrittura del proprietario (`u+w`).
- **Nuovi file.** Ricevono lo stesso uid/gid della cartella in cui vengono creati, oppure uid/gid fissi, con permessi `644` (file) e `755` (cartelle). Tutto è configurabile.
- **Link simbolici.** Vengono seguiti; quelli assoluti sono risolti dentro il filesystem ext. I link rotti appaiono come file vuoti in sola lettura.
- **Caratteri non ammessi da Windows** (`\ : * ? " < > |`, punto o spazio finale). Vengono mappati nell'area Unicode privata U+F000+c, la stessa convenzione di WSL e Cygwin, e tornano identici quando li riscrivi.
- **Maiuscole/minuscole.** ext4 le distingue: «Foto» e «foto» sono due cartelle diverse, e l'unità le mostra entrambe.

## Sicurezza dei dati

- **Sola lettura di default.** Aprire un disco non modifica nemmeno un byte; è verificato confrontando l'immagine prima e dopo.
- **Scrittura senza journal**, esattamente come `fuse2fs`: le modifiche vanno direttamente sul disco. Mentre è aperto in scrittura il filesystem è marcato «non pulito», come fa il kernel. Se il disco viene staccato senza chiudere, al rientro su Linux `e2fsck` lo sistema.
- **Scrittura rifiutata** quando il filesystem usa `quota`, `encrypt`, `casefold`, `verity`, `mmp`, un journal esterno o è marcato con errori. In questi casi resta disponibile la sola lettura.
- **Spazio riservato.** I blocchi riservati non vengono mai usati: come `fuse2fs`, si evita di esaurire completamente lo spazio a metà di un'operazione.

## Compilare

Servono:
- Visual Studio 2022 con il workload C++ (usa il suo CMake/Ninja);
- .NET 8 SDK;
- [WinFsp](https://winfsp.dev/rel/), che fornisce `winfsp-msil.dll`.

```bash
git clone --recurse-submodules <repo> winext4
```

```bash
powershell -ExecutionPolicy Bypass -File build.ps1
```

Il risultato finisce in `dist\WinExt4`. Con `-SelfContained` ottieni un unico `.exe` che non richiede il runtime .NET.

e2fsprogs è un sottomodulo fermo alla versione **v1.47.4**. Le poche correzioni di portabilità per MSVC (macro variadiche GNU, `__cdecl`) stanno in `native/patches/e2fsprogs-msvc.patch` e `native/build.cmd` le applica da solo. I file che normalmente genera `configure` sono in `native/port` (scritti a mano) e in `native/generated` (rigenerabili con `sh native/tools/gen.sh` da WSL).

## Test

Da WSL, con e2fsprogs installato, si creano le immagini di prova:

```bash
wsl sh tests/make_images.sh
```

Le varianti sono:
- ext2, ext3, ext4;
- ext4 con `inline_data` e `metadata_csum`;
- blocchi da 1K;
- immagini disco MBR e GPT.

Poi:

```bash
bash tests/run_tests.sh
```

```bash
dotnet run --project tests/MountTest -- tests/work/copia.img
```

- `run_tests.sh` scrive sulle immagini con `e4cli.exe` (crea, sovrascrive, rinomina, sposta, cancella, tronca, crea link simbolici). Poi le verifica con **`e2fsck -fn` e `debugfs` di Linux**.
- `MountTest` monta l'immagine con WinFsp e la usa con le normali API di file di Windows (copia, append, scrittura casuale, spostamento, attributi, date, eliminazione ricorsiva).
- `tests/make_dirty_journal.sh` crea un'immagine con una transazione nel journal non ancora applicata, per provare la riproduzione del journal.

## Struttura

```
native/            ext4core.dll (C, MSVC)
  src/ext4core.c   API: open/close, lookup, readdir, read/write, create, mkdir, rename, unlink, ...
  src/winio.c      io_manager di libext2fs per volumi grezzi e immagini (allineamento settori, lock del volume, cache)
  src/scan.c       ricerca delle partizioni ext su dischi fisici e immagini MBR/GPT
  tools/e4cli.c    piccolo client a riga di comando usato dai test
src/WinExt4/       GUI WPF (.NET 8) + montaggio WinFsp (Mount/Ext4FileSystem.cs)
tests/             immagini di prova e test end-to-end
third_party/       e2fsprogs (sottomodulo)
```

## Roadmap

Le prossime funzioni previste sono descritte in [docs/ROADMAP.md](docs/ROADMAP.md):
- hash e verifica di immagini, dischi e file (`.md5`);
- controllo dei GUID delle partizioni;
- azzeramento dello spazio libero ed export compresso (zstd/gz/xz/VHDX);
- apertura di immagini dentro archivi (`.tar.gz`, `.img.xz`, `.zip`…) senza estrarle;
- «Apri con…» per i file ext e WinExt4 nel menu «Apri con» di Windows;
- anteprima di testo e immagini;
- proprietà complete della partizione, con modifica di etichetta, UUID, spazio riservato, nome e tipo della partizione;
- fsck integrato;
- modalità «bozza», in cui le modifiche restano in un overlay finché non le applichi o le esporti.

## Licenza

GPL-2.0. Il motivo è che vengono incluse parti di e2fsprogs sotto GPL: il codice di riproduzione del journal di `debugfs` ed `e2fsck`. libext2fs è invece LGPL. WinFsp è GPLv3 con *FLOSS exception*, che ne consente l'uso da programmi open source con licenza diversa.
