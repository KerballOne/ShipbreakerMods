/**
 * Auto-scan Saves\Profiles for .lpw files, plus a manual-browse fallback for a
 * save shared out-of-band (e.g. a .zip a player sends a modder, same as the
 * Heidi case from the investigation this tool grew out of).
 */
import * as fs from "node:fs";
import * as os from "node:os";
import * as path from "node:path";
import { load } from "../lpw/rebuild";
import type { AssetKeyMap } from "../lpw/keymap";
import { loadDifficultyModes, resolveDifficultyMode, type DifficultyMode } from "../lpw/difficultyMode";

export function getSavesProfilesDir(): string {
  return path.join(
    os.homedir(),
    "AppData",
    "LocalLow",
    "Blackbird Interactive",
    "Hardspace_ Shipbreaker",
    "Saves",
    "Profiles",
  );
}

export interface ProfileSummary {
  fileName: string;
  profileName: string;
  rank: number | null;
  difficultyMode: string | null;
  /** Absolute path -- used as the opaque :id (base64-encoded) by the API. */
  path: string;
}

export function scanProfiles(keymap: AssetKeyMap, modes: DifficultyMode[]): ProfileSummary[] {
  const dir = getSavesProfilesDir();
  if (!fs.existsSync(dir)) {
    return [];
  }
  const results: ProfileSummary[] = [];
  for (const entry of fs.readdirSync(dir)) {
    if (!entry.toLowerCase().endsWith(".lpw")) continue;
    const fullPath = path.join(dir, entry);
    try {
      const summary = summarizeProfile(fullPath, keymap, modes);
      results.push(summary);
    } catch (err) {
      // A malformed/unrelated .lpw-named file shouldn't take down the whole
      // list -- skip it, don't crash the scan.
      // eslint-disable-next-line no-console
      console.warn(`Skipping unparseable save at ${fullPath}: ${(err as Error).message}`);
    }
  }
  return results;
}

export function summarizeProfile(filePath: string, keymap: AssetKeyMap, modes: DifficultyMode[]): ProfileSummary {
  const save = load(filePath, keymap);
  return {
    fileName: path.basename(filePath),
    profileName: save.generalData?.profileName ?? "<unknown>",
    rank: save.certification?.rank ?? null,
    difficultyMode: resolveDifficultyMode(save, modes),
    path: filePath,
  };
}

/** Validate a manually-supplied path parses as a valid .lpw before accepting
 * it (the "browse for a file" fallback). Throws if invalid. */
export function validateBrowsedPath(filePath: string, keymap: AssetKeyMap, modes: DifficultyMode[]): ProfileSummary {
  if (!fs.existsSync(filePath)) {
    throw new Error(`File not found: ${filePath}`);
  }
  return summarizeProfile(filePath, keymap, modes);
}

export function loadDifficultyModesData(dataDir: string): DifficultyMode[] {
  return loadDifficultyModes(path.join(dataDir, "difficulty_modes.json"));
}
