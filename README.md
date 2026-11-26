# Soffritto

![Soffritto: three development environments tossed in the same pan over the fire](docs/soffritto-hero.png)

Do you keep several copies of the same project, each with its own backend, frontend, app and
database? Soffritto puts them all in front of you in a single window: what is running, which
branch you are on, which migrations are missing, what can be reached from outside. And with one
click you start, stop, update and share.

## What it does

- **One environment, one column.** For every numbered copy of the project you see the branch,
  commits to pull or push, the working tree, and EF Core migrations that are applied, missing or
  out of order.
- **Start and stop.** Backend on Kestrel, frontend dev server, app and Metro bundler, each in its
  own console. Pull, branch switch and migrations run from the card, with guards that stop risky
  operations.
- **Disposable Docker environments.** One branch per repository, a database restored from the
  seed and migrated, api and web in containers: create one from the “+” and destroy it when you
  are done.
- **Share with a switch.** A pool of dev tunnels gives every service of the environment a public
  address, to try it from your phone or show it to a colleague.
- **Talks to agents.** An MCP server exposes the same environments to Claude Code and to any MCP
  client, and a Visual Studio extension brings the state of the open clone into the IDE.

Soffritto knows nothing about your project: names, folders, ports and commands live in its
configuration, which you fill in from the setup.

## Installation

1. Download `Soffritto.App-win-Setup.exe` from the [latest release](https://github.com/filippoitaliano/Soffritto/releases/latest).
2. Run it: it installs for your user, with no administrator rights and no .NET runtime to add.
3. On first launch the **Components** tab of the setup offers Git, Dev tunnel and Docker Desktop,
   registers the MCP server in Claude Code and installs the Visual Studio extension.

Updates arrive on their own: Soffritto downloads the new release at startup and installs it when
you close it.

Just want a look? **Ctrl+D** opens the demo with a made-up team, which works even without clones
and without a network.

## Requirements

Windows 10 or 11, 64-bit. Git for the clones; Dev tunnel for sharing and Docker Desktop for
disposable environments are optional. Your projects bring their own tools: .NET SDK, Node, and
whatever it takes to build them.

## Developing Soffritto

```
dotnet build src\Soffritto.Desktop
powershell -ExecutionPolicy Bypass -File release.ps1
```

The first command builds the app in Debug, the second creates the Setup.exe in
`artifacts\releases`. Architecture, configuration, guards and the details of every feature are in
the [technical guide](docs/GUIDE.md).

## License

[MIT](LICENSE)
