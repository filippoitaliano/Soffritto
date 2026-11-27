# Soffritto: technical guide

A Visual Studio 2026 extension: in an IDE tool window it shows up to which EF Core migration the
test database has got, which ones are missing, which ones are applied but unknown to the code,
plus the current git branch (with the clone's working folder), commits to pull and the state of
the dev tunnel associated with the clone.

The same state, for all the configured environments together, is available outside Visual Studio
in the Desktop app: see “Desktop app (multiple environments)”.

**Diagnosis is read-only** — on the database only `SELECT`, on git only `fetch` (which touches the
remote-tracking refs, never the working tree) and read commands.

**Three write actions are available on request**: applying the pending migrations of a context
(only when the operation is trivial), a fast-forward pull and switching branch from the
drop-down. See “Actions and guards”.

## Structure

| Project | Role |
|---|---|
| `Soffritto.Core` | Engine: migration scanning, reading `__EFMigrationsHistory`, git state, diff. No dependency on Visual Studio. |
| `Soffritto.Cli` | Command-line harness: same engine, output to the console. Useful to check things without opening the IDE. |
| `Soffritto.Presentation` | Orchestration shared by VSIX and app: per-section reads and their queue, actions with guards and follow-up refreshes, panel texts, Kestrel and tunnel host tools. Depends on Core and on `System.Management`; no dependency on Visual Studio or WPF. |
| `Soffritto.Vsix` | The extension package (out-of-process `VisualStudio.Extensibility` model): a Remote UI adapter over the shared orchestration. |
| `Soffritto.Desktop` | WPF executable independent of Visual Studio: the state of the configured environments in one window, one column per environment with the cards of its repositories, on the same orchestration as the VSIX. |
| `Soffritto.Mcp` | stdio MCP server for any MCP client: the same environment state and the same actions as the app, on local and Docker environments. See “MCP server”. |

## How it reads the state

The authoritative ID of an EF migration is the one declared in the `[Migration("...")]` attribute
of the `.Designer.cs` file, **not** the file name: in a repository the two can diverge (for
example `20250402144742_aggiuntaTabellaOrdini.cs` declaring `...AggiuntaTabellaOrdini`).
Deriving the ID from the file name produces false "missing" and false "unknown", so the scanner
reads the attribute and reports the divergence as a note.

Several contexts of the same backend can share the same `__EFMigrationsHistory` (for example
`ProjectContext` and `IdentityContext` on the same `DefaultConnection`, without a custom
`MigrationsHistoryTable`), so a history row is really unknown only if it belongs to **none** of
the configured contexts — otherwise the `IdentityContext` migrations would look unknown to
`ProjectContext` and vice versa.

States reported for each migration:

- **applied** — present in the files and in `__EFMigrationsHistory`
- **missing** — in the files but not applied
- **out of order** — not applied and with a timestamp earlier than the last applied one: the
  typical symptom of coming back from a parallel branch
- **unknown** — applied on the database but absent from the files of both contexts

## Latest migrations and the command to copy

Under each context the **latest 10 migrations** of the code appear, newest first, with the name
stripped of the timestamp (`AggiuntaTabellaOrdini`, not
`20250402144742_AggiuntaTabellaOrdini`) and the state label beside it. In front of the name is
the **migration's position in the context** (1 = the first): it makes the descending order
readable and matches the context's “applied/total” counter, so `212/214` with rows 214 and 213
“missing” reads at a glance. The copy icon at the end of the row puts on the clipboard the
`dotnet ef database update` command **up to that migration**, for that context:

```
$env:ASPNETCORE_ENVIRONMENT='Development'; $env:DOTNET_ENVIRONMENT='Development'; dotnet ef database update 20250402144742_AggiuntaTabellaOrdini --project "C:\Dev\backend-1\src\Project.Data\Project.Data.csproj" --startup-project "C:\Dev\backend-1\src\Project.Api\Project.Api.csproj" --context ProjectContext
```

Three choices about the copied text:

- **the two environment variables travel with the line**, for the same reason the button forces
  them (see “Apply migrations”): pasted into a terminal without them, EF would read
  `appsettings.json` and migrate a **different** database from the one shown in the panel. The
  syntax is PowerShell, Visual Studio's default terminal; the variables stay set for that
  terminal session;
- **the target is the full ID**, with the timestamp, even though the row shows the short name: EF
  accepts both, but the ID stays unambiguous if two migrations share the name;
- **the paths are absolute**, so the line works from any folder.

Below the list, when the context has more than eight migrations, there is a **minimal pager**
(`‹ 1-8 of 214 ›`) that pages further back in time: it is a window on rows already read, not a
new read, and every refresh starts again from the first page.

The list shows migrations whatever their state, and it is the only place where they appear: the
pending ones have no separate list, you recognise them by the red “missing” label.
Copying runs nothing and has no guards — unlike the button, which really runs the command and
appears only when the operation is trivial.

Stopping at a migration **earlier** than the last applied one is a rollback: EF runs the `Down()`
of the later migrations. The command is the right one to do it, but it is a destructive write
and stays your decision — that is why the panel only copies it.

## Panel layout

At the top there is a row with the activity and the **Refresh** button: the progress of an action
(pull, branch switch, database update, Kestrel start), the title of its failure, or the
microtext “ready” when nothing is happening. Below, one card per section, with a light border and
5 px between them; no background of their own, because the panel follows Visual Studio's light or
dark theme. At the bottom, a minimal footer with the extension version and the contact.

**Every card opens and closes** with the button in its header (arrow and title). Aligned contexts
start closed, because they have nothing to say beyond the counter, and they re-align on every
refresh; the other cards start open and stay as you leave them. Repository errors stay visible
even with the card closed.

There is no global refresh time: every section (Repository, each context, Dev tunnel) ends with
its own light timestamp, because the reads are independent and can finish at different times.

**The layout does not jump.** Status rows do not appear and disappear: they always keep their
space, and the lists inside the sections are aligned to the left edge under the icon, with no
indents wasting width, with an idle microtext when there is nothing to say (“ready”, “not read”,
“waiting for the first read”), and they are single-line with ellipsis and tooltip. During a
re-read every section keeps its previous content and only its timestamp changes to “reading…” or
“git fetch…”. Only real content changes height: migration rows, ports, errors.
In the app the environments' activity row and the timestamps are not written in the cards: you
read them in the tooltip of the card's status dot.

**The order of the sections** is the same as in the Desktop app: repository (with the errors, in
a rounded box tinted pale red, only when there is one), dev tunnel, migrations unknown to the
code, one block per context. The unknown migrations sit above the contexts because they are the
premise for reading their counters: when they appear they push the sections below them down, and
that is an acceptable price because they appear only when there really is something to say.

On the top edge, outside the scroll area and visible even over the splash, there is a 3 px
**activity bar**: transparent when idle, animated indeterminately while at least one read, one
action or the initial wait for the solution is in progress. It is the panel's only global
indicator and takes no space when it is not needed.

## Repository section

The repository row opens with the **name of the working folder** (e.g. `backend-1`): with
several active clones on the same machine it is the only piece of data that says which clone the
rest of the panel refers to. The tooltip shows the full path. Next to the name are the two dots
for **Kestrel** and the **tunnel host** and the **database** and **server** tags read from
`appsettings.Development.json`; the branch shows in the drop-down, with the commits to pull in
the detail row.

On the right are the icons: **start/stop Kestrel** (see “Kestrel from the panel”), **branch
cleanup**, **fetch** (`git fetch`, then a re-read of the git section only) and **pull** (`git pull
--ff-only`, see “Actions and guards”). Below, an **editable drop-down with the branches**: local
ones plus those that exist only on the remote, without the `origin/` prefix, in alphabetical
order, with the current branch always at the top. Typing in the box filters the entries by
substring (case-insensitive) and the menu opens on its own with the preview, modelled on Visual
Studio's Git picker, which is not available in Remote UI. Choosing an entry other than the current
branch runs `git switch` and then triggers a **full refresh**, because with another branch the
migrations on disk are different and so is the upstream to compare with. If the switch fails the
drop-down goes back to the current branch and the error appears in the activity row.

If the drop-down closes without a choice (Esc, click outside, Tab) the search is abandoned and the
box goes back to showing the current branch.

An implementation detail that explains the code: the list of entries is re-aligned entry by entry
instead of being emptied and refilled, because in an editable ComboBox removing the selected
entry also clears the text being typed.

### Kestrel from the panel

To run the backend without a debugger (and without tying up F5) the panel offers the same tools
as the backend card of the Desktop app, from the shared code (`BackendTools`):

- **Start** opens a console with `dotnet run --launch-profile Kestrel --no-launch-browser` from
  the configured api project folder (`apiProject`, for example `src\Project.Api`); the profile is
  `launchProfile`, `Kestrel` by default. It does not touch the dev tunnel host.
- **Stop** closes the console and its process tree, or the recognised processes if they were
  started some other way.
- The **dots** show the state: green running, orange console open but process not (yet) active,
  grey stopped. The second one is the host of the pool tunnel on which the backend has its port
  (see “Dev tunnel pool”): green when the processes include a `devtunnel host` for that tunnel, or
  when the last tunnel read reports a connected host. The host has no button of its own: it is
  turned on and off with the **Tunnel** switch on the environment card, as in the app
  (`EnvironmentTunnel`), which claims or releases the pool tunnel and drives the host through
  `PoolHosts`. The dot is on the right edge of the card and its tooltip gives the state and the
  time of the last read.
- In the **worktree of a Docker environment** the same button starts and stops the slot's api
  container (`docker compose up --build` and `stop`, like the card button in the app), and the dot
  is the container state read with `docker ps`.

The state is re-read from the processes every 3 seconds (WMI, a local read; `docker ps` in the
worktree of a Docker environment). The Kestrel console
carries in its title the same marker as the app (`AMBIENTI kestrel-N`), so panel and app
recognise each other. The consoles start outside the extension host's job when Windows allows it;
otherwise the activity row warns that they will close together with it.

## Dev tunnel section

Shows the port of the dev tunnel pool assigned to the environment's backend (see “Dev tunnel
pool”): tunnel ID, the single assigned port with its `*.devtunnels.ms` endpoint, and
reachability. The port has **its own copy icon**, which puts the full URL on the clipboard
(without a trailing slash; the clipboard is written through pure Win32 because the out-of-process
host does not load WPF/WinForms), and one that **opens it in the default browser**.

**The solution → port association comes from `tunnel-pool.json`**: the VSIX compares the folder
of the open solution with the default backend of every environment in the configuration, local or
Docker, and reads in `%LOCALAPPDATA%\Soffritto\tunnel-pool.json` the port assigned to that
backend. Then it looks with `devtunnel list` for the tunnel that has the assignment's Description
(for example `pool-1`) and reads its port with `devtunnel show`. Without an assignment, or with a
solution that is not the default backend of an environment, the section says “not shared” and
does not query the CLI: the port is assigned with the “Tunnel” switch on the environment card,
here or in the Desktop app. If the tunnel with that Description is not in the account, the section
says so and instead of the detail **lists the account's tunnels** read from `devtunnel list`: ID,
description, host connected or not, number of ports.

**Primary reachability is `hostConnections`** read from the CLI, not an HTTP probe: the
devtunnels gateway keeps a request to an unhosted tunnel hanging instead of refusing it, so
without a connected host a probe can only time out. The per-port HTTP probe (HEAD, 5 s timeout,
any HTTP status counts as a response) runs only with a connected host, to confirm the end-to-end
path gateway → host → app.

The `devtunnel.exe` CLI is looked for in the PATH and then among the WinGet packages
(`%LOCALAPPDATA%\Microsoft\WinGet\Packages\Microsoft.devtunnel_*`). It requires an existing login
(`devtunnel user login`); otherwise the CLI error appears in the section.

As with git fetch, **the tunnel is queried by full refreshes**: the automatic one when the panel
opens, the Refresh button and the ones that follow a pull or a branch switch. Until the first one
arrives the section stays on "dev tunnel not read". Only the refresh after a database update is a
local read, and it leaves the section as it is instead of clearing it.

## Actions and guards

The two actions appear as an icon only when the operation is trivial. The guard **is** the
feature: an always-visible button that sometimes does damage would be worse than no button.

### Apply migrations (icon next to the context)

Appears when `CanApplyPending`: there are pending migrations **and none is out of order**. With
even one out of order the gap comes from a parallel branch, the resulting schema may differ from
the one produced by applying them in sequence, and the decision goes back to you.

**Unknown migrations do not block**: they are inert rows in `__EFMigrationsHistory` and do not
prevent EF from correctly applying the pending ones. Gating on them would have made the action
unusable on a database that permanently contains one.

Runs `dotnet ef database update --project <p> --startup-project <s> --context <name>`, after a
confirmation that shows the **clone, server and database** read by the panel and states that
`ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` are forced to Development.

> ⚠️ `ASPNETCORE_ENVIRONMENT` and `DOTNET_ENVIRONMENT` are forced to `Development` by the code.
> It is not cosmetic: it is what decides *which* database gets touched. Without it, EF would read
> `appsettings.json` — which may point to a database other than `Project_dev` from
> `appsettings.Development.json` — and migrate a **different** database from the one the panel
> shows. The `launchSettings.json` profiles may set `Development`, but relying on their order for
> a destructive operation is not acceptable.

The build is not skipped (no `--no-build`): applying migrations compiled from stale sources is
worse than waiting a few tens of seconds.

### Pull (icon in the Repository section)

Always visible when git is available. Runs `git pull --ff-only`: the flag **is** the guard.
If a fast-forward is not possible (local commits to push, missing upstream, detached HEAD) git
**refuses** instead of creating a merge or conflicts, and the reason appears in the activity row.
With local changes that the fast-forward would overwrite, git refuses in the same way.
That is why this action does not ask for confirmation — it cannot produce a state you have to
untangle by hand. The tooltip says how many commits it would take according to the last fetch.

### Branch switch (drop-down in the Repository section)

Runs `git switch <branch>` on the chosen entry. Here too the guard is git's: if the local changes
would conflict with the target branch the switch is refused and the drop-down goes back to the
current branch. For a name that exists only on the remote, `git switch` creates the local branch
that tracks it.

### Branch cleanup (icon in the Repository section)

Deletes the local branches whose remote branch no longer exists. First it runs `git fetch
--prune`: as long as the deleted remote-tracking refs stay on disk git marks no upstream as
`gone`. The candidates are only branches with a `gone` upstream: never the current branch, never
those without an upstream. If there are none the panel re-reads itself and says so (“no local
branches to delete”); otherwise it asks for confirmation listing them by name together with the
clone, with Cancel as the default choice, and runs `git branch -D`.

`-D` and not `-d`: with squash or rebase `-d` would refuse precisely the branches already
integrated. The deletion is irreversible for the local refs: a `gone` branch can contain commits
never pushed, recoverable only from the reflog until it expires (`git reflog`, then
`git branch <name> <sha>`).

After every action the panel re-reads itself. Pull and branch switch trigger a **full refresh**
(fetch and tunnel included), because they change the code on disk and the remote reference point;
after a database update the re-read is local, since only the database has changed. A failed or
cancelled action re-reads nothing: the error stays in the box and to re-read you press Refresh.

Right after a pull or a branch switch the buttons become active again before the migrations have
been re-read: for a few seconds the database update button reflects the **previous** read, and
the guard may no longer hold for the code that just arrived (for example an out-of-order
migration brought in by the pull). The context's “reading…” timestamp marks that interval, and
the confirmation shows server and database anyway.

## Desktop app (multiple environments)

### What it shows

A window with one column per environment. Each column has a card for every repository the
environment includes, in catalog order (see “Configuration”), and every card has the git section:
branch, commits to pull, local changes, fetch, pull, branch cleanup and the branch switch
drop-down. The rest depends on the repository type: a backend card has the same content as the
VSIX panel (migrations per context, server and database), a frontend card the browser preview, an
app card start and Metro; a generic repository has only the git section and the VS Code button.
For example, with a catalog of one backend, one frontend and one app the cards are `backend-N`,
`frontend-N` and `app-N`.
Dev tunnels have no card of their own: a repository with “Shareable” turned on in the “Dev tunnel”
group of its setup card has the tunnel section in the body of its card, under the git section,
when the environment has “Tunnel” ticked in its header: the host dot, the assigned port and an
arrow of its own to open and close it. The port comes from the pool tunnel the environment has
claimed (see “Dev tunnel pool”): for the backend it is Kestrel's first https port, for the
frontend the dev server's http port, so the frontend of environment N also opens from outside the
machine; with ports written by hand in the setup, mandatory for a generic repository, the first
one applies. Closing a card also closes its tunnel section.
A repository that is not the first of its type in the environment carries the tag of its id next
to its name, to tell it apart (see “Repository tools”).

At the top of every column is the environment card. Its header holds the name, the backend's
database and server tags read from the appsettings (“DB: …” and “Server: …”), the activity,
environment start/stop, Refresh, the crossed-out eye that hides the column and, on the right edge,
the dot. A database to handle with care is marked “Protected DB” (see `protectedDatabase`); in
a Docker environment there is no server tag; under the note sit the secondary actions, “Reset to
dev” (the Reset) and the Tunnel switch. A Docker environment also has the “Docker” tag and, under
the note, “Repair env” and “Destroy environment” (see “Docker environments”). The card scrolls
together with the repository cards, so it always stays aligned with its column. A hidden column
disappears completely from the grid, and the state survives a restart. In the window header, next
to the app buttons, there is a chip per environment (number and dot, the activity in the
tooltip): it stays visible even when scrolling horizontally. Clicking a chip shows or hides the
column, and showing it brings it into view; a hidden column has the crossed-out eye on its chip.
After the chips, the “+” opens the “New Docker environment” panel.
Every port is a pill with the number in a grey badge and the public host without `https://`: a
click anywhere on the pill copies the URL. Whether it responds is told by the host dot: the pill
writes “no response” in red only when that port's HTTP probe got no response.
The migrations sit in a single Database card, under the app card: the migrations applied but
unknown to the code (when there are any) and then one context per section, separated by a line
and each with its own arrow. This way frontend and app stay at the same height in every column,
even when a misaligned context opens on its own.

Tunnel ports and context migrations use the same pill row, the `Lane` control: grey badge (port
number or position of the migration in the context), text, coloured note (migration state, port
not responding) and actions. With a single action the whole lane is the button and the glyph on
the right acts as the signal: a click on a port copies the URL, a click on a migration runs
`dotnet ef database update` up to it, with confirmation. With more actions each one has its own
round button on the right.

Every open column is 550 px wide, always: with few columns the leftover space stays empty on the
right, and when the open columns do not fit in the window a horizontal scroll bar appears, which
moves the columns with their environment cards. Hidden columns take no space.

Ctrl+G brings up a pixel-art kitten that strolls above the footer or stops to wag its tail, doze
and send hearts; pressing Ctrl+G again makes it disappear. It does not intercept clicks.

While it is visible the kitten also acts as a lookout: when a new alert appears it stops and says
it in a speech bubble, one at a time. It reports card errors (the red box and folders not found)
and migrations: contexts with missing or out-of-order migrations and migrations applied but
unknown to the code. It says each alert only once as long as it stays present; the alert becomes
new again if it disappears and comes back or if it changes. When it appears it says everything it
has not said yet, including the alerts born while it was hidden.

The same alerts also arrive as Windows notifications, always, even with the kitten hidden. They
come from the app's icon in the notification area, present as long as the app is open.
New alerts that arrive in the same round (every 2 seconds) end up in a single notification;
beyond the 255 characters Windows allows, the notification ends with “… and N more”. Here too
each one is notified once as long as it stays present, but the count is separate from the
kitten's: when it appears, the kitten also says what has already been notified. A click on the
notification or on the icon brings the window back to the foreground.

With the kitten visible, Win+Shift+S (the Windows snipping tool) sometimes makes it wave, on
average once in three times: it
stops, waves its paw and says a phrase in a speech bubble. The key is recognised even with the
window in the background and still goes through to Windows. If the kitten is saying an alert it
only waves its paw; when hidden it does nothing.

Ctrl+D opens demo mode: the team behind a card game for smartphones, with the made-up
repositories `deck-server`, `deck-backoffice` and `deck-app` and four environments (a story
being tried from the phone with the tunnel on, a bug with two migrations to apply, a review with
branches behind, an out-of-order migration and a tunnel not found, release acceptance testing in a
Docker environment) with activity rows that change on their own. Names, URLs and people do not
come from the configuration. The buttons, including “Destroy environment”, “Repair env”, “Reset
to dev” and the tunnel switch, respond only in the activity row: no command touches git,
databases, Docker, tunnels or processes. The pull request panel and the story points meeting are
fake too, with “Open in” changing the branches only in the cards, and the notes are made up and
not saved. The “+” for a new Docker environment disappears, and the setup shows only the
preferences: Save, Import and the tabs with actions on configuration, Docker and tunnels stay
locked for as long as the demo lasts. The demo works on a machine without clones, without a
network and without the DevOps service. The kitten, if
visible, comments on the fake alerts; Windows notifications stay quiet and on the way back report
the news from the real environments. Pressing Ctrl+D again goes back to the real environments,
which in the meantime have kept reading themselves; the frontend previews start over.

At the bottom of every column is the preview of the environment's frontend, in a 16:9 frame usable
with mouse and keyboard. The card is always there, even without sessions: the header shows the
expected dev server port, and the icon next to the enlarge button opens the selected view in the
default browser (in “Claude” the page of Claude's browser, or the dev server root if there is
none; in “Mine” the page the WebView2 has reached). It has two modes:

- “Claude”: the playwright-cli browser open on the port of the environment's dev server
  (the `devServerPortVariable` variable in the frontend's `envFile` env file, for example
  `frontend-N\env\dev.bat`, or `devServerDefaultPort` if it is missing). Those browsers are
  headless and otherwise invisible. Clicks and keyboard go to that browser, the same one Claude is
  driving: using it while Claude works changes the page under its feet. The dot is green when the
  browser is there, and the card opens on its own the first time it appears.
- “Mine”: a WebView2 of its own on the same address, with its own login and cookies, separate
  from Claude's browser. It is created only when you open it. In the card the page renders 1280 px
  wide and is scaled to the frame, without scroll bars (the wheel still scrolls); enlarged it goes
  back to 100%.

The enlarge icon brings the preview to the whole app window, over header and columns; Esc or the
button in the bar go back to the columns. From the enlarged preview Esc therefore does not reach
the page, which receives it from the card.

For “Claude” mode the app reads the running browsers from the CLI's registry
(`%LOCALAPPDATA%\ms-playwright\b`) every 3 seconds. If there is at least one, it starts the
playwright-cli dashboard (`playwright-cli show --port=0`) as a child process, opens one connection
per environment and closes it when no browser is left or when the app closes. The connection uses
the dashboard's internal protocol, which is not a public API: a CLI update may break it. It needs
`playwright-cli` in the PATH and the WebView2 runtime, already present on Windows 11.
There is only one dashboard per machine: if another one is already running (a `playwright-cli
show` launched by hand) the “Claude” previews stay empty until that one closes. The opposite also
holds: while the app is open, a `playwright-cli show` launched elsewhere does not open a window of
its own.

### Environment discovery

From the `environments` list in the configuration (see “Configuration”): one column for every
environment, local or Docker, in list order. A local environment (`kind: local`) looks for the
repositories it includes in `<clones folder>\<name with {n}>`, or in its own clones folder if it
has one. A Docker environment (`kind: docker`) has its worktrees in `<Docker worktree
folder>\<name with {n}>` (see “Docker environments”). The column of a Docker environment appears
when the “+” in the header creates it and disappears when “Destroy environment” removes it,
without restarting the app.
The columns are always there: a missing folder shows “folder not found” and starts no reads, and
with the backend missing neither the migrations nor its dev tunnel start.
The existence of the folders is checked again on every refresh.

### Refresh

On opening, a full refresh of everything starts, the same as the “Refresh all” button at the top;
every column also has its own button. Before that first refresh every local environment
re-aligns its pool-bound env files (see “Env files of local environments”). After actions the
VSIX rules apply, repository by repository: pull and branch switch re-read that whole repository,
database update and branch cleanup re-read only the local state, a failed or cancelled action
does not re-read. Every 5 minutes from opening, the app runs the same “Refresh all” on its own,
with the same rules (a repository with an action in progress is skipped); a manual refresh does
not restart the countdown. The VSIX does not have this periodic refresh. Apart from that, the only
periodic read is the one of local processes for the state of the tools (see below), every
3 seconds.

### Repository tools

Next to the name of every repository, in its card:

- **VS** (backend) opens the solution configured in `solution` (for example `Project.sln`) in the
  most recent Visual Studio installation found by `vswhere`, prereleases included; **Code**
  (frontend and app) opens the folder in VS Code, preferring Insiders.
- The **accessory command** (frontend, only if `extraCommand` is not empty) opens a console with
  that command in the clone folder; the button takes its name from the last word of the command
  (for example `npm run codegen` becomes “Codegen”). The console stays open when the command
  ends, so the outcome can be read.
- **Start**: for the backend it opens a console with `dotnet run --launch-profile Kestrel
  --no-launch-browser` from the `apiProject` folder (for example `src\Project.Api`); for the
  frontend and the app their `startCommand` (for the frontend `npm run start` by default).
  None of the three turns on a dev tunnel host.
- **Stop** closes the console and its whole process tree, or the recognised processes if they
  were started some other way (for example Kestrel started from Visual Studio).
- The **dot** shows the state: green running, orange console open but process not (yet) active
  (build, install, or ended with an error readable in the console), grey stopped. The tunnel
  host dot sits in the card's tunnel section and is described in “Dev tunnel pool”.

In the cards of a Docker environment the default backend and frontend run in a container, and
Start, Stop, the dot and the “Logs” button act on the container (see “Docker environments”).

Generic repositories start nothing: they only have **Code** and, if configured, their dev tunnel
section. The environment start/stop on the environment card starts Kestrel and the dev servers of
all the cards, in column order, but neither the app nor the hosts of the pool tunnels, which are
shared with the other environments; stop also stops the app and Metro. In a Docker environment
the environment start/stop starts and stops the containers and the web Compose Watch.

The state is derived from the machine's processes, not from the app's memory: Kestrel is the API
process configured in `apiProcess` (for example `Project.Api.exe`) under the backend folder, the
dev server is the `node` under the frontend folder whose command line contains the text of
`devServerProcessHint` (for example `webpack`), the app is any `node` under the app folder, the
host is `devtunnel host` with the tunnel id. Every console opened by the app carries a marker in
its title (`AMBIENTI kestrel-N`, `AMBIENTI frontend-N`, `AMBIENTI app-N`, `AMBIENTI metro-N`,
`AMBIENTI tunnel-pool-<Description>` for the host of a pool tunnel, `AMBIENTI
watch-web-<key>` and `AMBIENTI log-<service>-<key>` in Docker environments) that makes it
recognisable even after the app restarts.

**Default repository.** In an environment the first repository of each type (backend, frontend,
app, in catalog order) is the default one: it keeps the usual markers, with just the environment
number. The other repositories of the same type, and all the generic ones, use the key
`N-<id>`: a second backend with id `api2` opens the console `AMBIENTI kestrel-1-api2`. An id
always starts with a letter, so a derived key cannot be confused with a historical one.
The hosts of the pool tunnels do not depend on the repository: there is one console per tunnel,
for example `AMBIENTI tunnel-pool-pool-1`.

Editors and consoles start **outside** the app's Job Object (`CREATE_BREAKAWAY_FROM_JOB`, with a
job that allows it): closing the app does not close Visual Studio, VS Code or the started
servers. They start with the normal environment, without the MSBuild variables the app sets for
its own children.

### Actions and guards

The guards are those of the “Actions and guards” section, which lives in the shared code. The
gate on write actions is **per repository**: while a pull, a branch switch, a cleanup or a
database update is running on a repository, only its buttons are disabled (for the backend also
the contexts' database update); the other repositories, in the same column or in others, stay
usable. The Refresh buttons always stay active and re-read only the free repositories: the busy
one is re-read when its action ends, if it succeeded. Confirmations also name the environment,
because with twelve repositories in the same window it is the only way to notice you pressed the
button in the wrong card.

**Reset** and **“Open in”** in the PR panel touch all the cards of the environment, generic ones
included: every repository goes to the branch of the PR of its repository on the server, the
others to `dev`, the main development branch (see “Development branch”). If even one card has
local changes, a git operation half done or lacks the branch, nothing changes: this applies to a
generic repository too, so a generic repository with work in progress or without `dev` blocks
Reset and “Open in” for the environment it belongs to.

**Work on the environment.** A PR panel card shows in a blue tag (“Env 1”, “Env 1, 3”) the environments that sit exactly on
its PRs' branches, the other repositories on `dev`: the state “Open in” leaves. The environment
card mirrors it: under the name, a blue info FlashCard for each work item (or PR without one) whose
branches the environment sits on; a click opens the work item. The rule is one, `EnvironmentWork`
in Presentation, shared with the MCP server; it is recomputed with no reads of its own, after every
branch read of the environment and every PR read, which the app runs even with the panel closed.
Only the PRs the panel knows count, the active ones you opened or review, the two sections
separately: a work item without an open PR, or a colleague's PR you do not review, does not show.

### Dev tunnel

The tunnel section of a card reads the pool port assigned to that repository in that environment:
without an assignment it shows “not shared” and does not query the CLI; with the assignment it
shows only the assigned port, with its URL, read with `devtunnel show` on the pool tunnel that
hosts it. The pool rules, the buttons and the states of the section are in “Dev tunnel pool”.

All the cards with a dev tunnel query the `devtunnel` CLI one read at a time: it has not been
verified that it handles concurrent calls on the same login. While waiting for its turn the
section shows “queued…”. The tunnel does not hold back the other sections.

### Closing

If an environment has a write action in progress, closing asks for confirmation: an interrupted
`dotnet ef database update` can leave the database halfway through a migration.
At startup the app assigns itself to a Job Object with kill-on-close: however it ends, even a
crash, the system closes the git, devtunnel and dotnet processes it started. For the same reason
it sets `MSBUILDDISABLENODEREUSE=1`, `DOTNET_CLI_USE_MSBUILD_SERVER=0` and
`UseSharedCompilation=false` for its child processes: the build launched by the database update
does not leave MSBuild nodes or a shared compiler behind that the job would kill while another
build is using them. The price is a slightly slower build.

### Building and publishing

```
dotnet publish src\Soffritto.Desktop -c Release
```

The result is a single file,
`src\Soffritto.Desktop\bin\Release\net11.0-windows10.0.17763.0\win-x64\publish\Soffritto.exe`,
that works from any folder. It is framework-dependent and requires the Windows Desktop
Runtime 11 or later. `RollForward=LatestMajor` stays: without it, a net11.0 app on a machine that
has only a newer runtime does not start. `IncludeNativeLibrariesForSelfExtract` also brings the
native SqlClient SNI inside the exe.

### Relationship with the VSIX

VSIX and app use the same `Soffritto.Presentation` project: read and action sequences, guards,
texts and refresh rules live there, together with the Kestrel and tunnel host tools
(`BackendTools`, `ConsoleServices`, `ProcessTable`, `DetachedLauncher`), and each host provides
only how things are shown, how they are confirmed, how they are copied and where the root is.
Presentation depends neither on Visual Studio nor on WPF; to read the processes' command lines
it uses the `System.Management` package. A single `<Version>` in `Directory.Build.props` keeps
VSIX and app aligned. What stays duplicated is the
state machine of the branch drop-down (`UI/StatusViewModel.cs` in the VSIX,
`ViewModels/BranchPickerViewModel.cs` in the app), tied to each host's ViewModel type: a change of
behaviour in the drop-down must be carried over to both files.

The VSIX and the CLI work on the **first backend of the catalog**: they use its API process,
launch profile, appsettings and contexts. The VSIX recognises the environment from the folder of
the open solution by comparing it with the default backend of every environment, local or Docker
(`AppConfiguration.BackendEnvironmentOf`), and shows the pool port assigned to that backend.
It re-reads the configuration on every Refresh. In its header it shows the same tags as the app:
protected database, Docker, database and server. Opened on the worktree of a Docker environment
it applies the app's rules: the slot database guard on migration reads, update, rollback and
copying the command, no connection to a slot database that is stopped or starting, and
start/stop on the api container. It takes its colours from the Visual Studio theme, light or
dark, and from the app the shapes of cards, tags, dots and switch. The CLI and the MCP server do
not see Docker environments. With several backends in the catalog it must be opened only on the
default backend of an environment: on another backend it would not find the environment, it
would fall back on the folder's numeric suffix and its consoles would take the default backend's
`kestrel-N` marker. The setup reminds you of this with a warning.

## Dev tunnel pool

A repository is shared with colleagues through a port of a tunnel in the **pool**: a set of dev
tunnels in the account, listed in `tunnelPool` in the configuration and found again by
Description with `devtunnel list`. The pool applies to all environments, local and Docker. For
example, a pool of eight tunnels, from `pool-1` to `pool-8`, with Label `SoffrittoPool`, can host
the backend and frontend of environment N on `pool-N`.

Ports are assigned only on request, by turning on “Tunnel” in an environment header or when a
Docker environment is created. The assignments live in `<data folder>\tunnel-pool.json` (usually
`%LOCALAPPDATA%\Soffritto\tunnel-pool.json`): Description and id of the tunnel, port, environment
and repository. Soffritto creates and deletes the pool tunnels from the “Tunnels” tab of the
setup; the setup's Save creates only the tunnels listed in an imported configuration that the
account does not have.

A tunnel belongs to a single environment, so the pool is as large as the account's quota:
“Create all possible tunnels”, in the “Tunnels” tab, creates `pool-K` until `devtunnel create`
refuses, and writes every tunnel created into `config.json`. The limit is not written in the code.
To leave room for a tunnel created outside Soffritto you delete a tunnel from the same tab.

A tunnel expires after 30 days without use and the service does not accept longer expirations. At
every startup the app resets the expiration of the pool tunnels to 30 days
(`devtunnel update <id> --expiration 30d`): opening the app at least once a month keeps the
tunnels and their URLs.

### An environment's tunnel

The tunnel is turned on and off for the whole environment, with the “Tunnel” switch in the column
header. There are no tunnel commands in the cards. When the change is done the environment
re-reads itself, as with its Refresh button.

When “Tunnel” is turned on, the app gives a pool port, with anonymous access, to every repository
of the environment that exposes one:

- the port is Kestrel's first https port for a local backend, the dev server's http port for a
  local frontend, the container's host port (http) in a Docker environment; with ports written by
  hand in the setup, the first of those applies. A repository with no port to expose stays without
  a tunnel;
- a tunnel belongs to a single environment: the first repository claims the first tunnel with no
  assigned ports, in pool order, and the other repositories of the environment go on the same
  tunnel; a tunnel with ports of another environment is never used. Tunnels the account does not
  have and full ones are skipped: a tunnel hosts at most 9 ports. Without a usable tunnel the
  activity row says “pool full” and the switch goes back off. Assignments made before this rule
  stay where they are, even on a tunnel shared by several environments;
- anonymous access applies to the single port (`devtunnel access create <id> -p <port>
  --anonymous`); if the service does not grant it, the app deletes the port and cancels the
  assignment;
- a running host does not host the ports added after it started, so once ports are assigned the
  app turns on the tunnel host, or restarts it if it was already running;
- the app waits up to 30 seconds per port for the service to publish the URL, then rewrites the
  env variables that depend on the pool (see “Env files of local environments” and “Env files of
  Docker environments”). If a URL does not arrive, the activity row says so: turn the tunnel off
  and on again.

When “Tunnel” is turned off the app removes the anonymous access of every port of the
environment, deletes the ports from the tunnel, removes the assignments from `tunnel-pool.json`,
closes the host of the tunnel left without ports and rewrites the env variables that depended on
the pool. The tunnel becomes free again: turning “Tunnel” back on, the environment may receive a
different tunnel, with different URLs, and an app built with the old URL must be rebuilt.

### The tunnel host

Every pool tunnel has a single host: a console `AMBIENTI tunnel-pool-<Description>` (for example
`AMBIENTI tunnel-pool-pool-1`), opened in the data folder. The app does not turn the hosts on when
it opens. If the host of an environment with the tunnel closes, starting the environment turns it
back on; stopping the environment does not touch it.

The tunnel section appears in the card only if the repository has a pool port: it shows the port
with its URL, the buttons to copy and open it, and the host dot, green if the host is running,
orange if it is off and the URL does not respond.

### The pool in the setup's Tunnels tab

The “Tunnels” tab of the setup sums up the pool in one line (“2 of 8 tunnels in use: environments
1, 5”) and lists the tunnels, each with its Description, the environment that holds it or “free”,
the host dot and “Delete”. “Delete” applies only to a tunnel with no assigned ports: it closes its
host, deletes it from the account and removes it from `config.json`, after a confirmation. Below
the list, “Add tunnel” creates one more tunnel on the first free `pool-K` Description and “Create
all possible tunnels” creates them as long as the account allows. The tab's actions take effect
immediately, without Save, and their outcome appears under the buttons.

Under a tunnel only the things that do not add up appear: the differences between the devtunnels
service and `tunnel-pool.json` (ports without an assignment, assignments without a port) and the
ports left to an environment that no longer exists, each with “Release”. An assignment that has
lost its port (tunnel gone from the account, recreated with another id, port deleted by hand)
leaves `tunnel-pool.json` when the tunnel of that environment is turned on or off; if `devtunnel`
does not respond, the assignment stays.

### Env files of local environments

In a local environment the app keeps aligned the per-slot variables of each repository's “Env”
group in the setup, or of the type's “Suggested values” if the repository has not filled in the
group, whose local value depends on the environment:

- a variable whose value is exactly `{apiHostUrl}` gets `https://localhost:<first Kestrel https
  port of the default backend of the same environment>`, whether the backend is shared or not:
  this way a frontend running on the same machine as Kestrel does not depend on the pool;
- a variable with `{poolUrl}` or `{poolUrl:<id>}` gets the URL of the pool port of the indicated
  repository, for example for an app on a phone or emulator, which cannot reach the PC's
  `localhost`.

The aligned variables are those the setup's “Env” group declares for each repository, for example
the frontend's api URL with `{apiHostUrl}` in the `envFile` env file and the app's backend URL
with `{poolUrl:<backend id>}` in `.env`. A repository without an “Env” group receives nothing.

The app writes those variables without asking, at two moments: at the first full re-read after it
starts, for all of them, and after turning the environment's “Tunnel” on or off, for the
pool-dependent variables only. It rewrites a value only if it is
managed: absent, empty, or a URL pointing to `*.devtunnels.ms`, `localhost` or `127.0.0.1`. A
value written by hand pointing to another host stays as it is. A value the app cannot compute is
not written: a `{poolUrl…}` variable without a pool port assigned to the indicated repository
stays as it is, even after that port is released, and the activity row says which card to assign
it to. In the file only the lines of those variables change, and a file that does not exist is
not created. The dev server and the app bundle read the env only at startup, so after a rewrite
the activity row says, for each frontend with written variables, “restart the dev server of <id>
so it reads <names>”, adding “(and <accessory command> reads the new values)” if the frontend has
an accessory command (`extraCommand`); for the app it says to restart Metro. A value changed by
hand after the app started stays until the app's next restart, which brings it back to the
computed value if it is a managed value.

For a colleague who opens the shared frontend, an `{apiHostUrl}` value that the frontend bundle
carries into the browser points to `localhost`, that is, to the colleague's machine.

### Pool limits

- Only one devtunnel account applies on a machine, the one the CLI is authenticated with: pool,
  `tunnel-pool.json` and hosts apply to that account. With a different login the pool tunnels are
  not found (“no tunnel with Description …”).
- The same account used on two machines with the same pool Descriptions brings two hosts onto the
  same tunnel, which knock each other off: each machine uses its own Descriptions.
- The VSIX turns the environment's tunnel on and off with the same switch as the app, and so with
  the same pool rules.

## Docker environments

A Docker environment is an environment N with `kind: docker`: the repositories are worktrees
created from the source clones, the default backend and frontend run in the `api` and `web`
containers of the backend repository's compose, and the database is a SQL Server in a `db`
container, restored from the seed.
Its column has the “Docker” tag and, on the environment card, “Repair env” and “Destroy
environment”. A Docker environment is created from the “+” next to the environment numbers in the
header, which opens the “New Docker environment” panel (number, branch per repository, “Tunnel”,
“Create”), and destroyed with “Destroy environment” in its column. Seed, Docker disk space and the
global log live in the setup's “Docker” tab; the number of dev migrations missing from the seed
appears on the “Setup” button.

### The first Docker environment

A catalog without source clones, Env sections and a seed does not create Docker environments. The
first environment needs these steps, all in the setup except the first and the last:

1. Docker Desktop running.
2. Backend card, “Repositories” tab of the setup: in the “Docker” group, “Source clone” with
   “Suggest”, which asks for the clone URL, and “Fallback Docker files” until the branches you
   use have the Docker files (see “Docker files in the repositories”); in the “Env” group,
   “Suggested values”; in the “Database seed” group, the seed file, the database name and the SQL
   Server password, which is suggested automatically at the first seed field you edit.
3. Frontend card: “Source clone”, “Fallback Docker files” if needed, “Suggested values” in “Env”,
   and by hand, in “Per-environment values”, the per-slot variables your project needs on top,
   each with one value for the worktree and one for the container.
4. App card, if the environment includes it: “Source clone”, “Suggested values” in “Env”, and by
   hand in “Local files from the template environment” the git-ignored files the build needs,
   for example the local configuration of an SDK: the worktree does not have them because git
   does not version them.
5. Section “4 · Docker environments”: worktree folder (empty = `<clones folder>\ambienti-docker`),
   database port base (14000) and template environment (empty = the first local environment).
6. Save: creates the missing source clones (`git init --bare`, `remote add origin`, `fetch`).
   Then “Verify seed” on the backend card checks the seed.
7. “Docker” panel, “New Docker environment”: number, repositories with the branch of each,
   “Tunnel” if colleagues need the URLs, “Create”.

A repository's Env section also applies to local environments: the env alignment of local
environments uses its per-slot variables instead of the “Suggested values”.

### Docker files in the repositories

`Dockerfile` and `Dockerfile.dockerignore` sit in the root of the backend and the frontend, and
`compose.yaml` in the root of the backend: the files live in the repositories and whoever works on
the repositories edits them. In the configuration `docker.compose` and `docker.dockerfile` are
paths relative to the repository root, without `..` or a drive; the Dockerfile relative to the
root is also relative to the build context, as Compose Watch on Windows requires, since it
rejects a Dockerfile with an absolute path. The `.dockerignore` files keep the repository's env
files out of the images (for example `appsettings.Development.json`, the env `.bat` files and
`.env`): the images contain no env values, which reach the containers from the env_files.

At every creation and every start the app checks the Docker files in the root of each worktree. A
file versioned in the branch is used as it is. A file that is absent, or ignored by git because
it was copied earlier, is copied from the repository's “Fallback Docker files” folder
(`docker.filesFallback`, the path of a checkout that has the files), and its path goes into
`info/exclude` of the source clone's common git folder, which applies to all its worktrees.
The copies in the worktree are managed by Soffritto: edit them in the fallback folder or in the
repository, because a change made to the copy is overwritten at the next start and is lost with
“Destroy environment”.

### The compose contract

The app runs every command as `docker compose -p <project> -f <compose of the backend
worktree> -f <override> --env-file <slot.env>` from the slot folder, `<data
folder>\docker\ambiente-N`. Before every start it writes into the slot folder:

- `slot.env`, with the variables the compose interpolates: `API_CONTEXT`, `API_DOCKERFILE`,
  `API_HOST_PORT`, `API_ENV_FILE`, `WEB_CONTEXT`, `WEB_DOCKERFILE`, `WEB_HOST_PORT`,
  `WEB_ENV_FILE`, `DB_HOST_PORT`, `DB_IMAGE`, `DB_PASSWORD`, `DB_MEMORY_LIMIT_MB`. The contexts are
  the worktrees; `API_DOCKERFILE` and `WEB_DOCKERFILE` are relative to the context;
- `soffritto.override.yaml`, which puts the data folder label on every service listed by
  `docker compose config --services`;
- one env_file per repository in a container, `<id>.env`, with the api or web variables.

The compose has three services: the database (`db`, the seed service), the api (`api`) and the web
(`web`) in the `web` profile, which the app activates only if the environment has the frontend in
a container. The service names come from the configuration (`docker.service` and the seed
service) and must be those of the compose. Before
starting, `docker compose config --quiet` checks that no variable is missing. The api container
must receive from its env_file `ConnectionStrings__<name>` pointing to the database service and
the slot's database (see “Database guards”).

### Compose Watch for the web

The frontend code lives in the web image, with no mount of the worktree. The backend's
`compose.yaml` declares the Compose Watch rules for the web (`sync`, `sync+restart`, `rebuild`) on
the frontend files, for example `sync` of the sources and `rebuild` on `package.json`. After a
successful start of the web the app opens a console
`AMBIENTI watch-web-<key>` with `docker compose … --profile web watch --no-up web`; the key is the
project without `soffritto-` (for example `11`). The console closes before the web or the
environment is stopped and before “Destroy environment”. With the web container running and the
watch off, the frontend card has an orange dot, and Start rebuilds the image and reopens the
console.

On the development machine the first webpack build in the container takes about 100 seconds, and
a rebuild after a save about 5 seconds. A `docker compose up` launched by hand does not open the
watch. The watch starts after the build without an initial sync: a change saved between the build
and the start of the watch reaches the container the next time that file is saved.

### Slots and ports

Every Docker environment N publishes its ports on `127.0.0.1`, at base + N:

- database `14000 + N` (`docker.databasePortBase`), on container port 1433;
- api `15000 + N`, on container port 8080 (the backend's `docker.publishedPorts`, `15000:8080`);
- web `18000 + N`, on container port 8082 (the frontend's `docker.publishedPorts`, `18000:8082`).

Environment 11 therefore answers on `http://127.0.0.1:15011/swagger` (api), `http://127.0.0.1:18011`
(web) and `127.0.0.1,14011` (database). The temporary seed container uses the base, 14000.
Each base covers base + 1…base + 99, and the configuration rejects two overlapping bases.
Creation rejects an environment with a port already listening on the machine.

### Creation

“New Docker environment” suggests the first free number above the local environments and one row
per catalog repository: check box and branch, enabled only for repositories with a source clone.
The drop-down lists the branches on origin, with those already checked out by a worktree marked;
a new name is created from `origin/dev` (see “Development branch”), after a confirmation.
“Create” runs, in order:

1. checks without side effects: Docker reachable, number free, configuration valid with the new
   entry, source clones present, branches free, worktree folders absent, seed ready, ports free,
   no container or volume of the project;
2. registration of the entry in `config.json` and a new column in the grid;
3. worktrees from the source clones: an existing local branch is used, one only on origin becomes
   a local branch that tracks it, a new one is created from `origin/dev` without an upstream;
4. Docker files in the worktrees;
5. pool ports for backend and frontend, with “Tunnel” (ticked by default);
   with the pool full “Create” stops with the error;
6. env files of the worktrees and the containers;
7. backend worktree guard, `slot.env`, override and api container guard;
8. database from the seed, then `dotnet restore` and the branch's migrations that the seed does
   not have;
9. `docker compose up -d --build`, then the Compose Watch console.

An error after the registration leaves the entry and the column, and you fix it with “Destroy
environment”.

The “Open in” menu of the PR panel also has the “New Docker environment” entry, if the catalog has
a backend with compose and a source clone: it creates an environment on the first free number
above the local ones, with a worktree on the PR branch for every repository in the group and with
backend and frontend without a PR on `dev`. It assigns pool ports to backend and frontend; with
the pool full the environment is created anyway, without pool ports. First it fetches the source
clones of the repositories with a PR and rejects a branch that origin does not have; then it runs
the same steps as “Create”, so it also rejects a branch already checked out by another worktree.
The outcome appears under the PR group, the details in “Operations” on the environment card.

### Start, stop, logs and status

The start/stop on the environment card starts the whole slot (container env_files rewritten,
Docker files, `slot.env`, override, api guard, `docker compose up -d --build`) and then the
watch; stop closes the watch first and then runs `docker compose stop`. Start and Stop on the
backend and frontend cards act on that service only, with `up -d --build <service>`, and the
“Logs” button opens a console `AMBIENTI log-<service>-<key>` with `docker compose logs -f --tail
200 <service>`. Reset and “Open in” stop watch and containers before git, and reject a branch
already checked out by another worktree of the same source clone.

The logs sit close to what they describe, in sections closed by default:

- “api logs” and “web logs”, in the body of the backend and frontend cards: the last 200 lines of
  the container (`docker compose logs --tail 200 <service>`), re-read every 3 seconds only while
  the section is open;
- “Operations”, on the Docker environment card: the details of creation, start and stop, Reset,
  tunnel, “Repair env” and “Destroy environment” for that environment, last 300 lines. It is
  cleared when a new environment is created with the same number;
- the log in the setup's “Docker” tab holds the global operations (seed, cleanup, compaction,
  tunnel pool) and one outcome line for every environment created or destroyed. A creation that
  stops before the column exists leaves its error there.

Every 3 seconds the app reads with `docker ps` the containers of its data folder and checks that
the running services respond: a login on the slot's database, any HTTP response on the host port
of api and web. The row on the environment card shows the phase:

- stopped: no container running;
- starting: the containers are starting and do not respond yet; the environment dot pulses and
  the environment card scrolls by rows, while the repository cards always stay in view;
- ready: the running services respond;
- error: a container exited without a stop from the app, or it is running but has not responded
  for 5 minutes; the environment dot turns red.

With Docker stopped the column says “Docker not reachable” with the reason. With the database
stopped or starting, the backend card's migrations are not read, and they re-read themselves when
the database comes back. Soffritto does not restart stopped containers on its own: you start them
again with the start button on the environment card.

A local column whose backend points to the database of a Docker environment
(`127.0.0.1,<slot database port>` or `localhost,<port>` in the appsettings) follows the phase of
that database: with the database stopped or starting it does not read the migrations, and it
re-reads them when the database comes back.

### Destruction

“Destroy environment” first checks, without changing anything, that no worktree has code to lose:
modified or untracked files, a git operation half done, commits that are on no remote. Then it
asks for confirmation with the summary and, in order, closes the watch, runs
`docker compose down -v --remove-orphans` for the environment's project only, removes the images
built by its compose (`<project>-<service>`, that is api and web), removes the worktrees without
`--force`, deletes the slot folder with the env_files, releases the pool ports and removes the
entry from `config.json`, which closes the column, and finally deletes the environment note. The
branches in the source clones remain.

### Database seed

A project's migrations may not be able to build an empty database from scratch, which is why a
Docker environment always starts from the seed, a SQL Server backup configured on the backend card
(“Database seed” group: a `.bak` file, or a `.7z` with a single `.bak`, SQL Server image,
database, password, memory and service in the compose). The password protects only containers
bound to `127.0.0.1` with development data, and the configuration contains it. A `.7z` is
extracted with `tar.exe` only once, into
`<data folder>\docker\seed-cache\<SHA256 of the archive>\`.

“Verify seed”, in the setup, restores the seed into a temporary SQL Server (container
`soffritto-seed` on `127.0.0.1:14000`) and compares it with the migrations of `origin/dev` in the
source clone, after a fetch, or of the template environment's backend if there is no source
clone. The seed is compatible if it has no migrations the code does not know. The outcome is kept
in `<data folder>\docker\seed-info\<SHA256 of the file>.json`, and from there the app computes the
`origin/dev` migrations missing from the seed: the number appears on the “Setup” button and in the
setup's “Docker” tab, and it is recomputed at startup and every 30 minutes without a fetch, and
with a fetch when the tab opens.

“Align seed”, in the setup's “Docker” tab, takes effect immediately without Save and brings the
seed to `origin/dev`: fetch of the source clone,
seed worktree (the backend folder with `seed` in place of `{n}`, detached on `origin/dev`,
rejected if it has changes), temporary container, restore, `dotnet restore` and migrations with
`--connection` pointing to the container. Then it compacts the database files (recovery model
`SIMPLE`, `CHECKPOINT`, `DBCC SHRINKFILE` per file) even when there are 0 missing migrations, and
takes a compressed `BACKUP` into a new file next to the configured one,
`<name>-<yyyyMMdd-hhmmss>.bak`. The resulting seed stays in recovery model `SIMPLE`, like the slot
databases that restore it. `seed.path` in `config.json` switches to the new file even with the
setup open, and the draft's “Seed file” box follows it if you had not changed it;
the previous file stays: to go back, put the previous path back in the setup. A failed migration
leaves the seed as it is and shows the build's error lines in the log: when `dotnet ef` says only
“Build failed”, the app runs `dotnet build` again on the startup project to read them. “Check
seed” and “Align seed” run one at a time per data folder, with `<data folder>\docker\seed.lock`.

### Database guards

No operation on the database of a Docker environment goes to a database other than its slot's:

- in the backend worktree, before reading the migrations, a database update, a rollback, copying
  the command and the creation's migrations, the connection string of the first appsettings that
  contains it must point to `127.0.0.1,<slot port>` (or `localhost,<port>`) and to the seed
  database;
- before starting the api, the container's `ConnectionStrings__<name>`, as resolved by
  `docker compose config`, must point to the database service and to the seed database;
- `RESTORE` and `BACKUP` go only to a container that publishes SQL Server on `127.0.0.1` at the
  connection's port, and compaction only to the temporary seed container.

A stopped operation names file, server and database, never the password. `--connection` is used
only for the seed migrations. A `docker compose up` launched by hand does not go through the
guards; the VSIX opened on a worktree goes through the slot database guard like the app.

### Env files of Docker environments

Every repository has, in the “Env” group of its setup card, an adapter (“Env file format”), which
reads and writes its env file
(`.bat`, dotenv, `appsettings*.json` or manual, without a file), and three classes of variables:

- constants, read from the same repository in the template environment (`constantsSource`, or the
  target file) plus the manual variables from the setup; those starting with a `containerExclude`
  prefix do not reach the container (for the backend `Kestrel:`, otherwise the template
  environment's Kestrel endpoints would win over port 8080 of the Dockerfile);
- secrets, read from the repository's secrets source: a file relative to the repository in the
  template environment, or an absolute one;
- per-slot values, which win over the other two, with one value for the worktree and one for the
  container (empty = does not go to the container). The placeholders are `{n}`, `{hostPort}` and
  `{containerPort}` (the repository's container port), `{apiHostUrl}`
  (`http://127.0.0.1:<api port>`), `{apiContainerUrl}` (`http://api:8080`),
  `{hostConnection}` and `{containerConnection}` (the connection to the slot's database from the
  host and from the containers), `{poolUrl}` and `{poolUrl:<id>}` (the URL of the pool port of the
  repository or of repository `<id>`).

The worktree's env file is written only at creation and with “Repair env”, never at a start:
changes made by hand in the worktree stay until “Repair env”. “Repair env”, on the environment
card, rewrites the worktrees' env files, the containers' env_files and the local files, then
checks the backend worktree guard; a target file that no longer exists starts again from the
template environment's constants. The container's env_file is
rewritten at every start, with `A:B` names becoming `A__B`. A per-slot value that cannot be
computed, for example `{poolUrl:…}` without a pool port, removes the variable from the worktree
and the container, including the constant with the same name: the worktree line stays as it is,
even if written by hand. The exception is creation, when the target file does not exist yet and
the line takes the template environment's constant. The column and the setup preview show the
warning; for a `{poolUrl…}` variable the warning says which card to assign the pool port to.

Soffritto writes no secrets into versioned files or logs, and the `.dockerignore` files keep them
out of the images. With the `.bat` adapter and a `.bat` secrets source, the worktree file calls the
secrets file with `@CALL`; with the other adapters secrets are written only if the target file is
ignored by git. In the containers the secrets arrive from the env_files in the slot folder, which
contain them in clear text. The logs report names and counts, and the compose output goes through
a filter that strips the env_file values. To rotate a secret you change its source: the container
receives it at the next start, the worktree with `@CALL` already reads it from the source, the
other worktrees with “Repair env”. “Preview” in the setup shows the variables on a fictitious slot
with names, classes and origins, never values.

`localFiles` (in the setup “Local files from the template environment”) lists files relative to
the repository root that, at creation and with “Repair env”, are copied byte for byte from the
same repository in the template environment to the worktree: they are for files git does not
version, like the app files mentioned in “The first Docker environment”. A file the template does
not have is a warning in the log.

For the app, a per-slot variable with `{poolUrl:<backend id>}` takes the URL of the backend's pool
port: without a backend port the file's value stays, with the warning. The app's other variables,
which are not per-slot, keep the value copied from the template environment. For the frontend, a
variable with `{apiHostUrl}` in the worktree and `{apiContainerUrl}` in the container is
`http://127.0.0.1:<api port>` in the worktree and `http://api:8080` in the container; the dev
server port variable (`devServerPortVariable`) is the web's host port in the worktree, which the
card preview also uses, and the container port in the container, 8082 by default.

### Docker resource names

Docker resources carry the fingerprint of the data folder, so the Release and a build with
`SOFFRITTO_DATA` do not touch each other. The fingerprint is the first 8 hex digits of the SHA256
of the data folder path:

- the compose project of environment N is `soffritto-N`; with `SOFFRITTO_DATA` set it is
  `soffritto-<first 4 digits of the fingerprint>-N`, and the console key becomes
  `<4 digits>-N`;
- the temporary seed container is `soffritto-seed`, with the same prefix;
- every container has the label `soffritto.dati=<fingerprint>`.

The app reads only the containers with its label, and does not touch a project or a seed
container that exists without it.

### Memory and disk

The app puts no memory caps on the containers, except SQL Server's (the seed's `memoryLimitMb`,
2048 MB suggested). On the development machine an environment with the web uses about 6.6 GiB
(database 2, api 0.1, web 4.5), one without the web about 2.1 GiB: with Docker Desktop's
15.46 GiB about 2 environments with the web fit, or 5 without. More memory for Docker Desktop is
given to WSL with `memory=` in the `[wsl2]` section of `%UserProfile%\.wslconfig`, followed by
`wsl --shutdown`: it is a user setting, outside the app.

For disk, the estimate for 5 environments is at most 43.4 GB and fits in the development machine's
free space. An environment's database takes about 8 GB until the seed is compacted by a first
“Align seed”, and every app worktree where `node_modules` get installed adds 6.9 GB.

### Docker disk space

Docker Desktop on WSL 2 keeps images, containers, volumes and build cache in a virtual disk,
`%LOCALAPPDATA%\Docker\wsl\disk\docker_data.vhdx`. The file grows with builds and does not shrink
on its own when images and cache are removed: Docker reuses the freed space, but the PC will not
get it back until the file is compacted. The tool keeps it under control like this:

- **Destroy** removes the environment's api and web images.
- **Cleanup on close** (the “Clean up when the app closes” preference, on by default): when the
  app closes with “Exit”, a detached, windowless process removes the `<prefix>N-*` images of
  environments N that are no longer Docker environments in the configuration and trims the build
  cache to 10 GB (`docker builder prune --max-used-space`), removing the least recently used
  entries. It does not stop Docker, does not touch containers and asks for no permissions; it
  skips if a `docker compose` command is running (from the app or from the MCP server), if an
  action is in progress in the app or if Docker does not respond. The outcome goes to
  `<data folder>\docker\pulizia.log`.
- **“Docker” panel, “Docker disk space” section**: size of the file on the PC and internal usage
  by type, with “Clean up now” (the same cleanup, in the panel log) and “Compact disk”.
  “Compact disk” asks for confirmation, closes Docker Desktop (with all running containers, even
  those not belonging to environments), stops WSL with `wsl --shutdown` (including the other
  running distributions, which the confirmation lists), compacts the file with administrator
  rights (`Optimize-VHD`, or `diskpart` where the Hyper-V module is missing) and starts Docker
  Desktop again. Only space already freed gets compacted: run “Clean up now” first.

WSL's “sparse” disk, which would give the space back on its own, is not used: Microsoft keeps it
disabled because of the risk of data corruption (`wsl --manage <distro> --set-sparse true` requires
`--allow-unsafe`) and a sparse disk can no longer be compacted with `Optimize-VHD`.

### Limits

- A Docker environment requires the backend, with compose, Dockerfile, seed and the per-slot
  connection variable.
- Only the default backend and frontend go into containers: app and generic repositories stay
  worktrees with their local tools.
- In the container's web the suggested api URL is `http://api:8080`: the dev server proxy reaches
  it, but a link built with that URL and opened in the browser does not open, because `api`
  resolves only inside the compose network.
- Git checks out a branch in only one worktree: a branch already checked out by another worktree
  of the same source clone cannot be chosen.
- Every start rebuilds the images (`up --build`).
- The frontend's accessory command (`extraCommand`), in a worktree without `node_modules`, runs
  `npm ci` first.
- An environment's note is tied to its number and goes away with the environment: an environment
  recreated with the number of a destroyed one starts without a note.
- The CLI does not see Docker environments; the VSIX recognises them from the open solution, and
  the MCP server manages them with its `*_docker` tools (see “MCP server”).
- Previous images, branches and seeds are not cleaned up on their own.

## Command-line use

```
dotnet run --project src\Soffritto.Cli -- C:\Dev\backend-1 --fetch
```

Without `--fetch` it does not touch the network: the ahead/behind counts reflect the last fetch
done by hand and the dev tunnel is not queried. With `--fetch` the CLI also reads the pool port
assigned to the environment's backend, like the VSIX, or writes “not shared”. Exit code `0` if the
environment is aligned, `1` otherwise (the tunnel state does not count towards the exit code).

## MCP server

`Soffritto.Mcp` is a stdio MCP server that works with any MCP client: it works on the numbered
environments of the same `config.json` as the app. On local and Docker environments it performs
the same actions as the app's buttons, with the same Core and Presentation functions, so that
everything can be done from the app or from an agent without skills. The server instructions
explain the flow to any client.

Read tools:

- `list_environments` — the configured environments, local and Docker (`kind`), with their
  folders or worktrees, and which environment and repository a folder belongs to (by default the
  server's current folder, that is the session's). The `backend`, `frontend` and `app` keys are
  the environment's default repositories; the `repository` list contains all of them, with id,
  type and whether they are the default. `recognizedFolder` gives the type and id of the
  recognised repository. Instant.
- `environment_status` — for environment N (or the folder's): server and database the default
  backend points to, migrations per context, git, dev tunnels of backend and frontend, Kestrel and
  dev server addresses, frontend env variables, running services with their PIDs and a `problems`
  list already in words. The usual keys (`backend`, `devTunnel`, `frontend`, `app`) describe the
  default repositories, and stay even when the environment has none, with `exists: false` and a
  note; the `repository` list reports git, dev tunnel and services for every repository. A few
  seconds; `fetch` is off by default, `tunnel` on.

The dev tunnel of each repository is its pool port: the `devTunnel` section says with `shared`
whether the repository has an assigned port, and without a port it does not query the CLI. The
frontend's `pointsToEnvironmentBackend` key is true when a value in the frontend env contains
`localhost:<Kestrel https port of the environment's backend>` or the host of the URL of the
backend's pool port; the pool host counts only when the tunnel has been read.
`environment_status` applies to local environments; for a Docker environment it points to
`docker_environment_status`.

Docker environment tools:

- `docker_environment_status` — worktree, branch and git of every repository; for database, api
  and web the container and whether it responds, with the local address; pool URLs, Compose Watch
  console, migrations of the slot database compared with the branch, note and operation in
  progress. Read-only.
- `create_docker_environment` — the branches per catalog id, number (empty = the lowest free one),
  optional pool ports and the note; it performs the same creation as the “+” in the header and
  opens the web Compose Watch.
- `start_docker_environment`, `stop_docker_environment`, `start_docker_service`,
  `stop_docker_service` — like the buttons on the column and the cards, Compose Watch included.
- `docker_service_logs` — the last lines of `docker compose logs` for a service. Read-only.
- `apply_docker_migrations` — `dotnet restore` and `dotnet ef database update` of the worktree's
  branch, for all contexts or for one, with the guard on the slot database.
- `repair_docker_env` — like “Repair env” in the app.

Local environment tools:

- `start_local_service`, `stop_local_service` — like Start and Stop on a card: Kestrel, dev server
  or app (`bundler: true` for Metro), in consoles detached from the server with the usual markers
  (`LocalServices`, shared with the app). The stop of an app also stops its bundler.
- `start_local_environment`, `stop_local_environment` — like the play and stop of the column:
  the start brings up Kestrel and the dev servers that are off (not the app) and the environment's
  tunnel host if it dropped; the stop closes everything, app and bundler included.
- `pull_repository` — `git pull --ff-only`, like the pull icon.
- `switch_branch` — `git switch` to a branch among the local and remote ones, like the drop-down.
- `checkout_local_environment` — like Reset (no branches: every repository on the development
  branch) and “Open in” (a branch per catalog id): it checks every repository first (fetch, local
  changes, git operation halfway through, detached HEAD, branch present, action in progress, with
  `CheckoutGuard` shared with the app); if one fails nothing changes and the services keep
  running; otherwise it stops the services, then switch and, if behind, pull.
- `prune_branches`, `apply_local_migrations`, `update_local_database_to` — branch cleanup,
  database update and update to a chosen migration. They keep the app's guards: `CanApplyPending`
  (an out-of-order migration sends the decision back to the person), the refusal on
  `protectedDatabase`, `Development` forced on `dotnet ef`.

The irreversible ones go **in two steps**, because the app's confirmation dialog has no MCP
equivalent: elicitation is not reliable (see below). Without `confirm` the tool computes the plan
(branches to delete; clone, server, database and migrations to apply or revert) and returns it
with a code, the hash of the plan. The client shows it to the person and calls again with
`confirm` = code. The worker reads the state again and runs only if the plan is still the same;
otherwise it does nothing and returns the new plan with its new code.

Work items:

- `environments_work` — for every environment the work items (or PRs without one) whose branches
  it sits on, like the blue tag of the PR panel and its mirror on the environment card: it reads
  the PRs from Azure DevOps once and the current branch of every clone or worktree.
- `environment_status` and `docker_environment_status` have the same answer in the `work` section
  (`work: false` skips it). The PR read runs in parallel with the others and stops after 10 s:
  without Azure DevOps, or with a PAT this Windows user cannot decrypt, the section says why and
  the rest of the status comes out anyway.

The MCP server reads the PRs itself, with the app's connection and credentials
(`AzureDevOpsSecrets`, in Core): it does not depend on the app being open.

Tools for every environment, local or Docker:

- `enable_tunnel`, `disable_tunnel` — like the “Tunnel” switch in the environment header in the
  app: claim a pool tunnel and give a pool port to every repository that exposes one, or release
  them all; the env variables that depend on the pool are rewritten. The services read them when
  they start again.
- `run_extra_command` — the repository's extra command, like the accessory button on its card.
- `set_environment_note` — writes the note of an environment; the app shows it right away.
- `operation_status` — progress, log and outcome of an operation.

Destroying a Docker environment is not among the tools: a person does it with “Destroy
environment” in the app. Confirmation through MCP elicitation is not reliable: a client can
declare elicitation support and answer on its own with a refusal, without showing anything to the
person.

Long operations (creation, start and stop of containers, migrations, pool ports, git, extra
command) run in a **worker process** of their own: the server's exe started with `--operation <id>`
by an intermediate `cmd` that exits at once, like the service consoles, so a client that kills the
server with its whole process tree does not reach it. Each operation is a folder in
`<data folder>\operations\<id>` with `request.json` (kind and arguments), `worker.json` (PID and
start time of the worker), `log.txt` and, at the end, `outcome.json`. The tool waits a few seconds
(`waitSeconds`) and, if the operation takes longer, replies with the id to follow with
`operation_status`, which any server process can read: the operation survives the server and the
client session. A worker that disappears without an outcome (killed, machine restarted) shows up
as failed. One operation at a time runs on an environment, across processes (`operations.lock`);
folders older than a week are removed when a server starts.

The app watches `config.json`: a Docker environment created or destroyed by the MCP appears in or
disappears from the grid without a restart, and the note written by the MCP reaches the column
right away. Writes to `config.json` and `tunnel-pool.json` go through a cross-process lock
(`config.lock`, `tunnel-pool.lock` in the data folder), so app and MCP server do not overwrite each
other. Write actions on a clone (pull, branch switch, branch cleanup, database update, checkout) take
a cross-process lock on the repository (`RepositoryLock`, `locks\repository-<hash>.lock` in the
data folder), in the app as in the MCP worker: the second one says the repository is busy instead
of running in parallel. There is, however, no cross-process lock on compose commands for the same
environment: launching a start and a stop on the same environment at the same time from the app
and from the MCP gives an unpredictable result.

Every MCP client registers it as a stdio server that launches `Soffritto.Mcp.exe`. For example,
with Claude Code the per-user registration, pointing to the Debug build, is:

```
claude mcp add --scope user soffritto -- <repository folder>\src\Soffritto.Mcp\bin\Debug\net11.0\Soffritto.Mcp.exe
```

The process lives as long as the client session and keeps its DLLs locked, and so does a worker
while its operation runs: to rebuild the project close the session, or just the server (in Claude
Code from `/mcp`), and wait for the running operations.

With the Setup.exe installation the server is `Soffritto.Mcp.exe` next to the exe, and “Register
in Claude Code” in the setup's Components tab does the registration (see “Setup.exe”).

## Setup.exe

People who use Soffritto without developing it install it with the `Soffritto.App-win-Setup.exe`
from GitHub Releases: no repository, no SDK, no administrator rights.

### What it installs

Velopack installs per user into `%LOCALAPPDATA%\Soffritto.App`, with “Soffritto” shortcuts on the
desktop and in Start and the uninstall entry in “Installed apps”. The package contains:

- the Desktop app, **self-contained**: it runs on any Windows x64 without a .NET runtime;
- `Soffritto.Mcp.exe`, the MCP server, next to the exe and on the same .NET runtime (not as a
  single file, which on every start can exceed the MCP client's connection timeout);
- `vsix\Soffritto.Vsix.vsix`, the Visual Studio extension.

The package contains no configuration and the setup does not write the data folder
`%LOCALAPPDATA%\Soffritto`. That is why the `packId` is `Soffritto.App` and not `Soffritto`: with
the same name the program and the data would end up in the same folder. On the first launch after
the setup, `config.json` is already there if the machine had used Soffritto; otherwise the setup
starts empty, as for any exe without a configuration.

### First launch and the Components tab

On the first launch after the Setup.exe the Setup panel opens on the **Components** tab. The tab
is not part of the configuration: every button acts immediately.

- **External programs**: Git, Dev tunnel and Docker Desktop, looked for in the machine and user
  PATH re-read from the registry (a program just installed is not in the PATH received at startup)
  and, for devtunnel, among the WinGet packages. “Install” opens a console with
  `winget install --id <id> --exact`: licences and elevation are confirmed there. “Sign in to dev
  tunnel” opens `devtunnel user login`.
- **MCP server**: the registration state is read from `~\.claude.json` (the `soffritto` server
  registered for the user), without launching `claude mcp get`, which would start the server.
  “Register in Claude Code” opens a console with `claude mcp remove` and `claude mcp add --scope
  user`: a server with the same name is replaced.
- **Visual Studio extension**: “Install in Visual Studio” opens `VSLauncher.exe` with the
  `.vsix`, like a double-click (see “Installing the extension”).
- **Updates**: see below.

In a build launched from `bin\` or from `publish\` MCP and VSIX are not there, and the tab says so.

### Updates

The installed app looks for a new release at every start, in the background, and downloads it;
the footer shows “vX on close” next to the version, and on close Velopack installs silently
without reopening the app. “Check now” in the Components tab does the same check. The restart
after a setup Save does not install: the new copy would start while the files are being replaced.

Velopack launches `Update.exe`, which must outlive the app to install after it exits. The app's
kill-on-close job (`ChildProcessJob`) would close it: that is why the app passes Velopack its own
`IProcessImpl` (`Installation.DetachedProcess`) that starts processes with `DetachedLauncher`,
outside the job.

The release to update from is not in the code: `release.ps1` passes it as the MSBuild property
`UpdateRepository`, which ends up in an `AssemblyMetadata` of the exe. Development builds do not
have it and do not update.

### Creating and publishing a release

```
powershell -ExecutionPolicy Bypass -File release.ps1
```

The script reads the version from `Directory.Build.props`, publishes the exe and the MCP server
self-contained into `artifacts\pack`, adds the VSIX (`-SkipVsix` to do without it) and creates with
`vpk` (a local tool in `dotnet-tools.json`) the Setup.exe and the packages in `artifacts\releases`.
It does not touch the development builds in `bin\`. The release repository is the `origin`
remote, or `-Repository https://github.com/<owner>/<repo>`.

With `-Upload` it first downloads the latest release (for the delta package) and then uploads the
new one to GitHub as a draft, to publish from the website; with `-Upload -Publish` it publishes it
right away. The token comes from `GITHUB_TOKEN` or from `gh auth token`. Updates and the anonymous
download of the Setup.exe work only if the release repository is public.

The packaged exe is not a single file: self-contained, WPF cannot load
`DirectWriteForwarder.dll` (C++/CLI) from the bundle and the exe exits with
`BadImageFormatException`. The Velopack package is an archive anyway, and the deltas stay small.

## Installing the extension

Visual Studio must be closed.

```
powershell -ExecutionPolicy Bypass -File install.ps1
```

The script builds in Release, removes the previous version and reinstalls. Once installed:
**View › Other Windows › Soffritto**.

Since 1.0.2 the package includes `Soffritto.Presentation.dll` next to Core: the panel's
orchestration lives there. Since 1.0.3 it also includes `System.Management.dll` (with
`System.CodeDom.dll`) for the Kestrel state: as for SqlClient, the fixed RID brings the real
Windows implementation into the package and not the placeholder. The version is bumped in
`Directory.Build.props`, which applies to the whole solution: bump it for every install that must
actually reach Visual Studio.

### `VSIXInstaller.exe` from the CLI does not work, and the arguments are not to blame

On VS 2026, extensions with `ExtensionType="VisualStudio.Extensibility"` cannot be installed by
invoking `VSIXInstaller.exe` from the command line. They always fail with:

```
RequiresInstallerException: Requested operation must be completed using Visual Studio Installer.
Multiple targets are currently not supported for this operation.
```

Verified by elimination: the error is **identical** with `/instanceIds`, with `/admin`, and with a
manifest reduced to a single `<InstallationTarget>`. The stack confirms it —
`ExtensionService.HandleExtensibilityType(..., IList validSKUs, ...)`, reached from
`GetInstallableData`, that is only on the **installation** path. Uninstalling, which does not go
through there, works normally from the CLI.

Double-clicking the `.vsix` works instead, because it does not go through `VSIXInstaller`: the
`.vsix` association points to `VSLauncher.exe`, which routes to the right installer.
`install.ps1` does exactly the same, so it shows a window and cannot be silent.

Summary of the viable routes:

| Route | Silent | Notes |
|---|---|---|
| `F5` on the `.Vsix` project | — | **Recommended for iterating**: experimental instance, does not touch the working installation |
| `install.ps1` / double-click | no | Installs into the real instance, requires confirmation |
| `VSIXInstaller.exe` from the CLI | — | Not supported for this type of extension |

### `RuntimeIdentifier` is required for SqlClient

The `.Vsix` project pins `win-x64`. It is not a whim: the VSIX **does not contain a `.deps.json`**,
so no RID resolution happens at runtime. `Microsoft.Data.SqlClient` ships in `lib/` a placeholder
assembly (~93 KB) that throws *"Microsoft.Data.SqlClient is not supported on this platform"* for
any operation, and keeps the real implementation under `runtimes/win/lib/net9.0/` (~1.7 MB).
Without a RID the host loads the placeholder.

By pinning the RID, MSBuild flattens the correct Windows assembly and the native
`Microsoft.Data.SqlClient.SNI.dll` into the root. Side effect: the output moves to
`bin\Release\net10.0-windows8.0\win-x64\`, which is why `install.ps1` searches for the `.vsix`
instead of building the path by hand.

The VSIX targets `net10.0-windows8.0` and not `net11.0` like the executables: the Visual Studio 18
extension host brings only runtimes 8 and 10 with it, and the 18.12 extensibility SDK declares that
framework. That is why `Core` and `Presentation`, which the VSIX references, also stay on
`net10.0`. The generated manifest requires Visual Studio 18.12 or later.

### Other notes

- Removal from the CLI works: `install.ps1 -UninstallOnly`.
- The wait uses `Start-Process -Wait` and not `&`: the installers relaunch themselves elevated, so
  with `&` PowerShell would read the exit code of the process that has already exited.
- `InstallationTargetArchitecture = VisualStudioArchitecture.Amd64` in `ExtensionMetadata` is
  needed anyway: without it, an AnyCPU project generates two `<InstallationTarget>` (amd64 and
  arm64). It was not the cause of this error, but it remains a needless ambiguity in the manifest.
- Outside the Marketplace there is no automatic update: reinstall after every change.
- The VSIX is not signed: on the first install Visual Studio reports "unverified publisher". This
  is expected and blocks nothing.

## Per-section loading

On opening, the panel shows a **splash** with the logo (drawn in XAML, no bitmaps: Remote UI does
not load them reliably) and the status text, instead of a dashed header and empty sections that
would look like a broken panel; it disappears once the first real read has started, and in any
case not before **three seconds** from opening: the reads run behind the splash, so when the
panel appears the sections are already populated instead of filling in piecemeal. At the same
time it runs a **full refresh** on its own, the same as the **Refresh** button: it re-reads
everything, runs `git fetch` and queries the dev tunnel. When the IDE starts, the window is
restored before the solution is loaded and Remote UI offers no convenient “solution ready” event
to hook, so the first refresh starts after one second and, as long as the solution does not
appear to be open, retries every two seconds for a minute, showing “waiting for the solution to
load…” in the activity row; if you press Refresh in the meantime the wait stops. After a minute
without a solution the panel says so and stays idle. The three reads (migrations+database, git,
tunnel) are **independent**: they start together, each section updates as soon as its own read
finishes and meanwhile shows its own read indicator. The migrations do not wait for the fetch or
for the tunnel resolution, and the tunnel (the slowest read: `devtunnel list`, `devtunnel show`,
HTTP probe) holds back no other section.

Reads **do not block actions**: the database update and pull buttons are disabled only while a
write action is in progress, not while a section is still reading. The Refresh button follows the
same rule: a refresh requested while a section is still reading does not start a second parallel
read for that section (two concurrent `git fetch` runs would fight over the repository lock), but
queues it and runs it as soon as the current one finishes.

## Configuration

Nothing is written in the code: clones folder, repositories, environments, clone URLs, folder
names (with `{n}` in place of the number), dev tunnels, EF contexts, appsettings, start commands,
dev server port, Docker environments, env files, seed, tunnel pool and the Azure DevOps connection
live in a single configuration, which app, VSIX, CLI and MCP server read from the same file. The
model is `src\Soffritto.Core\Configuration\AppConfiguration.cs`.

The configuration (`schemaVersion: 2`) has a **repository catalog** (`repositories`), a list of
**environments** (`environments`), the `docker` section shared by Docker environments and the
tunnel pool (`tunnelPool`). Every catalog entry is an object with `type` (`backend`, `frontend`,
`app` or `generic`), a stable `id` (lowercase letters, digits and hyphens), `cloneUrl`, `folder`
and the fields of its type, at the same level; `type` can be in any position in the object. Every
environment has its `number` (from 1 to 99), the ids of the `repositories` it includes (the order
in the column stays the catalog's), `kind` (`local` or `docker`) and, if needed, a `root` of its
own in place of the clones folder. Entries with `kind: docker` are written and removed by creation
(the “+” in the header, the PR panel, the MCP server) and by “Destroy environment”.
A local environment can have `protectedDatabase: true` (the “Protected DB” box in the setup): its
backend's database is protected, for example because others use it too, so app and VSIX read its
migrations but offer no update, rollback or command copy, the column shows the “Protected DB” tag
and the MCP server declares it in `list_environments` and `environment_status`
(`protectedDatabase`, `protectedDatabaseReason`). Everything else in the environment works as
usual. A Docker environment always has its own database and does not accept the field.

Catalog entry fields related to tunnels and Docker:

- `devTunnel`: when present, the repository is shared through a pool port; `ports`, optional,
  replaces the type's ports (Kestrel's first https port for the backend, the dev server port for
  the frontend) and is mandatory for a generic repository;
- `sourceClone`: the full path of the bare source clone from which the worktrees of Docker
  environments are created; without it, the repository does not go into a Docker environment;
- `docker`: `compose` (backend only) and `dockerfile`, relative to the repository root;
  `service`, the service in the compose; `publishedPorts`, entries `base:container port`;
  `filesFallback`, the folder of the fallback Docker files; `localFiles`, the files copied from the
  template environment. Empty fields
  take the type's defaults: `compose.yaml`, `Dockerfile`, `api` and `15000:8080` for the backend,
  `Dockerfile`, `web` and `18000:8082` for the frontend;
- `env`: `adapter` (`bat`, `dotenv`, `appSettings` or `manual`), `target`, `constantsSource`,
  `containerExclude`, `manual` (name and value), `secrets` (adapter and `source`) and `slot` (name,
  `local` and `container`), described in “Env files of Docker environments”;
- `seed`, backend only: `kind` (`sqlServer`), `path` and `sqlServer` with `image`,
  `databaseName`, `password`, `memoryLimitMb` and `service`.

The shared `docker` section has `worktreeRoot` (empty = `<root>\ambienti-docker`, which also holds
the source clones, in `sorgenti\<repository>.git`), `databasePortBase` (14000) and
`templateEnvironment`, the local environment from which constants, secrets and local files are
read (empty = the first local environment). `tunnelPool.tunnels` lists the pool tunnels with
`description` and `label`.

```json
{
  "repositories": [
    { "type": "generic", "id": "docs", "folder": "docs-{n}",
      "devTunnel": { "ports": [ { "number": 3000, "protocol": "http" } ] } },
    { "type": "frontend", "id": "frontend", "folder": "frontend-{n}",
      "sourceClone": "C:\\Dev\\ambienti-docker\\sorgenti\\frontend.git",
      "devTunnel": {},
      "env": { "adapter": "bat", "target": "env\\dev.bat",
        "secrets": { "adapter": "bat", "source": "env\\secrets.bat" },
        "slot": [ { "name": "API_URL", "local": "{apiHostUrl}", "container": "{apiContainerUrl}" },
                  { "name": "DEV_SERVER_PORT", "local": "{hostPort}", "container": "{containerPort}" } ] } }
  ],
  "tunnelPool": { "tunnels": [ { "description": "pool-1", "label": "SoffrittoPool" } ] }
}
```

**Earlier formats.** A `config.json` with a `schemaVersion` earlier than the current one is not
read: app, VSIX, CLI and MCP server reject it with a message saying which `schemaVersion` the
running version reads. An app or a VSIX of an earlier version does not read the current format:
update them together.

### Where it lives

The working copy is `%LOCALAPPDATA%\Soffritto\config.json`: the setup writes it and app, VSIX and
CLI read it.

For provisioning, put a `soffritto.json` next to the exe (for the release, in the publish
folder). At startup, if its content has changed since the last import, it replaces the working
copy; if it is the same, it is not re-imported, and the changes made from the setup stay.
The app never writes that file. `--config <file>` uses another file in its place. The pattern
`soffritto*.json` is in `.gitignore`, so a configuration file left in the repository does not end
up in a commit. The imported file must be in the catalog format; before replacing the working
copy the app copies it to `config.prima-import.json`, and the setup message says so. An import
also replaces the Docker environment entries: a Docker environment that the imported file does
not have loses its entry, while containers and worktrees stay on disk, and the message says so.

While the app is running, `config.json` is written by the setup's Save, by the creation and
destruction of Docker environments and by “Align seed”. The writes go one at a time through a lock
in the app process:
each one re-reads the file, changes only its own part, validates and writes. The setup's Save
keeps the Docker entries and a `seed.path` changed in the file while the setup was open, and the
message says which value won. If `config.json` is missing or unreadable, creation, destruction and
“Align seed” refuse to write; the setup's Save writes the new configuration and leaves the copy of
the unreadable file in `config.illeggibile.json`.

**Trying a build on isolated data.** With the `SOFFRITTO_DATA` environment variable set to a full
path, app, MCP server and CLI use that folder instead of `%LOCALAPPDATA%\Soffritto`, for
configuration, notes, settings and browser profile; the window title shows it (“Soffritto ·
data: …”). Set it on the same command line as the launch:

```
$env:SOFFRITTO_DATA='C:\Data\Soffritto-test'; Start-Process '<Debug exe>'
```

Empty counts as not set (PowerShell deletes it). With a value that is not a full path, even one
made only of spaces, no data is read or written: the app shows the error and exits, the MCP server
replies with the error, the CLI exits with an exception. The consoles and the Visual Studio opened
by the isolated app inherit the variable: an already updated VSIX, in that Visual Studio, would
read the isolated folder.

### The Setup panel

It opens from the “Setup” button in the header, and on its own at first launch or after an
import. Under the header it has a row of tabs; every opening starts from the first one, and the
errors of any tab appear in the panel footer. In every tab the section titles sit on the page and
each card wraps one topic. The “in configuration” tag next to a title marks the sections that end
up in `config.json` and in a provisioning `soffritto.json`. Every field has an “i” button next to
its label: on hover it shows the field description, on click it keeps it open until you click
elsewhere. The tabs:

- **Repositories**: the clones folder and the repository catalog, with the development branch
  convention above it (see “Development branch”) and then one card per repository. At the top of
  the card sit the type drop-down, the id, the clone URL and the folder; below, groups that open
  and close: the type's fields (“Backend”, “Frontend” or “App”), “Dev tunnel” with “Shareable” and
  the optional ports, “Docker” with “Source clone” and “Suggest”, “Env” with “Suggested values”
  and “Preview” and, for the backend, “Database seed” with “Verify seed”.
  The arrows reorder the cards and “Add repository” adds one;
- **Local environments**: the local environments, one per row with the number, the check boxes of
  the included repositories and the optional folder, with “Add environment”; Docker environments
  are not in the list and are created from the “+” in the header;
- **Docker**: worktree and source clone folder, database port base and template environment;
  then, with actions that take effect immediately without Save, the seed state with the missing
  migrations and “Align seed”, Docker disk space with “Clean up now”, “Compact disk” and the
  “Clean up when the app closes” preference, and the log of global Docker operations;
- **Tunnels**: the tunnel pool (see “The pool in the setup's Tunnels tab”);
- **DevOps**: the provider, today only Azure DevOps with collection, project, authentication and
  “Test connection”; GitHub and GitLab appear as a preview, disabled, to show what will be needed
  once they are supported, and write nothing into `config.json`;
- **Preferences**: theme, kitten, background and demo mode, which apply immediately.

When the type of a card changes, the fields of the previous type stay in memory and come back
when you return to it. The fields of the “Docker” group show the type's default when they are
empty, and write into the file only when changed. “Suggested values” replaces the “Env” group with
the type's values: for the backend the `appSettings` adapter on the first appsettings, `Kestrel:`
excluded from the container and the connection to the slot's database; for the frontend the
`.bat` adapter on the `envFile` env file, secrets from `secrets.bat` in the same folder and, if
`devServerPortVariable` is set, the dev server port variable; for the app the dotenv adapter on
`.env`. Variables with project-specific names, like the api URL for the frontend or the backend's
pool URL for the app, are not suggested: add them in the “Env” group. They are editable suggested
values: after “Suggested values” you change them in the “Env” group like any other value. “Check
seed” works on the configuration shown, even if not saved.

Errors appear while you type, in red; warnings in orange, and they do not prevent saving: two
repositories of the same type in the same environment, a Docker environment whose backend does not
exclude `Kestrel:` from the container, an environment that uses a backend other than the first in
the catalog, several backends with the VSIX.

As long as a Docker environment exists, some fields are read-only, with a padlock next to the
label, because changing them would move worktrees, databases or ports already in use: folder,
source clone, compose, service and published ports of the repositories included in a Docker
environment; database name, password and service of the seed; the backend's per-slot connection
variable; worktree folder and database port base. To change them, destroy those environments
first.

“Save” writes the configuration and then, in order:

- runs `git clone` only for the missing folders that have a URL; a folder that already exists is
  never touched, not even if its origin is different;
- creates the missing source clones, with `git init --bare`, `remote add origin` and `fetch`; a
  source clone without a clone URL is an error;
- creates the pool tunnels the account does not have, “Owner only” and without ports (`devtunnel
  create --description <Description> --labels <Label> --expiration 30d`);
- reopens the app with the new configuration. If a clone or a tunnel did not succeed, or if it
  created a tunnel, it does not reopen: a second Save reopens. With an operation in progress in
  the Docker panel the restart is postponed.

### Development branch

`dev` is the main development branch, the same for every repository in the catalog
(`src\Soffritto.Core\GitConventions.cs`): Reset brings the repositories back there, the new
branches of Docker environments are created from `origin/dev`, “Verify seed” and “Align seed”
compare the seed with the migrations of `origin/dev`. Every repository in the catalog must have it
on origin. The setup reminds you of this in a light blue note above the catalog, in the
“Repositories” tab.

### Azure DevOps

The pull request panel, and the MCP server for the work items of the environments, connect directly
to Azure DevOps, without going through the Azure DevOps MCP server:
from the MCP it takes the collection URL, project and PAT, sent in Basic auth with an empty user as
its `VssBasicCredential` does (`src\Soffritto.Core\AzureDevOps\AzureDevOpsConnection.cs`).
The default authentication is the Windows user; with a PAT, the token is saved encrypted with DPAPI
for the current Windows user (`AzureDevOpsSecrets`): the working copy does not contain it in clear
text, and the app and the MCP server, running as the same user, both read it; for another user or
on another machine it must be entered again. In the imported file you can write `"pat"` in
clear text: at the first save it becomes `"patProtected"`.

### Connection string

It stays the clone's: it is read from the first of the configured appsettings that contains it
(JSONC, so variants commented out with `//` are ignored), the same one `dotnet ef` uses.
In a Docker environment that worktree file is written by the `appSettings` adapter at creation and
with “Repair env”, with the connection to the slot's database (`127.0.0.1,<slot port>`); the
adapter rewrites the JSON and the file's comments do not survive. The guards in “Database guards”
check exactly that connection. A local environment can point with its appsettings to the database
of a Docker environment: the local column then follows the phase of that database (see “Start,
stop, logs and status”).
