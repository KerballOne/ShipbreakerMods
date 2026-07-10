/**
 * JSON view of an LpwSave's decoded fields, for the "Advanced mode" raw text
 * editor. Editing goes through this JSON representation rather than raw
 * bytes -- toAdvancedJson()/fromAdvancedJson() are the only conversion point,
 * and applying an edit still goes through the same rebuild() every other
 * write path uses, so it inherits the same round-trip safety instead of
 * reinventing it.
 *
 * Only fields with both a decoder AND an encoder are exposed (generalData,
 * certification, actionTracker, messageHistory, difficultyModeBytes,
 * voiceData, oxygenDrainData, foodChoiceData, habData). The last 4 were
 * reverse-engineered from scratch across 5 real saves -- their byte layout
 * (single int32 / single byte / count-prefixed int32 list) is confirmed via
 * byte-identical round-trip, but the semantic MEANING of specific values is
 * NOT confirmed against in-game labels (see each decode function's own
 * docstring in sections.ts for what's actually been observed).
 * randomSceneHistory is decoded but has no encoder (see sections.ts) -- it's
 * included read-only for visibility; submitting a changed value is rejected.
 * Any other undecoded/unknown section (AvailableShipsData, AbilitiesData,
 * CurrencyData, etc.) is not shown at all and is untouched by rebuild()'s
 * raw-payload passthrough regardless.
 *
 * MessageData timestamps are bigint (int64) -- JSON has no bigint type, so
 * they're serialized as decimal strings and parsed back with BigInt(), never
 * routed through JS `number` (which cannot represent the full int64 range
 * exactly).
 */
import type { LpwSave } from "./parse";
import type { AssetKeyMap } from "./keymap";
import { resolveDifficultyMode, type DifficultyMode } from "./difficultyMode";

export interface AdvancedJson {
  generalData: {
    profileName: string;
    tutorialCompleted: boolean;
    currentTutorialObjectiveHash: number;
    debtPaidOff: boolean;
    hasPendingShiftExpenses: boolean;
    previousShiftEarnings: number;
    resetOnlineStatsRequired: boolean;
    shiftsUntilPolaris: number;
  } | null;
  certification: {
    rank: number;
    xp: number;
    tiers: Record<string, number>;
  } | null;
  actionTracker: Record<string, number> | null;
  messageHistory: {
    primary: Record<string, string>;
    pending: Record<string, string>;
  } | null;
  difficultyMode: {
    name: string | null;
    bytesHex: string;
  } | null;
  /** Read-only -- no encoder exists for this section. See module docstring. */
  randomSceneHistory: string[] | null;
  /** Meaning unconfirmed -- see sections.ts decodeVoiceData. */
  voiceData: number | null;
  /** Meaning unconfirmed -- see sections.ts decodeOxygenDrainData. */
  oxygenDrainData: number | null;
  /** Meaning unconfirmed -- see sections.ts decodeFoodChoiceData. */
  foodChoiceData: number | null;
  /** Per-slot meaning unconfirmed -- see sections.ts decodeHabData. */
  habData: number[] | null;
}

export function toAdvancedJson(save: LpwSave, modes: DifficultyMode[]): AdvancedJson {
  return {
    generalData: save.generalData === null ? null : { ...save.generalData },
    certification:
      save.certification === null
        ? null
        : {
            rank: save.certification.rank,
            xp: save.certification.xp,
            tiers: Object.fromEntries([...save.certification.tiers].map(([k, v]) => [String(k), v])),
          },
    actionTracker: save.actionTracker === null ? null : Object.fromEntries(save.actionTracker),
    messageHistory:
      save.messageHistory === null
        ? null
        : {
            primary: Object.fromEntries([...save.messageHistory.primary].map(([k, v]) => [k, v.toString()])),
            pending: Object.fromEntries([...save.messageHistory.pending].map(([k, v]) => [k, v.toString()])),
          },
    difficultyMode:
      save.difficultyModeBytes === null
        ? null
        : {
            name: resolveDifficultyMode(save, modes),
            bytesHex: save.difficultyModeBytes.toString("hex").toUpperCase(),
          },
    randomSceneHistory: save.randomSceneHistory,
    voiceData: save.voiceData,
    oxygenDrainData: save.oxygenDrainData,
    foodChoiceData: save.foodChoiceData,
    habData: save.habData === null ? null : [...save.habData],
  };
}

function assertPlainObject(value: unknown, path: string): asserts value is Record<string, unknown> {
  if (typeof value !== "object" || value === null || Array.isArray(value)) {
    throw new Error(`Expected an object at '${path}'`);
  }
}

/**
 * Parse and validate an edited AdvancedJson blob, then apply it onto `save`
 * in place (mutating the same decoded fields rebuild() reads from). Throws a
 * descriptive Error on any structural problem or unknown asset name --
 * callers should treat any thrown error as "reject the edit, keep the file
 * unchanged" (no partial application: validation runs fully against a plain
 * parsed object before anything is written onto `save`).
 */
export function fromAdvancedJson(save: LpwSave, keymap: AssetKeyMap, modes: DifficultyMode[], raw: unknown): void {
  assertPlainObject(raw, "$");
  const data = raw as Record<string, unknown>;

  // randomSceneHistory is read-only -- reject if the submitted value differs
  // at all from what was originally exported (catches "edited a value that
  // silently can't be saved" before it's too late, since there's no encoder).
  if ("randomSceneHistory" in data) {
    const submitted = data.randomSceneHistory;
    const original = save.randomSceneHistory;
    const same =
      (submitted === null && original === null) ||
      (Array.isArray(submitted) &&
        original !== null &&
        submitted.length === original.length &&
        submitted.every((v, i) => v === original[i]));
    if (!same) {
      throw new Error("'randomSceneHistory' is read-only in this version -- it cannot be edited (no encoder exists for this section). Revert it to its original value to save your other changes.");
    }
  }

  let newGeneralData: LpwSave["generalData"] = save.generalData;
  if ("generalData" in data && data.generalData !== null) {
    assertPlainObject(data.generalData, "generalData");
    const gd = data.generalData;
    for (const key of [
      "profileName",
      "tutorialCompleted",
      "currentTutorialObjectiveHash",
      "debtPaidOff",
      "hasPendingShiftExpenses",
      "previousShiftEarnings",
      "resetOnlineStatsRequired",
      "shiftsUntilPolaris",
    ]) {
      if (!(key in gd)) throw new Error(`generalData is missing required field '${key}'`);
    }
    if (typeof gd.profileName !== "string") throw new Error("generalData.profileName must be a string");
    if (typeof gd.tutorialCompleted !== "boolean") throw new Error("generalData.tutorialCompleted must be a boolean");
    if (typeof gd.currentTutorialObjectiveHash !== "number" || !Number.isInteger(gd.currentTutorialObjectiveHash)) {
      throw new Error("generalData.currentTutorialObjectiveHash must be an integer");
    }
    if (typeof gd.debtPaidOff !== "boolean") throw new Error("generalData.debtPaidOff must be a boolean");
    if (typeof gd.hasPendingShiftExpenses !== "boolean") throw new Error("generalData.hasPendingShiftExpenses must be a boolean");
    if (typeof gd.previousShiftEarnings !== "number") throw new Error("generalData.previousShiftEarnings must be a number");
    if (typeof gd.resetOnlineStatsRequired !== "boolean") throw new Error("generalData.resetOnlineStatsRequired must be a boolean");
    if (typeof gd.shiftsUntilPolaris !== "number" || !Number.isInteger(gd.shiftsUntilPolaris)) {
      throw new Error("generalData.shiftsUntilPolaris must be an integer");
    }
    newGeneralData = {
      profileName: gd.profileName,
      tutorialCompleted: gd.tutorialCompleted,
      currentTutorialObjectiveHash: gd.currentTutorialObjectiveHash,
      debtPaidOff: gd.debtPaidOff,
      hasPendingShiftExpenses: gd.hasPendingShiftExpenses,
      previousShiftEarnings: gd.previousShiftEarnings,
      resetOnlineStatsRequired: gd.resetOnlineStatsRequired,
      shiftsUntilPolaris: gd.shiftsUntilPolaris,
    };
  }

  let newCertification: LpwSave["certification"] = save.certification;
  if ("certification" in data && data.certification !== null) {
    assertPlainObject(data.certification, "certification");
    const cert = data.certification;
    if (typeof cert.rank !== "number" || !Number.isInteger(cert.rank)) throw new Error("certification.rank must be an integer");
    if (typeof cert.xp !== "number") throw new Error("certification.xp must be a number");
    assertPlainObject(cert.tiers, "certification.tiers");
    const tiers = new Map<number, number>();
    for (const [k, v] of Object.entries(cert.tiers)) {
      const ctype = Number(k);
      if (!Number.isInteger(ctype)) throw new Error(`certification.tiers key '${k}' must be an integer`);
      if (typeof v !== "number" || !Number.isInteger(v)) throw new Error(`certification.tiers['${k}'] must be an integer`);
      tiers.set(ctype, v);
    }
    newCertification = { rank: cert.rank, xp: cert.xp, tiers };
  }

  let newActionTracker: LpwSave["actionTracker"] = save.actionTracker;
  if ("actionTracker" in data && data.actionTracker !== null) {
    assertPlainObject(data.actionTracker, "actionTracker");
    const tracker = new Map<string, number>();
    for (const [name, v] of Object.entries(data.actionTracker)) {
      if (typeof v !== "number" || !Number.isInteger(v)) throw new Error(`actionTracker['${name}'] must be an integer`);
      keymap.hashOf(name); // throws a descriptive error if the name is unknown
      tracker.set(name, v);
    }
    newActionTracker = tracker;
  }

  let newMessageHistory: LpwSave["messageHistory"] = save.messageHistory;
  if ("messageHistory" in data && data.messageHistory !== null) {
    assertPlainObject(data.messageHistory, "messageHistory");
    const mh = data.messageHistory;
    assertPlainObject(mh.primary, "messageHistory.primary");
    assertPlainObject(mh.pending, "messageHistory.pending");
    function parseTimestampMap(obj: Record<string, unknown>, path: string): Map<string, bigint> {
      const result = new Map<string, bigint>();
      for (const [name, v] of Object.entries(obj)) {
        if (typeof v !== "string" || !/^-?\d+$/.test(v)) {
          throw new Error(`${path}['${name}'] must be a decimal string (int64 timestamp)`);
        }
        keymap.hashOf(name);
        result.set(name, BigInt(v));
      }
      return result;
    }
    newMessageHistory = {
      primary: parseTimestampMap(mh.primary, "messageHistory.primary"),
      pending: parseTimestampMap(mh.pending, "messageHistory.pending"),
    };
  }

  let newDifficultyModeBytes: LpwSave["difficultyModeBytes"] = save.difficultyModeBytes;
  if ("difficultyMode" in data && data.difficultyMode !== null) {
    assertPlainObject(data.difficultyMode, "difficultyMode");
    const dm = data.difficultyMode;
    if (typeof dm.bytesHex !== "string" || !/^[0-9a-fA-F]{8}$/.test(dm.bytesHex)) {
      throw new Error("difficultyMode.bytesHex must be an 8-character hex string (4 bytes)");
    }
    newDifficultyModeBytes = Buffer.from(dm.bytesHex, "hex");
    // Not required to match a known mode in `modes` -- an unrecognized value
    // is allowed through (surfaces as difficultyMode: null on next load) so
    // advanced mode can still round-trip a save with a mode this tool
    // doesn't have a name for yet, rather than hard-blocking it.
    void modes;
  }

  let newVoiceData: LpwSave["voiceData"] = save.voiceData;
  if ("voiceData" in data && data.voiceData !== null) {
    if (typeof data.voiceData !== "number" || !Number.isInteger(data.voiceData)) {
      throw new Error("voiceData must be an integer");
    }
    newVoiceData = data.voiceData;
  }

  let newOxygenDrainData: LpwSave["oxygenDrainData"] = save.oxygenDrainData;
  if ("oxygenDrainData" in data && data.oxygenDrainData !== null) {
    if (typeof data.oxygenDrainData !== "number" || !Number.isInteger(data.oxygenDrainData) || data.oxygenDrainData < 0 || data.oxygenDrainData > 255) {
      throw new Error("oxygenDrainData must be an integer between 0 and 255 (single byte)");
    }
    newOxygenDrainData = data.oxygenDrainData;
  }

  let newFoodChoiceData: LpwSave["foodChoiceData"] = save.foodChoiceData;
  if ("foodChoiceData" in data && data.foodChoiceData !== null) {
    if (typeof data.foodChoiceData !== "number" || !Number.isInteger(data.foodChoiceData)) {
      throw new Error("foodChoiceData must be an integer");
    }
    newFoodChoiceData = data.foodChoiceData;
  }

  let newHabData: LpwSave["habData"] = save.habData;
  if ("habData" in data && data.habData !== null) {
    if (!Array.isArray(data.habData)) throw new Error("habData must be an array");
    for (const v of data.habData) {
      if (typeof v !== "number" || !Number.isInteger(v)) throw new Error("habData entries must all be integers");
    }
    newHabData = data.habData as number[];
  }

  // Nothing thrown -- apply everything at once.
  save.generalData = newGeneralData;
  save.certification = newCertification;
  save.actionTracker = newActionTracker;
  save.messageHistory = newMessageHistory;
  save.difficultyModeBytes = newDifficultyModeBytes;
  save.voiceData = newVoiceData;
  save.oxygenDrainData = newOxygenDrainData;
  save.foodChoiceData = newFoodChoiceData;
  save.habData = newHabData;
}
