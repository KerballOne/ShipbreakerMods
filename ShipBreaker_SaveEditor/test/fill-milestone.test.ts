import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, loadCertificationLevels, fillToMilestone } from "../src/lpw/milestones";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const DATA_DIR = path.join(__dirname, "..", "data");
const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const milestoneData = loadMilestoneData(path.join(DATA_DIR, "milestones.json"));
const certLevels = loadCertificationLevels(path.join(DATA_DIR, "certification_levels.json"));

test("fillToMilestone: test5 (rank 4, pristine) filled forward to 07_00 gets prerequisites and lands at rank 7", () => {
  const save = load(path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw"), keymap);
  const reference = load(path.join(DATA_DIR, "reference_completed_save.lpw"), keymap);

  const result = fillToMilestone(save, reference, "PAT_CMP_07_00_CalyssiaAntiUnion_Complete", milestoneData, certLevels);

  assert.equal(result.newRank, 7);
  assert.ok(save.actionTracker!.has("PAT_CMP_02_00_HABIntro_Night_Complete"), "prerequisite before target should be filled");
  assert.ok(save.actionTracker!.has("PAT_CMP_05_02_WeaverLouFriction_ScenePlayed"), "prerequisite right before target should be filled");
  assert.ok(
    save.actionTracker!.has("PAT_CMP_07_00_CalyssiaAntiUnion_Complete"),
    "target itself IS filled/marked complete -- fillToMilestone is inclusive of target (asymmetric with gotoMilestone, which strips the target so it retriggers; fill means 'mark this done', goto means 'let me redo this')",
  );
  assert.ok(!save.actionTracker!.has("PAT_CMP_09_00_RhodesArrives_Complete"), "milestones after target must not be filled");
});

test("fillToMilestone does not lower an already-higher rank", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const reference = load(path.join(DATA_DIR, "reference_completed_save.lpw"), keymap);
  const rankBefore = save.certification!.rank;

  const result = fillToMilestone(save, reference, "PAT_CMP_02_00_HABIntro_Night_Complete", milestoneData, certLevels);

  assert.equal(result.newRank, rankBefore, "filling forward to an early milestone on an already-advanced save must not roll rank back");
});
