# ShipBreaker_SaveEditor

A local web app for fixing a stuck Hardspace: Shipbreaker save without touching a command line. Run it, open the browser tab it launches, pick your save, and fix it.

Two things it does:

- **Retrigger a stuck story beat.** If a story milestone never fired (a real bug some players have hit — see "Why this exists" below), roll it back so it retriggers on next load, or, if the save has already moved past it, complete just that one missing beat in place without touching anything else.
- **Switch difficulty mode.** The game only lets you set a save's difficulty mode (Standard / Open Shift / No Revives / Limited) once, at profile creation. This lets you change it on an existing save.

Every change is backed up automatically first — nothing is ever touched without a copy saved alongside it, and you can restore any prior backup from inside the app.

## Why this exists

Some Hardspace: Shipbreaker saves can get stuck: a story milestone's completion marker never gets recorded (usually from an old bug in a since-fixed mod, or a rare game-side edge case), so the game keeps waiting on a beat that will never fire again on its own. This tool was built directly from fixing real player saves in that state.

## Installation

1. Download the latest release zip and extract it anywhere (e.g. your Desktop).
2. Run `start.bat`.
3. Windows Firewall may show a one-time prompt the first time you run it — click **Allow access**. This app only listens on your own PC (`localhost`); it does not accept connections from your network or the internet.
4. A browser tab opens automatically at `http://localhost:4173`.

No Python, no Node.js install, no command line — everything needed is bundled in the zip.

## Using it

1. **Pick a save.** Saves in your game's `Saves\Profiles` folder are found automatically. You can also paste a full path to a `.lpw` file if you're fixing a copy someone sent you.
2. **Story milestones.** Each milestone shows as complete (green), not yet reached (grey), or a gap (red) — a gap means the save has already moved past that point without ever completing it, the exact class of bug this tool exists to fix.
   - **Go Back** on a completed milestone rolls the save back so it retriggers on next load.
   - **Go Forward** on an unreached milestone fills in its prerequisites and marks it done.
   - **Complete Missing** on a gap fixes just that one milestone in place, without touching rank, XP, or anything else — the save already moved past this point, so nothing else needs to change.
3. **Difficulty mode.** Pick a mode and confirm. If another of your saves already uses that mode, you'll be warned (the game only allows one active save per mode at a time) but the change isn't blocked.
4. **Backups.** Every change backs up the file first. Use **View backups** on the save's page to restore any previous state, not just the most recent one.

## Safety

- A backup is made automatically before every single write — this is not optional and there's no setting to turn it off.
- Backups live in this app's own `backups\` folder, never inside your game's save folder, and are never deleted automatically.
- This app only binds to `localhost` (127.0.0.1) — it is not reachable from your network or the internet under any circumstance.

## Development

```
npm install
npm run dev      # runs the server directly via tsx, no build step
npm test         # unit + integration tests (24 tests, all fixture-based)
npm run build    # compiles to dist/ for the packaged release
```

`src/lpw/` is a TypeScript port of `SaveEditor/lpw_tool.py`'s binary `.lpw` save format reader/writer, verified byte-identical against real save files and cross-validated against the Python reference implementation for every write operation.

## Credit

Difficulty-mode byte values originally sourced from a community writeup (GophTheGreat, Discord, ~2021) covering 3 of the 4 modes; independently re-verified and extended to all 4 modes this project supports, against real save files and in-game confirmation.
