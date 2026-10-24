# Roadmap

Idee in ordine di priorità suggerita. L'ordine segue il rapporto tra rischio e beneficio: prima le funzioni che riusano il livello di I/O già esistente, per ultima quella che cambia di più il modello di scrittura.

| # | Funzione | Dipende da | Dimensione |
|---|---|---|---|
| 1 | Hash e verifica (immagini, dischi, file) | — | piccola |
| 2 | Controllo GUID delle partizioni | — | piccola |
| 3 | Azzeramento dello spazio libero ed export compresso | 1 per l'hash durante l'export | media |
| 4 | fsck integrato | — | media |
| 5 | Modifiche in sospeso (overlay / bozza) | 1 per l'impronta della sorgente, 4 è utile | grande |

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
- **Modalità «bozza».** Insieme al punto 5 l'azzeramento può stare nell'overlay: utile per esportare un'immagine compatta senza toccare il disco originale.

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

## 4. fsck

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
- **Insieme all'overlay (punto 5)**: eseguire la riparazione **nell'overlay**, mostrare cosa cambierebbe e applicarla solo su conferma. È un «fsck con anteprima», che i tool di Linux non offrono.

### Ripiego temporaneo

Per le **immagini**, se WSL è installato, si può lanciare `e2fsck` di WSL sul file. Per i dischi grezzi no: servirebbe `wsl --mount`.

---

## 5. Modifiche in sospeso (overlay / «bozza»)

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
4. Opzionale: hash prima e dopo (punto 1), ed `e2fsck -n` sul risultato (punto 4).

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

## Altre idee

- Pulsante «Espelli» che chiude il volume e richiede la rimozione sicura del dispositivo (`CM_Request_Device_Eject`).
- Test su dischi con settori fisici da 4K (4Kn) e su schede SD reali.
- Installer firmato, con associazione dei file `.img` e opzione per installare WinFsp.
- Modifica degli attributi estesi e delle ACL POSIX dalla finestra Proprietà.
