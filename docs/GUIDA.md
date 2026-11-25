# Soffritto: guida tecnica

Estensione Visual Studio 2026: mostra in un pannello dell'IDE fino a che migrazione EF Core è
arrivato il database di test, quali mancano, quali risultano applicate ma sconosciute al codice,
più branch git corrente (con la cartella di lavoro del clone), commit da pullare e stato del
dev tunnel associato al clone.

Lo stesso stato, per tutti gli ambienti configurati insieme, è disponibile fuori da Visual Studio con
l'exe Desktop: vedi «Exe Desktop (più ambienti)».

**La diagnosi è sola lettura** — sul database solo `SELECT`, su git solo `fetch` (che tocca i
remote-tracking ref, mai il working tree) e comandi di lettura.

**Tre azioni di scrittura sono disponibili a richiesta**: applicare le migrazioni pendenti di
un context (solo quando l'operazione è banale), un pull in fast-forward e il cambio di branch
dalla tendina. Vedi «Azioni e guardie».

## Struttura

| Progetto | Ruolo |
|---|---|
| `Soffritto.Core` | Motore: scansione migrazioni, lettura `__EFMigrationsHistory`, stato git, diff. Nessuna dipendenza da Visual Studio. |
| `Soffritto.Cli` | Harness da riga di comando: stesso motore, output su console. Utile per verificare senza aprire l'IDE. |
| `Soffritto.Presentation` | Orchestrazione condivisa tra VSIX ed exe: letture per sezione e loro coda, azioni con guardie e refresh successivi, testi del pannello, strumenti di Kestrel e dell'host del tunnel. Dipende da Core e da `System.Management`; nessuna dipendenza da Visual Studio o WPF. |
| `Soffritto.Vsix` | Il pacchetto dell'estensione (modello out-of-process `VisualStudio.Extensibility`): adapter Remote UI dell'orchestrazione condivisa. |
| `Soffritto.Desktop` | Eseguibile WPF indipendente da Visual Studio: stato degli ambienti configurati in una finestra, una colonna per ambiente con le card dei suoi repository, sulla stessa orchestrazione della VSIX. |
| `Soffritto.Mcp` | Server MCP stdio per qualunque client MCP: lo stesso stato degli ambienti come tool, in sola lettura sugli ambienti locali, e le stesse azioni dell'exe sugli ambienti Docker. Vedi «Server MCP». |

## Come legge lo stato

L'ID autoritativo di una migrazione EF è quello dichiarato nell'attributo `[Migration("...")]`
del file `.Designer.cs`, **non** il nome del file: in un repository i due possono divergere (per
esempio `20250402144742_aggiuntaTabellaOrdini.cs` che dichiara `...AggiuntaTabellaOrdini`).
Dedurre l'ID dal nome file produce falsi "mancante" e falsi "sconosciuta", quindi lo scanner legge
l'attributo e segnala la divergenza come nota.

Più context dello stesso backend possono condividere la stessa `__EFMigrationsHistory` (per
esempio `ProjectContext` e `IdentityContext` sulla stessa `DefaultConnection`, senza
`MigrationsHistoryTable` personalizzato), perciò una riga della history è davvero sconosciuta solo
se non appartiene a **nessuno** dei context configurati — altrimenti le migrazioni di
`IdentityContext` risulterebbero sconosciute a `ProjectContext` e viceversa.

Stati riportati per ogni migrazione:

- **applicata** — presente nei file e in `__EFMigrationsHistory`
- **mancante** — nei file ma non applicata
- **fuori ordine** — non applicata e con timestamp precedente all'ultima applicata: il sintomo
  tipico del rientro da un branch parallelo
- **sconosciuta** — applicata sul database ma assente dai file di entrambi i context

## Ultime migrazioni e comando da copiare

Sotto ogni context compaiono le **ultime 10 migrazioni** del codice, dalla più recente, col nome
privato del timestamp (`AggiuntaTabellaOrdini`, non
`20250402144742_AggiuntaTabellaOrdini`) e l'etichetta di stato a fianco. Davanti al
nome c'è la **posizione della migrazione nel context** (1 = la prima): rende leggibile l'ordine
decrescente e combacia con il contatore «applicate/totali» del context, così `212/214` con le
righe 214 e 213 «mancante» si legge senza pensarci. L'icona di
copia a fine riga mette negli appunti il comando `dotnet ef database update` **fino a quella
migrazione**, per quel context:

```
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:DOTNET_ENVIRONMENT='Development'; dotnet ef database update 20250402144742_AggiuntaTabellaOrdini --project "C:\Dev\backend-1\src\Project.Data\Project.Data.csproj" --startup-project "C:\Dev\backend-1\src\Project.Api\Project.Api.csproj" --context ProjectContext
```

Tre scelte sul testo copiato:

- **le due variabili d'ambiente viaggiano con la riga**, per la stessa ragione per cui il bottone
  le forza (vedi «Applica migrazioni»): incollata in un terminale senza di esse, EF leggerebbe
  `appsettings.json` e migrerebbe un database **diverso** da quello mostrato nel pannello. La
  sintassi è PowerShell, il terminale predefinito di Visual Studio; le variabili restano
  impostate per quella sessione di terminale;
- **il target è l'ID completo**, con timestamp, anche se la riga mostra il nome breve: EF accetta
  entrambi, ma l'ID resta inequivocabile se due migrazioni condividono il nome;
- **i percorsi sono assoluti**, quindi la riga funziona da qualunque cartella.

Sotto l'elenco, quando le migrazioni del context sono più di otto, c'è un **pager minimale**
(`‹ 1-8 di 214 ›`) che sfoglia le pagine più indietro nel tempo: è una finestra sulle righe già
lette, non una nuova lettura, e ogni refresh riparte dalla prima pagina.

L'elenco mostra le migrazioni qualunque sia il loro stato ed è l'unico posto in cui compaiono: le
pendenti non hanno un elenco a parte, si riconoscono dall'etichetta «mancante» in rosso.
Copiare non esegue nulla e non ha guardie — a differenza del bottone, che il comando lo lancia
davvero e compare solo quando l'operazione è banale.

Fermarsi a una migrazione **precedente** all'ultima applicata è un rollback: EF esegue i `Down()`
delle migrazioni successive. Il comando è quello corretto per farlo, ma è una scrittura
distruttiva e resta una tua decisione — per questo il pannello lo copia e basta.

## Layout del pannello

In alto una riga con l'attività e il pulsante **Aggiorna**: il progresso di un'azione (pull,
cambio branch, database update, avvio di Kestrel), il titolo del suo fallimento, o il microtesto
«pronto» quando non succede niente. Sotto, una card per sezione, con bordo leggero e 5 px tra
l'una e l'altra; senza sfondo proprio, perché il pannello segue il tema chiaro o scuro di Visual
Studio. In fondo, un footer minimale con la versione dell'estensione e il contatto.

**Ogni card si apre e si chiude** con il bottone nell'intestazione (freccia e titolo). I context
allineati partono chiusi, perché non hanno niente da dire oltre al contatore, e si riallineano a
ogni refresh; le altre card partono aperte e restano come le lasci. Gli errori del repository
restano visibili anche a card chiusa.

Non c'è un orario globale di aggiornamento: ogni sezione (Repository, ciascun context, Dev
tunnel) chiude con il proprio timestamp leggero, perché le letture sono indipendenti e possono
finire in momenti diversi.

**Il layout non scatta.** Le righe di stato non compaiono e scompaiono: hanno sempre il loro
spazio, e gli elenchi dentro le sezioni sono allineati al bordo sinistro sotto l'icona, senza
rientri che sprecano larghezza, con un microtesto di idle quando non c'è niente da dire («pronto», «non letto», «in attesa
della prima lettura»), e sono a riga singola con ellissi e tooltip. Durante una rilettura ogni
sezione conserva il contenuto precedente e solo il suo timestamp cambia in «lettura…» o
«git fetch…». Cambiano altezza solo i contenuti veri: righe di migrazioni, porte, errori.
Nell'exe la riga di attività degli ambienti e i timestamp non sono scritti nelle card: si leggono
nel tooltip del pallino di stato della card.

**L'ordine delle sezioni** è lo stesso dell'exe Desktop: repository (con gli errori, in un
riquadro stondato dal rosso tenue, solo quando ce n'è uno), dev tunnel, migrazioni sconosciute al
codice, un blocco per context. Le migrazioni sconosciute stanno sopra i context perché sono la
premessa per leggerne i contatori: comparendo spostano le sezioni sotto di loro, ed è un prezzo
accettabile perché compaiono solo quando c'è davvero qualcosa da dire.

Sul bordo superiore, fuori dallo scroll e visibile anche sopra lo splash, c'è una **barra di
attività** alta 3 px: trasparente da ferma, animata in modo indeterminato finché almeno una
lettura, un'azione o l'attesa iniziale della soluzione è in corso. È l'unico indicatore globale
del pannello e non occupa spazio quando non serve.

## Sezione Repository

La riga del repository si apre con il **nome della cartella di lavoro** (es. `backend-1`):
con più cloni attivi sulla stessa macchina è l'unico dato che dice a quale clone si riferisce
il resto del pannello. Il tooltip mostra il percorso completo. Accanto al nome ci sono i due
pallini di **Kestrel** e dell'**host del tunnel** e i tag di **database** e **server** letti da
`appsettings.Development.json`; il branch si legge nella tendina, con i commit da pullare nella
riga di dettaglio.

A destra ci sono le icone: **avvia/ferma Kestrel** (vedi «Kestrel dal pannello»), **pulizia
branch**, **fetch** (`git fetch`, poi rilettura della sola sezione git) e **pull** (`git pull
--ff-only`, vedi «Azioni e guardie»). Sotto, una **tendina editabile con i
branch**: locali più quelli presenti solo sul remote, senza il prefisso `origin/`, in ordine
alfabetico, con il branch corrente sempre in testa. Scrivendo nella casella le voci si filtrano
per sottostringa (senza distinguere le maiuscole) e il menu si apre da solo con l'anteprima, sul
modello del picker Git di Visual Studio, che nel Remote UI non è disponibile. Scegliere una voce
diversa dal branch corrente esegue `git switch` e poi scatta un **refresh completo**, perché con
un altro branch le migrazioni sul disco sono altre e l'upstream da confrontare è un altro. Se lo
switch fallisce la tendina torna sul branch corrente e l'errore compare nella riga di attività.

Se la tendina si chiude senza una scelta (Esc, click fuori, Tab) la ricerca è abbandonata e la
casella torna a mostrare il branch corrente.

Un dettaglio implementativo che spiega il codice: la lista delle voci viene riallineata voce per
voce invece di svuotata e riempita, perché in una ComboBox editabile rimuovere la voce selezionata
azzera anche il testo che si sta scrivendo.

### Kestrel dal pannello

Per far girare il backend senza debugger (e senza occupare F5) il pannello offre gli stessi
strumenti della card backend dell'exe Desktop, dal codice condiviso (`BackendTools`):

- **Avvia** apre una console con `dotnet run --launch-profile Kestrel --no-launch-browser` dalla
  cartella del progetto api configurata (`apiProject`, per esempio `src\Project.Api`); il profilo
  è `launchProfile`, `Kestrel` di default. Non tocca l'host del dev tunnel.
- **Ferma** chiude la console e il suo albero di processi, oppure i processi riconosciuti se
  sono partiti in altro modo.
- I **pallini** dicono lo stato: verde acceso, arancio console aperta ma processo non (ancora)
  attivo, grigio spento. Il secondo è l'host del tunnel del pool su cui il backend ha la sua
  porta (vedi «Pool di dev tunnel»): verde quando tra i processi c'è un `devtunnel host` di quel
  tunnel, o quando l'ultima lettura del tunnel riporta un host connesso. L'host non ha un
  pulsante suo: si accende e si spegne con l'interruttore **Tunnel** della card dell'ambiente,
  come nell'exe (`EnvironmentTunnel`), che reclama o rilascia il tunnel del pool e governa l'host
  attraverso `PoolHosts`. Il pallino è sul bordo destro della card e il suo tooltip dice stato e
  ora dell'ultima lettura.
- Nel **worktree di un ambiente Docker** lo stesso bottone avvia e ferma il container dell'api
  dello slot (`docker compose up --build` e `stop`, come il bottone della card nell'exe), e il
  pallino è lo stato del container letto con `docker ps`.

Lo stato si rilegge dai processi ogni 3 secondi (WMI, lettura locale; `docker ps` nel worktree di
un ambiente Docker). La console di Kestrel
porta nel titolo lo stesso marcatore dell'exe (`AMBIENTI kestrel-N`), quindi pannello ed exe si
riconoscono a vicenda. Le console partono fuori dal job dell'host delle estensioni quando
Windows lo consente; altrimenti la riga di attività avverte che si chiuderanno con lui.

## Sezione Dev tunnel

Mostra la porta del pool di dev tunnel assegnata al backend dell'ambiente (vedi «Pool di dev
tunnel»): ID del tunnel, la sola porta assegnata con il suo endpoint `*.devtunnels.ms`, e
raggiungibilità. La porta ha la **sua icona di copia**, che mette negli appunti l'URL completo
(senza slash finale; la clipboard è scritta via Win32 puro perché l'host out-of-process non
carica WPF/WinForms), e quella che lo **apre nel browser predefinito**.

**L'associazione soluzione → porta viene da `tunnel-pool.json`**: la VSIX confronta la cartella
della soluzione aperta con il backend predefinito di ogni ambiente della configurazione, locale o Docker,
e legge in `%LOCALAPPDATA%\Soffritto\tunnel-pool.json` la porta assegnata a quel backend.
Poi cerca con `devtunnel list` il tunnel che ha la Description dell'assegnazione (per esempio
`pool-1`) e ne legge la porta con `devtunnel show`. Senza assegnazione, o con una soluzione che
non è il backend predefinito di un ambiente, la sezione dice «non condiviso» e non interroga la
CLI: la porta si assegna con l'interruttore «Tunnel» della card dell'ambiente, qui o nell'exe Desktop. Se il tunnel con quella
Description non c'è nell'account, la sezione lo dice e al posto del dettaglio **elenca i tunnel
dell'account** letti da `devtunnel list`: ID, descrizione, host connesso o no, numero di porte.

La **raggiungibilità primaria è `hostConnections`** letto dalla CLI, non un probe HTTP: la
gateway devtunnels tiene appesa la richiesta verso un tunnel non hostato invece di rifiutarla,
quindi senza host connesso un probe può solo scadere in timeout. Il probe HTTP per porta
(HEAD, timeout 5 s, qualunque status HTTP conta come risposta) viene eseguito solo a host
connesso, per confermare il percorso end-to-end gateway → host → app.

La CLI `devtunnel.exe` viene cercata nel PATH e poi tra i pacchetti WinGet
(`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Microsoft.devtunnel_*`). Richiede la login già
fatta (`devtunnel user login`); in caso contrario l'errore della CLI compare nella sezione.

Come per il fetch git, **il tunnel viene interrogato dai refresh completi**: quello automatico
all'apertura del pannello, il pulsante Aggiorna e quelli che seguono un pull o un cambio branch.
Finché non arriva il primo la sezione resta su "dev tunnel non letto". Solo il refresh dopo un
database update è una lettura locale e lascia la sezione com'è invece di azzerarla.

## Azioni e guardie

Le due azioni compaiono come icona solo quando l'operazione è banale. La guardia **è** la
funzionalità: un bottone sempre visibile che a volte fa danni sarebbe peggio di nessun bottone.

### Applica migrazioni (icona accanto al context)

Compare quando `CanApplyPending`: ci sono migrazioni pendenti **e nessuna è fuori ordine**.
Con anche una sola fuori ordine il buco viene da un branch parallelo, lo schema risultante può
divergere da quello prodotto applicandole in sequenza, e la decisione torna a te.

Le migrazioni **sconosciute non bloccano**: sono righe inerti in `__EFMigrationsHistory` e non
impediscono a EF di applicare correttamente le pendenti. Gate-arle avrebbe reso l'azione
inutilizzabile su un database che ne contiene una in modo permanente.

Esegue `dotnet ef database update --project <p> --startup-project <s> --context <nome>`, previa
conferma che riporta **clone, server e database** letti dal pannello e dichiara che
`ASPNETCORE_ENVIRONMENT` e `DOTNET_ENVIRONMENT` vengono forzate a Development.

> ⚠️ `ASPNETCORE_ENVIRONMENT` e `DOTNET_ENVIRONMENT` sono forzati a `Development` dal codice.
> Non è cosmetica: è ciò che determina *quale* database viene toccato. Senza, EF leggerebbe
> `appsettings.json` — che può puntare a un database diverso da `Project_dev` di
> `appsettings.Development.json` — e migrerebbe un database **diverso** da quello che il pannello
> mostra. I profili di `launchSettings.json` possono impostare `Development`, ma dipendere dal
> loro ordine per un'operazione distruttiva non è accettabile.

Il build non viene saltato (nessun `--no-build`): applicare migrazioni compilate da sorgenti non
aggiornati è peggio che attendere qualche decina di secondi.

### Pull (icona nella sezione Repository)

Sempre visibile quando git è disponibile. Esegue `git pull --ff-only`: il flag **è** la guardia.
Se il fast-forward non è possibile (commit locali da pushare, upstream mancante, HEAD detached)
git **rifiuta** invece di creare un merge o dei conflitti, e il motivo compare nella riga di
attività. Con modifiche locali che il fast-forward sovrascriverebbe git rifiuta allo stesso modo.
È la ragione per cui questa azione non chiede conferma — non può produrre uno stato da
districare a mano. Il tooltip riporta quanti commit prenderebbe secondo l'ultimo fetch.

### Cambio branch (tendina nella sezione Repository)

Esegue `git switch <branch>` sulla voce scelta. Anche qui la guardia è di git: se le modifiche
locali entrerebbero in conflitto col branch di destinazione lo switch viene rifiutato e la
tendina torna sul branch corrente. Per un nome presente solo sul remote, `git switch` crea il
branch locale che lo traccia.

### Pulizia branch (icona nella sezione Repository)

Elimina i branch locali il cui branch remoto non esiste più. Prima esegue `git fetch --prune`:
finché i remote-tracking ref cancellati restano sul disco git non marca nessun upstream come
`gone`. I candidati sono solo i branch con upstream `gone`: mai il branch corrente, mai quelli
senza upstream. Se non ce ne sono il pannello si rilegge e lo dice («nessun branch locale da
eliminare»); altrimenti chiede conferma elencandoli per nome insieme al clone, con Annulla come
scelta predefinita, ed esegue `git branch -D`.

`-D` e non `-d`: con squash o rebase `-d` rifiuterebbe proprio i branch già integrati.
L'eliminazione è irreversibile per i ref locali: un branch `gone` può contenere commit mai
pushati, recuperabili solo dal reflog finché non scade (`git reflog`, poi
`git branch <nome> <sha>`).

Dopo tutte le azioni il pannello si rilegge da solo. Pull e cambio branch fanno scattare un
**refresh completo** (fetch e tunnel compresi), perché cambiano il codice sul disco e il punto di
riferimento remoto; dopo un database update la rilettura è locale, visto che è cambiato solo il
database. Un'azione fallita o annullata non rilegge niente: l'errore resta nel riquadro e per
rileggere si preme Aggiorna.

Subito dopo un pull o un cambio branch i pulsanti tornano attivi prima che le migrazioni siano
rilette: per qualche secondo il bottone di database update riflette la lettura **precedente**, e
la guardia potrebbe non valere più per il codice appena arrivato (per esempio una migrazione
fuori ordine portata dal pull). Il timestamp «lettura…» del context segnala quell'intervallo, e
la conferma riporta comunque server e database.

## Exe Desktop (più ambienti)

### Cosa mostra

Una finestra con una colonna per ambiente. Ogni colonna ha una card per ogni repository che
l'ambiente include, nell'ordine del catalogo (vedi «Configurazione»), e ogni card ha la sezione
git: branch, commit da pullare, modifiche locali, fetch, pull, pulizia branch e tendina di
cambio branch. Il resto dipende dal tipo del repository: la card di un backend ha lo stesso
contenuto del pannello della VSIX (migrazioni per context, server e database), quella di un
frontend l'anteprima del browser, quella di un'app avvio e Metro; un repository generico ha la
sola sezione git e il bottone VS Code. Per esempio, con un catalogo di un backend, un frontend e
un'app le card sono `backend-N`, `frontend-N` e `app-N`.
I dev tunnel non hanno una card loro: un repository con «Condivisibile» acceso nel gruppo «Dev
tunnel» della sua card del setup ha nel corpo della sua card, sotto la sezione git, la sezione del tunnel, quando l'ambiente ha «Tunnel»
spuntato nell'intestazione: il pallino dell'host, la porta assegnata e una freccia propria per
aprirla e chiuderla. La porta viene dal tunnel del pool che l'ambiente ha reclamato (vedi
«Pool di dev tunnel»): per il backend è la prima porta https di Kestrel, per
il frontend la porta http del dev server, così il frontend dell'ambiente N si apre anche da fuori
dalla macchina; con le porte scritte a mano nel setup, obbligatorie per un generico, vale la
prima. Chiudendo una card si chiude anche la sua sezione del tunnel.
Un repository che non è il primo del suo tipo nell'ambiente porta accanto al nome il tag del suo
id, per distinguerlo (vedi «Strumenti dei repository»).

In cima a ogni colonna c'è la card dell'ambiente. Nell'intestazione stanno il nome, i tag di
database e server del backend letti dagli appsettings, l'attività, avvia/ferma d'ambiente,
Aggiorna, l'occhio sbarrato che nasconde la colonna e, sul bordo destro, il pallino. Un tag è
arancione se il suo nome è tra quelli di `highlightedTags` del backend («Tag da evidenziare» nel
setup); in un ambiente Docker il tag del server non c'è; sotto la
nota stanno le azioni secondarie, «Reset to dev» (il Reset) e l'interruttore Tunnel. Un ambiente Docker ha in più il
tag «Docker» e, sotto la nota, «Ripara env» e «Distruggi ambiente» (vedi «Ambienti Docker»). La card scorre
insieme alle card dei repository, quindi resta sempre allineata alla sua colonna. Una colonna
nascosta sparisce del tutto dalla griglia, e lo stato resta dopo un riavvio. Nella testata della
finestra, accanto ai bottoni delle app, c'è un chip per ambiente (numero e pallino, l'attività nel
tooltip): resta visibile anche scorrendo in orizzontale. Il clic su un chip mostra o nasconde la
colonna, e mostrandola la porta in vista; una colonna nascosta ha l'occhio barrato sul suo chip.
Un ambiente con tutti i repository su `dev` ha lo stesso tramonto pesca-rosa sulla card
dell'ambiente e sul chip. Dopo i chip, il «+» apre il riquadro «Nuovo ambiente Docker».
Ogni porta è una pillola con il numero in un badge grigio e l'host pubblico senza `https://`: un
clic in qualunque punto della pillola copia l'URL. Che risponda lo dice il pallino dell'host: la pillola scrive in rosso «nessuna
risposta» solo quando il probe HTTP di quella porta non ha avuto risposta.
Le migrazioni stanno in una sola card Database, sotto la card dell'app: le migrazioni applicate
ma sconosciute al codice (quando ce ne sono) e poi un context per sezione, separate da una linea
e ognuna con la sua freccia. Così frontend e app restano alla stessa altezza in tutte le colonne,
anche quando un context disallineato si apre da solo.

Porte del tunnel e migrazioni dei context usano la stessa riga a pillola, il controllo `Lane`:
badge grigio (numero di porta o posizione della migrazione nel context), testo, nota colorata
(stato della migrazione, porta che non risponde) e azioni. Con una sola azione la lane intera è
il bottone e il glifo a destra fa da segnale: un clic su una porta copia l'URL, un clic su una
migrazione lancia `dotnet ef database update` fino a lei, con conferma. Con più azioni ognuna ha
il suo bottone tondo a destra.

Ogni colonna aperta è larga 550 px, sempre: con poche colonne lo spazio che avanza resta vuoto a
destra, e quando le colonne aperte non stanno nella finestra compare lo scroll orizzontale, che
sposta le colonne con le loro card dell'ambiente. Le colonne nascoste non occupano spazio.

Ctrl+G fa comparire un gattino in pixel art che passeggia sopra il footer o si ferma a
scodinzolare, sonnecchiare e mandare cuori; ripremendo Ctrl+G sparisce. Non intercetta i click.

Mentre è visibile il gattino fa anche da vedetta: quando compare una segnalazione nuova si ferma
e la dice in un fumetto, una per volta. Riporta gli errori delle card (il riquadro rosso e le
cartelle non trovate) e le migrazioni: context con migrazioni mancanti o fuori ordine e
migrazioni applicate ma sconosciute al codice. Ogni segnalazione la dice una volta sola finché
resta presente; torna nuova se sparisce e ricompare o se cambia. Quando compare dice tutto
quello che non ha ancora detto, comprese le segnalazioni nate mentre era nascosto.

Le stesse segnalazioni arrivano anche come notifica di Windows, sempre, anche con il gattino
nascosto. Partono dall'icona dell'exe nell'area di notifica, presente finché l'exe è aperto.
Le segnalazioni nuove arrivate nello stesso giro (ogni 2 secondi) finiscono in una notifica
sola; oltre i 255 caratteri che Windows ammette la notifica chiude con «… e altre N». Anche
qui ognuna si notifica una volta finché resta presente, ma il conteggio è separato da quello
del gattino: quando compare, il gattino dice anche ciò che è già stato notificato. Un clic
sulla notifica o sull'icona riporta in primo piano la finestra.

Con il gattino visibile, Win+Shift+S (lo strumento di cattura di Windows) a volte lo fa salutare, in media una
volta su tre: si
ferma, agita la zampa e dice una frase in un fumetto. Il tasto si riconosce anche con la
finestra in secondo piano e prosegue comunque verso Windows. Se il gattino sta dicendo una
segnalazione saluta solo con la zampa; da nascosto non fa niente.

Ctrl+D apre la modalità demo: il team di un gioco di carte per smartphone, con i repository
inventati `mazzo-server`, `mazzo-backoffice` e `mazzo-app` e quattro ambienti (una storia provata
dal telefono col tunnel acceso, un bug con due migrazioni da applicare, una review con branch
indietro, migrazione fuori ordine e tunnel non trovato, il collaudo della release in un ambiente
Docker) con righe di attività che cambiano da sole. Nomi, URL e persone non vengono dalla
configurazione. I pulsanti, compresi «Distruggi ambiente», «Ripara env», «Reset to dev» e
l'interruttore del tunnel, rispondono solo nella riga di attività: nessun comando tocca git,
database, Docker, tunnel o processi. Anche il pannello delle pull request e la riunione dei
punti storia sono finti, con «Apri in» che cambia i branch solo nelle card, e le note sono
inventate e non si salvano. Il «+» del nuovo ambiente Docker sparisce, e il setup mostra solo
le preferenze: Salva, Importa e le schede con azioni su configurazione, Docker e tunnel restano
bloccati finché dura la demo. La demo funziona su una macchina senza cloni, senza rete e senza
il servizio DevOps. Il gattino, se
visibile, commenta le segnalazioni finte; le notifiche di Windows tacciono e al ritorno dicono
le novità degli ambienti veri. Ripremendo Ctrl+D si torna agli ambienti veri, che nel
frattempo hanno continuato a leggersi; le anteprime del frontend ripartono da capo.

In fondo a ogni colonna c'è l'anteprima del frontend dell'ambiente, in un riquadro 16:9
utilizzabile con mouse e tastiera. La card c'è sempre, anche senza sessioni: l'intestazione
mostra la porta prevista del dev server, e l'icona accanto all'ingrandimento apre la vista
selezionata nel browser predefinito (in «Claude» la pagina del browser di Claude, o la radice del
dev server se non c'è; in «Mio» la pagina a cui è arrivata la WebView2). Ha due modalità:

- «Claude»: il browser di playwright-cli aperto sulla porta del dev server dell'ambiente
  (la variabile `devServerPortVariable` nel file di env `envFile` del frontend, per esempio
  `frontend-N\env\dev.bat`, o `devServerDefaultPort` se manca). Quei browser sono
  headless e altrimenti non si vedono. I click e la tastiera arrivano a quel browser, lo stesso
  che sta pilotando Claude: usarlo mentre Claude lavora gli cambia la pagina sotto i piedi. Il
  pallino è verde quando il browser c'è, e la card si apre da sola la prima volta che compare.
- «Mio»: una WebView2 propria sullo stesso indirizzo, con login e cookie suoi, separata dal
  browser di Claude. Nasce solo quando la si apre. Nella card la pagina si rende larga 1280 px
  e viene scalata sul riquadro, senza scrollbar (la rotella scorre comunque); ingrandita torna
  al 100%.

L'icona di ingrandimento porta l'anteprima a tutta la finestra dell'app, sopra testata e
colonne; Esc o il pulsante nella barra tornano alle colonne. Dall'anteprima ingrandita Esc non
arriva quindi alla pagina, che lo riceve dalla card.

Per la modalità «Claude» l'exe legge i browser accesi dal registro della CLI
(`%LOCALAPPDATA%\ms-playwright\b`) ogni 3 secondi. Se ce n'è almeno uno avvia la dashboard di
playwright-cli (`playwright-cli show --port=0`) come processo figlio, apre una connessione per
ambiente e la chiude quando non resta nessun browser o quando si chiude l'exe. La connessione
usa il protocollo interno della dashboard, che non è un'API pubblica: un aggiornamento della CLI
può romperla. Serve `playwright-cli` nel PATH e il runtime WebView2, già presente su Windows 11.
La dashboard è una sola per macchina: se ne gira già un'altra (un `playwright-cli show` lanciato
a mano) le anteprime «Claude» restano vuote finché quella non si chiude. Vale anche il contrario:
mentre l'exe è aperto, un `playwright-cli show` lanciato altrove non apre una finestra sua.

### Scoperta degli ambienti

Dalla lista `environments` della configurazione (vedi «Configurazione»): una colonna per ogni
ambiente, locale o Docker, nell'ordine della lista. Un ambiente locale (`kind: local`) cerca i
repository che include in `<cartella dei cloni>\<nome con {n}>`, oppure nella cartella dei cloni
sua se ce l'ha. Un ambiente Docker (`kind: docker`) ha i suoi worktree in `<cartella dei
worktree Docker>\<nome con {n}>` (vedi «Ambienti Docker»). La colonna di un ambiente Docker
compare quando il «+» della testata lo crea e sparisce quando «Distruggi ambiente» lo toglie, senza
riavviare l'exe.
Le colonne ci sono sempre: una cartella mancante mostra «cartella non trovata» e non avvia
letture, e con il backend mancante non partono né le migrazioni né il suo dev tunnel.
L'esistenza delle cartelle si ricontrolla a ogni refresh.

### Refresh

All'apertura parte un refresh completo di tutto, lo stesso del pulsante «Aggiorna tutto» in
alto; ogni colonna ha anche il suo pulsante. Prima di quel primo refresh ogni ambiente locale
riallinea i suoi env legati al pool (vedi «Env degli ambienti locali»). Dopo le azioni valgono le regole della VSIX,
repository per repository: pull e cambio branch rileggono tutto quel repository, database
update e pulizia branch rileggono solo lo stato locale, un'azione fallita o annullata non
rilegge. Ogni 5 minuti dall'apertura l'exe esegue da solo lo stesso «Aggiorna tutto», con le
stesse regole (un repository con un'azione in corso viene saltato); un refresh manuale non fa
ripartire il conteggio. La VSIX non ha questo refresh periodico. A parte questo, l'unica
lettura periodica è quella dei processi locali per lo stato degli strumenti (vedi sotto), ogni
3 secondi.

### Strumenti dei repository

Accanto al nome di ogni repository, nella sua card:

- **VS** (backend) apre la soluzione configurata in `solution` (per esempio `Project.sln`)
  nell'installazione di Visual Studio più recente trovata da `vswhere`, prerelease comprese;
  **Code** (frontend e app) apre la cartella in VS Code, preferendo l'Insiders.
- Il **comando accessorio** (frontend, solo se `extraCommand` non è vuoto) apre una console con
  quel comando nella cartella del clone; il bottone prende il nome dall'ultima parola del comando
  (per esempio `npm run codegen` diventa «Codegen»). La console resta aperta a fine comando,
  così l'esito si legge.
- **Avvia**: per il backend apre una console con `dotnet run --launch-profile Kestrel
  --no-launch-browser` dalla cartella `apiProject` (per esempio `src\Project.Api`); per il
  frontend e per l'app il loro `startCommand` (per il frontend `npm run start` di default).
  Nessuno dei tre accende l'host di un dev tunnel.
- **Ferma** chiude la console e tutto il suo albero di processi, oppure i processi riconosciuti
  se sono partiti in altro modo (per esempio Kestrel avviato da Visual Studio).
- Il **pallino** dice lo stato: verde acceso, arancio console aperta ma processo non (ancora)
  attivo (build, installazione, oppure terminato con un errore leggibile nella console), grigio
  spento. Il pallino dell'host del tunnel sta nella sezione del tunnel della card ed è descritto
  in «Pool di dev tunnel».

Nelle card di un ambiente Docker il backend e il frontend predefiniti girano in un container, e
Avvia, Ferma, il pallino e il bottone «Log» agiscono sul container (vedi «Ambienti Docker»).

I repository generici non avviano niente: hanno solo **Code** e, se configurato, la sezione del
loro dev tunnel. L'avvia/ferma d'ambiente nella card dell'ambiente avvia Kestrel e dev server di
tutte le card, in ordine di colonna, ma non l'app né gli host dei tunnel del pool, che sono
condivisi con gli altri ambienti; lo stop ferma anche app e Metro. In un ambiente Docker
l'avvia/ferma d'ambiente avvia e ferma i container e il Compose Watch del web.

Lo stato si ricava dai processi della macchina, non dalla memoria dell'exe: Kestrel è il
processo dell'API configurato in `apiProcess` (per esempio `Project.Api.exe`) sotto la cartella
del backend, il dev server è il `node` sotto quella del frontend che ha nella riga di comando il
testo di `devServerProcessHint` (per esempio `webpack`), l'app è qualunque `node` sotto quella dell'app,
l'host è `devtunnel host` con l'id del tunnel. Ogni console aperta dall'exe porta nel titolo un
marcatore (`AMBIENTI kestrel-N`, `AMBIENTI frontend-N`, `AMBIENTI app-N`, `AMBIENTI metro-N`,
`AMBIENTI tunnel-pool-<Description>` per l'host di un tunnel del pool, `AMBIENTI
watch-web-<chiave>` e `AMBIENTI log-<servizio>-<chiave>` negli ambienti Docker) che la fa
riconoscere anche dopo un riavvio dell'exe.

**Repository predefinito.** In un ambiente il primo repository di ogni tipo (backend, frontend,
app, in ordine di catalogo) è il predefinito: tiene i marcatori di sempre, con il solo numero
dell'ambiente. Gli altri repository dello stesso tipo, e tutti i generici, usano la chiave
`N-<id>`: un secondo backend con id `api2` apre la console `AMBIENTI kestrel-1-api2`. Un id
comincia sempre con una lettera, quindi una chiave derivata non si confonde con una storica.
Gli host dei tunnel del pool non dipendono dal repository: la console è una per tunnel, per
esempio `AMBIENTI tunnel-pool-pool-1`.

Editor e console partono **fuori** dal Job Object dell'exe (`CREATE_BREAKAWAY_FROM_JOB`, con
il job che lo consente): chiudere l'app non chiude Visual Studio, VS Code né i server avviati.
Partono con l'ambiente normale, senza le variabili MSBuild che l'exe imposta per i propri figli.

### Azioni e guardie

Le guardie sono quelle della sezione «Azioni e guardie», che vive nel codice condiviso. Il gate
delle azioni di scrittura è **per repository**: mentre un pull, un cambio branch, una pulizia o
un database update è in corso su un repository, sono disabilitati solo i suoi pulsanti (per il
backend anche il database update dei context); gli altri repository, della stessa colonna o
delle altre, restano utilizzabili. Gli Aggiorna restano sempre attivi e rileggono i soli
repository liberi: quello occupato si rilegge a fine azione, se riuscita. Le conferme nominano anche l'ambiente, perché con dodici repository nella stessa finestra è l'unico
modo per accorgersi di aver premuto il bottone nella card sbagliata.

**Reset** e **«Apri in»** del pannello delle PR toccano tutte le card dell'ambiente, generici
compresi: ogni repository va sul branch della PR del suo repository sul server, gli altri su
`dev`, il branch di sviluppo principale (vedi «Branch di sviluppo»). Se anche una sola card ha modifiche locali, un'operazione git a metà o non ha il branch,
non cambia niente: vale anche per un repository generico, quindi un generico in lavorazione o
senza `dev` blocca Reset e «Apri in» dell'ambiente in cui è incluso.

### Dev tunnel

La sezione del tunnel di una card legge la porta del pool assegnata a quel repository in
quell'ambiente: senza assegnazione mostra «non condiviso» e non interroga la CLI; con
l'assegnazione mostra solo la porta assegnata, con il suo URL, letta con `devtunnel show` sul
tunnel del pool che la ospita. Le regole del pool, i pulsanti e gli stati della sezione sono in
«Pool di dev tunnel».

Tutte le card con un dev tunnel interrogano la CLI `devtunnel` una lettura alla volta: non è
verificato che regga chiamate concorrenti sulla stessa login. Mentre aspetta il proprio turno la
sezione mostra «in coda…». Il tunnel non trattiene le altre sezioni.

### Chiusura

Se un ambiente ha un'azione di scrittura in corso, la chiusura chiede conferma: un
`dotnet ef database update` interrotto può lasciare il database a metà di una migrazione.
L'exe si assegna all'avvio a un Job Object con kill-on-close: qualunque sia il modo in cui
termina, anche un crash, il sistema chiude git, devtunnel e dotnet avviati da lui. Per lo
stesso motivo imposta per i processi figli `MSBUILDDISABLENODEREUSE=1`,
`DOTNET_CLI_USE_MSBUILD_SERVER=0` e `UseSharedCompilation=false`: la build lanciata dal
database update non lascia nodi MSBuild o compilatore condiviso che il job ucciderebbe mentre
un'altra build li sta usando. Il prezzo è una compilazione un po' più lenta.

### Compilare e pubblicare

```
dotnet publish src\Soffritto.Desktop -c Release
```

Il risultato è un solo file,
`src\Soffritto.Desktop\bin\Release\net11.0-windows10.0.17763.0\win-x64\publish\Soffritto.exe`,
che funziona da qualunque cartella. È framework-dependent e richiede il Windows Desktop
Runtime 11 o successivo. `RollForward=LatestMajor` resta: senza, un'app net11.0 su una macchina
che ha solo un runtime più recente non parte. `IncludeNativeLibrariesForSelfExtract` porta dentro l'exe anche
la SNI nativa di SqlClient.

### Rapporto con la VSIX

VSIX ed exe usano lo stesso progetto `Soffritto.Presentation`: sequenze di
lettura e di azione, guardie, testi e regole di refresh stanno lì, insieme agli strumenti di
Kestrel e dell'host del tunnel (`BackendTools`, `ConsoleServices`, `ProcessTable`,
`DetachedLauncher`), e ciascun host fornisce solo come si mostra, come si conferma, come si
copia e dove sta la radice. Presentation non dipende da Visual Studio né da WPF; per leggere
le righe di comando dei processi usa il pacchetto `System.Management`. Una sola `<Version>` in
`Directory.Build.props` tiene allineate VSIX ed exe. Resta duplicata la
macchina a stati della tendina dei branch (`UI/StatusViewModel.cs` nella VSIX,
`ViewModels/BranchPickerViewModel.cs` nell'exe), legata al tipo di ViewModel di ciascun host:
un cambio di comportamento della tendina va riportato in entrambi i file.

La VSIX e la CLI lavorano sul **primo backend del catalogo**: ne usano processo dell'API,
profilo di avvio, appsettings e context. La VSIX riconosce l'ambiente dalla cartella della
soluzione aperta confrontandola con il backend predefinito di ogni ambiente, locale o Docker
(`AppConfiguration.BackendEnvironmentOf`), e mostra la porta del pool assegnata a quel backend.
Rilegge la configurazione a ogni Aggiorna. Nell'intestazione mostra gli stessi tag dell'exe:
database protetto, Docker, database e server. Aperta sul worktree di un ambiente Docker applica
le regole dell'exe: guardia del database dello slot su letture delle migrazioni, update,
rollback e copia del comando, nessuna connessione a un database dello slot spento o in avvio, e
avvia/ferma sul container dell'api. Prende i colori dal tema di Visual Studio, chiaro o scuro, e
dall'exe le forme di card, tag, pallini e interruttore. CLI e server MCP non vedono gli ambienti
Docker. Con più backend nel catalogo va aperta solo sul backend predefinito di un
ambiente: su un altro backend non troverebbe l'ambiente, ricadrebbe sul suffisso numerico della
cartella e le sue console prenderebbero il marcatore `kestrel-N` del predefinito. Il setup lo
ricorda con un avviso.

## Pool di dev tunnel

Un repository si condivide con i colleghi tramite una porta di un tunnel del **pool**: un insieme
di dev tunnel dell'account, elencati in `tunnelPool` della configurazione e ritrovati per
Description con `devtunnel list`. Il pool vale per tutti gli ambienti, locali e Docker. Per
esempio, un pool di otto tunnel, da `pool-1` a `pool-8`, con Label `SoffrittoPool`, può
ospitare backend e frontend dell'ambiente N su `pool-N`.

Le porte si assegnano solo su richiesta, accendendo «Tunnel» nell'intestazione di un ambiente o
alla creazione di un ambiente Docker. Le assegnazioni stanno in `<cartella dati>\tunnel-pool.json` (di solito
`%LOCALAPPDATA%\Soffritto\tunnel-pool.json`): Description e id del tunnel, porta, ambiente e
repository. Soffritto crea e cancella i tunnel del pool dalla scheda «Tunnel» del setup; il
Salva del setup crea solo i tunnel elencati in una configurazione importata che l'account non ha.

Un tunnel è di un solo ambiente, quindi il pool è grande quanto la quota dell'account: «Crea
tutti i tunnel possibili», nella scheda «Tunnel», crea `pool-K` finché `devtunnel create` non
rifiuta, e scrive ogni tunnel creato in `config.json`. Il limite non è scritto nel codice. Per
lasciare un posto a un tunnel creato fuori da Soffritto si elimina un tunnel dalla stessa
scheda.

Un tunnel scade dopo 30 giorni senza uso e il servizio non accetta scadenze più lunghe. A ogni
avvio l'exe riporta a 30 giorni la scadenza dei tunnel del pool
(`devtunnel update <id> --expiration 30d`): aprendo l'exe almeno una volta al mese i tunnel e i
loro URL restano.

### Il tunnel di un ambiente

Il tunnel si accende e si spegne per tutto l'ambiente, con l'interruttore «Tunnel»
nell'intestazione della colonna. Nelle card non ci sono comandi sul tunnel. A cambio finito
l'ambiente si rilegge da solo, come con il suo pulsante Aggiorna.

Accendendo «Tunnel» l'exe dà una porta del pool, con l'accesso anonimo, a ogni repository
dell'ambiente che ne espone una:

- la porta è la prima porta https di Kestrel per un backend locale, la porta http del dev server
  per un frontend locale, la porta host del container (http) in un ambiente Docker; con le porte
  scritte a mano nel setup vale la prima di quelle. Un repository senza porta da esporre resta
  senza tunnel;
- un tunnel è di un solo ambiente: il primo repository reclama il primo tunnel senza porte
  assegnate, nell'ordine del pool, e gli altri repository dell'ambiente vanno sullo stesso
  tunnel; un tunnel con porte di un altro ambiente non si usa mai. Si saltano i tunnel che
  l'account non ha e quelli pieni: un tunnel ospita al massimo 9 porte. Senza un tunnel
  utilizzabile la riga di attività dice «pool pieno» e l'interruttore torna spento. Le
  assegnazioni fatte prima di questa regola restano dove sono, anche su un tunnel condiviso da
  più ambienti;
- l'accesso anonimo vale per la sola porta (`devtunnel access create <id> -p <porta>
  --anonymous`); se il servizio non lo concede, l'exe cancella la porta e annulla
  l'assegnazione;
- un host acceso non ospita le porte aggiunte dopo il suo avvio, quindi a porte assegnate l'exe
  accende l'host del tunnel, o lo riavvia se era già acceso;
- l'exe aspetta fino a 30 secondi per porta che il servizio pubblichi l'URL, poi riscrive le
  variabili di env che dipendono dal pool (vedi «Env degli ambienti locali» ed «Env degli
  ambienti Docker»). Se un URL non arriva, la riga di attività lo dice: si spegne e si
  riaccende il tunnel.

Spegnendo «Tunnel» l'exe toglie l'accesso anonimo di ogni porta dell'ambiente, cancella le
porte dal tunnel, toglie le assegnazioni da `tunnel-pool.json`, chiude l'host del tunnel rimasto
senza porte e riscrive le variabili di env che dipendevano dal pool. Il tunnel torna libero:
riaccendendo «Tunnel» l'ambiente può ricevere un tunnel diverso, con URL diversi, e un'app
compilata con l'URL vecchio va ricompilata.

### L'host del tunnel

Ogni tunnel del pool ha un solo host: una console `AMBIENTI tunnel-pool-<Description>` (per
esempio `AMBIENTI tunnel-pool-pool-1`), aperta nella cartella dati. L'exe non accende gli host
all'apertura. Se l'host di un ambiente con il tunnel si chiude, lo riaccende l'avvio
dell'ambiente; lo stop dell'ambiente non lo tocca.

La sezione del tunnel compare nella card solo se il repository ha una porta del pool: mostra la
porta con il suo URL, i bottoni per copiarlo e aprirlo, e il pallino dell'host, verde se l'host
è acceso, arancio se è spento e l'URL non risponde.

### Il pool nella scheda Tunnel del setup

La scheda «Tunnel» del setup riassume il pool in una riga («2 tunnel su 8 in uso: ambienti 1,
5») ed elenca i tunnel, ognuno con la Description, l'ambiente che lo ha o «libero», il pallino
dell'host e «Elimina». «Elimina» vale solo per un tunnel senza porte assegnate: chiude il suo
host, lo cancella dall'account e lo toglie da `config.json`, dopo una conferma. Sotto l'elenco,
«Aggiungi tunnel» crea un tunnel in più sulla prima Description `pool-K` libera e «Crea tutti i
tunnel possibili» ne crea finché l'account ne concede. Le azioni della scheda valgono subito,
senza Salva, e il loro esito compare sotto i bottoni.

Sotto un tunnel compaiono solo le cose che non tornano: le differenze tra il servizio
devtunnels e `tunnel-pool.json` (porte senza assegnazione, assegnazioni senza porta) e le porte
rimaste a un ambiente che non c'è più, ognuna con «Rilascia». Un'assegnazione che ha perso la
sua porta (tunnel sparito dall'account, ricreato con un altro id, porta cancellata a mano) esce
da `tunnel-pool.json` quando si accende o si spegne il tunnel di quell'ambiente; se `devtunnel`
non risponde, l'assegnazione resta.

### Env degli ambienti locali

In un ambiente locale l'exe tiene allineate le variabili per slot del gruppo «Env» di ogni
repository nel setup, o dei «Valori proposti» del tipo se il repository non ha il gruppo compilato,
il cui valore locale dipende dall'ambiente:

- una variabile che vale esattamente `{apiHostUrl}` prende `https://localhost:<prima porta https
  di Kestrel del backend predefinito dello stesso ambiente>`, che il backend sia condiviso o no:
  così un frontend che gira sulla stessa macchina di Kestrel non dipende dal pool;
- una variabile con `{poolUrl}` o `{poolUrl:<id>}` prende l'URL della porta del pool del
  repository indicato, per esempio per un'app su telefono o emulatore, che non raggiunge
  `localhost` del PC.

Le variabili allineate sono quelle che il gruppo «Env» del setup dichiara per ogni repository,
per esempio l'URL dell'api del frontend con `{apiHostUrl}` nel file di env `envFile` e l'URL del
backend dell'app con `{poolUrl:<id del backend>}` in `.env`. Un repository senza gruppo «Env»
non riceve niente.

L'exe scrive quelle variabili senza chiedere, in due momenti: alla prima rilettura completa
dopo il suo avvio, per tutte, e dopo aver acceso o spento «Tunnel» dell'ambiente, per le sole
variabili che dipendono dal pool. Riscrive un valore solo se è
gestito: assente, vuoto, oppure un URL verso `*.devtunnels.ms`, `localhost` o `127.0.0.1`. Un
valore scritto a mano verso un altro host resta com'è. Un valore che l'exe non sa calcolare non
si scrive: una variabile `{poolUrl…}` senza una porta del pool assegnata al repository indicato
resta com'è, anche dopo il rilascio di quella porta, e la riga di attività dice a quale card
assegnarla. Nel file cambiano solo le righe di quelle variabili, e un file che non c'è non si
crea. Il dev server e il bundle dell'app leggono l'env solo all'avvio, quindi dopo una
riscrittura la riga di attività dice, per ogni frontend con variabili scritte, «riavvia il dev
server di <id> perche' legga <nomi>», con in più «(e <comando accessorio> legge i valori nuovi)»
se il frontend ha un comando accessorio (`extraCommand`); per l'app dice di riavviare Metro. Un
valore cambiato a mano dopo l'avvio dell'exe resta fino al riavvio successivo dell'exe, che lo
riporta al valore calcolato se è un valore gestito.

Per un collega che apre il frontend condiviso, un valore `{apiHostUrl}` che il bundle del
frontend porta nel browser punta a `localhost`, cioè alla macchina del collega.

### Limiti del pool

- Su una macchina vale un solo account devtunnel, quello con cui la CLI è autenticata: pool,
  `tunnel-pool.json` e host valgono per quell'account. Con un login diverso i tunnel del pool non
  si trovano («nessun tunnel con Description …»).
- Lo stesso account usato su due macchine con le stesse Description del pool porta due host
  sullo stesso tunnel, che si staccano a vicenda: ogni macchina usa Description proprie.
- La VSIX accende e spegne il tunnel dell'ambiente con lo stesso interruttore dell'exe, quindi
  con le stesse regole del pool.

## Ambienti Docker

Un ambiente Docker è un ambiente N con `kind: docker`: i repository sono worktree nati dai cloni
sorgente, il backend e il frontend predefiniti girano nei container `api` e `web` del compose del
repository backend, e il database è un SQL Server in un container `db`, ripristinato dal seed.
La sua colonna ha il tag «Docker» e, nella card dell'ambiente, «Ripara env» e «Distruggi ambiente». Un
ambiente Docker si crea dal «+» accanto ai numeri degli ambienti nella testata, che apre il
riquadro «Nuovo ambiente Docker» (numero, branch per repository, «Tunnel», «Crea»), e si
distrugge con «Distruggi ambiente» nella sua colonna. Seed, spazio di Docker e log globale stanno nella
scheda «Docker» del setup; il numero delle migrazioni di dev che mancano al seed compare sul
bottone «Setup».

### Il primo ambiente Docker

Un catalogo senza cloni sorgente, sezioni Env e seed non crea ambienti Docker. Per il primo
ambiente servono questi passi, nel setup tranne il primo e l'ultimo:

1. Docker Desktop acceso.
2. Card del backend, scheda «Repository» del setup: nel gruppo «Docker», «Clone sorgente» con
   «Proponi», che chiede l'URL di clone, e «File Docker di riserva» finché i branch da usare non
   hanno i file Docker (vedi «File Docker nei repository»); nel gruppo «Env», «Valori proposti»;
   nel gruppo «Seed del database», il file del seed, il nome del database e la password di SQL
   Server, che si propone da sola al primo campo del seed che si modifica.
3. Card del frontend: «Clone sorgente», «File Docker di riserva» se serve, «Valori proposti» in
   «Env», e a mano, in «Valori per ambiente», le variabili per slot che il tuo progetto vuole in
   più, ciascuna con un valore per il worktree e uno per il container.
4. Card dell'app, se l'ambiente la include: «Clone sorgente», «Valori proposti» in «Env», e a
   mano in «File locali dall'ambiente modello» i file ignorati da git che servono alla build,
   per esempio la configurazione locale di un SDK: il worktree non li ha perché git non li
   versiona.
5. Sezione «4 · Ambienti Docker»: cartella dei worktree (vuota = `<cartella dei cloni>\ambienti-docker`),
   base delle porte dei database (14000) e ambiente modello (vuoto = il primo ambiente locale).
6. Salva: crea i cloni sorgente che mancano (`git init --bare`, `remote add origin`, `fetch`).
   Poi «Verifica seed» nella card del backend controlla il seed.
7. Pannello «Docker», «Nuovo ambiente Docker»: numero, repository con il branch di ciascuno,
   «Tunnel» se servono gli URL per i colleghi, «Crea».

La sezione Env di un repository vale anche negli ambienti locali: l'allineamento degli env dei
locali usa le sue variabili per slot al posto dei «Valori proposti».

### File Docker nei repository

`Dockerfile` e `Dockerfile.dockerignore` stanno nella radice del backend e del frontend, e
`compose.yaml` nella radice del backend: i file vivono nei repository e li modifica chi lavora
sui repository. In configurazione `docker.compose` e `docker.dockerfile` sono percorsi relativi
alla radice del repository, senza `..` né disco; il Dockerfile relativo alla radice è anche
relativo al contesto della build, come chiede Compose Watch su Windows, che rifiuta un
Dockerfile con percorso assoluto. I `.dockerignore` lasciano fuori dalle immagini i file di env
del repository (per esempio `appsettings.Development.json`, i `.bat` di env e `.env`): le
immagini non contengono valori di env, che arrivano ai container dagli env_file.

A ogni creazione e a ogni avvio l'exe controlla i file Docker nella radice di ogni worktree. Un
file versionato nel branch si usa così com'è. Un file assente, o ignorato da git perché copiato
in precedenza, si copia dalla cartella «File Docker di riserva» del repository
(`docker.filesFallback`, il percorso di un checkout che ha i file), e il suo percorso entra in
`info/exclude` della cartella git comune del clone sorgente, che vale per tutti i suoi worktree.
Le copie nel worktree sono gestite da Soffritto: si modificano nella cartella di riserva o
nel repository, perché una modifica fatta sulla copia viene sovrascritta al prossimo avvio e si
perde con «Distruggi ambiente».

### Contratto del compose

L'exe lancia ogni comando come `docker compose -p <progetto> -f <compose del worktree del
backend> -f <override> --env-file <slot.env>` dalla cartella dello slot, `<cartella
dati>\docker\ambiente-N`. Prima di ogni avvio scrive nella cartella dello slot:

- `slot.env`, con le variabili che il compose interpola: `API_CONTEXT`, `API_DOCKERFILE`,
  `API_HOST_PORT`, `API_ENV_FILE`, `WEB_CONTEXT`, `WEB_DOCKERFILE`, `WEB_HOST_PORT`,
  `WEB_ENV_FILE`, `DB_HOST_PORT`, `DB_IMAGE`, `DB_PASSWORD`, `DB_MEMORY_LIMIT_MB`. I contesti
  sono i worktree; `API_DOCKERFILE` e `WEB_DOCKERFILE` sono relativi al contesto;
- `soffritto.override.yaml`, che mette l'etichetta della cartella dati su ogni servizio
  elencato da `docker compose config --services`;
- un env_file per repository nel container, `<id>.env`, con le variabili dell'api o del web.

Il compose ha tre servizi: il database (`db`, il servizio del seed), l'api (`api`) e il web
(`web`) nel profilo `web`, che l'exe attiva solo se l'ambiente ha il frontend nel container. I
nomi dei servizi vengono dalla configurazione (`docker.service` e il servizio del seed) e devono
essere quelli del compose. Prima
dell'avvio `docker compose config --quiet` controlla che non manchi una variabile. Il container
dell'api deve ricevere dal suo env_file `ConnectionStrings__<nome>` verso il servizio del
database e il database dello slot (vedi «Guardie del database»).

### Compose Watch del web

Il codice del frontend sta nell'immagine del web, senza mount del worktree. Il `compose.yaml`
del backend dichiara per il web le regole di Compose Watch (`sync`, `sync+restart`, `rebuild`)
sui file del frontend, per esempio `sync` dei sorgenti e `rebuild` su `package.json`. Dopo un avvio riuscito del web l'exe apre una console
`AMBIENTI watch-web-<chiave>` con `docker compose … --profile web watch --no-up web`; la chiave
è il progetto senza `soffritto-` (per esempio `11`). La console si chiude prima dello stop
del web o dell'ambiente e prima di «Distruggi ambiente». Con il container del web acceso e il watch
spento la card del frontend ha il pallino arancio, e Avvia ricostruisce l'immagine e riapre la
console.

Sulla macchina di sviluppo la prima compilazione di webpack nel container dura circa 100
secondi, e una ricompilazione dopo un salvataggio circa 5 secondi. Un `docker compose up`
lanciato a mano non apre il watch. Il watch parte dopo la build senza una sincronizzazione
iniziale: una modifica salvata tra la build e l'avvio del watch arriva nel container al
salvataggio successivo di quel file.

### Slot e porte

Ogni ambiente Docker N pubblica le sue porte su `127.0.0.1`, a base + N:

- database `14000 + N` (`docker.databasePortBase`), sulla porta 1433 del container;
- api `15000 + N`, sulla porta 8080 del container (`docker.publishedPorts` del backend, `15000:8080`);
- web `18000 + N`, sulla porta 8082 del container (`docker.publishedPorts` del frontend, `18000:8082`).

L'ambiente 11 risponde quindi su `http://127.0.0.1:15011/swagger` (api), `http://127.0.0.1:18011`
(web) e `127.0.0.1,14011` (database). Il container temporaneo del seed usa la base, 14000.
Ogni base copre base + 1…base + 99, e la configurazione rifiuta due basi che si sovrappongono. La
creazione rifiuta un ambiente con una porta già in ascolto sulla macchina.

### Creazione

«Nuovo ambiente Docker» propone il primo numero libero sopra gli ambienti locali e una riga per
repository del catalogo: casella e branch, abilitati solo per i repository con il clone
sorgente. La tendina elenca i branch di origin, con quelli già portati da un worktree marcati;
un nome nuovo nasce da `origin/dev` (vedi «Branch di sviluppo»), dopo una conferma. «Crea» esegue, nell'ordine:

1. controlli senza effetti: Docker raggiungibile, numero libero, configurazione valida con la
   voce nuova, cloni sorgente presenti, branch liberi, cartelle dei worktree assenti, seed
   pronto, porte libere, nessun container o volume del progetto;
2. registrazione della voce in `config.json` e colonna nuova nella griglia;
3. worktree dai cloni sorgente: un branch locale esistente si usa, uno solo su origin diventa un
   branch locale che lo traccia, uno nuovo nasce da `origin/dev` senza upstream;
4. file Docker nei worktree;
5. porte del pool per backend e frontend, con «Tunnel» (spuntato in partenza);
   con il pool pieno «Crea» si ferma con l'errore;
6. env dei worktree e dei container;
7. guardia del worktree del backend, `slot.env`, override e guardia del container dell'api;
8. database dal seed, poi `dotnet restore` e le migrazioni del branch che il seed non ha;
9. `docker compose up -d --build`, poi la console del Compose Watch.

Un errore dopo la registrazione lascia la voce e la colonna, e si rimedia con «Distruggi ambiente».

Il menu «Apri in» del pannello delle PR ha anche la voce «Nuovo ambiente Docker», se il catalogo
ha un backend con compose e clone sorgente: crea un ambiente sul primo numero libero sopra i
locali, con un worktree sul branch della PR per ogni repository del gruppo e con backend e
frontend senza PR su `dev`. Assegna le porte del pool a backend e frontend; con il pool pieno
l'ambiente si crea lo stesso, senza porte del pool. Prima fa la fetch dei cloni sorgente dei
repository con una PR e rifiuta un branch che origin non ha; poi esegue gli stessi passi di
«Crea», quindi rifiuta anche un branch già portato da un altro worktree. L'esito compare sotto
il gruppo di PR, il dettaglio in «Operazioni» nella card dell'ambiente.

### Avvio, stop, log e stato

L'avvia/ferma della card dell'ambiente avvia tutto lo slot (env_file dei container riscritti,
file Docker, `slot.env`, override, guardia dell'api, `docker compose up -d --build`) e poi il
watch; lo stop chiude prima il watch e poi esegue `docker compose stop`. Avvia e Ferma della card
del backend e del frontend agiscono sul solo servizio, con `up -d --build <servizio>`, e il bottone
«Log» apre una console `AMBIENTI log-<servizio>-<chiave>` con `docker compose logs -f --tail 200
<servizio>`. Reset e «Apri in» fermano watch e container prima di git, e rifiutano un branch già
portato da un altro worktree dello stesso clone sorgente.

I log stanno vicino a ciò che descrivono, in sezioni chiuse di default:

- «Log di api» e «Log di web», nel corpo della card del backend e del frontend: le ultime 200
  righe del container (`docker compose logs --tail 200 <servizio>`), rilette ogni 3 secondi solo
  mentre la sezione è aperta;
- «Operazioni», nella card dell'ambiente Docker: il dettaglio di creazione, avvio e stop, Reset,
  tunnel, «Ripara env» e «Distruggi ambiente» di quell'ambiente, ultime 300 righe. Si azzera quando nasce
  un ambiente nuovo con lo stesso numero;
- il log della scheda «Docker» del setup tiene le operazioni globali (seed, pulizia,
  compattazione, pool dei tunnel) e una riga d'esito per ogni ambiente creato o distrutto. Una
  creazione che si ferma prima che la colonna esista lascia l'errore lì.

Ogni 3 secondi l'exe legge con `docker ps` i container della sua cartella dati e controlla che i
servizi accesi rispondano: un login sul database dello slot, una risposta HTTP qualunque sulla
porta host di api e web. La riga della card dell'ambiente dice la fase:

- spento: nessun container acceso;
- avvio: i container partono e non rispondono ancora; il pallino dell'ambiente pulsa e la card
  dell'ambiente scorre a righe, mentre le card dei repository restano sempre in vista;
- pronto: i servizi accesi rispondono;
- errore: un container è uscito senza uno stop dell'exe, o è acceso ma non risponde da 5
  minuti; il pallino dell'ambiente diventa rosso.

Con Docker spento la colonna dice «Docker non raggiungibile» con il motivo. Con il database
spento o in avvio le migrazioni della card del backend non si leggono, e si rileggono da sole
quando il database torna. Soffritto non riaccende da solo i container fermi: si
riaccendono con l'avvia della card dell'ambiente.

Una colonna locale il cui backend punta al database di un ambiente Docker
(`127.0.0.1,<porta del database dello slot>` o `localhost,<porta>` negli appsettings) segue la
fase di quel database: con il database spento o in avvio non legge le migrazioni, e le rilegge
quando il database torna.

### Distruzione

«Distruggi ambiente» controlla prima, senza modificare niente, che nessun worktree abbia codice da
perdere: file modificati o non tracciati, un'operazione git a metà, commit che non stanno su
nessun remoto. Poi chiede conferma con il riepilogo e, nell'ordine, chiude il watch, esegue
`docker compose down -v --remove-orphans` del solo progetto dell'ambiente, toglie le immagini
costruite dal suo compose (`<progetto>-<servizio>`, cioè api e web), toglie i worktree senza
`--force`, cancella la cartella dello slot con gli env_file, rilascia le porte del pool e toglie
la voce da `config.json`, che chiude la colonna. Restano i branch nei cloni sorgente e la nota
dell'ambiente.

### Seed del database

Le migrazioni di un progetto possono non saper costruire un database vuoto da zero, per questo un
ambiente Docker parte sempre dal seed, un backup SQL Server configurato nella card del backend (gruppo «Seed del database»:
file `.bak`, o `.7z` con un solo `.bak`, immagine di SQL Server, database, password, memoria e
servizio nel compose). La password protegge solo container legati a `127.0.0.1` con dati di
sviluppo, e la configurazione la contiene. Un `.7z` si estrae con `tar.exe` una volta sola, in
`<cartella dati>\docker\seed-cache\<SHA256 dell'archivio>\`.

«Verifica seed», nel setup, ripristina il seed in un SQL Server temporaneo (container
`soffritto-seed` su `127.0.0.1:14000`) e lo confronta con le migrazioni di `origin/dev` del
clone sorgente, dopo un fetch, o del backend dell'ambiente modello se il clone sorgente non c'è.
Il seed è compatibile se non ha migrazioni che il codice non conosce. L'esito resta in
`<cartella dati>\docker\seed-info\<SHA256 del file>.json`, e da lì l'exe calcola le migrazioni
di `origin/dev` che mancano al seed: il numero compare sul bottone «Setup» e nella scheda
«Docker» del setup, si ricalcola all'avvio e ogni 30 minuti senza fetch, e con un fetch
all'apertura della scheda.

«Allinea seed», nella scheda «Docker» del setup, vale subito senza Salva e porta il seed a
`origin/dev`: fetch del clone sorgente,
worktree del seed (la cartella del backend con `seed` al posto di `{n}`, staccato su
`origin/dev`, rifiutato se ha modifiche), container temporaneo, ripristino, `dotnet restore` e
migrazioni con `--connection` verso il container. Poi compatta i file del database (recovery
model `SIMPLE`, `CHECKPOINT`, `DBCC SHRINKFILE` per file) anche quando le migrazioni mancanti
sono 0, e fa un `BACKUP` compresso in un file nuovo accanto al configurato,
`<nome>-<aaaaMMgg-hhmmss>.bak`. Il seed prodotto resta in recovery model `SIMPLE`, come i
database degli slot che lo ripristinano. `seed.path` in `config.json` passa al file nuovo anche
con il setup aperto, e la casella «File del seed» della bozza lo segue se non l'avevi cambiata;
il file precedente resta: per tornare indietro si rimette il percorso
precedente nel setup. Una migrazione fallita lascia il seed com'è e mostra nel log le righe di
errore del build: quando `dotnet ef` dice solo «Build failed», l'exe rilancia `dotnet build`
sullo startup project per leggerle. «Verifica seed» e «Allinea seed» girano una alla volta per
cartella dati, con `<cartella dati>\docker\seed.lock`.

### Guardie del database

Nessuna operazione sul database di un ambiente Docker parte verso un database diverso da quello
del suo slot:

- nel worktree del backend, prima di leggere le migrazioni, di un database update, di un
  rollback, della copia del comando e delle migrazioni della creazione, la connection string
  del primo appsettings che la contiene deve puntare a `127.0.0.1,<porta dello slot>` (o
  `localhost,<porta>`) e al database del seed;
- prima di avviare l'api, `ConnectionStrings__<nome>` del container, come la risolve
  `docker compose config`, deve puntare al servizio del database e al database del seed;
- `RESTORE` e `BACKUP` partono solo verso un container che pubblica SQL Server su `127.0.0.1`
  alla porta della connessione, e la compattazione solo sul container temporaneo del seed.

Un'operazione fermata dice file, server e database, mai la password. `--connection` si usa solo
per le migrazioni del seed. Un `docker compose up` lanciato a mano non passa dalle guardie; la
VSIX aperta su un worktree passa dalla guardia del database dello slot come l'exe.

### Env degli ambienti Docker

Ogni repository ha nel gruppo «Env» della sua card del setup un adapter («Formato del file di
env»), che legge e scrive il suo file di env
(`.bat`, dotenv, `appsettings*.json` o manuale, senza file), e tre classi di variabili:

- costanti, lette dallo stesso repository nell'ambiente modello (`constantsSource`, o il file
  target) più le variabili manuali del setup; al container non arrivano quelle che iniziano con
  un prefisso di `containerExclude` (per il backend `Kestrel:`, altrimenti gli endpoint Kestrel
  dell'ambiente modello vincerebbero sulla porta 8080 del Dockerfile);
- segreti, letti dalla fonte dei segreti del repository: un file relativo al repository
  nell'ambiente modello, o assoluto;
- valori per slot, che vincono sulle altre due, con un valore per il worktree e uno per il
  container (vuoto = non va al container). I segnaposto sono `{n}`, `{hostPort}` e
  `{containerPort}` (la porta del container del repository), `{apiHostUrl}`
  (`http://127.0.0.1:<porta dell'api>`), `{apiContainerUrl}` (`http://api:8080`),
  `{hostConnection}` e `{containerConnection}` (la connessione al database dello slot
  dall'host e dai container), `{poolUrl}` e `{poolUrl:<id>}` (l'URL della porta del pool del
  repository o del repository `<id>`).

Il file di env del worktree si scrive solo alla creazione e con «Ripara env», mai a un avvio:
le modifiche fatte a mano nel worktree restano fino a «Ripara env». «Ripara env», nella card
dell'ambiente, riscrive i file di env dei worktree, gli env_file dei container e i file locali,
poi controlla la guardia del worktree del backend; un file target che non c'è più riparte dalle
costanti dell'ambiente modello. L'env_file del container si
riscrive a ogni avvio, con i nomi `A:B` che diventano `A__B`. Un valore per slot che non si sa
calcolare, per esempio `{poolUrl:…}` senza porta del pool, toglie la variabile dal worktree e
dal container, compresa la costante con lo stesso nome: la riga del worktree resta com'è, anche
se scritta a mano. Fa eccezione la creazione, quando il file target non esiste ancora e la riga
prende la costante dell'ambiente modello. La colonna e l'anteprima del setup mostrano l'avviso;
per una variabile `{poolUrl…}` l'avviso dice a quale card assegnare la porta del pool.

Soffritto non scrive segreti in file versionati né nei log, e i `.dockerignore` li tengono
fuori dalle immagini. Con l'adapter `.bat` e la fonte dei segreti `.bat` il file del worktree
richiama il file dei segreti con `@CALL`; con gli altri adapter i segreti si scrivono solo se il
file target è ignorato da git. Nei container i segreti arrivano dagli env_file della cartella
dello slot, che li contengono in chiaro. I log riportano nomi e conteggi, e l'uscita di compose
passa da un filtro che toglie i valori degli env_file. Per ruotare un segreto si cambia la sua
fonte: il container lo riceve al prossimo avvio, il worktree con `@CALL` lo legge già dalla
fonte, gli altri worktree con «Ripara env». «Anteprima» nel setup mostra le variabili su uno
slot fittizio con nomi, classi e origini, mai valori.

`localFiles` (nel setup «File locali dall'ambiente modello») elenca file relativi alla radice
del repository che, alla creazione e con «Ripara env», si copiano byte per byte dallo stesso
repository nell'ambiente modello al worktree: servono per i file che git non versiona, come
quelli dell'app indicati in «Il primo ambiente Docker». Un file che il modello non ha è un
avviso nel log.

Per l'app, una variabile per slot con `{poolUrl:<id del backend>}` vale l'URL della porta del
pool del backend: senza porta del backend resta il valore del file, con l'avviso. Le altre
variabili dell'app, che non sono per slot, restano il valore copiato dall'ambiente modello. Per
il frontend, una variabile con `{apiHostUrl}` nel worktree e `{apiContainerUrl}` nel container
vale `http://127.0.0.1:<porta dell'api>` nel worktree e `http://api:8080` nel container; la
variabile della porta del dev server (`devServerPortVariable`) vale la porta host del web nel
worktree, che usa anche l'anteprima della card, e la porta del container nel container, 8082 di
default.

### Nomi delle risorse Docker

Le risorse Docker portano l'impronta della cartella dati, così la Release e una build con
`SOFFRITTO_DATA` non si toccano a vicenda. L'impronta sono le prime 8 cifre esadecimali dello
SHA256 del percorso della cartella dati:

- il progetto compose dell'ambiente N è `soffritto-N`; con `SOFFRITTO_DATA` impostata è
  `soffritto-<prime 4 cifre dell'impronta>-N`, e la chiave delle console diventa
  `<4 cifre>-N`;
- il container temporaneo del seed è `soffritto-seed`, con lo stesso prefisso;
- ogni container ha l'etichetta `soffritto.dati=<impronta>`.

L'exe legge solo i container con la sua etichetta, e non tocca un progetto o un container del
seed che esiste senza.

### Memoria e disco

L'exe non mette tetti di memoria ai container, tranne quello di SQL Server (`memoryLimitMb` del
seed, 2048 MB proposti). Sulla macchina di sviluppo un ambiente con il web usa circa 6.6 GiB
(database 2, api 0.1, web 4.5), uno senza web circa 2.1 GiB: con i 15.46 GiB di Docker Desktop
stanno circa 2 ambienti con il web, o 5 senza. Più memoria a Docker Desktop si dà a WSL con
`memory=` nella sezione `[wsl2]` di `%UserProfile%\.wslconfig`, seguito da `wsl --shutdown`:
è un'impostazione dell'utente, fuori dall'exe.

Per il disco la stima di 5 ambienti è al più 43.4 GB e sta nello spazio libero della macchina
di sviluppo. Il database di un ambiente occupa circa 8 GB finché il seed non è compattato da un
primo «Allinea seed», e ogni worktree dell'app in cui si installano i `node_modules` aggiunge
6.9 GB.

### Spazio di Docker

Docker Desktop su WSL 2 tiene immagini, container, volumi e cache di build in un disco virtuale,
`%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx`. Il file cresce con le build e non si
restringe da solo quando immagini e cache si tolgono: Docker riusa lo spazio liberato, ma il PC
non lo riavrà finché il file non viene compattato. Lo strumento lo tiene sotto controllo così:

- **Distruggi** toglie le immagini dell'api e del web dell'ambiente.
- **Pulizia alla chiusura** (preferenza «Pulisci alla chiusura dell'exe», attiva di default):
  quando l'exe si chiude con «Esci», un processo staccato e senza finestra toglie le immagini
  `<prefisso>N-*` degli ambienti N che non sono più ambienti Docker della configurazione e riduce
  la cache di build a 10 GB (`docker builder prune --max-used-space`), togliendo le voci usate
  meno di recente. Non ferma Docker, non tocca i container e non chiede permessi; salta se un
  comando `docker compose` sta lavorando (dall'exe o dal server MCP), se c'è un'azione in corso
  nell'exe o se Docker non risponde. L'esito va in `<cartella dati>\docker\pulizia.log`.
- **Pannello «Docker», sezione «Spazio di Docker»**: dimensione del file sul PC e uso interno
  per tipo, con «Pulisci ora» (la stessa pulizia, nel log del pannello) e «Compatta disco».
  «Compatta disco» chiede conferma, chiude Docker Desktop (con tutti i container accesi, anche
  quelli non degli ambienti), ferma WSL con `wsl --shutdown` (anche le altre distribuzioni
  accese, che la conferma elenca), compatta il file con i permessi di amministratore
  (`Optimize-VHD`, o `diskpart` dove il modulo Hyper-V manca) e riaccende Docker Desktop. Si
  compatta solo lo spazio già liberato: prima conviene «Pulisci ora».

Il disco «sparse» di WSL, che restituirebbe lo spazio da solo, non si usa: Microsoft lo tiene
disattivato per rischio di corruzione dei dati (`wsl --manage <distro> --set-sparse true` chiede
`--allow-unsafe`) e un disco sparse non si può più compattare con `Optimize-VHD`.

### Limiti

- Un ambiente Docker richiede il backend, con compose, Dockerfile, seed e la variabile di
  connessione per slot.
- Nei container entrano solo il backend e il frontend predefiniti: app e generici restano
  worktree con i loro strumenti locali.
- Nel web del container l'URL dell'api proposto è `http://api:8080`: il proxy del dev server lo
  raggiunge, ma un link costruito con quell'URL e aperto nel browser non si apre, perché `api`
  si risolve solo dentro la rete di compose.
- Git porta un branch in un solo worktree: un branch già portato da un altro worktree dello
  stesso clone sorgente non si sceglie.
- Ogni avvio ricostruisce le immagini (`up --build`).
- Il comando accessorio del frontend (`extraCommand`), in un worktree senza `node_modules`,
  esegue prima `npm ci`.
- La nota di un ambiente è legata al numero: un ambiente ricreato con il numero di uno distrutto
  ritrova la sua nota.
- CLI e server MCP non vedono gli ambienti Docker; la VSIX li riconosce dalla soluzione aperta.
- Immagini, branch e seed precedenti non si puliscono da soli.

## Uso da riga di comando

```
dotnet run --project src\Soffritto.Cli -- C:\Dev\backend-1 --fetch
```

Senza `--fetch` non tocca la rete: i conteggi ahead/behind riflettono l'ultimo fetch fatto a
mano e il dev tunnel non viene interrogato. Con `--fetch` la CLI legge anche la porta del pool
assegnata al backend dell'ambiente, come la VSIX, o scrive «non condiviso». Exit code `0` se
l'ambiente è allineato, `1` altrimenti (lo stato del tunnel non concorre all'exit code).

## Server MCP

`Soffritto.Mcp` è un server MCP stdio che funziona con qualunque client MCP: lavora sugli
ambienti numerati della stessa `config.json` dell'exe. Sugli ambienti locali è **sola lettura**:
sul database solo la `SELECT` sulla history, su git al massimo un `fetch` a richiesta, sui
processi solo la lettura WMI. Sugli ambienti Docker fa le stesse azioni dell'exe, con le stesse
funzioni di Core. Le istruzioni del server spiegano il flusso a qualunque client, senza bisogno di
skill.

Tool di lettura:

- `elenca_ambienti` — gli ambienti configurati, locali e Docker (`tipo`), con le loro cartelle o
  worktree, e a quale ambiente e repository appartiene una cartella (di default la cartella
  corrente del server, cioè quella della sessione). Le chiavi `backend`, `frontend` e `app` sono
  i repository predefiniti dell'ambiente; la lista `repository` li contiene tutti, con id, tipo e
  se sono predefiniti. `cartellaRiconosciuta` dice tipo e id del repository riconosciuto.
  Istantaneo.
- `stato_ambiente` — per l'ambiente N (o quello della cartella): server e database a cui punta il
  backend predefinito, migrazioni per context, git, dev tunnel del backend e del frontend,
  indirizzi di Kestrel e del dev server, variabili dell'env del frontend, servizi accesi con i
  loro PID e un elenco `problemi` già in parole. Le chiavi di sempre (`backend`, `devTunnel`,
  `frontend`, `app`) descrivono i predefiniti, e restano anche quando l'ambiente non ne ha uno,
  con `esiste: false` e una nota; la lista `repository` riporta per ogni repository git, dev
  tunnel e servizi. Qualche secondo; `fetch` è spento di default, `tunnel` acceso.

Il dev tunnel di ogni repository è la sua porta del pool: la sezione `devTunnel` dice con
`condiviso` se il repository ha una porta assegnata, e senza porta non interroga la CLI. La
chiave `puntaAlBackendDellAmbiente` del frontend è vera quando un valore dell'env del frontend
contiene `localhost:<porta https di Kestrel del backend dell'ambiente>` oppure l'host dell'URL
della porta del pool del backend; l'host del pool conta solo quando il tunnel è stato letto.
`stato_ambiente` vale per gli ambienti locali; per un ambiente Docker rimanda a
`stato_ambiente_docker`.

Tool degli ambienti Docker:

- `stato_ambiente_docker` — worktree, branch e git di ogni repository; per database, api e web il
  container e se risponde, con l'indirizzo locale; URL del pool, console del Compose Watch,
  migrazioni del database dello slot rispetto al branch, nota e operazione in corso. Sola lettura.
- `crea_ambiente_docker` — i branch per id del catalogo, numero (vuoto = il più basso libero),
  porte del pool facoltative e la nota; fa la stessa creazione del «+» della testata e apre il
  Compose Watch del web.
- `avvia_ambiente_docker`, `ferma_ambiente_docker`, `avvia_servizio_docker`,
  `ferma_servizio_docker` — come i pulsanti della colonna e delle card, Compose Watch compreso.
- `log_servizio_docker` — le ultime righe di `docker compose logs` di un servizio. Sola lettura.
- `applica_migrazioni_docker` — `dotnet restore` e `dotnet ef database update` del branch del
  worktree, per tutti i context o per uno, con la guardia sul database dello slot.
- `ripara_env_docker`, `accendi_tunnel_docker`, `spegni_tunnel_docker` — come «Ripara env» e
  l'interruttore «Tunnel» dell'intestazione dell'ambiente nell'exe.
- `nota_ambiente` — scrive la nota di un ambiente, locale o Docker; l'exe la mostra subito.
- `stato_operazione` — avanzamento, log ed esito di un'operazione.

La distruzione di un ambiente Docker non è tra gli strumenti: la fa una persona con «Distruggi
ambiente» nell'exe. La conferma via elicitation di MCP non è affidabile: un client può dichiarare
l'elicitation e rispondere da solo con un rifiuto, senza mostrare niente alla persona.

Creazione, avvio, migrazioni e porte del pool girano in background nel processo del
server: lo strumento aspetta qualche secondo (`attesaSecondi`) e, se l'operazione dura di più,
risponde con un id da seguire con `stato_operazione`. Su un ambiente gira un'operazione alla volta
per processo. Le operazioni muoiono con il processo del server, cioè con la sessione del client:
un ambiente creato a metà si toglie con «Distruggi ambiente» nell'exe.

L'exe osserva `config.json`: un ambiente Docker creato o distrutto dall'MCP compare o sparisce
dalla griglia senza riavvio, e la nota scritta dall'MCP arriva subito sulla colonna. Le scritture
di `config.json` e di `tunnel-pool.json` passano da un lock fra processi (`config.lock`,
`tunnel-pool.lock` nella cartella dati), quindi exe e server MCP non si sovrascrivono. Non c'è
invece un lock fra processi sui comandi compose dello stesso ambiente: lanciare insieme dall'exe e
dall'MCP un avvio e uno stop sullo stesso ambiente dà un esito imprevedibile.

Ogni client MCP lo registra come server stdio che lancia `Soffritto.Mcp.exe`. Per esempio,
con Claude Code la registrazione per l'utente, puntando alla build Debug, è:

```
claude mcp add --scope user soffritto -- <cartella del repository>\src\Soffritto.Mcp\bin\Debug\net11.0\Soffritto.Mcp.exe
```

Il processo vive quanto la sessione del client e tiene bloccate le sue DLL: per ricompilare il
progetto va chiusa la sessione, o il solo server (in Claude Code da `/mcp`).

Con l'installazione del Setup.exe il server sta in `mcp\Soffritto.Mcp.exe` accanto all'exe, e
«Registra in Claude Code» nella scheda Componenti del setup fa la registrazione (vedi «Setup.exe»).

## Setup.exe

Chi usa Soffritto senza svilupparlo lo installa con lo `Soffritto.App-win-Setup.exe`
delle GitHub Releases: niente repository, niente SDK, niente diritti di amministratore.

### Cosa installa

Velopack installa per l'utente in `%LOCALAPPDATA%\Soffritto.App`, con i collegamenti
«Soffritto» sul desktop e in Start e la voce per disinstallare in «App installate». Il
pacchetto contiene:

- l'exe Desktop **self-contained**: parte su qualunque Windows x64 senza runtime .NET;
- `mcp\Soffritto.Mcp.exe`, il server MCP, self-contained con le sue DLL nella cartella `mcp`
  (non in un file solo, che a ogni avvio può superare il timeout di connessione del client MCP);
- `vsix\Soffritto.Vsix.vsix`, l'estensione di Visual Studio.

Il pacchetto non contiene nessuna configurazione e il setup non scrive la cartella dati
`%LOCALAPPDATA%\Soffritto`. Per questo il `packId` è `Soffritto.App` e non
`Soffritto`: con lo stesso nome programma e dati finirebbero nella stessa cartella. Al primo
avvio dopo il setup, `config.json` c'è già se la macchina aveva usato Soffritto; altrimenti il
setup parte vuoto, come per ogni exe senza configurazione.

### Primo avvio e scheda Componenti

Al primo avvio dopo il Setup.exe il pannello Setup si apre sulla scheda **Componenti**. La scheda
non fa parte della configurazione: ogni bottone agisce subito.

- **Programmi esterni**: Git, Dev tunnel e Docker Desktop, cercati nel PATH della macchina e
  dell'utente riletto dal registro (un programma appena installato non è nel PATH ricevuto
  all'avvio) e, per devtunnel, tra i pacchetti WinGet. «Installa» apre una console con
  `winget install --id <id> --exact`: licenze ed elevazione si confermano lì. «Accedi a dev
  tunnel» apre `devtunnel user login`.
- **Server MCP**: lo stato della registrazione si legge da `~\.claude.json` (il server
  `soffritto` registrato per l'utente), senza lanciare `claude mcp get`, che avvierebbe il
  server. «Registra in Claude Code» apre una console con `claude mcp remove` e `claude mcp add
  --scope user`: un server con lo stesso nome viene sostituito.
- **Estensione di Visual Studio**: «Installa in Visual Studio» apre `VSLauncher.exe` con il
  `.vsix`, come il doppio click (vedi «Installazione dell'estensione»).
- **Aggiornamenti**: vedi sotto.

In una build lanciata da `bin\` o da `publish\` MCP e VSIX non ci sono, e la scheda lo dice.

### Aggiornamenti

L'exe installato cerca la release nuova a ogni avvio, in background, e la scarica; il footer
mostra «vX alla chiusura» accanto alla versione, e alla chiusura Velopack installa in silenzio
senza riaprire l'exe. «Cerca ora» nella scheda Componenti fa la stessa ricerca. Il riavvio dopo un
Salva del setup non installa: la copia nuova partirebbe mentre i file vengono sostituiti.

Velopack lancia `Update.exe`, che deve sopravvivere all'exe per installare dopo la sua uscita.
Il job kill-on-close dell'exe (`ChildProcessJob`) lo chiuderebbe: per questo l'exe passa a
Velopack un `IProcessImpl` proprio (`Installation.DetachedProcess`) che avvia con
`DetachedLauncher`, fuori dal job.

La release da cui aggiornarsi non è nel codice: `release.ps1` la passa come proprietà MSBuild
`UpdateRepository`, che finisce in un `AssemblyMetadata` dell'exe. Le build di sviluppo non ce
l'hanno e non si aggiornano.

### Creare e pubblicare una release

```
powershell -ExecutionPolicy Bypass -File release.ps1
```

Lo script legge la versione da `Directory.Build.props`, pubblica exe e server MCP self-contained
in `artifacts\pack`, aggiunge la VSIX (`-SkipVsix` per farne a meno) e crea con `vpk` (tool locale
in `dotnet-tools.json`) il Setup.exe e i pacchetti in `artifacts\releases`. Non tocca le build
di sviluppo in `bin\`. Il repository delle release è il remote `origin`, o
`-Repository https://github.com/<proprietario>/<repo>`.

Con `-Upload` scarica prima l'ultima release (per il pacchetto delta) e poi carica la nuova su
GitHub come bozza, da pubblicare dal sito; con `-Upload -Publish` la pubblica subito. Il token viene
da `GITHUB_TOKEN` o da `gh auth token`. Gli aggiornamenti e il download anonimo del Setup.exe
funzionano solo se il repository delle release è pubblico.

L'exe del pacchetto non è un file singolo: self-contained, WPF non carica dal bundle
`DirectWriteForwarder.dll` (C++/CLI) e l'exe esce con `BadImageFormatException`. Il pacchetto di
Velopack è comunque un archivio, e i delta restano piccoli.

## Installazione dell'estensione

Visual Studio deve essere chiuso.

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

Lo script compila in Release, rimuove la versione precedente e reinstalla. A installazione
avvenuta: **Visualizza › Altre finestre › Soffritto**.

Dalla 1.0.2 il pacchetto include `Soffritto.Presentation.dll` accanto a Core:
l'orchestrazione del pannello vive lì. Dalla 1.0.3 include anche `System.Management.dll` (con
`System.CodeDom.dll`) per lo stato di Kestrel: come per SqlClient, il RID fissato porta nel
pacchetto l'implementazione Windows reale e non il segnaposto. La versione si alza in
`Directory.Build.props`, che vale per tutta la soluzione: va alzata a ogni installazione che deve
arrivare davvero in Visual Studio.

### `VSIXInstaller.exe` da CLI non funziona, e non è colpa degli argomenti

Su VS 2026 le estensioni con `ExtensionType="VisualStudio.Extensibility"` non si installano
invocando `VSIXInstaller.exe` da riga di comando. Falliscono sempre con:

```
RequiresInstallerException: Requested operation must be completed using Visual Studio Installer.
Multiple targets are currently not supported for this operation.
```

Verificato per esclusione: l'errore è **identico** con `/instanceIds`, con `/admin`, e con un
manifest ridotto a un solo `<InstallationTarget>`. Lo stack lo conferma —
`ExtensionService.HandleExtensibilityType(..., IList validSKUs, ...)`, raggiunto da
`GetInstallableData`, cioè solo sul percorso di **installazione**. La disinstallazione, che non
passa da lì, dalla CLI funziona regolarmente.

Il doppio click sul `.vsix` invece funziona perché non passa da `VSIXInstaller`: l'associazione
di `.vsix` punta a `VSLauncher.exe`, che instrada verso l'installer corretto. `install.ps1` fa
esattamente la stessa cosa, quindi mostra una finestra e non può essere silenzioso.

Riepilogo delle vie percorribili:

| Via | Silenziosa | Note |
|---|---|---|
| `F5` sul progetto `.Vsix` | — | **Consigliata per iterare**: istanza sperimentale, non tocca l'installazione di lavoro |
| `install.ps1` / doppio click | no | Installa nell'istanza reale, richiede conferma |
| `VSIXInstaller.exe` da CLI | — | Non supportato per questo tipo di estensione |

### `RuntimeIdentifier` è obbligatorio per SqlClient

Il progetto `.Vsix` fissa `win-x64`. Non è un vezzo: il VSIX **non contiene un `.deps.json`**,
quindi a runtime non avviene la risoluzione per RID. `Microsoft.Data.SqlClient` pubblica in `lib/`
un assembly segnaposto (~93 KB) che lancia *"Microsoft.Data.SqlClient is not supported on this
platform"* per qualunque operazione, e tiene l'implementazione reale sotto
`runtimes/win/lib/net9.0/` (~1,7 MB). Senza RID l'host carica il segnaposto.

Fissando il RID, MSBuild appiattisce nella root l'assembly Windows corretto e la
`Microsoft.Data.SqlClient.SNI.dll` nativa. Effetto collaterale: l'output si sposta in
`bin\Release\net10.0-windows8.0\win-x64\`, per questo `install.ps1` cerca il `.vsix` invece di comporre il
percorso a mano.

La VSIX ha come framework `net10.0-windows8.0` e non `net11.0` come gli eseguibili: l'host delle
estensioni di Visual Studio 18 porta con sé solo i runtime 8 e 10, e la SDK di estensibilità 18.12
dichiara quel framework. Per questo anche `Core` e `Presentation`, che la VSIX referenzia, restano
a `net10.0`. Il manifest generato chiede Visual Studio 18.12 o successivo.

### Altre note

- La rimozione dalla CLI funziona: `install.ps1 -UninstallOnly`.
- L'attesa usa `Start-Process -Wait` e non `&`: gli installer si ri-lanciano elevati da sé, quindi
  con `&` PowerShell leggerebbe l'exit code del processo già uscito.
- `InstallationTargetArchitecture = VisualStudioArchitecture.Amd64` in `ExtensionMetadata` serve
  comunque: senza, un progetto AnyCPU genera due `<InstallationTarget>` (amd64 e arm64). Non era la
  causa di questo errore, ma resta un'ambiguità inutile nel manifest.
- Fuori dal Marketplace non esiste aggiornamento automatico: dopo ogni modifica va reinstallata.
- Il VSIX non è firmato: al primo install Visual Studio segnala "publisher non verificato". È
  atteso e non blocca nulla.

## Caricamento per sezione

All'apertura il pannello mostra uno **splash** con il logo (disegnato in XAML, niente bitmap: il
Remote UI non le carica in modo affidabile) e il testo di stato, al posto di intestazione a
trattini e sezioni vuote che sembrerebbero un pannello rotto; sparisce quando la prima lettura
vera è partita e comunque non prima di **tre secondi** dall'apertura: le letture corrono dietro
allo splash, così quando il pannello compare le sezioni sono già popolate invece di riempirsi
alla spicciolata. Nello stesso momento esegue da solo un **refresh completo**, lo stesso del pulsante
**Aggiorna**: rilegge tutto, esegue `git fetch` e interroga il dev tunnel. All'avvio dell'IDE la
finestra viene ripristinata prima che la soluzione sia caricata e il Remote UI non offre un evento
«soluzione pronta» comodo da intercettare, quindi il primo refresh parte dopo un secondo e, finché
la soluzione non risulta aperta, riprova ogni due secondi per un minuto mostrando «in attesa che la
soluzione sia caricata…» nella riga di attività; se nel frattempo premi Aggiorna l'attesa si
interrompe. Dopo un minuto senza soluzione il pannello lo dice e resta fermo. Le tre letture (migrazioni+database, git, tunnel)
sono **indipendenti**: partono insieme, ogni sezione si aggiorna appena la sua finisce e nel
frattempo mostra il proprio indicatore di lettura. Le migrazioni non aspettano il fetch né la
risoluzione del tunnel, e il tunnel (la lettura più lenta: `devtunnel list`, `devtunnel show`,
probe HTTP) non trattiene nessun'altra sezione.

Le letture **non bloccano le azioni**: il pulsante di database update e quello di pull sono
disabilitati solo mentre un'azione di scrittura è in corso, non mentre una sezione sta ancora
leggendo. Il pulsante Aggiorna segue la stessa regola: un refresh chiesto mentre una sezione è
ancora in lettura non avvia una seconda lettura in parallelo per quella sezione (due `git fetch`
concorrenti si contenderebbero il lock del repository), ma la accoda e la esegue appena la
corrente finisce.

## Configurazione

Niente è scritto nel codice: cartella dei cloni, repository, ambienti, URL di clone, nomi delle
cartelle (con `{n}` al posto del numero), dev tunnel, context EF, appsettings, comandi di avvio,
porta del dev server, ambienti Docker, env, seed, pool di tunnel e connessione ad Azure DevOps
stanno in una configurazione sola, che exe, VSIX, CLI e server MCP leggono dallo stesso file. Il
modello è `src\Soffritto.Core\Configuration\AppConfiguration.cs`.

La configurazione (`schemaVersion: 2`) ha un **catalogo di repository** (`repositories`), una
lista di **ambienti** (`environments`), la sezione `docker` comune agli ambienti Docker e il
pool di tunnel (`tunnelPool`). Ogni voce del catalogo è un oggetto con `type` (`backend`,
`frontend`, `app` o `generic`), un `id` stabile (lettere minuscole, cifre e trattini), `cloneUrl`,
`folder` e i campi del suo tipo, allo stesso livello; `type` può stare in qualunque posizione
dell'oggetto. Ogni ambiente ha il suo `number` (da 1 a 99), gli id dei `repositories` che include
(l'ordine nella colonna resta quello del catalogo), `kind` (`local` o `docker`) e, se serve, una
`root` sua al posto della cartella dei cloni. Le voci con `kind: docker` le scrivono e le tolgono
la creazione (il «+» della testata, il pannello delle PR, il server MCP) e «Distruggi ambiente».
Un ambiente locale può avere `protectedDatabase: true` (casella «DB protetto» nel setup): il
database del suo backend è protetto, per esempio perché lo usano anche altri, quindi exe e VSIX ne leggono le migrazioni ma non
offrono update, rollback né copia del comando, la colonna mostra il tag «DB protetto» e il
server MCP lo dichiara in `elenca_ambienti` e `stato_ambiente` (`databaseProtetto`,
`vincoloDatabase`). Tutto il resto dell'ambiente funziona come sempre. Un ambiente Docker ha
sempre il suo database e non accetta il campo.

Campi della voce del catalogo legati a tunnel e Docker:

- `devTunnel`: presente, il repository si condivide con una porta del pool; `ports`, facoltativo,
  sostituisce le porte del tipo (la prima porta https di Kestrel per il backend, la porta del
  dev server per il frontend) ed è obbligatorio per un generico;
- `sourceClone`: il percorso completo del clone sorgente bare da cui nascono i worktree degli
  ambienti Docker; senza, il repository non entra in un ambiente Docker;
- `docker`: `compose` (solo backend) e `dockerfile`, relativi alla radice del repository;
  `service`, il servizio nel compose; `publishedPorts`, voci `base:porta del container`;
  `filesFallback`, la cartella dei file Docker di riserva; `localFiles`, i file copiati
  dall'ambiente modello. I campi vuoti
  valgono i default del tipo: `compose.yaml`, `Dockerfile`, `api` e `15000:8080` per il backend,
  `Dockerfile`, `web` e `18000:8082` per il frontend;
- `env`: `adapter` (`bat`, `dotenv`, `appSettings` o `manual`), `target`, `constantsSource`,
  `containerExclude`, `manual` (nome e valore), `secrets` (adapter e `source`) e `slot` (nome,
  `local` e `container`), descritti in «Env degli ambienti Docker»;
- `seed`, solo sul backend: `kind` (`sqlServer`), `path` e `sqlServer` con `image`,
  `databaseName`, `password`, `memoryLimitMb` e `service`.

La sezione `docker` comune ha `worktreeRoot` (vuoto = `<root>\ambienti-docker`, dove stanno
anche i cloni sorgente, in `sorgenti\<repository>.git`), `databasePortBase` (14000) e
`templateEnvironment`, l'ambiente locale da cui si leggono costanti, segreti e file locali
(vuoto = il primo ambiente locale). `tunnelPool.tunnels` elenca i tunnel del pool con
`description` e `label`.

```json
{
  "repositories": [
    { "type": "generic", "id": "docs", "folder": "docs-{n}",
      "devTunnel": { "ports": [ { "number": 3000, "protocol": "http" } ] } },
    { "type": "frontend", "id": "frontend", "folder": "frontend-{n}",
      "sourceClone": "C:\\Dev\\ambienti-docker\\sorgenti\\frontend.git",
      "devTunnel": {},
      "env": { "adapter": "bat", "target": "env\\dev.bat",
        "secrets": { "adapter": "bat", "source": "env\\segreti.bat" },
        "slot": [ { "name": "API_URL", "local": "{apiHostUrl}", "container": "{apiContainerUrl}" },
                  { "name": "DEV_SERVER_PORT", "local": "{hostPort}", "container": "{containerPort}" } ] } }
  ],
  "tunnelPool": { "tunnels": [ { "description": "pool-1", "label": "SoffrittoPool" } ] }
}
```

**Formati precedenti.** Un `config.json` con uno `schemaVersion` precedente a quello corrente
non si legge: exe, VSIX, CLI e server MCP lo rifiutano con un messaggio che dice quale
`schemaVersion` legge la versione in uso. Un exe o una VSIX di versione precedente non leggono
il formato corrente: vanno aggiornati insieme.

### Dove sta

La copia di lavoro è `%LOCALAPPDATA%\Soffritto\config.json`: la scrive il setup e la
leggono exe, VSIX e CLI.

Per il provisioning si mette un `soffritto.json` accanto all'exe (per la release, nella
cartella di publish). All'avvio, se il suo contenuto è cambiato dall'ultimo import, sostituisce
la copia di lavoro; se è lo stesso, non si reimporta, e le modifiche fatte dal setup restano.
L'exe non scrive mai quel file. `--config <file>` usa un altro file al suo posto. Il pattern
`soffritto*.json` è in `.gitignore`, così un file di configurazione lasciato nel repository
non finisce in un commit. Il file importato deve essere nel formato con il catalogo; prima
di sostituire la copia di lavoro l'exe la copia in `config.prima-import.json`, e il messaggio del
setup lo dice. Un import sostituisce anche le voci degli ambienti Docker: un ambiente Docker che
il file importato non ha perde la voce, mentre container e worktree restano sul disco, e il
messaggio lo dice.

Mentre l'exe gira, `config.json` lo scrivono il Salva del setup, la creazione e la distruzione
degli ambienti Docker e «Allinea seed». Le scritture passano una alla volta da un lock del
processo dell'exe:
ognuna rilegge il file, cambia solo la sua parte, valida e scrive. Il Salva del setup tiene le
voci Docker e un `seed.path` cambiato nel file mentre il setup era aperto, e il messaggio dice
quale valore ha vinto. Se `config.json` manca o non si legge, creazione, distruzione e
«Allinea seed» rifiutano di scrivere; il Salva del setup scrive la configurazione nuova e
lascia la copia del file illeggibile in `config.illeggibile.json`.

**Provare una build su dati isolati.** Con la variabile d'ambiente `SOFFRITTO_DATA` impostata
a un percorso completo, exe, server MCP e CLI usano quella cartella al posto di
`%LOCALAPPDATA%\Soffritto`, per configurazione, note, impostazioni e profilo del browser; il
titolo della finestra la mostra («Soffritto · dati: …»). Si imposta nella stessa riga di
comando dell'avvio:

```
$env:SOFFRITTO_DATA='C:\Dati\Soffritto-prova'; Start-Process '<exe Debug>'
```

Vuota vale come non impostata (PowerShell la cancella). Con un valore che non è un percorso
completo, anche di soli spazi, nessun dato viene letto o scritto: l'exe mostra l'errore ed esce,
il server MCP risponde con l'errore, la CLI esce con un'eccezione. Le console e il Visual Studio
aperti dall'exe isolato ereditano la variabile: una VSIX già aggiornata, in quel Visual Studio,
leggerebbe la cartella isolata.

### Il pannello Setup

Si apre dal bottone «Setup» della testata, e da solo al primo avvio o dopo un import. Sotto
l'intestazione ha una riga di schede; ogni apertura parte dalla prima, e gli errori di qualunque
scheda compaiono nel piede del pannello. In ogni scheda i titoli delle sezioni stanno sulla
pagina e le card racchiudono un argomento ciascuna. Il tag «in configurazione» accanto a un
titolo segna le sezioni che finiscono in `config.json` e in uno `soffritto.json` di
provisioning. Ogni campo ha accanto all'etichetta un bottone «i»: al passaggio del mouse mostra
la descrizione del campo, al clic la tiene aperta finché non si clicca altrove. Le schede:

- **Repository**: la cartella dei cloni e il catalogo dei repository, con sopra la convenzione
  del branch di sviluppo (vedi «Branch di sviluppo») e poi una card per repository. In cima alla
  card stanno il tipo a tendina, l'id, l'URL di clone e la cartella; sotto, gruppi che si aprono
  e si chiudono: i campi del tipo («Backend», «Frontend» o «App»), «Dev tunnel» con
  «Condivisibile» e le porte facoltative, «Docker» con «Clone sorgente» e «Proponi», «Env» con
  «Valori proposti» e «Anteprima» e, per il backend, «Seed del database» con «Verifica seed».
  Le frecce riordinano le card e «Aggiungi repository» ne aggiunge una;
- **Ambienti locali**: gli ambienti locali, uno per riga con il numero, le caselle dei
  repository inclusi e la cartella facoltativa, con «Aggiungi ambiente»; gli ambienti Docker non
  sono in elenco e si creano dal «+» della testata;
- **Docker**: cartella dei worktree e dei cloni sorgente, base delle porte dei database e
  ambiente modello; poi, con azioni che valgono subito senza Salva, lo stato del seed con le
  migrazioni mancanti e «Allinea seed», lo spazio di Docker con «Pulisci ora», «Compatta disco» e
  la preferenza «Pulisci alla chiusura dell'exe», e il log delle operazioni Docker globali;
- **Tunnel**: il pool di tunnel (vedi «Il pool nella scheda Tunnel del setup»);
- **DevOps**: il provider, oggi solo Azure DevOps con collection, progetto, autenticazione e
  «Prova connessione»; GitHub e GitLab compaiono in anteprima, disabilitati, per mostrare cosa
  servirà quando saranno supportati, e non scrivono niente in `config.json`;
- **Preferenze**: tema, gattino, sfondo e modalità demo, che si applicano subito.

Cambiando il tipo di una card i campi del tipo precedente restano in memoria e ricompaiono
tornandoci. I campi del gruppo «Docker» mostrano il default del tipo quando sono vuoti, e
scrivono nel file solo quando si cambiano. «Valori proposti» sostituisce il gruppo «Env» con i
valori del tipo: per il backend l'adapter `appSettings` sul primo appsettings, `Kestrel:` escluso
dal container e la connessione al database dello slot; per il frontend l'adapter `.bat` sul file
di env `envFile`, i segreti da `secrets.bat` nella stessa cartella e, se `devServerPortVariable`
è impostata, la variabile della porta del dev server; per l'app l'adapter dotenv su `.env`. Le
variabili con nomi del progetto, come l'URL dell'api per il frontend o l'URL del backend sul pool
per l'app, non si propongono: si aggiungono nel gruppo «Env». Sono valori proposti modificabili: dopo
«Valori proposti» si cambiano nel gruppo «Env» come ogni altro valore. «Verifica seed» lavora
sulla configurazione mostrata, anche non salvata.

Gli errori compaiono mentre si scrive, in rosso; in arancio gli avvisi, che non impediscono di
salvare: due repository dello stesso tipo nello stesso ambiente, un ambiente Docker il cui
backend non esclude `Kestrel:` dal container, un ambiente che usa un backend diverso dal primo
del catalogo, più backend con la VSIX.

Finché esiste un ambiente Docker, alcuni campi sono in sola lettura, con un lucchetto accanto
all'etichetta, perché cambiarli sposterebbe worktree, database o porte già in uso: cartella, clone sorgente, compose, servizio e
porte pubblicate dei repository inclusi in un ambiente Docker; nome del database, password e
servizio del seed; la variabile per slot della connessione del backend; cartella dei worktree e
base delle porte dei database. Per cambiarli si distruggono prima quegli ambienti.

«Salva» scrive la configurazione e poi, nell'ordine:

- esegue `git clone` per le sole cartelle mancanti con un URL; una cartella che c'è già non
  viene mai toccata, nemmeno se il suo origin è diverso;
- crea i cloni sorgente che mancano, con `git init --bare`, `remote add origin` e `fetch`; un
  clone sorgente senza URL di clone è un errore;
- crea i tunnel del pool che l'account non ha, «Owner only» e senza porte (`devtunnel create
  --description <Description> --labels <Label> --expiration 30d`);
- riapre l'exe con la configurazione nuova. Se un clone o un tunnel non è riuscito, o se ha
  creato un tunnel, non riapre: un secondo Salva riapre. Con un'operazione in corso nel pannello
  Docker il riavvio si rimanda.

### Branch di sviluppo

`dev` è il branch di sviluppo principale, uguale per ogni repository del catalogo
(`src\Soffritto.Core\GitConventions.cs`): Reset riporta lì i repository, i branch nuovi degli
ambienti Docker nascono da `origin/dev`, «Verifica seed» e «Allinea seed» confrontano il seed con
le migrazioni di `origin/dev`. Ogni repository del catalogo lo deve avere su origin. Il setup lo
ricorda in una nota azzurra sopra il catalogo, nella scheda «Repository».

### Azure DevOps

Il pannello delle pull request si collega direttamente ad Azure DevOps, senza passare dal server
MCP: dell'MCP riprende URL della collection, progetto e PAT, mandato in Basic auth con utente
vuoto come fa la sua `VssBasicCredential` (`src\Soffritto.Core\AzureDevOps\AzureDevOpsConnection.cs`).
L'autenticazione predefinita è l'utente Windows; con il PAT, il token si salva cifrato con DPAPI
per l'utente Windows corrente: la copia di lavoro non lo contiene in chiaro, ma su un altro
utente o un'altra macchina va reinserito. Nel file importato si può scrivere `"pat"` in chiaro: al
primo salvataggio diventa `"patProtected"`.

### Connection string

Resta quella del clone: la si legge dal primo degli appsettings configurati che la contiene
(JSONC, quindi le varianti commentate con `//` vengono ignorate), la stessa che usa `dotnet ef`.
In un ambiente Docker quel file del worktree lo scrive l'adapter `appSettings` alla creazione e
con «Ripara env», con la connessione al database dello slot (`127.0.0.1,<porta dello slot>`);
l'adapter riscrive il JSON e i commenti del file non sopravvivono. Le guardie di «Guardie del
database» controllano proprio quella connessione. Un ambiente locale può puntare con i suoi
appsettings al database di un ambiente Docker: la colonna locale segue allora la fase di quel
database (vedi «Avvio, stop, log e stato»).
