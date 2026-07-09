import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, buildMilestoneChecklist, completeMissingMilestone } from "../src/lpw/milestones";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const DATA_DIR = path.join(__dirname, "..", "data");
const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const milestoneData = loadMilestoneData(path.join(DATA_DIR, "milestones.json"));

test("completeMissingMilestone: synthetic gap on Beltalowda is fixed without touching rank/xp", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const reference = load(path.join(DATA_DIR, "reference_completed_save.lpw"), keymap);
  save.actionTracker!.delete("PAT_CMP_04_01_LouHopes_Night_Complete");
  const rankBefore = save.certification!.rank;
  const xpBefore = save.certification!.xp;

  let checklist = buildMilestoneChecklist(save, milestoneData);
  assert.equal(checklist.find((e) => e.name === "PAT_CMP_04_01_LouHopes_Night_Complete")!.status, "gap");

  const result = completeMissingMilestone(save, reference, "PAT_CMP_04_01_LouHopes_Night_Complete", milestoneData);

  assert.equal(result.patAdded, "PAT_CMP_04_01_LouHopes_Night_Complete");
  assert.ok(save.actionTracker!.has("PAT_CMP_04_01_LouHopes_Night_Complete"));
  assert.equal(save.certification!.rank, rankBefore, "rank must not change -- the save was already past this point");
  assert.equal(save.certification!.xp, xpBefore, "xp must not change");

  checklist = buildMilestoneChecklist(save, milestoneData);
  assert.equal(checklist.find((e) => e.name === "PAT_CMP_04_01_LouHopes_Night_Complete")!.status, "complete");
});

test("completeMissingMilestone: does not fill unrelated prerequisites (narrow fix, unlike fillToMilestone)", () => {
  const save = load(path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw"), keymap);
  const reference = load(path.join(DATA_DIR, "reference_completed_save.lpw"), keymap);

  completeMissingMilestone(save, reference, "PAT_CMP_07_00_CalyssiaAntiUnion_Complete", milestoneData);

  assert.ok(save.actionTracker!.has("PAT_CMP_07_00_CalyssiaAntiUnion_Complete"));
  assert.ok(!save.actionTracker!.has("PAT_CMP_05_02_WeaverLouFriction_ScenePlayed"), "earlier prerequisites should be untouched");
});
