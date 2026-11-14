# Live acceptance

Builds and fixture tests do not prove game-runtime or Discord acceptance.
Use a disposable KOTH world before enabling the plugin on a production server.
The plugin does not launch Quasar or a game server during automated tests.

1. Install the hub package and matching companion. Restart Quasar and the test
   game server. Confirm the KOTH page reports live scores and blocked client
   reset packets. Check plugin logs for attachment errors.
2. Award capture points and apply a death penalty. Refresh the page and compare
   with the mod's in-game score output before and after a world save.
3. Save a dedicated KOTH channel in the bot's configured guild. Test it, then
   trigger a capture. Confirm delivery identifies the correct server. Confirm
   normal Quasar bot functions still work in their existing channels.
4. Remove Send Messages permission, trigger another capture, and confirm a
   delivery failure is shown. Restore permission and confirm buffered delivery.
   Restart Quasar and confirm an already delivered capture is not replayed.
5. As a Viewer, confirm scores remain readable and channel/reset controls are
   absent. Attempt the protected operations directly and confirm refusal. Test
   the Discord reset command with an unmapped user and a mapped user lacking
   server-control permission.
6. On the disposable standalone world, reset scores with `RESET`. Confirm a
   readable backup exists beside `Scores.data`, live and saved scores are empty,
   and the previous world-stream confirmation cannot reset again. Stop the
   server, restore the backup as described in README, and verify restored scores.
7. Change worlds and reconnect the server. Confirm no stale scores or queued
   announcements cross to the new world. On clusters, confirm scores stay
   node-specific and reset is unavailable. Nexus-connected reset must also be
   refused.
8. Check keyboard navigation, narrow screens, light/dark modes, and custom
   Quasar branding. No browser-only permissions may protect a write operation.

The supplied Workshop `clear` handler ignores sender identity. Validate its
Harmony guard with a controlled test client on the disposable server; do not
send reset packets to production. Disabling/uninstalling the companion removes
this protection because the Workshop source is not modified.
