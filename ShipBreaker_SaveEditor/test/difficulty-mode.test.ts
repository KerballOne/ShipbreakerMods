/**
 * Difficulty-mode byte constants verified against all 4 confirmed real saves:
 * Beltalowda = Open Shift, Heidi = No Revives, NEVARMOAR = Limited,
 * test5 = Standard (all confirmed against the in-game profile-select card).
 */
import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadDifficultyModes, resolveDifficultyMode } from "../src/lpw/difficultyMode";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const KEYMAP_PATH = path.join(__dirname, "..", "data", "asset_save_keys.json");
const MODES_PATH = path.join(__dirname, "..", "data", "difficulty_modes.json");

const keymap = AssetKeyMap.load(KEYMAP_PATH);
const modes = loadDifficultyModes(MODES_PATH);

test("Beltalowda's save resolves to Open Shift mode", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const mode = resolveDifficultyMode(save, modes);
  assert.equal(mode, "Open Shift");
});

test("Heidi's save resolves to No Revives mode", () => {
  const save = load(path.join(FIXTURES_DIR, "heidi_original.lpw"), keymap);
  const mode = resolveDifficultyMode(save, modes);
  assert.equal(mode, "No Revives");
});

test("NEVARMOAR's save resolves to Limited mode", () => {
  const save = load(path.join(FIXTURES_DIR, "nevarmoar_rank9.lpw"), keymap);
  const mode = resolveDifficultyMode(save, modes);
  assert.equal(mode, "Limited");
});

test("test5's save resolves to Standard mode", () => {
  const save = load(path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw"), keymap);
  const mode = resolveDifficultyMode(save, modes);
  assert.equal(mode, "Standard");
});
