/**
 * ProfileDifficultyData: get/set the save's 4-byte difficulty-mode value
 * against the 4 confirmed named constants (data/difficulty_modes.json).
 *
 * New in this port -- no equivalent in lpw_tool.py. Investigated 2026-07-08,
 * starting from a Discord user's (GophTheGreat) hex-editing notes listing 3
 * modes. All 4 real values are now directly confirmed against known saves via
 * their in-game profile-select cards (not just by-elimination guessing):
 *   - Standard    = 9F4525AF (test5's save, confirmed in-game -- the Discord
 *                   thread's "Normal mode", renamed to match the game's own label)
 *   - Open Shift  = A4C73E62 (Beltalowda's save, confirmed in-game)
 *   - No Revives  = 6E0C960E (Heidi's save, confirmed in-game)
 *   - Limited     = 79862906 (NEVARMOAR's save, confirmed in-game -- a 4th
 *                   mode the original Discord thread never mentioned at all;
 *                   found by decoding the save's actual bytes first, then
 *                   confirming the label via screenshot, not community-sourced)
 *
 * The game only allows ONE active save per difficulty mode at a time -- callers
 * should check for a conflict against other known profiles before writing.
 */
import * as fs from "node:fs";
import type { LpwSave } from "./parse";

export interface DifficultyMode {
  name: string;
  bytesHex: string;
  confirmed: boolean;
  note: string;
}

export function loadDifficultyModes(path: string): DifficultyMode[] {
  const raw = fs.readFileSync(path, "utf8");
  return JSON.parse(raw);
}

/** Resolve a save's current ProfileDifficultyData bytes to a known mode name,
 * or null if the bytes don't match any of the 3 known constants (e.g. an
 * unrecognized/future difficulty mode). */
export function resolveDifficultyMode(save: LpwSave, modes: DifficultyMode[]): string | null {
  if (save.difficultyModeBytes === null) return null;
  const hex = save.difficultyModeBytes.toString("hex").toUpperCase();
  const match = modes.find((m) => m.bytesHex.toUpperCase() === hex);
  return match?.name ?? null;
}

/** Set a save's ProfileDifficultyData bytes to a named mode's known constant.
 * Mutates save.difficultyModeBytes in place -- caller must have already run
 * decodeKnownSections() so this field is populated, and must call rebuild()
 * afterward to actually re-encode the section. */
export function setDifficultyMode(save: LpwSave, modeName: string, modes: DifficultyMode[]): void {
  const mode = modes.find((m) => m.name === modeName);
  if (mode === undefined) {
    throw new Error(`Unknown difficulty mode '${modeName}'. Known modes: ${modes.map((m) => m.name).join(", ")}`);
  }
  if (save.difficultyModeBytes === null) {
    throw new Error("ProfileDifficultyData not decoded -- this save may not have that section");
  }
  save.difficultyModeBytes = Buffer.from(mode.bytesHex, "hex");
}
