# Soffritto

![Soffritto: tre ambienti di sviluppo saltano nella stessa padella sul fuoco](docs/soffritto-hero.png)

Hai più copie dello stesso progetto, ognuna con il suo backend, il suo frontend, la sua app e il
suo database? Soffritto te le mette tutte davanti in una finestra: cosa è acceso, su che branch
sei, quali migrazioni mancano, chi è raggiungibile da fuori. E con un clic accendi, spegni,
aggiorni e condividi.

## Cosa fa

- **Un ambiente, una colonna.** Per ogni copia numerata del progetto vedi branch, commit da
  pullare o da pushare, working tree, migrazioni EF Core applicate, mancanti o fuori ordine.
- **Accendi e spegni.** Backend con Kestrel, dev server del frontend, app e bundler Metro, ognuno
  nella sua console. Pull, cambio branch e migrazioni si fanno dalla card, con guardie che
  fermano le operazioni rischiose.
- **Ambienti Docker usa e getta.** Un branch per repository, un database ripristinato dal seed e
  migrato, api e web nei container: si crea dal «+» e si distrugge quando hai finito.
- **Condividi con un interruttore.** Un pool di dev tunnel dà un indirizzo pubblico a ogni
  servizio dell'ambiente, per provare dal telefono o farlo vedere a un collega.
- **Parla con gli agenti.** Un server MCP mostra gli stessi ambienti a Claude Code e a ogni
  client MCP, e un'estensione di Visual Studio porta lo stato del clone aperto dentro l'IDE.

Soffritto non sa niente del tuo progetto: nomi, cartelle, porte e comandi stanno nella sua
configurazione, che si compila dal setup.

## Installazione

1. Scarica `Soffritto.App-win-Setup.exe` dall'[ultima release](https://github.com/filippoitaliano/Soffritto/releases/latest).
2. Avvialo: installa per il tuo utente, senza diritti di amministratore e senza runtime .NET da
   aggiungere.
3. Al primo avvio la scheda **Componenti** del setup ti propone Git, Dev tunnel e Docker Desktop,
   registra il server MCP in Claude Code e installa l'estensione di Visual Studio.

Gli aggiornamenti arrivano da soli: Soffritto scarica la release nuova all'avvio e la installa
quando lo chiudi.

Vuoi solo dare un'occhiata? **Ctrl+D** apre la demo con un team inventato, che funziona anche
senza cloni e senza rete.

## Requisiti

Windows 10 o 11 a 64 bit. Git per i cloni; Dev tunnel per la condivisione e Docker Desktop per
gli ambienti usa e getta sono facoltativi. I tuoi progetti portano i loro strumenti: SDK .NET,
Node, e quello che serve per compilarli.

## Per chi sviluppa Soffritto

```
dotnet build src\Soffritto.Desktop
powershell -ExecutionPolicy Bypass -File release.ps1
```

Il primo comando compila l'exe in Debug, il secondo crea il Setup.exe in `artifacts\releases`.
Architettura, configurazione, guardie e dettagli di ogni funzione sono nella
[guida tecnica](docs/GUIDA.md).

## Licenza

[MIT](LICENSE)
