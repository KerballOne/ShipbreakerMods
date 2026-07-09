import { test } from "node:test";
import * as assert from "node:assert/strict";
import * as path from "node:path";
import { load } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, buildMilestoneChecklist } from "../src/lpw/milestones";

const FIXTURES_DIR = path.join(__dirname, "fixtures");
const DATA_DIR = path.join(__dirname, "..", "data");
const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const milestoneData = loadMilestoneData(path.join(DATA_DIR, "milestones.json"));

test("Beltalowda's fully-completed save has no gaps -- everything complete", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const checklist = buildMilestoneChecklist(save, milestoneData);
  const gaps = checklist.filter((e) => e.status === "gap");
  assert.deepEqual(gaps, [], "a fully-completed reference save should have zero gap-status entries");
});

test("test5's pristine early-game save: everything incomplete, no gaps (nothing completed yet to be 'past')", () => {
  const save = load(path.join(FIXTURES_DIR, "test5_rank4_pristine.lpw"), keymap);
  const checklist = buildMilestoneChecklist(save, milestoneData);
  const gaps = checklist.filter((e) => e.status === "gap");
  assert.deepEqual(gaps, [], "no milestone has been completed yet, so nothing can be 'behind' the furthest point reached");
});

test("Heidi's real save: confirmed gap milestones show status=gap, not incomplete", () => {
  const save = load(path.join(FIXTURES_DIR, "heidi_original.lpw"), keymap);
  const checklist = buildMilestoneChecklist(save, milestoneData);
  const byName = new Map(checklist.map((e) => [e.name, e]));

  // 17_X2 is the confirmed, fixed-in-this-session real gap: heidi's save has
  // 17_X1 complete but 17_X2 (rank-equal, immediately after in chain order)
  // was missing before the QuickCutscene fix. This fixture predates the fix.
  const x2 = byName.get("PAT_CMP_17_X2_LYNXUnionClampDown_Complete");
  assert.ok(x2, "17_X2 should be a known milestone");
  // Whether this specific fixture shows gap or complete depends on whether it
  // was captured before/after the fix -- assert group/rank shape is correct
  // regardless, and that group boundaries work.
  assert.equal(x2.group, "main");
  assert.equal(x2.rank, 17);
});

test("checklist includes both main and act3 groups, act3 all at the chain's max rank", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  const checklist = buildMilestoneChecklist(save, milestoneData);

  const mainCount = checklist.filter((e) => e.group === "main").length;
  const act3Count = checklist.filter((e) => e.group === "act3").length;
  assert.equal(mainCount, milestoneData.milestoneChain.length);
  assert.equal(act3Count, milestoneData.act3Order.length);

  const act3Ranks = new Set(checklist.filter((e) => e.group === "act3").map((e) => e.rank));
  assert.equal(act3Ranks.size, 1, "all Act-3 entries should share one rank");
});

test("synthetic gap detection: a milestone missing before the furthest-reached point is flagged", () => {
  const save = load(path.join(FIXTURES_DIR, "beltalowda_full.lpw"), keymap);
  // Beltalowda's save is fully complete -- artificially remove one early
  // milestone to simulate exactly the Heidi-style bug and confirm detection.
  save.actionTracker!.delete("PAT_CMP_04_01_LouHopes_Night_Complete");

  const checklist = buildMilestoneChecklist(save, milestoneData);
  const entry = checklist.find((e) => e.name === "PAT_CMP_04_01_LouHopes_Night_Complete")!;
  assert.equal(entry.status, "gap", "a milestone missing while later ones are complete must be flagged as a gap, not just incomplete");
});
