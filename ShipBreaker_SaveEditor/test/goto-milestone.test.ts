import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, loadCertificationLevels, gotoMilestone } from "../src/lpw/milestones";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const KEYMAP_PATH = path.join(__dirname, "..", "data", "asset_save_keys.json");
const MILESTONES_PATH = path.join(__dirname, "..", "data", "milestones.json");
const CERT_LEVELS_PATH = path.join(__dirname, "..", "data", "certification_levels.json");

const keymap = AssetKeyMap.load(KEYMAP_PATH);
const milestoneData = loadMilestoneData(MILESTONES_PATH);
const certLevels = loadCertificationLevels(CERT_LEVELS_PATH);

test("gotoMilestone: 07_00 on Beltalowda lands at rank 7, strips Act-3", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const result = gotoMilestone(save, "PAT_CMP_07_00_CalyssiaAntiUnion_Complete", milestoneData, certLevels);

  assert.equal(result.newRank, 7, "should land on target's own rank, not a lower preserved-predecessor rank");
  assert.equal(save.certification!.rank, 7);
  assert.ok(!save.actionTracker!.has("PAT_CMP_07_00_CalyssiaAntiUnion_Complete"));
  assert.ok(!save.actionTracker!.has("PAT_CMP_09_00_RhodesArrives_Complete"), "downstream milestone should be stripped");

  // Act-3 sweep must run regardless of target (2026-07-08 fix)
  const remainingAct3 = [...save.actionTracker!.keys()].filter((k) => k.startsWith("PAT_CMP_A3_"));
  assert.deepEqual(remainingAct3, [], "Act-3 content must be fully stripped even for an early-chain target");
});

test("gotoMilestone: same-rank-cluster case (17_03_LouUpsetAboutKaito_Night) preserves same-rank siblings", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const result = gotoMilestone(save, "PAT_CMP_17_03_LouUpsetAboutKaito_Night_Complete", milestoneData, certLevels);

  assert.equal(result.newRank, 17, "same-rank cluster must not under-drop rank below preserved rank-17 siblings");
  assert.ok(save.actionTracker!.has("PAT_CMP_17_01_KaitoScrewUpWarning_Complete"), "preserved rank-17 sibling should survive");
});

test("gotoMilestone: 17_X2 strips Act-3 as before (no regression)", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const result = gotoMilestone(save, "PAT_CMP_17_X2_LYNXUnionClampDown_Complete", milestoneData, certLevels);

  assert.equal(result.newRank, 17);
  const remainingAct3 = [...save.actionTracker!.keys()].filter((k) => k.startsWith("PAT_CMP_A3_"));
  assert.deepEqual(remainingAct3, []);
});
