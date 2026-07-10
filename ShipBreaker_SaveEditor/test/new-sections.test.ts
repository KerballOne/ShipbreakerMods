/**
 * Round-trip + basic-edit tests for the 4 newly-decoded sections (VoiceData,
 * OxygenDrainData, FoodChoiceData, HabData) -- reverse-engineered from
 * scratch by byte-diffing 5 real saves (see each decode function's docstring
 * in sections.ts). Byte layout is confirmed via these round-trip tests;
 * semantic meaning of specific values is NOT confirmed.
 */
import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import * as fs from "node:fs";
import { load, rebuild } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadDifficultyModes } from "../src/lpw/difficultyMode";
import { toAdvancedJson, fromAdvancedJson } from "../src/lpw/advancedJson";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const DATA_DIR = path.join(__dirname, "..", "data");
const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const difficultyModes = loadDifficultyModes(path.join(DATA_DIR, "difficulty_modes.json"));

const fixtureFiles = ["beltalowda_full.lpw", "heidi_original.lpw", "nevarmoar_rank9.lpw", "test5_rank4_pristine.lpw"];

for (const fixtureFile of fixtureFiles) {
  test(`decode+rebuild round-trip (no edits) includes the 4 new sections -- ${fixtureFile}`, () => {
    const fixturePath = path.join(FIXTURES_DIR, fixtureFile);
    const original = fs.readFileSync(fixturePath);
    const save = load(fixturePath, keymap);

    assert.equal(typeof save.voiceData, "number");
    assert.equal(typeof save.oxygenDrainData, "number");
    assert.equal(typeof save.foodChoiceData, "number");
    assert.ok(Array.isArray(save.habData));
    assert.equal(save.habData!.length, 7, "all 5 known real saves have exactly 7 HabData entries");

    const rebuilt = rebuild(save, keymap);
    assert.ok(original.equals(rebuilt), "decoding+re-encoding the 4 new sections must not change a single byte when nothing was edited");
  });
}

test("editing voiceData through advanced JSON changes only that field", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json = toAdvancedJson(save, difficultyModes);
  assert.equal(json.voiceData, 0);

  json.voiceData = 2;
  fromAdvancedJson(save, keymap, difficultyModes, json);
  assert.equal(save.voiceData, 2);

  const rebuilt = rebuild(save, keymap);
  // Re-decode the rebuilt bytes and confirm only voiceData changed.
  const tmpPath = path.join(__dirname, "_tmp_voicedata_test.lpw");
  fs.writeFileSync(tmpPath, rebuilt);
  const reloaded = load(tmpPath, keymap);
  fs.unlinkSync(tmpPath);
  assert.equal(reloaded.voiceData, 2);
  assert.equal(reloaded.oxygenDrainData, save.oxygenDrainData);
  assert.equal(reloaded.foodChoiceData, save.foodChoiceData);
  assert.deepEqual(reloaded.habData, save.habData);
});

test("editing habData through advanced JSON preserves entry count and values", () => {
  const fixturePath = path.join(FIXTURES_DIR, "beltalowda_full.lpw");
  const save = load(fixturePath, keymap);
  const json = toAdvancedJson(save, difficultyModes);
  assert.ok(json.habData);
  json.habData![0] = 99;

  fromAdvancedJson(save, keymap, difficultyModes, json);
  assert.equal(save.habData![0], 99);
  assert.equal(save.habData!.length, 7);
});

test("fromAdvancedJson rejects an out-of-range oxygenDrainData (must fit in one byte)", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json: any = toAdvancedJson(save, difficultyModes);
  json.oxygenDrainData = 300;

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json), /between 0 and 255/);
});

test("fromAdvancedJson rejects a non-array habData", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json: any = toAdvancedJson(save, difficultyModes);
  json.habData = "not an array";

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json), /habData must be an array/);
});
