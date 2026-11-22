# Architecture

`Koth.Browser` is the .NET 10 Razor component library for the WebAssembly
dashboard. The worker adapter, `Koth.Quasar`, registers its page assembly and
the sidebar link. The manifest names `Koth.Browser.dll` beside the worker DLL.
The page owns its server panels and has no named outlet contributions.

`Koth.Magnetar` is the bundled .NET Standard 2.0 companion.
`src/Shared/Contracts.cs` supplies the companion protocol DTOs to both the
browser assembly and the separately built companion. The worker references
the browser assembly for its component type and data contracts.

## Ownership

- The mod owns capture rules, scoring, prizes, and `Scores.data`.
- The companion owns simulation-thread reflection, packet validation, event
  buffering, and backed-up score resets.
- The browser renders scores, collects reset confirmation, and edits channel settings.
- The worker owns channel configuration, delivery cursors, background collection and KOTH command handlers.
- Quasar owns bot connection/authentication, rate limiting, Discord identity
  mapping, permissions, audited operation dispatch, and agent transport.

The plugin uses existing contracts only: `IQuasarChatCommand`,
`IQuasarDiscordChannels`, `IQuasarCompanionChannel`, and the Quasar operation
dispatcher. A channel ID field is sufficient to select a dedicated channel;
no core contract extension or bot socket is needed.

## Page and server panels

The page inherits the host's full content width, outer spacing, heading styles,
and panel appearance. Each connected server/node gets a keyed native HTML
expansion panel. Panels open independently and own their own snapshot, loading
state, reset confirmation, and delivery status. Requests never use a shared
selected-server field.

Opening a panel loads a fresh snapshot. A five-second timer reads scores only
while that panel is expanded; a separate page timer updates server discovery.
The renderer serializes state changes, and a per-panel busy guard prevents
polling and resetting from overlapping. World/score-epoch changes invalidate the
panel's confirmation. Removing a disconnected server or leaving the page
cancels the corresponding timers and outstanding requests.

The page calls the signed-in panel's `quasarSdk.request` and asks
`quasar.core/dashboard.permission` which controls to show. Quasar routes both
local and hosted calls to the worker dispatcher, which checks each operation
against the current user's effective policies. Each request carries an abort
signal linked to the lifetime of its page or server panel.

## Companion protocol v1

The plugin ID is `owendb.koth`. Quasar discovers the public
`QuasarCompanionPluginId` property and
`Task<string> HandleQuasarCompanionRequestAsync(string, string, CancellationToken)`
method. Operations accept at most 4,096 JSON characters and run on the game
thread through `MySandboxGame.Invoke`.

| Operation | Request | Response |
| --- | --- | --- |
| `snapshot` | Version, stream ID, sequence cursor | Availability/status, world, live faction scores, reset support, event cursors, up to 32 announcements. |
| `reset` | Version, current stream ID, exact `RESET` confirmation | Verified backup filename and new score snapshot/stream ID. |

Attachment finds `KingOfTheHill.Core` in the current world's script assemblies
and resolves its actual session component. Reflection validates `Planets`,
`Descriptions.Session.PlanetScores`, the score properties, and the plugin
message handler's signature. Missing or changed shapes refuse availability.
Scores are capped at 10,000 entries and display strings at 256 characters.
Announcements are capped at 2,048 bytes, with a 256-entry ring buffer.

The fourth secure-message callback parameter means `fromServer`; the supplied
mod calls it `reliable`. The Harmony prefix uses positional `__3` to avoid
depending on that misleading name. Registration, unregistration, mod reads,
and writes take place on the simulation thread. World replacement detaches
the old receiver/patch and creates a new stream. Disposal queues cleanup there.

Reset verifies a full live-score XML backup, invokes the mod's existing handler
locally with a server-origin flag, then checks the saved XML and live list. It
restores original state on failure where possible. Nexus-active/unknown and
Quasar-cluster resets are refused before mutation. A successful reset rotates
the stream and discards pre-reset announcements.

## Quasar operations

All operations are under `owendb.koth`; JSON calls use Quasar's browser SDK
operation dispatcher on the worker. There are no independently authorized plugin HTTP
endpoints and no public anonymous scoreboard.

| Operation | Default policy | Effect |
| --- | --- | --- |
| `servers.list` | `CanView` | Read connected server/node IDs. |
| `scores.read` | `CanView` | Read live scores from one exact connected target. |
| `discord.status` | `CanManageDiscord` | Read delivery status. |
| `settings.read` | `CanManageDiscord` | Read channel/delivery settings. |
| `settings.set` | `CanManageDiscord` | Persist settings; audited, agents denied. |
| `discord.test` | `CanManageDiscord` | Send a test message; audited, agents denied. |
| `scores.reset` | `CanControlServers` | Back up and reset; audited, agents denied. |

Quasar administrators can edit effective operation rules through UI Plugins.
Mutation handlers recheck access at execution. Discord resets dispatch with the
mapped caller's real principal, so command discovery alone grants no authority.
The ordinary score command is intentionally available to players and can query
connected KOTH servers. It performs no mutation and returns no credentials.

## Deployment and rollback

QuasarHub pins a public source commit and builds the UI plus owned companion.
Restart Quasar to load the UI; restart game servers to load the matching
companion. Enable/disable and safe boot use Quasar's existing lifecycle. Removing
the plugin leaves mod scores untouched. To roll back, pin a previously tested
source commit and install its worker, UI and companion together. Preserve `PluginData` and
world storage. Do not modify a published source commit in place.
