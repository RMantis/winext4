# Roadmap

Idee in ordine di priorità suggerita. L'ordine segue il rapporto tra rischio e beneficio: prima le funzioni che riusano il livello di I/O già esistente, per ultima quella che cambia di più il modello di scrittura.

| # | Funzione | Dipende da | Dimensione |
|---|---|---|---|
| 1 | Hash e verifica (immagini, dischi, file) | — | piccola |
| 2 | Controllo GUID delle partizioni | — | piccola |
| 3 | Azzeramento dello spazio libero ed export compresso | 1 per l'hash durante l'export | media |
| 4 | Aprire immagini dentro archivi (`.tar.gz`, `.img.xz`, `.zip`…) senza estrarle | 6 per scriverci | media |
| 5 | fsck integrato | — | media |
| 6 | Modifiche in sospeso (overlay / bozza) | 1 per l'impronta della sorgente, 5 è utile | grande |
| 7 | «Apri con…» per i file dentro ext e integrazione con «Apri con» di Windows | — | piccola |
| 8 | Anteprima di testo e immagini nella finestra | — | piccola |

I punti 7 e 8 sono miglioramenti dell'interfaccia indipendenti dagli altri: si possono fare in qualsiasi momento, anche per primi.

---

## 1. Hash e integrità

### Cosa serve

- **Hash di un'immagine**, di un **disco intero** o di una **singola partizione**:
  - MD5 per compatibilità, SHA-256 consigliato;
  - con progresso e annullamento.
  - Un disco o una partizione si leggono solo con nessuno in scrittura. Va fatto in sola lettura, oppure con il volume bloccato.
- **Esporta disco → immagine**, con file `.md5` / `.sha256` accanto:
  - formato `md5sum` (`<hash> *<file>`), così si verifica anche su Linux con `md5sum -c`;
  - hash calcolato **durante** la copia, in un solo passaggio;
  - opzionale: rilettura dell'immagine scritta per confermarla;
  - opzionale: file sparso per le zone a zero.
- **Scrivi immagine → disco**, controllando il `.md5`:
  1. verifica l'hash dell'immagine (prima della scrittura o durante);
  2. scrivi;
  3. rileggi il disco e confronta.
- **Hash dei file dentro il filesystem**:
  - un file, oppure una cartella in modo ricorsivo, con output compatibile `md5sum`;
  - verifica di una cartella contro un file `md5sum` esistente, per esempio generato su Linux.

### Note di implementazione

- **Lettura dei dati.** Per dischi e immagini si riusa `w4_dev_pread` (`native/src/winio.c`), che gestisce già allineamento ai settori e volumi grezzi. Per i file dentro il filesystem basta `Ext4Volume.Read`.
- **Algoritmi di hash.** Basta `System.Security.Cryptography` (MD5, SHA256) lato .NET, con lettura a blocchi da 1–4 MB in un thread separato.
- **Scrittura su disco intero: controlli di sicurezza obbligatori.**
  - Mai sul disco di sistema o di avvio.
  - Il disco deve essere grande almeno quanto l'immagine.
  - Conferma esplicita mostrando modello, dimensione e numero del disco.
  - Bloccare e smontare **tutti** i volumi del disco di destinazione, non solo uno.
  - Valutare se mettere temporaneamente offline il disco (`IOCTL_DISK_SET_DISK_ATTRIBUTES`).
- **Dopo la scrittura** va forzata la rilettura della tabella delle partizioni (`IOCTL_DISK_UPDATE_PROPERTIES`) e poi una nuova scansione.

---

## 2. Controllo GUID delle partizioni

### Cosa serve

- **Mostrare gli identificatori** nella lista dei dischi e in «Informazioni sul filesystem»:
  - **type GUID** GPT (per esempio `0FC63DAF-8483-4772-8E79-3D69D8477DE4` = Linux filesystem data), oppure il tipo MBR (`0x83`);
  - **GUID univoco** della partizione GPT (PARTUUID, quello che Linux usa in `root=PARTUUID=…`);
  - **UUID del filesystem** ext, già mostrato.
- **Segnalare le incoerenze**:
  - filesystem ext su una partizione con tipo diverso da Linux, per esempio «Microsoft basic data». Windows potrebbe proporre di formattarla o assegnarle una lettera;
  - partizione di tipo Linux che non contiene ext;
  - GUID duplicati tra dischi collegati insieme, per esempio una chiavetta clonata. Su Linux causa montaggi sbagliati, e WinExt4 li deve distinguere per disco e offset, non solo per UUID;
  - header GPT primario e di backup non coerenti, o CRC errati.
- **Opzionale, con conferma**: correggere il type GUID in «Linux filesystem». Così Windows smette di assegnare una lettera e di proporre la formattazione. Si fa con `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, che aggiorna anche il CRC e la copia di backup.

### Note

- **Dati necessari.** `IOCTL_DISK_GET_DRIVE_LAYOUT_EX` restituisce già sia il `PartitionType` sia il `PartitionId` (GPT); `scan.c` li legge senza esporli. Basta aggiungerli a `e4_part`. Per le immagini c'è già il parser GPT in `scan.c`.
- **Riconoscimento della chiavetta.** I GUID danno anche un identificatore stabile, che permette di ricordare le preferenze per singola chiavetta (lettera di unità, apertura automatica).

---

## 3. Azzeramento dello spazio libero ed export compresso

### Azzeramento dello spazio libero

Scrivere zeri su tutti i **blocchi liberi** del filesystem, secondo la bitmap dei blocchi, e solo dove non sono già a zero. I vantaggi:
- un'immagine esportata si comprime moltissimo: i dati cancellati ma non sovrascritti diventano zeri;
- l'immagine può essere salvata come **file sparso** su NTFS;
- su SSD/SD si può usare in alternativa il **discard/TRIM** dei blocchi liberi (`ext2fs_discard` e `IOCTL_STORAGE_MANAGE_DATA_SET_ATTRIBUTES` sul volume).

Come funziona:
- **Lettura prima della scrittura.** Ogni blocco libero si legge e si scrive solo se contiene qualcosa diverso da zero. Così chiavette e SD non si consumano inutilmente e l'operazione è molto più veloce su dischi già puliti.
- **Lavoro su intervalli.** Si parte dalle zone libere contigue della bitmap (`ext2fs_find_first_set_block_bitmap2`) e le si elabora a blocchi grandi, per esempio da 4 MB.
- **Sicurezza.** Va fatto con il volume bloccato in scrittura. Si escludono i blocchi del journal, che sono allocati e quindi già esclusi, e i blocchi riservati. È annullabile in qualsiasi momento, perché ogni blocco azzerato era comunque libero.
- **Partizioni intere.** Opzionalmente si azzerano anche lo spazio non partizionato e le aree tra le partizioni, quando si esporta l'intero disco.
- **Modalità «bozza».** Insieme al punto 6 l'azzeramento può stare nell'overlay: utile per esportare un'immagine compatta senza toccare il disco originale.

### Export compresso (disco o immagine)

- **Formati di output**:
  - `.img.zst` (zstd: veloce, ottimo rapporto, si decomprime con `zstd -d`);
  - `.img.gz` (compatibile ovunque);
  - `.img.xz` (il più compatto, lento);
  - `.vhdx` dinamico, apribile direttamente da Windows e Hyper-V;
  - **file sparso** non compresso: un `.img` normale che su NTFS occupa solo lo spazio dei dati.
- **Export «solo blocchi usati».** Si leggono soltanto i blocchi allocati secondo la bitmap e al posto degli altri si scrivono zeri, senza nemmeno leggerli. È più veloce dell'azzeramento e non modifica la sorgente, per cui funziona anche in sola lettura. Il prezzo è che i dati cancellati non finiscono nell'immagine: va spiegato all'utente, perché non è una copia forense.
- **Hash nello stesso passaggio** (punto 1). Si possono calcolare due hash:
  - dell'**immagine non compressa** (`.img.md5`): verifica i dati indipendentemente dalla compressione;
  - del **file compresso** (`.img.zst.md5`): verifica il download o la copia.
- **Scrittura su disco direttamente da un file compresso**: decomprimendo in streaming e verificando l'hash dell'immagine non compressa, senza estrarla prima su disco.
- **Librerie**: zstd ed xz si compilano facilmente con MSVC, e gzip c'è già in .NET (`ZLibStream`/`GZipStream`). Conviene metterle in `ext4core.dll` (zstd, liblzma) o usare pacchetti NuGet; per il VHDX dinamico c'è l'API `virtdisk` di Windows.

---

## 4. Aprire immagini dentro archivi senza estrarle

### Obiettivo

Aprire direttamente `backup.tar.gz` o `raspios.img.xz` e sfogliare, estrarre o montare il filesystem ext che contiene, **senza prima decomprimere** gigabyte su disco.

### Formati

| Formato | Accesso casuale | Note |
|---|---|---|
| `.tar` | diretto | Il membro `.img` è un intervallo contiguo dentro il tar: basta un offset. È il caso più semplice. |
| `.img.gz`, `.tar.gz`, `.tgz` | con indice | gzip è un flusso unico: serve l'indice di punti di ripresa descritto sotto. |
| `.img.zst`, `.tar.zst` | diretto se *seekable*, altrimenti indice | Il formato zstd *seekable* ha già una tabella dei frame; un `.zst` normale si tratta come gzip. |
| `.img.xz`, `.tar.xz` | per blocchi | xz ha un indice dei blocchi. Il file è veloce se è stato creato multi-blocco (`xz -T0`), lento se è un blocco unico. |
| `.zip` | per singolo membro | I membri «stored» hanno accesso diretto; quelli deflate si trattano come gzip, indicizzando solo quel membro. |
| `.bz2`, `.7z` | — | Esclusi per ora: lenti o complessi da indicizzare. |

### Come funziona l'accesso casuale

1. **Primo passaggio**:
   - decomprime tutto il flusso una volta, con progresso e annullamento, senza scrivere niente su disco;
   - nel frattempo trova i membri del tar. Basta leggere gli header ogni 512 byte, gestendo i nomi lunghi GNU/PAX e i membri sparsi GNU;
   - controlla quali membri contengono un filesystem ext o una tabella delle partizioni.
2. **Indice dei punti di ripresa**, la tecnica di `zran.c` di zlib e di `indexed_gzip`:
   - ogni ~1–4 MB di dati decompressi salva la posizione nel file compresso, la posizione decompressa, i bit residui e gli ultimi 32 KB decompressi, che sono il dizionario di deflate;
   - per leggere all'offset X si riparte dal punto precedente più vicino, con `inflateSetDictionary`, e si decomprime al massimo un intervallo.
3. **Cache su disco dell'indice**:
   - si salva in `%LOCALAPPDATA%\WinExt4\index\`, con chiave dimensione + data + hash dei primi e degli ultimi MB dell'archivio;
   - le aperture successive sono immediate;
   - pesa circa 32 KB per punto: per esempio ~8 MB per un'immagine da 1 GB con un punto ogni 4 MB.
4. **Cache in memoria** dei blocchi decompressi recenti (LRU, qualche decina di MB). libext2fs legge molti metadati vicini tra loro, e la cache a blocchi esistente in `winio.c` assorbe il resto.

### Implementazione

- **Nuovo tipo di sorgente** in `winio.c`, accanto a «file» e «volume»: `w4_dev` con `pread` che passa dall'indice. Il resto di ext4core non cambia, perché vede un normale dispositivo a blocchi.
- **Librerie**:
  - zlib o miniz per deflate, che serve un'API a basso livello per gestire il dizionario;
  - zstd e liblzma, le stesse del punto 3.
- **Scansione**: `e4_enum_image` riconosce l'archivio dai magic number (non dall'estensione) e, come oggi, restituisce una sorgente per ogni filesystem trovato in ogni membro. Per esempio `backup.tar.gz → sd-card.img → partizione 2`.
- **Sola lettura per natura**: la scrittura dentro un archivio passa dall'overlay del punto 6. Le modifiche vanno nel delta e alla fine si **esporta** una nuova immagine, eventualmente già compressa (punto 3). Senza overlay, «Sblocca scrittura» resta disattivato con una spiegazione.
- **Hash**: il primo passaggio può calcolare gratis l'hash del membro decompresso (punto 1), da confrontare con un eventuale `.md5` accanto all'archivio.

### Limiti da comunicare

- **Primo accesso**: richiede un passaggio completo di decompressione, circa quanto `gunzip` sullo stesso file, ma senza consumare spazio su disco.
- **gzip e xz a blocco unico**: le letture sparse sono più lente che su un `.img` normale. Per un uso intensivo, come il montaggio con molte letture casuali, va proposto «Estrai come .img».

---

## 5. fsck

### Obiettivo

Controllare e riparare il filesystem da Windows, senza passare da Linux. Oggi un filesystem marcato con errori viene aperto solo in sola lettura e serve `e2fsck` su Linux.

### Approccio

- **Portare `e2fsck` di e2fsprogs** con la stessa tecnica già usata per libext2fs:
  - compilazione MSVC;
  - shim POSIX in `native/port`;
  - `io_manager` WinExt4.

  `e2fsck` è più grande (circa 40k righe) e usa più POSIX: segnali, `getopt`, terminale. Va però compilato come **libreria** chiamata dalla DLL, non come programma interattivo:
  - risposte alle domande gestite dal codice (modalità `-n` / `-p` / `-y`);
  - output e barra di progresso (`-C`) inviati alla GUI.
- **Modalità**:
  1. **Controlla (sola lettura, `-n`)**: sempre sicuro, nessun blocco del volume.
  2. **Ripara automaticamente (`-p`)**: solo correzioni sicure; volume bloccato.
  3. **Ripara tutto (`-y`)**: con conferma esplicita; volume bloccato.
- **Integrazione nella GUI**: proporlo da solo quando il superblocco segnala una di queste condizioni:
  - smontaggio non pulito;
  - errori;
  - orfani;
  - superamento del numero massimo di montaggi o dell'intervallo di controllo.

  Dopo una riparazione riuscita la scrittura torna disponibile.
- **Insieme all'overlay (punto 6)**: eseguire la riparazione **nell'overlay**, mostrare cosa cambierebbe e applicarla solo su conferma. È un «fsck con anteprima», che i tool di Linux non offrono.

### Ripiego temporaneo

Per le **immagini**, se WSL è installato, si può lanciare `e2fsck` di WSL sul file. Per i dischi grezzi no: servirebbe `wsl --mount`.

---

## 6. Modifiche in sospeso (overlay / «bozza»)

### Obiettivo

Aprire un disco o un'immagine e modificarlo **senza scrivere subito sulla sorgente**:
- le modifiche finiscono in un file separato;
- puoi chiudere tutto, riavviare il PC o portare quel file su un altro PC insieme all'immagine;
- quando hai finito decidi cosa fare:
  - **applicare** le modifiche al disco o all'immagine (merge);
  - **esportare** una nuova immagine finale;
  - **scartarle**.

### Overlay a blocchi oppure a file?

| | **A. Overlay a blocchi (copy-on-write)** | B. Overlay a file (stile overlayfs) |
|---|---|---|
| Come funziona | Il livello di I/O redirige ogni blocco scritto in un file «delta»; le letture guardano prima il delta, poi la sorgente | File modificati copiati interi in una cartella «upper», con *whiteout* per le cancellazioni |
| Semantica ext4 | **Completa e gratuita**: libext2fs continua a fare tutto (permessi, link, xattr, journal, cartelle grandi) | Va reimplementata: rename di cartelle, hardlink, metadati, cancellazioni |
| Leggibilità del delta | Binario | Si vedono i file modificati in una cartella |
| Merge | Ricopiare i blocchi: banale e **idempotente** | Riapplicare operazioni sui file: complesso, con conflitti |
| Dimensione | Proporzionale ai blocchi toccati | Proporzionale ai file toccati, e un file grande viene copiato intero anche per un byte |

Il vero overlayfs è una funzione del kernel Linux. Su Windows si potrebbe usare solo dentro WSL, che è proprio ciò che vogliamo evitare.

**Scelta consigliata: A, overlay a blocchi.** Per avere la comodità di B si aggiunge una vista **«Modifiche»**: un confronto tra l'albero dei file della sorgente e quello di sorgente+delta, con l'elenco dei file aggiunti, modificati ed eliminati, calcolato al volo. Il formato resta semplice e robusto, e l'utente vede comunque cosa ha cambiato.

### Formato del file delta (bozza)

```
header   magic "WX4DELTA", versione, dimensione blocco I/O
         impronta della sorgente: UUID ext, dimensione, s_wtime/s_kbytes_written,
         hash del superblocco e dei descrittori di gruppo, (opz.) hash completo
indice   mappa blocco sorgente -> offset nel delta (B-tree o tabella a pagine)
dati     blocchi scritti
log      record append-only con checksum, uno per ogni "checkpoint"
         (ogni ext2fs_flush), così un crash lascia sempre l'ultimo stato coerente
```

- **Formati esistenti come alternativa.** Si potrebbe usare un formato già noto:
  - per le immagini, una **VHDX differenziale** con padre l'immagine, gestita dall'API `virtdisk` di Windows;
  - **qcow2**, che libext2fs sa già in parte leggere.

  La VHDX differenziale però funziona solo se la sorgente è un VHD(X), non con un disco fisico. Il formato proprio vale per entrambi i casi.

### Merge (applica)

1. Verificare che la sorgente **non sia cambiata** dall'apertura, confrontando l'impronta. Se è cambiata, rifiutare e offrire l'esportazione come nuova immagine.
2. Bloccare il volume e scrivere i blocchi del delta nell'ordine del log.
3. Ricopiare un blocco con contenuto assoluto è **idempotente**: se il merge si interrompe (crash, USB staccata), alla riapertura riprende da capo e il risultato è lo stesso. Il delta va eliminato solo dopo il flush e la rilettura di verifica.
4. Opzionale: hash prima e dopo (punto 1), ed `e2fsck -n` sul risultato (punto 5).

### Esporta immagine

Sorgente + delta → nuovo `.img`, con il `.md5` del punto 1. La sorgente resta intatta.

### Casi delicati

- **Sorgente con journal da riprodurre**: la riproduzione avviene nell'overlay, quindi anche questo è annullabile.
- **Spazio**: il delta cresce fino a, al massimo, la dimensione del filesystem. Va mostrato lo spazio usato e avvisato quando cresce troppo.
- **Montaggio WinFsp sopra l'overlay**: nessun cambiamento, perché lavora sopra `Ext4Volume`. Il blocco del volume sorgente non serve più, dato che non ci si scrive.
- **Portabilità**: il delta va riconosciuto anche se la sorgente su un altro PC ha un percorso diverso. Contano l'UUID e l'impronta, non il percorso.

### UX

- Pulsante **«Modifica come bozza»** accanto a «Sblocca scrittura»: diventa la modalità consigliata per le chiavette.
- Indicatore «Modifiche in sospeso: 12 file, 340 MB» con i pulsanti **Applica / Esporta immagine / Scarta / Mostra modifiche**.
- Riaprendo un disco per cui esiste una bozza: «Hai modifiche in sospeso del 24/10, vuoi riprenderle?».

---

## 7. «Apri con…»

### Dentro WinExt4: aprire un file ext con un programma a scelta

- **Menu contestuale.** Si aggiunge **«Apri con…»** accanto ad «Apri». Il file viene copiato in una cartella temporanea, come fa già il doppio clic. Poi si mostra la finestra standard di Windows per scegliere il programma (`SHOpenWithDialog`), con l'opzione «usa sempre questa app» lasciata a Windows.
- **Sottomenu rapido.** Elenca i programmi registrati per quell'estensione (`SHAssocEnumHandlers`), con le loro icone, come fa Esplora risorse.
- **Riportare le modifiche nel disco ext.** Oggi la copia temporanea è in sola lettura. Con il volume aperto in scrittura (o in modalità bozza, punto 6) si può:
  - aprire la copia modificabile;
  - osservarla con `FileSystemWatcher`;
  - quando il programma la salva, mostrare «*foto.jpg è stato modificato: aggiornarlo nel disco ext?*» con le scelte Aggiorna / Ignora / Aggiorna sempre per questa sessione.

  Va scritto con lo stesso percorso di «Importa», che conserva proprietario e permessi del file originale. Se nel frattempo il file sul disco ext è cambiato (mtime diverso), bisogna avvisare.
- **Pulizia.** Le cartelle temporanee vanno eliminate alla chiusura del volume. Se un file modificato non è stato riportato, prima si chiede conferma.
- **Più file insieme.** «Apri» o «Apri con» su più file selezionati, per esempio più immagini in un visualizzatore.

### In Windows: WinExt4 nel menu «Apri con» di Esplora risorse

- **Registrazione per utente** (`HKCU\Software\Classes`, niente privilegi di amministratore):
  - `Applications\WinExt4.exe` con `SupportedTypes`;
  - `OpenWithProgids` per `.img`, `.raw`, `.bin`, `.dd`, `.ext2/3/4`, `.vhd`, e per gli archivi del punto 4 (`.img.gz`, `.img.xz`, `.tar.gz`…).

  Così WinExt4 compare in «Apri con» senza togliere a nessuno l'associazione predefinita.
- **Voce di menu sui file immagine.** Opzionale: «Apri con WinExt4» nel menu contestuale classico, tramite un verbo `shell\OpenWithWinExt4`.
- **Dove attivarla**: una casella nelle Impostazioni, e nell'installer citato in «Altre idee».
- **Già pronto**: la seconda istanza passa già il file a quella in esecuzione (anche a quella in background), quindi l'apertura da Esplora risorse non avvia un secondo processo.

---

## 8. Anteprima di file di testo e immagini

### Obiettivo

Vedere al volo il contenuto dei file selezionati, senza estrarli né aprire altri programmi. L'anteprima è sempre in sola lettura e non modifica niente, quindi è utile anche per controllare una chiavetta prima di sbloccare la scrittura.

### Interfaccia

- **Pannello laterale** a destra della lista, attivabile con un pulsante o con Alt+P come in Esplora risorse. Mostra l'anteprima del file selezionato.
- **Barra spaziatrice** per un'anteprima a tutto schermo (stile «Quick Look»). Con ← → si passa al file precedente o successivo della cartella.

### Tipi supportati

- **Testo**:
  - `.txt`, `.log`, `.conf`, `.cfg`, `.ini`, `.sh`, `.py`, `.json`, `.xml`, `.yaml`, `.md`, `.service`, `.desktop`, file di `/etc` senza estensione…;
  - per i file senza estensione decide il contenuto: è testo se non ci sono byte NUL nei primi KB e l'UTF-8 è valido;
  - codifica UTF-8, con ripiego su Latin-1, e fine riga LF o CRLF;
  - carattere monospazio e a capo automatico disattivabile;
  - lettura limitata ai primi 1–2 MB, con l'avviso «file troncato, mostrati i primi 2 MB».
- **Immagini**: PNG, JPEG, GIF (primo fotogramma), BMP, TIFF, ICO tramite i decoder di WPF; WebP, HEIC e AVIF se Windows ha i relativi codec WIC. Il comportamento previsto:
  - adattamento alla finestra e zoom;
  - dimensioni in pixel;
  - decodifica ridotta alla dimensione del pannello (`DecodePixelWidth`) per non caricare in memoria foto enormi.
- **Altri file**:
  - vista **esadecimale** dei primi KB;
  - riconoscimento del tipo dal contenuto (magic number: ELF, gzip, zip, PDF, SQLite…);
  - per i link simbolici, la destinazione e l'anteprima del file a cui puntano.
- **Successivamente**: PDF e documenti tramite i *preview handler* di Windows (`IPreviewHandler`), gli stessi del riquadro di anteprima di Esplora risorse.

### Implementazione

- **Lettura senza file temporanei.** I dati si leggono direttamente con `Ext4Volume.Read`, solo la parte necessaria: per il testo i primi MB, per le immagini il file intero ma con un limite, per esempio 50 MB.
- **Interfaccia reattiva.** La lettura avviene in background e viene annullata se la selezione cambia, così scorrere velocemente la lista resta fluido.
- **Unità montata.** Per i file aperti dall'unità di Windows il riquadro di anteprima di Esplora risorse funziona già. Il punto 8 serve per la finestra di WinExt4, che lavora anche senza WinFsp e in sola lettura.

---

## Altre idee

- Pulsante «Espelli» che chiude il volume e richiede la rimozione sicura del dispositivo (`CM_Request_Device_Eject`).
- Test su dischi con settori fisici da 4K (4Kn) e su schede SD reali.
- Installer firmato, con registrazione in «Apri con» (punto 7) e opzione per installare WinFsp.
- Modifica degli attributi estesi e delle ACL POSIX dalla finestra Proprietà.
