# King of the Hill for Quasar

KOTH scores, capture announcements, and backed-up score resets inside
[Quasar](https://github.com/CometWorks/quasar), using Quasar's existing Discord bot.
Licensed under **GNU AGPL version 3 only** ([LICENSE](LICENSE)).

## Install

The 0.2 package requires **Quasar 1.7.0 or newer**, Magnetar, and the
[King of the Hill Workshop mod, 2388326362](https://steamcommunity.com/sharedfiles/filedetails/?id=2388326362).
Keep the mod enabled in the world's mod list. The 0.2 UI targets the WebAssembly
dashboard. Use a matching Quasar build and plugin package until the release and
hub pin are published.

1. Open **UI Plugins** in Quasar and refresh QuasarHub.
2. Install **King of the Hill** and restart Quasar when prompted.
3. Restart the managed game servers to load the bundled Magnetar companion.
4. Open **King of the Hill** in the sidebar and expand a connected server or node.

The companion attaches to the mod's live session component. Scores include
changes not yet written to `Scores.data`, such as death penalties. Each server
has its own expansion panel. Opening a panel reads its scores, and expanded
panels update every five seconds. Several servers can stay expanded together.
The connected server list also updates every five seconds; disconnected servers
are removed and their pending requests are canceled. Unknown or incompatible
mod versions produce a status message instead of guessed scores.

The page uses Quasar's normal full-width content area and shared page-heading
and panel styles. It adds no separate page width cap or outer padding. There is
no server dropdown or manual Refresh button. Collapse a panel to stop polling
its scores; reopening it reads a fresh snapshot.

## Dedicated Discord channel

Use a dedicated KOTH channel with the **same bot Quasar already runs**. No second
bot, token, webhook, or listener is needed. One saved KOTH channel receives
announcements from all connected KOTH servers, prefixed with the source server.

1. Configure and connect Quasar's existing bot to your Discord server.
2. Create a text channel for KOTH and give the bot **View Channel** and
   **Send Messages** permissions.
3. In Discord, enable **User Settings > Advanced > Developer Mode**. Right-click
   the channel and select **Copy Channel ID**.
4. On Quasar's KOTH page, enter the ID under **Discord announcements**, enable
   **Send capture announcements**, and save.
5. Select **Test saved channel** to check delivery. This explicitly sends a test
   message through Quasar's bot, even when capture announcements are disabled.

Changing these settings or sending a test requires `CanManageDiscord`. The bot
can post only to its configured guild. A failed test reports the channel,
connection, and permission checks to make. No Discord channel creation is
performed by the plugin.

Capture messages are collected in the background without an open browser. They
retain the mod's existing text and use Quasar's rate limiter and disabled
mentions. Death-penalty chat does not use the mod's plugin announcement channel,
so it updates scores but is not sent as a capture announcement.

## Commands

These commands extend Quasar's existing bot. The score command is also available
in game through Quasar.Agent. In-game `/koth` remains the mod's own command.

| Command | Behavior |
| --- | --- |
| `/koth server:list` | Lists connected server/node IDs. |
| `/koth server:<server-id>` | Shows up to 50 faction scores, with a private Discord reply. |
| `!koth list` | Lists IDs in game. |
| `!koth score` | Reads the current standalone server's scores. |
| `!koth <server-id>` | Reads a specific server or node. |
| `/koth-reset confirmation:<server-id> \| <world-stream-id> \| RESET` | Backs up and resets all scores on one standalone server. |

The reset command is Discord-only, requires a mapped Quasar administrator, and
passes through Quasar's audited operation dispatcher with `CanControlServers`.
Copy the exact server and world stream IDs from the KOTH page's reset section.
An in-game score command needs Magnetar 2.4.3.6 or newer. Quasar registers plugin
commands at startup; restart Quasar and reconnect/restart servers after install.

## Reset and recovery

On the KOTH page, expand the server and **Reset all KOTH scores**, type `RESET`, then select
**Back up and reset scores**. This clears faction scores for every planet in
that world. It does not reset capture progress, prize counters, faction balances,
or previously awarded items. Only users with `CanControlServers` see the reset
controls. Server-side operation authorization also protects direct calls.

Before changing anything, the companion serializes the **live** scores into a
unique `QuasarKoth-<timestamp>-<id>.xml` file and reads it back to verify it.
The file is in the mod's own world storage directory beside `Scores.data`.
The mod then performs its own clear, and the companion verifies both live and
persisted scores. A persistence failure restores the original live and saved
scores where storage remains writable. The backup remains available if recovery
fails. Successful resets rotate the stream ID, so stale confirmations and retry
requests cannot clear scores again.

Each server panel keeps its own scores, errors, reset confirmation, and delivery
status. A world or score-epoch change clears that panel's reset confirmation.

To restore a backup, stop the game server, preserve its current `Scores.data`,
copy the selected backup to `Scores.data` in the same mod storage directory,
and restart the server. Backup retention is manual; include world storage in
your regular world backups. Backups include faction names and IDs.

**Nexus and Quasar cluster resets are refused.** The supplied mod can replicate
entire score lists between Nexus sectors; there is no defined score authority or
cross-node rollback here. Cluster views show each node separately and never add
replicated scores together. Announcements come from local scoring messages, not
Nexus's forwarded chat. Install the companion on every participating process
to protect each process's plugin-message handler.

## Security and delivery limits

The supplied mod accepts a `clear` packet on channel `8008` without checking its
sender. While attached, the companion adds a Harmony prefix that rejects
client-sent packets before that handler runs. Its own announcement receiver also
accepts only server-originated messages. Removing the companion restores the
mod's original behavior; this is not a permanent modification of the Workshop
files. Incompatible versions are reported and should not be treated as protected.

The companion retains the most recent 256 announcements per world/score epoch;
Quasar polls every five seconds, reading up to 32 events per server per poll.
Failed Discord posts retain the cursor and retry while events remain buffered.
First observation, enabling announcements, a new world, and a successful reset
establish a baseline rather than replaying old messages. Persisted cursors avoid
normal duplicate delivery across Quasar restarts. Delivery is **at least once**:
a crash after Discord accepts a message but before its cursor reaches disk can
duplicate it. A prolonged outage can overflow the bounded buffer; this is logged.
The server buffer is not durable across game-process restarts.

Settings and cursors live under
`<Quasar install directory>/PluginData/owendb.koth/settings.json`, outside the
plugin update/shadow-copy directories. Malformed settings fail startup rather
than silently replacing the channel configuration. Quasar's safe mode can
disable a bad plugin. No game binaries, bot credentials, or Workshop content are
shipped in this repository.

## Build and tests

Install the .NET 10 SDK. Build against the Quasar 1.7 dashboard branch or release.

```sh
dotnet build src/Koth.Quasar/Koth.Quasar.csproj -c Release \
  -p:QuasarPluginAbstractionsProject=/path/to/quasar/Quasar.Plugin.Abstractions/Quasar.Plugin.Abstractions.csproj
dotnet run --project tests/Koth.Tests/Koth.Tests.csproj -c Release \
  -p:QuasarPluginAbstractionsProject=/path/to/quasar/Quasar.Plugin.Abstractions/Quasar.Plugin.Abstractions.csproj
dotnet build src/Koth.Magnetar/Koth.Magnetar.csproj -c Release \
  -p:DS64=/path/to/DedicatedServer64 -p:Magnetar=/path/to/Magnetar
```

QuasarHub builds the UI against the running host's
`QuasarPluginAbstractionsAssembly` and supplies `DS64` and `Magnetar` for its
owned companion. Local Steam/Magnetar defaults are included; override
`MagnetarBin` if the SDK is in another directory. Proprietary server assemblies
are references only and are never copied into the package. CI uses a public
worker abstractions snapshot for compilation and component tests. Browser
acceptance requires the 1.7 dashboard. Companion builds need a local server
installation.

The worker build places `Koth.Browser.dll` beside `Koth.Quasar.dll`. Package
both with the manifest and its stylesheet directory. Quasar serves the browser
assembly under the signed-in panel and runs the same `/koth` page locally and
at hosted panel paths.

Tests exercise mod reflection, live score projection, Harmony packet rejection,
backup failure and rollback, stale reset protection, Nexus/cluster refusal,
dedicated-channel delivery, retries, cursor persistence, operation policies, and
viewer rendering. They use a mod-shape fixture; they do not replace a live game
acceptance test. No Quasar service or game server is launched by these tests.

To check the packaged browser UI, run Quasar's `Quasar.Wasm.DashboardChecks`
with its published Release dashboard and browser prerequisites. Set
`QUASAR_TEST_PLUGIN_PACKAGE` to the built KOTH package root and
`QUASAR_TEST_PLUGIN_DRIVER` to the absolute path of
[`tests/Browser/koth.test.mjs`](tests/Browser/koth.test.mjs). The driver opens
the route, checks the assembly and stylesheet, expands a server, and saves
settings in the disposable test installation.

See [Architecture](docs/Architecture.md) and
[Live acceptance](docs/LiveAcceptance.md) for contracts and runtime checks.

## License and credits

Copyright (C) 2026 OwendB1. This project is licensed under AGPL-3.0-only.
The UI links to its public source. Modified hosted versions must offer their
corresponding source under the license.

The integration follows the King of the Hill mod's existing score schema and
plugin message protocol. Its feature set is informed by the supplied KOTH Torch
plugin (`DevPlugin-master`); it does not depend on Torch or redistribute that
archive. Quasar, Magnetar, the Workshop mod, Harmony, and Newtonsoft.Json remain
separately maintained projects with their respective licenses.
