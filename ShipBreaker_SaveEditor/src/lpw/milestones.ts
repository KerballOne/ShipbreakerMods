/**
 * setRank(), gotoMilestone(), snapshot/diff helpers. Ported from
 * SaveEditor/lpw_tool.py's set_rank()/goto_milestone()/_snapshot()/_print_diff().
 *
 * The XP-headroom calculation lives inside gotoMilestone() here (in the Python
 * tool it's in the CLI command handler, _cmd_goto_milestone) since this is meant
 * to be a clean callable used by the backend API, not a CLI-only concern.
 */
import * as fs from "node:fs";
import type { LpwSave } from "./parse";

export interface Milestone {
  name: string;
  rank: number;
}

export interface MessageMapEntry {
  messageName: string;
  gapStart: string;
  gapEnd: string | null;
}

export interface MilestoneData {
  milestoneChain: Milestone[];
  messageMap: MessageMapEntry[];
  act3Order: string[];
  sequentialStoryPatRegex: RegExp;
}

export function loadMilestoneData(path: string): MilestoneData {
  const raw = fs.readFileSync(path, "utf8");
  const parsed = JSON.parse(raw) as {
    milestoneChain: Milestone[];
    messageMap: MessageMapEntry[];
    act3Order: string[];
    sequentialStoryPatRegex: string;
  };
  return {
    milestoneChain: parsed.milestoneChain,
    messageMap: parsed.messageMap,
    act3Order: parsed.act3Order,
    sequentialStoryPatRegex: new RegExp(parsed.sequentialStoryPatRegex),
  };
}

interface CertificationLevelEntry {
  rank: number;
  certificationName: string;
  requiredXP: number;
}

export function loadCertificationLevels(path: string): CertificationLevelEntry[] {
  const raw = fs.readFileSync(path, "utf8");
  const entries: CertificationLevelEntry[] = JSON.parse(raw);
  return [...entries].sort((a, b) => a.rank - b.rank);
}

/**
 * Set CurrentCertificationRank and optionally remove PAT_RankXX_Reached /
 * PAT_CMP_RankXX_ShiftTracker entries for ranks above the new value. Does NOT
 * touch currency/upgrades/durability/available-ships/XP/NonSeq-rank-window PATs.
 */
export function setRank(
  save: LpwSave,
  rank: number,
  opts: { trimReachedAbove?: boolean; trimShiftTrackerAbove?: boolean } = {},
): void {
  const { trimReachedAbove = true, trimShiftTrackerAbove = true } = opts;
  if (save.certification === null) {
    throw new Error("CertificationTierData not decoded");
  }
  save.certification.rank = rank;

  if (save.actionTracker === null) return;

  if (trimReachedAbove) {
    for (let r = rank + 1; r < 100; r++) {
      save.actionTracker.delete(`PAT_Rank${String(r).padStart(2, "0")}_Reached`);
    }
  }
  if (trimShiftTrackerAbove) {
    for (let r = rank + 1; r < 100; r++) {
      save.actionTracker.delete(`PAT_CMP_Rank${String(r).padStart(2, "0")}_ShiftTracker`);
    }
  }

  save.actionTracker.set("PAT_STICKERS_EmployeeAdvancement_RankUp", rank);
}

export interface GotoMilestoneOptions {
  rankOffset?: number;
  preserveTarget?: boolean;
  stripDownstreamStoryPats?: boolean;
  stripMessages?: boolean;
  /** If false, skip the XP-headroom auto-calculation (matches --no-xp). */
  setXp?: boolean;
}

export interface GotoMilestoneResult {
  newRank: number;
  xpSet: number | null;
  xpNote: string;
}

/**
 * Roll a save back to just BEFORE `target` -- `target` itself (and everything
 * after it in the chain) is REMOVED by default, so it retriggers on next load.
 *
 * Rank is floored at the TARGET's own rank, not just the highest rank among
 * what's preserved (2026-07-08 fix -- see comment inline). The Act-3 sub-chain
 * sweep always runs regardless of target (2026-07-08 fix -- a fully-completed
 * reference save has Act-3 content regardless of rollback target; the original
 * Python tool only swept it when targeting 17_X1/17_X2/later, which left
 * impossible save states like "rank 7 with Act-3 completion markers" when
 * rolling back an early milestone on a completed save).
 */
export function gotoMilestone(
  save: LpwSave,
  target: string,
  data: MilestoneData,
  certLevels: CertificationLevelEntry[],
  opts: GotoMilestoneOptions = {},
): GotoMilestoneResult {
  const {
    rankOffset = 0,
    preserveTarget = false,
    stripDownstreamStoryPats = true,
    stripMessages = true,
    setXp = true,
  } = opts;

  const names = data.milestoneChain.map((m) => m.name);
  const idx = names.indexOf(target);
  if (idx === -1) {
    throw new Error(`'${target}' not in MILESTONE_CHAIN. Known milestones: ${names.join(", ")}`);
  }
  const targetRank = data.milestoneChain[idx].rank;

  // cutoff = index one past the last PRESERVED milestone. If preserving
  // target, that's target's own index (inclusive); if not, it's idx-1.
  const cutoff = preserveTarget ? idx : idx - 1;
  const preserved = data.milestoneChain.slice(0, cutoff + 1);
  const preservedRank = preserved.length > 0 ? Math.max(...preserved.map((m) => m.rank)) : 1;
  // Land on the TARGET's own rank, not just the highest rank among preserved --
  // a milestone only becomes eligible to trigger at its OWN rank, so that must
  // be the floor. max() with preservedRank still correctly handles the
  // same-rank-cluster case (e.g. several rank-17 milestones preserved before a
  // rank-17 target -- target's own rank matches theirs, so this is a no-op).
  const baseRank = Math.max(preservedRank, targetRank);
  const newRank = baseRank + rankOffset;

  setRank(save, newRank);

  if (save.actionTracker !== null) {
    for (const name of names.slice(cutoff + 1)) {
      save.actionTracker.delete(name);
    }

    if (stripDownstreamStoryPats) {
      const keptNames = new Set(preserved.map((m) => m.name));
      for (const name of Array.from(save.actionTracker.keys())) {
        if (keptNames.has(name)) continue;
        if (data.sequentialStoryPatRegex.test(name)) {
          save.actionTracker.delete(name);
        }
      }
    }
  }

  if (stripMessages && save.messageHistory !== null) {
    const checkpointOrder = [...names, ...data.act3Order];
    const cutoffCheckpointIdx = cutoff >= 0 ? checkpointOrder.indexOf(names[cutoff]) : -1;
    for (const { messageName, gapStart } of data.messageMap) {
      const gapStartIdx = checkpointOrder.indexOf(gapStart);
      if (gapStartIdx === -1) continue;
      if (gapStartIdx > cutoffCheckpointIdx) {
        save.messageHistory.primary.delete(messageName);
      }
    }
  }

  let xpSet: number | null = null;
  let xpNote = "";
  if (setXp && save.certification !== null) {
    // The HUD progress bar shows (CurrentXP - rank[N-1].RequiredXP) out of
    // (rank[N].RequiredXP - rank[N-1].RequiredXP) when at rank N -- i.e. it's
    // progress toward completing the save's OWN current rank, not the next
    // one. So "5 XP of headroom" means CurrentXP sits 5 below THIS rank's own
    // threshold (newRank's), not the next rank's.
    const levelEntry = certLevels.find((e) => e.rank === newRank);
    if (levelEntry !== undefined) {
      xpSet = levelEntry.requiredXP - 5.0;
      save.certification.xp = xpSet;
      xpNote = `5 below rank ${newRank}'s own ${levelEntry.requiredXP} threshold`;
    } else {
      xpNote = `certification_levels.json missing rank ${newRank} -- xp left unchanged`;
    }
  }

  return { newRank, xpSet, xpNote };
}

export interface SaveSnapshot {
  rank: number | null;
  xp: number | null;
  actionTracker: Map<string, number>;
  messageHistory: Map<string, bigint>;
}

/** Capture a comparable snapshot of every decoded field, for diffing
 * before/after a gotoMilestone run. */
export function snapshot(save: LpwSave): SaveSnapshot {
  return {
    rank: save.certification?.rank ?? null,
    xp: save.certification?.xp ?? null,
    actionTracker: save.actionTracker !== null ? new Map(save.actionTracker) : new Map(),
    messageHistory: save.messageHistory !== null ? new Map(save.messageHistory.primary) : new Map(),
  };
}

export interface SnapshotDiff {
  rankChanged: [number | null, number | null] | null;
  xpChanged: [number | null, number | null] | null;
  actionTrackerRemoved: Array<[string, number]>;
  actionTrackerAdded: Array<[string, number]>;
  actionTrackerChanged: Array<[string, number, number]>;
  messageRemoved: string[];
  messageAdded: string[];
}

export function diffSnapshots(before: SaveSnapshot, after: SaveSnapshot): SnapshotDiff {
  const beforeKeys = new Set(before.actionTracker.keys());
  const afterKeys = new Set(after.actionTracker.keys());

  const actionTrackerRemoved: Array<[string, number]> = [...beforeKeys]
    .filter((k) => !afterKeys.has(k))
    .sort()
    .map((k) => [k, before.actionTracker.get(k)!]);
  const actionTrackerAdded: Array<[string, number]> = [...afterKeys]
    .filter((k) => !beforeKeys.has(k))
    .sort()
    .map((k) => [k, after.actionTracker.get(k)!]);
  const actionTrackerChanged: Array<[string, number, number]> = [...beforeKeys]
    .filter((k) => afterKeys.has(k) && before.actionTracker.get(k) !== after.actionTracker.get(k))
    .sort()
    .map((k) => [k, before.actionTracker.get(k)!, after.actionTracker.get(k)!]);

  const msgBeforeKeys = new Set(before.messageHistory.keys());
  const msgAfterKeys = new Set(after.messageHistory.keys());
  const messageRemoved = [...msgBeforeKeys].filter((k) => !msgAfterKeys.has(k)).sort();
  const messageAdded = [...msgAfterKeys].filter((k) => !msgBeforeKeys.has(k)).sort();

  return {
    rankChanged: before.rank !== after.rank ? [before.rank, after.rank] : null,
    xpChanged: before.xp !== after.xp ? [before.xp, after.xp] : null,
    actionTrackerRemoved,
    actionTrackerAdded,
    actionTrackerChanged,
    messageRemoved,
    messageAdded,
  };
}

export interface FillMilestoneOptions {
  fillMessages?: boolean;
  setXp?: boolean;
}

export interface FillMilestoneResult {
  newRank: number;
  xpSet: number | null;
  xpNote: string;
  patsAdded: string[];
  messagesAdded: string[];
}

/**
 * Inverse of gotoMilestone: patch `save` FORWARD to have completed everything
 * up to and including `target`, copying actual values from `reference` (a
 * known-good, fully-completed save -- data/reference_completed_save.lpw,
 * bundled with the app) rather than fabricating placeholder values.
 *
 * For each milestone in MILESTONE_CHAIN at or before `target` (main-story
 * chain only -- does NOT walk the Act-3 sub-chain, since fill-forward across
 * A3_SC content hasn't been needed/tested yet): if `save` is missing the PAT,
 * copies `reference`'s value for it verbatim (not hardcoded to 1, since a
 * PAT's real completed value can differ -- e.g.
 * PAT_CMP_11_00_RhodesDemoCharges_Complete was seen at 2 in Beltalowda's save,
 * not 1).
 *
 * Unlike the Python CLI (which leaves rank/XP untouched, requiring a separate
 * set-rank call), this bundles the rank/XP bump so a single "Go Forward"
 * click in the UI is a complete action -- lands on the target's own rank via
 * the same floor logic gotoMilestone() uses.
 */
export function fillToMilestone(
  save: LpwSave,
  reference: LpwSave,
  target: string,
  data: MilestoneData,
  certLevels: CertificationLevelEntry[],
  opts: FillMilestoneOptions = {},
): FillMilestoneResult {
  const { fillMessages = true, setXp = true } = opts;

  if (save.actionTracker === null || reference.actionTracker === null) {
    throw new Error("ActionTrackerData not decoded on save and/or reference");
  }

  const names = data.milestoneChain.map((m) => m.name);
  const idx = names.indexOf(target);
  if (idx === -1) {
    throw new Error(`'${target}' not in MILESTONE_CHAIN. Known milestones: ${names.join(", ")}`);
  }
  const targetRank = data.milestoneChain[idx].rank;

  const patsAdded: string[] = [];
  for (const name of names.slice(0, idx + 1)) {
    if (save.actionTracker.has(name)) continue;
    if (reference.actionTracker.has(name)) {
      save.actionTracker.set(name, reference.actionTracker.get(name)!);
      patsAdded.push(name);
    }
  }

  const messagesAdded: string[] = [];
  if (fillMessages && save.messageHistory !== null && reference.messageHistory !== null) {
    const checkpointOrder = [...names, ...data.act3Order];
    const wanted = new Set(
      data.messageMap
        .filter(({ gapStart }) => {
          const gapStartIdx = checkpointOrder.indexOf(gapStart);
          return gapStartIdx !== -1 && gapStartIdx <= idx;
        })
        .map((m) => m.messageName),
    );
    for (const msgName of wanted) {
      if (save.messageHistory.primary.has(msgName)) continue;
      const refTs = reference.messageHistory.primary.get(msgName);
      if (refTs !== undefined) {
        save.messageHistory.primary.set(msgName, refTs);
        messagesAdded.push(msgName);
      }
    }
  }

  // Bump rank to at least the target's own rank -- same floor logic as
  // gotoMilestone(), since a milestone can't be "reached" below its own rank.
  const currentRank = save.certification?.rank ?? 1;
  const newRank = Math.max(currentRank, targetRank);
  if (save.certification !== null) {
    setRank(save, newRank);
  }

  let xpSet: number | null = null;
  let xpNote = "";
  if (setXp && save.certification !== null) {
    const levelEntry = certLevels.find((e) => e.rank === newRank);
    if (levelEntry !== undefined) {
      xpSet = levelEntry.requiredXP - 5.0;
      save.certification.xp = xpSet;
      xpNote = `5 below rank ${newRank}'s own ${levelEntry.requiredXP} threshold`;
    } else {
      xpNote = `certification_levels.json missing rank ${newRank} -- xp left unchanged`;
    }
  }

  return { newRank, xpSet, xpNote, patsAdded, messagesAdded };
}

export interface CompleteMissingResult {
  patAdded: string | null;
  messageAdded: string | null;
}

/**
 * Narrow fix for a single "gap" milestone: the save has already moved past
 * this point (something later in the chain is complete) but this one PAT was
 * never recorded -- e.g. Heidi's real 17_X2 bug. Unlike fillToMilestone, this
 * does NOT walk every prerequisite up to target and does NOT touch rank/XP --
 * the save is already at/beyond this rank, so bumping rank would be wrong.
 * Just copies this one PAT's value (and its mapped message, if any) from the
 * reference save.
 */
export function completeMissingMilestone(
  save: LpwSave,
  reference: LpwSave,
  target: string,
  data: MilestoneData,
): CompleteMissingResult {
  if (save.actionTracker === null || reference.actionTracker === null) {
    throw new Error("ActionTrackerData not decoded on save and/or reference");
  }

  const known = [...data.milestoneChain.map((m) => m.name), ...data.act3Order];
  if (!known.includes(target)) {
    throw new Error(`'${target}' is not a known milestone`);
  }

  let patAdded: string | null = null;
  if (!save.actionTracker.has(target)) {
    const refValue = reference.actionTracker.get(target);
    if (refValue === undefined) {
      throw new Error(`Reference save has no value for '${target}' -- cannot complete it`);
    }
    save.actionTracker.set(target, refValue);
    patAdded = target;
  }

  let messageAdded: string | null = null;
  if (save.messageHistory !== null && reference.messageHistory !== null) {
    const mapEntry = data.messageMap.find((m) => m.gapStart === target);
    if (mapEntry !== undefined && !save.messageHistory.primary.has(mapEntry.messageName)) {
      const refTs = reference.messageHistory.primary.get(mapEntry.messageName);
      if (refTs !== undefined) {
        save.messageHistory.primary.set(mapEntry.messageName, refTs);
        messageAdded = mapEntry.messageName;
      }
    }
  }

  return { patAdded, messageAdded };
}

export type MilestoneStatus = "complete" | "incomplete" | "gap";

export interface MilestoneChecklistEntry {
  name: string;
  rank: number;
  group: "main" | "act3";
  status: MilestoneStatus;
}

/**
 * Build the full milestone checklist (main story chain + Act-3 sub-chain,
 * each its own group) with a per-entry status:
 *   - "complete": the PAT is present in ActionTrackerData.
 *   - "gap": the PAT is ABSENT but an entry LATER in the same group's chain
 *     order IS complete -- i.e. the save has moved past this point without
 *     ever completing it. This is the exact failure signature the whole tool
 *     grew out of (Heidi's real save had several of these -- see
 *     project_industrial_action_investigation memory) and deserves visual
 *     distinction from an ordinary "not reached yet" milestone.
 *   - "incomplete": the PAT is absent and nothing later in the chain is
 *     complete either -- an ordinary "haven't gotten there yet" milestone.
 *
 * Act-3 is walked as its own group/order (act3Order) since it's a separate
 * downstream sub-chain from the main numbered milestones, not interleaved
 * with them in rank order (all 11 Act-3 entries share the same nominal rank,
 * 17, per MILESTONE_CHAIN/project_industrial_action_investigation memory).
 */
export function buildMilestoneChecklist(save: LpwSave, data: MilestoneData): MilestoneChecklistEntry[] {
  const present = save.actionTracker ?? new Map<string, number>();

  function statusesForChain(names: string[]): MilestoneStatus[] {
    const completeFlags = names.map((n) => present.has(n));
    let lastCompleteIdx = -1;
    for (let i = completeFlags.length - 1; i >= 0; i--) {
      if (completeFlags[i]) {
        lastCompleteIdx = i;
        break;
      }
    }
    return completeFlags.map((isComplete, i) => {
      if (isComplete) return "complete";
      return i < lastCompleteIdx ? "gap" : "incomplete";
    });
  }

  const mainNames = data.milestoneChain.map((m) => m.name);
  const mainStatuses = statusesForChain(mainNames);
  const mainEntries: MilestoneChecklistEntry[] = data.milestoneChain.map((m, i) => ({
    name: m.name,
    rank: m.rank,
    group: "main",
    status: mainStatuses[i],
  }));

  const act3Statuses = statusesForChain(data.act3Order);
  // All Act-3 milestones share rank 17 (see MILESTONE_CHAIN -- nothing past
  // 17_X2 changes rank) -- use the main chain's own rank-17 entries as the
  // source of truth rather than hardcoding, in case that ever changes.
  const act3Rank = Math.max(...data.milestoneChain.map((m) => m.rank));
  const act3Entries: MilestoneChecklistEntry[] = data.act3Order.map((name, i) => ({
    name,
    rank: act3Rank,
    group: "act3",
    status: act3Statuses[i],
  }));

  return [...mainEntries, ...act3Entries];
}
