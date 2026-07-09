# SaveEditor

Standalone Python tool to decode and edit Hardspace: Shipbreaker `.lpw` player
profile save files. Not a BepInEx mod — a command-line tool for offline save
inspection/editing, built out of the Industrial Action / QuickCutscene
investigation (see `project_industrial_action_investigation` memory).

No dependencies beyond the standard library. Python 3.9+.

## Prerequisites

You need `asset_save_keys.json` to resolve PAT/asset names to their in-save
hash values. This is **not** shipped with the game — it's produced by the
`PartInfoLogger` BepInEx mod in this repo, which reflects the live
`AssetSaveKeyMapService` while the game is running and dumps the table to
`campaign_progress/asset_save_keys.json` (see `PartInfoLogger/Plugin.cs`,
`DumpCampaignProgress`). Run the game with PartInfoLogger installed at least
once, visit the Hab, and grab that file.

The asset hash is `FNV1a32(SaveKey)`, where `SaveKey` is a separate
per-asset string that does **not** always equal the asset's `.name` —
recomputing hashes from names yourself will produce wrong values for at
least some entries. Always use the live-captured table.

## Usage

```
python lpw_tool.py --keymap path/to/asset_save_keys.json dump SAVE.lpw
python lpw_tool.py --keymap path/to/asset_save_keys.json set-pat SAVE.lpw PAT_NAME --value 1 --out OUT.lpw
python lpw_tool.py --keymap path/to/asset_save_keys.json set-pat SAVE.lpw PAT_NAME --out OUT.lpw   # omit --value to remove
python lpw_tool.py --keymap path/to/asset_save_keys.json set-rank SAVE.lpw 17 --out OUT.lpw
python lpw_tool.py --keymap path/to/asset_save_keys.json goto-milestone SAVE.lpw PAT_CMP_17_X2_LYNXUnionClampDown_Complete --out OUT.lpw
python lpw_tool.py list-milestones
python lpw_tool.py --keymap path/to/asset_save_keys.json rename-header SAVE.lpw new_profile_name --out OUT.lpw
```

`set-rank` also trims `PAT_RankXX_Reached` / `PAT_CMP_RankXX_ShiftTracker`
entries above the new rank by default (`--no-trim` to keep them), and syncs
`PAT_STICKERS_EmployeeAdvancement_RankUp` to the new rank. It does **not**
touch currency, upgrades, durability, available ships, XP, or
per-certification-type tier values — see the investigation memory for why
that's a deliberate scope decision, not an oversight.

`goto-milestone` is the "time machine": rolls a save back to just before a
named story milestone in `MILESTONE_CHAIN` (`list-milestones` to see the
full ordered list, rank 2 through 17), so its trigger can be re-approached
from below. This replicates the recipe confirmed live on 2026-07-08 (the
first successful milestone retrigger of the whole investigation, `17_X1`
on `heidi_test1`): sets rank to `target's rank - 1`, and **removes** (not
zeroes) the target and every later milestone in the chain if present.
Earlier milestones are left untouched — force-filling them was tried once
and made no measurable difference. Critically: a not-yet-triggered
milestone PAT must be fully **absent** from `ActionTrackerData`, not set to
`0` — confirmed no genuine save ever carries a `_Complete`-style milestone
PAT at value 0 (the format only ever serializes PATs that have actually
posted), and a leftover `key: 0` entry from an earlier "reset" attempt is
suspected to be why two earlier retrigger attempts on the same save failed
even with the correct rank already rolled back.

`rename-header` computes the target `Saves/Profiles` filename for you
(`vglpp3_<FNV1a32(new_name)>.lpw`) — the game derives save filenames
deterministically from the profile name, confirmed against all real
profiles inspected during the investigation.

## Safety

**Never point `--out` at the live `Saves\Profiles` directory directly**,
and never run this tool against a save you haven't backed up. This tool
has no built-in protection against that — it will happily overwrite
whatever path you give it. Always write to a scratch location first, then
copy manually once you've verified the output.

The game only supports one active career profile at a time — loading a
new/renamed profile requires removing the previous one from
`Saves/Profiles` first (back it up as a `.zip` before doing so).

## File format (reverse-engineered)

See the module docstring in `lpw_tool.py` for the full writeup: header
layout, FNV-1a32 section framing, and which of the 22 registered save-data
sections have been decoded into structured fields
(`ActionTrackerData`, `RandomSceneHistory`, `GeneralData`,
`CertificationTierData`) versus preserved as opaque byte blobs
(everything else — safe to round-trip, not yet individually editable).

## Known limitations

- Only 4 of ~19-23 sections are decoded into editable fields. Others
  round-trip safely but aren't inspectable/editable by name.
- Per-scene one-shot trigger state (`TriggerableBase.HasBeenTriggered`)
  is **not** stored anywhere in this file format — confirmed by
  exhaustively decoding/ruling out all registered sections. Editing PAT
  values alone cannot force a story scene to re-trigger. See the
  investigation memory for the full negative-result writeup.
- The `.ship` file (ship-in-progress save, a separate much larger
  ECS-based format) is not covered by this tool at all.
