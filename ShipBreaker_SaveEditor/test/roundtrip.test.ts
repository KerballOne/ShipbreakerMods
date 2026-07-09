/**
 * Stage 1 + 2 verification (see plan): byte-identical round-trip is the safety
 * property that gates ever wiring up live-file writing in the UI.
 *
 * Stage 1: parse -> rebuild (no decode/edits) must be byte-identical.
 * Stage 2: parse -> decodeKnownSections -> rebuild (no edits) must ALSO be
 * byte-identical -- isolates bugs in the structured-section encoders specifically,
 * since stage 1 passing but stage 2 failing points the finger exactly at one of
 * the 5 decode/encode pairs in sections.ts.
 */
import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as fs from "node:fs";
import * as path from "node:path";
import { parse, type LpwSave } from "../src/lpw/parse";
import { decodeKnownSections, rebuild } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const KEYMAP_PATH = path.join(__dirname, "..", "data", "asset_save_keys.json");

const fixtureFiles = fs
  .readdirSync(FIXTURES_DIR)
  .filter((f) => f.endsWith(".lpw"));

const keymap = AssetKeyMap.load(KEYMAP_PATH);

assert.ok(fixtureFiles.length > 0, "no .lpw fixtures found -- did the copy step run?");

for (const fixtureFile of fixtureFiles) {
  const fixturePath = path.join(FIXTURES_DIR, fixtureFile);

  test(`stage 1: pure round-trip (no decode) -- ${fixtureFile}`, () => {
    const original = fs.readFileSync(fixturePath);
    const save = parse(original);
    const rebuilt = rebuild(save, keymap);
    assert.ok(
      original.equals(rebuilt),
      `byte mismatch: original ${original.length} bytes, rebuilt ${rebuilt.length} bytes`,
    );
  });

  test(`stage 2: decode + rebuild (no edits) -- ${fixtureFile}`, () => {
    const original = fs.readFileSync(fixturePath);
    const save: LpwSave = parse(original);
    decodeKnownSections(save, keymap);
    const rebuilt = rebuild(save, keymap);
    assert.ok(
      original.equals(rebuilt),
      `byte mismatch after decode+rebuild: original ${original.length} bytes, rebuilt ${rebuilt.length} bytes`,
    );
  });
}
