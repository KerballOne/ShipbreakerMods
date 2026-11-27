# Reactor work done in the Flight Control session (0.7.4 – 0.7.5)

Written 2026-11-26 for the Reactor Control session. All of it is committed on branch `kb1`.

## 1. The reactor game runs only on the Expanse reactor (0.7.4)
- `ReactorControl.InjectorsByShape`: when the ring has no 1Ax1Ax6 injector thruster bodies (`!byBody`), it logs
  `"[Reactor] by shape: no injector thruster bodies (1Ax1Ax6) on the ring: not the Expanse reactor; no reactor game on this ship."`
  and returns null.
- Before this, stock reactors pulsed bright as if they were the Expanse core.

## 2. The shutdown seed comes from the game's ship seed (0.7.4, commit 249be4b)
- **Bug:** players got the same Memorize pattern on every newly spawned ship.
- **Fix:** `ReactorModes.SeedFor(core)` (ReactorModes.cs:55).
  - It reads `ModuleService.Instance?.CurrentShipPreview`, falling back to `ShipSetup.Preview`.
  - It returns `(preview.Seed * 31 ^ StableHash(preview.ShipName)) & 0x7fffffff`.
  - With no preview, it falls back to the core's name and position.
  - `StableHash` is FNV-based. Don't use `string.GetHashCode`.
- **Why the seed works:**
  - `ShipPreview.Seed` comes from `UnityEngine.Random.Range` and is new on each spawn. A save keeps it, so a reload gets the same game.
  - All three Tachi ships (Tachi, Rocinante, Beratnas Gas Hauler) have `NoRandomSeed = 0` and `SetSeed = 0`. Checked with UnityPy on `Tachi_assets_all.bundle`.
  - VacuumShaders' `Random.InitState(0)` was ruled out: it runs only in an unused mesh-bake tool.
- **Log line:** `"[Reactor] shutdown seed {Seed}, from {SeedSource()}."`, for example
  `shutdown seed 844092821, from the ship's seed 1545884403 and name 'Rocinante'`.
- **Confirmed:** in the user's own FreePlay runs and in a player's log. Seeds vary per spawn, and the mode varies too (the player got Balance and Resonance).
- **Defaults:** the player who kept getting Memorize had Memorize as the default mode. Seed variety only shows once the mode varies or they change the setting.

## 3. A restored save keeps the pulled injectors (0.7.4, commit 6155c56)
- A setup made after a reload counts injectors that were already pulled as disconnected.
- If every injector was pulled before the save, the restored core is cold and the class B reactor unit can be removed safely.

## 4. Handle pulled = the joint to the engine nozzle is gone (0.7.5, commit efeba1d)
- **Bug, from a player:** the beams and the terminal screen never changed when injectors were pulled.
  - Their setup matched ours: core `PRF_SubSystems_Reactor_ClassB`, injectors jointed to `EngineNozzle_08`..`EngineNozzle_15`.
  - The Mistral nozzle flaps stay jointed to the injectors after the pull. The old check counted any remaining mount joint as "still mounted".
- **Fix:** the user's idea. The injector mid-body joints directly to the single big engine nozzle, and the handle pull breaks that joint. Changes are in `ReactorControl.UpdateConnections` (around lines 701–789):
  - `Engine(e)`: the part name starts with `"EngineNozzle"`. Differs from the injection nozzle.
  - `engineJoints` = the number of direct joints from the injector to the engine nozzle.
  - On the first pass: `injector.EngineMounted = engineJoints > 0;` (new field, line 61).
  - `mounted = EngineMounted ? engineJoints : <old released-mount count>`
  - `handlePulled = EngineMounted ? engineJoints == 0 : <old rule: Released.Count > 0 && mounted == 0>`
  - The old rule is kept only for injectors that aren't jointed to an engine nozzle.
  - The log gains `"; {engineJoints} joint(s) to the engine nozzle (handle pulled: off the engine nozzle)"`.
- **Confirmed by the player's 0.7.5 log** (Resonance, order 6 0 2 1 4 5 7 3):
  - Injectors 6 and 0 were each disconnected and judged owed.
  - Injectors 3 and 1 were each disconnected and logged as MISTAKE.
  - The core escalated to LEVEL 2 (UNSTABLE).
  - No exceptions.

## Rules the user set during this work
- No ad-hoc testing of every little thing. Make checks simple and robust.
- No troubleshooting builds sent to remote players.
- Commit, push and release only when asked. "go" = build and deploy for testing. Zips need testing before any release.
- The BepInEx `LogOutput.log` is overwritten on each game launch.

## Open items
- The 0.7.5 Nexus zip triggers Chrome's "This file is dangerous" warning.
  - Its contents match 0.7.4 (DLL + `ships.json`), so it's most likely a Safe Browsing reputation false positive.
  - Next step: check the Nexus virus scan and report it to Google.
- The 0.7.4 git tag still points at `0361179`, not `249be4b`. The user may move it themselves.
