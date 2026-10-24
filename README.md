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
   - vedere l'**anteprima** del file selezionato nel pannello a destra (Alt+P per mostrarlo o nasconderlo): testo UTF-8/Latin-1, immagini PNG/JPEG/GIF/BMP/TIFF/ICO (e WebP/HEIC/AVIF se Windows ha i codec), vista esadecimale per gli altri file. La **barra spaziatrice** apre l'anteprima grande, ← → passano al file vicino;
   - fare doppio clic (o Invio) su un file per aprirne una copia temporanea, oppure usare **Apri con…** nel menu contestuale (i programmi registrati per quel tipo di file, con le loro icone, più «Scegli un'altra app…»).
4. **Sblocca scrittura** riapre la partizione in lettura/scrittura:
   - la partizione viene bloccata e smontata per Windows, così nessun altro programma la tocca;
   - se il journal contiene transazioni non applicate (disco staccato da Linux senza smontarlo), vengono riprodotte prima di scrivere;
   - a questo punto puoi importare file e cartelle (anche trascinandoli nella finestra), rinominare, eliminare, creare cartelle e cambiare permessi e proprietario.
   - i file aperti con un altro programma sono copie modificabili: quando il programma salva, WinExt4 chiede se aggiornare il file sul disco ext, mantenendo proprietario e permessi Linux. Alla chiusura del volume le copie temporanee vengono eliminate, e se qualcuna non è stata riportata te lo chiede prima.
5. **Monta su Windows** assegna una lettera di unità e il disco si usa da Esplora risorse e da qualsiasi programma. Prima del montaggio una finestra spiega come vengono trattati permessi e attributi e chiede proprietario e permessi da dare ai nuovi file.
6. **Chiudi** (oppure torna in sola lettura) **prima di scollegare** un disco aperto in scrittura.

### Modifica come bozza

**Modifica come bozza** permette di modificare un disco, un'immagine o anche un'immagine dentro un archivio **senza scrivere sulla sorgente**:
- ogni blocco modificato finisce in un file `.wx4draft` (in `%LOCALAPPDATA%\WinExt4\drafts`); la sorgente viene solo letta, quindi un disco non viene nemmeno bloccato per Windows;
- si lavora come in scrittura normale: importare, rinominare, eliminare, cambiare permessi, anche montando la bozza su Windows;
- la bozza sopravvive a chiusure, crash e riavvii: ogni salvataggio di libext2fs è un punto di ripristino, e quello che è stato scritto dopo l'ultimo viene scartato. Si riprende con «Modifica come bozza» (WinExt4 propone la bozza esistente) o aprendo il file `.wx4draft`, anche su un altro PC: la sorgente viene ritrovata dall'impronta (UUID e hash dell'inizio del filesystem), non dal percorso;
- il banner della bozza offre:
  - **Modifiche…**: l'elenco dei file aggiunti, modificati ed eliminati rispetto alla sorgente;
  - **Esporta immagine…**: sorgente + bozza in una nuova immagine (anche compressa o VHD, con `.md5`/`.sha256`), senza toccare la sorgente;
  - **Applica…**: scrive i blocchi della bozza sulla sorgente. Viene rifiutata se la sorgente è cambiata dopo la creazione della bozza. Se si interrompe si ripete e il risultato è lo stesso;
  - **Scarta**: elimina la bozza.
- se la sorgente aveva un journal da riprodurre, la riproduzione avviene nella bozza.

Le immagini si aprono in tre modi: con «Apri immagine», trascinando il file nella finestra, oppure da Esplora risorse con «Apri con WinExt4». Quest'ultima voce si attiva in *Impostazioni → Mostra WinExt4 in «Apri con»*: la registrazione è per il solo utente e non cambia il programma predefinito di nessun tipo di file. Sono supportati:
- filesystem «nudi», creati con `mkfs.ext4 file.img`;
- immagini disco con tabella MBR (anche partizioni logiche);
- immagini disco con tabella GPT.

### Proprietà della partizione

Pulsante ⓘ (senza file selezionati) oppure clic destro su una partizione → **Proprietà…**:
- **Generale**: spazio occupato/libero/riservato con grafico, inode, cartelle e conteggio rapido dei file (scansione delle tabelle degli inode, senza percorrere le cartelle);
- **Partizione**: tabella, GUID del disco, PARTUUID, tipo, stato delle due copie GPT; si possono cambiare **nome**, **tipo** e **attributi** GPT (per esempio «nessuna lettera di unità», che impedisce a Windows di assegnarle una lettera) o il tipo MBR;
- **Filesystem**: funzionalità con spiegazione, journal, contatori, date ed errori registrati; con la scrittura sbloccata si cambiano **etichetta**, **UUID**, **spazio riservato** a root e chi può usarlo, controlli periodici, comportamento in caso di errore e opzioni di montaggio predefinite (come `tune2fs`/`e2label`). Per i filesystem con `metadata_csum` il cambio di UUID richiede prima `metadata_csum_seed`, che la finestra propone di attivare;
- **Avanzate**: riepilogo stile `dumpe2fs -h` da copiare.

### Hash, immagini e scrittura su disco

Clic destro su una partizione nell'elenco a sinistra:
- **Calcola hash** della partizione, del disco intero o dell'immagine intera (MD5 e SHA-256 in un solo passaggio, con confronto con un hash incollato e salvataggio di `.md5`/`.sha256`).
- **Azzera lo spazio libero** (volume aperto in scrittura): scrive zeri sui blocchi liberi che non lo sono già, leggendo prima per non consumare inutilmente chiavette e SD. I dati cancellati diventano irrecuperabili e le immagini esportate, soprattutto compresse, molto più piccole.
- **Esporta come immagine** (partizione o disco intero) in formato `.img` (anche sparso), `.img.gz`, `.img.zst`, `.img.xz` (multi-blocco, compressione parallela) o **VHD dinamico** (si apre direttamente in Windows/Hyper-V). L'hash viene calcolato durante la copia: accanto al file vengono creati `.md5`/`.sha256` in formato `md5sum` (per i formati compressi una coppia per il file e una, `disco.img.sha256`, per i dati decompressi). Opzione di rilettura di verifica.
- **Scrivi un'immagine** sulla partizione o sul disco intero, anche direttamente da `.gz`, `.zst`, `.xz` o `.vhd` (decompressione in streaming, preceduta da un passaggio di verifica che ne ricava dimensione e hash): se accanto all'immagine c'è un `.sha256`/`.md5` (o `SHA256SUMS`) viene verificata prima di scrivere; dopo la scrittura il disco viene riletto e confrontato. Il disco da cui gira Windows non è mai una destinazione ammessa, la dimensione viene controllata, le unità del disco vengono smontate e serve una conferma esplicita.

Nel menu dei file: **Calcola hash → MD5 / SHA-256** produce un elenco in formato `md5sum`/`sha256sum` (anche ricorsivo sulle cartelle), e **Verifica con un file md5sum/sha256sum** controlla i file della cartella corrente contro un elenco (selezionato nel disco ext o scelto da Windows), segnalando file diversi o mancanti.

### Controllo delle partizioni

Per ogni partizione WinExt4 legge tipo (GUID GPT o tipo MBR), PARTUUID, nome e attributi GPT, e verifica CRC e copia di backup della tabella GPT. Accanto alla partizione compare un ⚠ (con i dettagli nel suggerimento e in un avviso quando la apri) se:
- un filesystem ext sta su una partizione di tipo non Linux (per esempio «Microsoft basic data»): con **Imposta tipo «Linux»** si corregge la sola tabella delle partizioni, senza toccare i dati;
- una partizione di tipo Linux non contiene ext;
- la tabella GPT principale o quella di backup è danneggiata, o le due copie non coincidono;
- due dischi collegati hanno lo stesso UUID del filesystem o lo stesso PARTUUID (tipico delle chiavette clonate).

I dettagli sono anche nel pulsante ⓘ (informazioni sul filesystem).

### Immagini dentro archivi

«Apri immagine» (o il trascinamento) accetta anche archivi: **.tar**, **.tar.gz/.tgz**, **.img.gz** (anche multi-membro), **.img.xz/.tar.xz**, **.img.zst/.tar.zst** e **.zip** (membri memorizzati o deflate, anche zip64). Le immagini dentro l'archivio compaiono nell'elenco come le altre e si sfogliano, si estraggono, si montano (in sola lettura), se ne calcola l'hash o si esportano, **senza estrarle prima**:
- tar e zip memorizzati si leggono direttamente;
- gzip e zip deflate: al primo uso l'archivio viene letto una volta per costruire un indice di punti di ripresa ogni ~4 MB (come `zran` di zlib), salvato in `%LOCALAPPDATA%\WinExt4\index`: le aperture successive sono immediate;
- xz multi-blocco (`xz -T0`, o creato da WinExt4) e zstd a più frame si leggono un blocco alla volta;
- xz e zstd a blocco unico vengono decompressi una volta in una cache sparsa (`%LOCALAPPDATA%\WinExt4\cache`, ripulita dopo 14 giorni di inutilizzo).

Le immagini negli archivi sono in sola lettura; per modificarle si usa **Modifica come bozza** e poi **Esporta immagine**.

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
- `tests/draft_test.sh` prova le bozze con `e4cli` (sorgente intatta, ripresa dopo una scrittura interrotta, impronta, applicazione idempotente verificata con `e2fsck`/`debugfs`); `tests/ui/draft_logic_test.ps1` e `tests/ui/draft_test.ps1` fanno lo stesso dalla libreria .NET e dalla GUI.

## Struttura

```
native/            ext4core.dll (C, MSVC)
  src/ext4core.c   API: open/close, lookup, readdir, read/write, create, mkdir, rename, unlink, ...
  src/winio.c      io_manager di libext2fs per volumi grezzi e immagini (allineamento settori, lock del volume, cache)
  src/scan.c       ricerca delle partizioni ext su dischi fisici e immagini MBR/GPT
  src/overlay.c    bozze: delta copy-on-write a blocchi da 4 KB, commit, applicazione
  tools/e4cli.c    piccolo client a riga di comando usato dai test
src/WinExt4/       GUI WPF (.NET 8) + montaggio WinFsp (Mount/Ext4FileSystem.cs)
tests/             immagini di prova e test end-to-end
third_party/       e2fsprogs (sottomodulo)
```

## Roadmap

Le prossime funzioni previste sono descritte in [docs/ROADMAP.md](docs/ROADMAP.md):
- fsck integrato.

## Licenza

GPL-2.0. Il motivo è che vengono incluse parti di e2fsprogs sotto GPL: il codice di riproduzione del journal di `debugfs` ed `e2fsck`. libext2fs è invece LGPL. WinFsp è GPLv3 con *FLOSS exception*, che ne consente l'uso da programmi open source con licenza diversa.
