import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { rebuild } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadDifficultyModes } from "../src/lpw/difficultyMode";
import { toAdvancedJson, fromAdvancedJson } from "../src/lpw/advancedJson";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const DATA_DIR = path.join(__dirname, "..", "data");
const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const difficultyModes = loadDifficultyModes(path.join(DATA_DIR, "difficulty_modes.json"));

test("toAdvancedJson -> fromAdvancedJson with no edits round-trips byte-identical", () => {
  const fixturePath = path.join(FIXTURES_DIR, "beltalowda_full.lpw");
  const save = load(fixturePath, keymap);
  const originalBytes = rebuild(save, keymap);

  const json = toAdvancedJson(save, difficultyModes);
  // Simulate the edit-as-text round-trip a real user submission goes through.
  const reparsed = JSON.parse(JSON.stringify(json));
  fromAdvancedJson(save, keymap, difficultyModes, reparsed);

  const rebuiltBytes = rebuild(save, keymap);
  assert.ok(originalBytes.equals(rebuiltBytes), "no-op advanced edit must be byte-identical");
});

test("fromAdvancedJson applies a real edit (rank change) correctly", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json = toAdvancedJson(save, difficultyModes);
  assert.ok(json.certification);
  json.certification.rank = 9;

  fromAdvancedJson(save, keymap, difficultyModes, json);
  assert.equal(save.certification!.rank, 9);
});

test("fromAdvancedJson rejects an unknown asset name in actionTracker", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json = toAdvancedJson(save, difficultyModes);
  assert.ok(json.actionTracker);
  json.actionTracker["PAT_CMP_TOTALLY_MADE_UP_Complete"] = 1;

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json), /not found in asset_save_keys/);
});

test("fromAdvancedJson rejects a non-integer certification.rank", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const json: any = toAdvancedJson(save, difficultyModes);
  json.certification.rank = "nine";

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json), /certification\.rank must be an integer/);
});

test("fromAdvancedJson rejects editing randomSceneHistory (read-only, no encoder)", () => {
  const fixturePath = path.join(FIXTURES_DIR, "beltalowda_full.lpw");
  const save = load(fixturePath, keymap);
  const json: any = toAdvancedJson(save, difficultyModes);
  json.randomSceneHistory = [...(json.randomSceneHistory ?? []), "SomeMadeUpScene"];

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json), /read-only/);
});

test("fromAdvancedJson leaves save untouched when validation fails partway through", () => {
  const fixturePath = path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw");
  const save = load(fixturePath, keymap);
  const rankBefore = save.certification!.rank;
  const json: any = toAdvancedJson(save, difficultyModes);
  // Valid certification edit, but an invalid actionTracker entry after it --
  // must not partially apply the certification change.
  json.certification.rank = 15;
  json.actionTracker["PAT_CMP_NOT_REAL_Complete"] = 1;

  assert.throws(() => fromAdvancedJson(save, keymap, difficultyModes, json));
  assert.equal(save.certification!.rank, rankBefore, "no partial application -- earlier fields must be unchanged after a later field fails validation");
});

test("fromAdvancedJson parses int64 message timestamps via BigInt, not number", () => {
  const fixturePath = path.join(FIXTURES_DIR, "beltalowda_full.lpw");
  const save = load(fixturePath, keymap);
  const json = toAdvancedJson(save, difficultyModes);
  assert.ok(json.messageHistory);
  const [firstName] = Object.keys(json.messageHistory.primary);
  assert.equal(typeof json.messageHistory.primary[firstName], "string", "timestamps must serialize as strings, not JS numbers, to preserve int64 precision");

  fromAdvancedJson(save, keymap, difficultyModes, json);
  assert.equal(typeof save.messageHistory!.primary.get(firstName), "bigint");
});
