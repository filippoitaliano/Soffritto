# Project rules

## A generic, independent tool

- Soffritto (exe, VSIX, CLI, MCP, Core, Presentation) works with any repository catalog and any
  project: a project's names, paths, ports and habits live only in `config.json`, never in the
  code, in UI texts or in the MCP tool descriptions.
- Every action can be done without Claude, from the app; the MCP is another door onto the same
  actions and explains itself (server instructions and tool descriptions), without relying on
  skills.
- The skills and rules in `~\.claude` that use the tool are optional conveniences: the repository
  does not depend on them.

## Develop on the Debug build, work with the installed app

- The exe the user works with is the one from the Setup.exe:
  `%LOCALAPPDATA%\Soffritto.App\current\Soffritto.exe`, Start shortcut “Soffritto”. It stays
  running during development and testing.
- Build, launch and test the Debug build: `dotnet build` and `dotnet run` without
  `-c Release`, with output in `bin\Debug\`. It coexists with the installed app, because
  `SingleInstance` derives the mutex name from the exe path as well: at the end of a test close
  only the Debug one.
- A new version reaches the installed app only through a release: `release.ps1` runs on request,
  and `-Upload` only on an explicit request to publish.

## A version bump for every change

- Every code change (Desktop exe, VSIX, Core, Presentation, Cli) bumps `<Version>` in
  `Directory.Build.props`, in the same commit as the change. By default bump the patch
  (1.0.5 → 1.0.6).
- The third number of `<Version>` is a running counter that never resets: when the major or the
  minor goes up, the third number continues from its previous value (1.0.159 → 2.0.160, not
  2.0.0).
- There is a single version for exe and VSIX: the footer of both shows it, and the VSIX installer
  updates nothing if the version does not change.
- Changes that touch only documentation or rules (README, `docs/`, `CLAUDE.md`, `.claude/`) leave
  the version as it is.

## Glossary

### Environment (N)
- Definition: an entry of the `environments` list in `config.json`, with a number N from 1 to 99, a type (`local` or `docker`) and the catalog repositories it includes; its folders are `<root>\<folder>` with `{n}` replaced by N (e.g. `backend-N`, `frontend-N`, `app-N`), or worktrees under the worktree folder for Docker environments. It corresponds to a column of the Desktop app.
- Does not mean: the ASP.NET environment (`ASPNETCORE_ENVIRONMENT`/`DOTNET_ENVIRONMENT`), which `MigrationApplier.UpdateDatabaseAsync` always forces to `Development`; and it has no dev tunnel of its own (see “Tunnel pool”).
- Context: `src/Soffritto.Core/Configuration/AppConfiguration.cs` (`FolderOf`), `src/Soffritto.Desktop/EnvironmentLayout.cs`, `src/Soffritto.Core/Docker/DockerSlot.cs`.

### Tunnel pool
- Definition: the dev tunnels listed in `tunnelPool.tunnels` of `config.json` (Description `pool-K`, label `SoffrittoPool`). Soffritto creates them from the “Tunnels” tab of the setup and assigns ports to them, at most 9 per tunnel. A tunnel belongs to a single environment: the first port assigned to that environment claims it, and it becomes free again when the environment has no ports left. The host of a pool tunnel is a console `AMBIENTI tunnel-pool-pool-K`.
- Does not mean: a tunnel created and deleted together with the environment, nor a tunnel shared by several environments (only tunnels with assignments that predate the rule stay shared). The pool is not edited by hand with `devtunnel`.
- Context: `src/Soffritto.Core/Docker/TunnelPool.cs`, `src/Soffritto.Presentation/PoolHosts.cs`.

### Pool port
- Definition: a port of a pool tunnel assigned to a repository of an environment (by turning on “Tunnel” in the environment header) and recorded in `%LOCALAPPDATA%\Soffritto\tunnel-pool.json`: the Kestrel https port for a local backend, the dev server port for a local frontend, the slot host port for containers.
- Does not mean: the local port of the service itself; without an assignment the service is not shared and the `{poolUrl…}` variables of the env files stay empty.
- Context: `src/Soffritto.Core/Docker/TunnelPool.cs`, `src/Soffritto.Core/Env/EnvPoolSync.cs`.
