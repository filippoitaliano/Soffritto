using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

var builder = Host.CreateApplicationBuilder(args);

// stdout e' il canale JSON-RPC: qualunque log scritto li' rompe il protocollo.
builder.Logging.ClearProviders();
builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
builder.Logging.SetMinimumLevel(LogLevel.Warning);

builder.Services
    .AddMcpServer(o => o.ServerInstructions =
        "Ambienti di sviluppo numerati della stessa configurazione dell'exe Soffritto. Un ambiente N "
        + "raccoglie repository del catalogo (backend, frontend, app, generici). Due tipi: "
        + "LOCALE, cloni sulla macchina con servizi avviati in console; per questi gli strumenti sono solo in "
        + "lettura (elenca_ambienti, stato_ambiente), con una sola eccezione: esegui_comando_extra lancia nel "
        + "clone il comando in piu' del repository (comandoExtra in elenca_ambienti), lo stesso del bottone "
        + "accessorio dell'exe, e vale per ambienti locali e Docker. Un ambiente locale con databaseProtetto=true usa un "
        + "database protetto (per esempio usato anche da altri): non vi si applicano migrazioni ne' si altera lo schema, quindi non "
        + "si sceglie per un lavoro che crea o applica migrazioni. "
        + "DOCKER, usa e getta e dedicato a un lavoro: ogni repository e' un worktree su un branch, e un "
        + "compose accende il database (ripristinato da un seed e migrato al branch), l'api e il web; su "
        + "questi si agisce liberamente con gli strumenti *_docker. "
        + "Flusso tipico per un lavoro nuovo: elenca_ambienti per gli id del catalogo; crea_ambiente_docker "
        + "con il branch di ogni repository (un branch che non esiste nasce dal branch di sviluppo "
        + "principale) e una nota che dice a cosa serve; si scrive codice nei worktree che la risposta "
        + "indica; il web riceve il codice salvato dal Compose Watch, l'api si ricostruisce con "
        + "avvia_servizio_docker; dopo una migrazione nuova applica_migrazioni_docker; stato_ambiente_docker "
        + "e log_servizio_docker per verificare. Commit e push si fanno con git nei worktree. "
        + "Le operazioni lunghe (creazione, avvio, migrazioni) girano in background: la risposta contiene "
        + "un id da seguire con stato_operazione. Un ambiente Docker si distrugge solo dall'exe: lo fa una "
        + "persona con «Distruggi ambiente» sulla colonna dell'ambiente, e a chi chiede di distruggerne uno "
        + "si indica quel pulsante. A fine lavoro l'ambiente si lascia acceso, salvo diversa indicazione "
        + "di chi lo usa.")
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
