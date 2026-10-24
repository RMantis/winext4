# Roadmap

Idee in ordine di priorità suggerita. ✅ = implementato. L'ordine segue il rapporto tra rischio e beneficio: prima le funzioni che riusano il livello di I/O già esistente, per ultima quella che cambia di più il modello di scrittura.

| # | Funzione | Dipende da | Dimensione |
|---|---|---|---|
| 1 | ✅ Hash e verifica (immagini, dischi, file) | — | piccola |
| 2 | ✅ Controllo GUID delle partizioni | — | piccola |
| 3 | ✅ Azzeramento dello spazio libero ed export compresso | 1 per l'hash durante l'export | media |
| 4 | ✅ Aprire immagini dentro archivi (`.tar.gz`, `.img.xz`, `.zip`…) senza estrarle | 6 per scriverci | media |
| 5 | fsck integrato | — | media |
| 6 | ✅ Modifiche in sospeso (overlay / bozza) | 1 per l'impronta della sorgente, 5 è utile | grande |
| 7 | ✅ «Apri con…» per i file dentro ext e integrazione con «Apri con» di Windows | — | piccola |
| 8 | ✅ Anteprima di testo e immagini nella finestra | — | piccola |
| 9 | ✅ Proprietà della partizione: tutte le informazioni e modifica dei parametri modificabili | 2 per i GUID | media |

I punti 7, 8 e 9 sono miglioramenti dell'interfaccia indipendenti dagli altri: si possono fare in qualsiasi momento, anche per primi.

---

## 1. ✅ Hash e integrità

*Implementato* (hash, export con `.md5`/`.sha256` e file sparso, scrittura con verifica preventiva e rilettura, protezione del disco di sistema, hash e verifica dei file interni). La scrittura su disco intero è da verificare su hardware reale.

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

## 2. ✅ Controllo GUID delle partizioni

*Implementato:* lettura di tipo/PARTUUID/nome/attributi, verifica delle due copie GPT, avvisi di incoerenza e duplicati, correzione del tipo (`e4_edit_partition`, anche per nome e attributi). La modifica sui dischi fisici è da verificare su hardware reale. Resta da fare: preferenze ricordate per singola chiavetta.

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

## 3. ✅ Azzeramento dello spazio libero ed export compresso

*Implementato:* azzeramento con lettura preventiva, export in `.img` sparso / `.img.gz` / `.img.zst` (con checksum) / `.img.xz` (multi-blocco) / VHD dinamico, doppio hash (file e contenuto), scrittura su disco direttamente da file compressi o VHD. Al posto del VHDX c'è il VHD dinamico (formato più semplice, apribile da Windows). Restano da fare: TRIM/discard dei blocchi liberi, azzeramento delle zone non partizionate, export «solo blocchi usati» e azzeramento nell'overlay.

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

## 4. ✅ Aprire immagini dentro archivi senza estrarle

*Implementato:* dispositivi virtuali in ext4core (`e4_open_cb`/`e4_enum_cb`), decompressore deflate proprio con punti di ripresa e indice salvato, xz per blocchi (stream sintetico a blocco singolo passato a liblzma), zstd per frame, cache sparsa per gli stream a blocco unico, tar (GNU/PAX) e zip (zip64). La scrittura passa dalla bozza (punto 6); restano da fare la scrittura su disco direttamente da un membro di un archivio e i VHD con accesso casuale.

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

## 6. ✅ Modifiche in sospeso (overlay / «bozza»)

*Implementato* con l'overlay a blocchi (scelta A), in `native/src/overlay.c`:
- **formato** `.wx4draft`: header da 4 KB (magic `WX4DRAFT`, dimensione della sorgente, impronta SHA-512 dei primi 128 KB, etichetta, UUID, descrizione, stato di applicazione, crc32c), poi record da 32 byte + 4 KB di dati con crc32c, e record di *commit* scritti a ogni flush di libext2fs. Alla riapertura i record dopo l'ultimo commit vengono scartati;
- i blocchi riscritti più volte tra due commit si aggiornano sul posto, e alla chiusura il file viene compattato (riscritto a parte e sostituito con `MoveFileEx`) quando più di metà è spazio superato;
- **applica**: verifica l'impronta, scrive i blocchi con lo stato «in corso» nell'header e poi «applicata». Ripetere un'applicazione interrotta dà lo stesso risultato;
- **esporta**: la bozza viene riaperta in sola lettura (`E4_OPEN_DRAFT_VIEW`), così l'immagine esportata risulta pulita, e passa dall'export del punto 3;
- **modifiche**: confronto tra l'albero della sorgente e quello della bozza;
- funziona anche su immagini dentro archivi (solo esportazione) e con il montaggio WinFsp.

Differenze rispetto al progetto qui sotto: l'impronta usa l'hash dell'inizio del filesystem (superblocco e descrittori di gruppo) invece di un hash completo, e l'indice è ricostruito in memoria alla riapertura invece di essere salvato nel file.

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

## 7. ✅ «Apri con…»

*Implementato.* Resta da fare: aprire più file insieme con un singolo programma (oggi ognuno viene aperto separatamente).

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

## 8. ✅ Anteprima di file di testo e immagini

*Implementato* (pannello, anteprima grande con la barra spaziatrice, vista esadecimale, riconoscimento del tipo dal contenuto). Resta da fare: PDF e documenti tramite i *preview handler* di Windows.

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

## 9. ✅ Proprietà della partizione

*Implementato.* Il test su disco reale (`tests/vhd_tools_test.ps1`, da eseguire come amministratore) è da completare.

### Obiettivo

Una finestra **Proprietà** per la partizione o l'immagine aperta, simile a quella dei dischi in Windows, che mostra **tutto** ciò che si sa su partizione e filesystem. I parametri che si possono cambiare in sicurezza diventano modificabili.

Oggi esiste solo «Informazioni sul filesystem» (pulsante ⓘ senza selezione): è in sola lettura e mostra un sottoinsieme dei dati.

### Informazioni mostrate

**Partizione** (per le immagini, quelle della tabella interna all'immagine):
- disco: modello, bus (USB/SD/interno), numero del disco, dimensione totale;
- tabella delle partizioni (MBR o GPT), numero di partizione, offset di inizio e fine, dimensione;
- **type GUID** GPT con nome leggibile («Linux filesystem data»), oppure tipo MBR (`0x83`);
- **GUID univoco** della partizione (PARTUUID) e **nome della partizione** GPT;
- attributi GPT (sola lettura, nascosta, *no automount*, obbligatoria);
- percorso del volume Windows e lettera di unità, se Windows ne ha assegnata una.

**Filesystem** (le stesse informazioni di `dumpe2fs -h`, in forma leggibile):
- tipo (ext2/3/4), **etichetta**, **UUID**;
- **spazio**: totale, **occupato**, **libero**, disponibile per gli utenti normali, riservato a root (in % e in blocchi). Un grafico a barre mostra occupato / libero / riservato;
- **inode**: totali, usati, liberi, dimensione dell'inode;
- **numero di file e di cartelle**:
  - le cartelle si contano subito, sommando i contatori dei descrittori di gruppo;
  - per i file c'è un pulsante «Conta» che percorre l'albero con progresso e annullamento. Dà il numero esatto di file regolari, cartelle, link simbolici e file speciali, più i file più grandi;
- dimensione del blocco, blocchi per gruppo, numero di gruppi, `flex_bg`;
- **funzionalità** attive (`extent`, `64bit`, `metadata_csum`, `inline_data`, `huge_file`…), ognuna con una descrizione al passaggio del mouse;
- **journal**: presenza, dimensione, stato (pulito / da riprodurre);
- **date**: creazione del filesystem, ultimo montaggio, ultima scrittura, ultimo controllo;
- contatore dei montaggi e massimo, intervallo tra i controlli;
- ultimo punto di montaggio su Linux;
- **stato**: pulito, non smontato correttamente, con errori. Se ci sono errori: numero, primo e ultimo errore registrati (funzione, inode, blocco, data);
- opzioni di montaggio predefinite, comportamento in caso di errore (*continue* / *remount-ro* / *panic*), uid/gid riservati, sistema che l'ha creato, revisione;
- pulsante **«Copia tutto»** e vista «grezza» stile `dumpe2fs -h`, per incollare le informazioni in una segnalazione.

### Cosa si può modificare

Le modifiche richiedono la scrittura sbloccata (o la modalità bozza, punto 6) e una conferma che mostra il valore vecchio e quello nuovo. Funzionano come `tune2fs` o `e2label`: si modifica il superblocco e si scrive (`ext2fs_mark_super_dirty` + flush). Il checksum del superblocco viene ricalcolato in automatico.

| Parametro | Equivalente Linux | Note |
|---|---|---|
| **Etichetta** del filesystem | `e2label` | Massimo 16 byte UTF-8. |
| **UUID** del filesystem (nuovo casuale o specificato) | `tune2fs -U` | Va avvisato che `/etc/fstab`, GRUB o initramfs potrebbero farvi riferimento. Con `metadata_csum` e senza `metadata_csum_seed`, cambiare l'UUID obbliga a riscrivere tutti i checksum: proporre invece di attivare `metadata_csum_seed` (supportato dal kernel 4.4 in poi), che lo rende un'operazione sul solo superblocco. |
| **Blocchi riservati** a root (%) e uid/gid riservati | `tune2fs -m / -r / -u / -g` | Utile su chiavette e dischi di dati, dove il 5% riservato è spazio sprecato. |
| Numero massimo di montaggi, intervallo tra i controlli, azzeramento del contatore | `tune2fs -c / -i / -C` | |
| Comportamento in caso di errore | `tune2fs -e` | |
| Opzioni di montaggio predefinite (`acl`, `user_xattr`, `discard`…) | `tune2fs -o` | |
| Cancellare l'ultimo punto di montaggio | `tune2fs -M` | |
| **Nome della partizione** GPT | `sgdisk -c` | Solo su GPT. |
| **Type GUID** / tipo MBR | `sgdisk -t`, `sfdisk --part-type` | Collegato al punto 2. |
| **Attributi** GPT (per esempio *no automount*, che impedisce a Windows di assegnare una lettera) | `sgdisk -A` | |

**Modifiche alla tabella delle partizioni:**
- su un disco reale si fanno con `IOCTL_DISK_SET_DRIVE_LAYOUT_EX`, che aggiorna anche l'header di backup e i CRC;
- dentro un'immagine si riscrivono header primario e di backup, con i CRC ricalcolati da noi.

**Non modificabili** (si mostrano ma non si cambiano), con spiegazione:
- dimensione del blocco e dell'inode, numero di inode: richiederebbero di ricreare il filesystem;
- attivazione o disattivazione di funzionalità «strutturali» (`64bit`, `extent`, `metadata_csum`…): troppo rischiose senza i controlli completi di `tune2fs`/`e2fsck`. Eventualmente in futuro, insieme a fsck (punto 5).
- **ridimensionamento** di partizione e filesystem (`resize2fs`): è una funzione a sé, da valutare separatamente (vedi «Altre idee»).

### Implementazione

- **Dati del filesystem.** Vanno ampliati `e4_get_info` / `e4_info` in `ext4core`: date di creazione e di controllo, contatori, errori registrati, opzioni predefinite, campi del journal, conteggio delle cartelle dai descrittori di gruppo. Poi una nuova funzione `e4_set_super(fs, campo, valore)` con una lista chiusa di campi ammessi, invece di esporre il superblocco grezzo.
- **Dati della partizione.** `scan.c` legge già il layout del disco: bisogna esporre in `e4_part` type GUID, PARTUUID, nome e attributi GPT. Per le immagini c'è il parser GPT esistente.
- **Interfaccia.** Una finestra con schede «Generale» (grafico e dati principali), «Partizione», «Filesystem», «Avanzate» (vista grezza). I campi modificabili diventano attivi solo con la scrittura sbloccata, con un pulsante «Applica» e conferma esplicita.
- **Collegamenti con gli altri punti.** È il posto naturale per:
  - i pulsanti «Calcola hash» (punto 1);
  - i controlli e la correzione dei GUID (punto 2);
  - «Azzera lo spazio libero» ed «Esporta immagine» (punto 3);
  - «Controlla / Ripara» (punto 5).

---

## Altre idee

- Pulsante «Espelli» che chiude il volume e richiede la rimozione sicura del dispositivo (`CM_Request_Device_Eject`).
- Test su dischi con settori fisici da 4K (4Kn) e su schede SD reali.
- Installer firmato, con registrazione in «Apri con» (punto 7) e opzione per installare WinFsp.
- Modifica degli attributi estesi e delle ACL POSIX dalla finestra Proprietà.
- Ridimensionamento di partizione e filesystem (porting di `resize2fs`).
