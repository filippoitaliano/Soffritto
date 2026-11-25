# Regole di progetto

## Strumento generico e indipendente

- Soffritto (exe, VSIX, CLI, MCP, Core, Presentation) funziona con qualunque catalogo di
  repository e con qualunque progetto: nomi, percorsi, porte e abitudini di un progetto stanno
  solo in `config.json`, mai nel codice, nei testi della UI o nelle descrizioni degli strumenti
  MCP.
- Ogni azione si può fare senza Claude, dall'exe; l'MCP è un'altra porta sulle stesse azioni e
  si spiega da solo (istruzioni del server e descrizioni degli strumenti), senza contare su skill.
- Le skill e le regole in `~\.claude` che usano lo strumento sono comodità facoltative: il
  repository non dipende da loro.

## Si sviluppa sulla build Debug, si lavora con l'installazione

- L'exe con cui l'utente lavora è quello del Setup.exe:
  `%LOCALAPPDATA%\Soffritto.App\current\Soffritto.exe`, collegamento Start «Soffritto». Resta
  acceso durante lo sviluppo e le prove.
- Si compila, si avvia e si prova la build Debug: `dotnet build` e `dotnet run` senza
  `-c Release`, con l'output in `bin\Debug\`. Convive con l'installazione, perché
  `SingleInstance` calcola il nome del mutex anche dal percorso dell'exe: a fine prova si chiude
  solo la Debug.
- Una versione nuova arriva all'installazione solo con una release: `release.ps1` si lancia su
  richiesta, e `-Upload` solo su richiesta esplicita di pubblicare.

## Versione a ogni modifica

- Ogni modifica al codice (exe Desktop, VSIX, Core, Presentation, Cli) alza `<Version>` in
  `Directory.Build.props`, nello stesso commit della modifica. Di default si alza la patch
  (1.0.5 → 1.0.6).
- Il terzo numero di `<Version>` è un progressivo che non si azzera mai: quando salgono la major
  o la minor il terzo numero prosegue dal valore precedente (1.0.159 → 2.0.160, non 2.0.0).
- È una versione sola per exe e VSIX: il footer di entrambi la mostra, e l'installer della VSIX
  non aggiorna niente se la versione non cambia.
- Le modifiche solo di documentazione o di regole (README, `docs/`, `CLAUDE.md`, `.claude/`) lasciano la
  versione com'è.

## Glossario

### Ambiente (N)
- Definizione: voce della lista `environments` di `config.json`, con un numero N da 1 a 99, un tipo (`local` o `docker`) e i repository del catalogo che include; le cartelle sono `<root>\<folder>` con `{n}` sostituito da N (es. `backend-N`, `frontend-N`, `app-N`), o worktree sotto la cartella dei worktree per gli ambienti Docker. Corrisponde a una colonna dell'exe Desktop.
- Non significa: ambiente ASP.NET (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`), che `MigrationApplier.UpdateDatabaseAsync` forza sempre a `Development`; e non ha un dev tunnel proprio (vedi «Pool di tunnel»).
- Contesto: `src/Soffritto.Core/Configuration/AppConfiguration.cs` (`FolderOf`), `src/Soffritto.Desktop/EnvironmentLayout.cs`, `src/Soffritto.Core/Docker/DockerSlot.cs`.

### Pool di tunnel
- Definizione: i dev tunnel elencati in `tunnelPool.tunnels` di `config.json` (Description `pool-K`, label `SoffrittoPool`). Soffritto li crea dalla scheda «Tunnel» del setup e ci assegna le porte, al massimo 9 per tunnel. Un tunnel è di un solo ambiente: lo reclama la prima porta assegnata a quell'ambiente e torna libero quando l'ambiente non ha più porte. L'host di un tunnel del pool è una console `AMBIENTI tunnel-pool-pool-K`.
- Non significa: un tunnel creato e cancellato insieme all'ambiente, né un tunnel condiviso da più ambienti (restano condivisi solo i tunnel con assegnazioni precedenti alla regola). Il pool non si modifica a mano con `devtunnel`.
- Contesto: `src/Soffritto.Core/Docker/TunnelPool.cs`, `src/Soffritto.Presentation/PoolHosts.cs`.

### Porta del pool
- Definizione: una porta di un tunnel del pool assegnata a un repository di un ambiente (accendendo «Tunnel» nell'intestazione dell'ambiente) e registrata in `%LOCALAPPDATA%\Soffritto\tunnel-pool.json`: la porta https di Kestrel per un backend locale, quella del dev server per un frontend locale, la porta host dello slot per i container.
- Non significa: la porta locale del servizio in sé; senza assegnazione il servizio non è condiviso e le variabili `{poolUrl…}` degli env restano senza valore.
- Contesto: `src/Soffritto.Core/Docker/TunnelPool.cs`, `src/Soffritto.Core/Env/EnvPoolSync.cs`.
