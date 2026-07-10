/**
 * decodeKnownSections(), rebuild(), load(), saveTo(). Ported from
 * SaveEditor/lpw_tool.py's decode_known_sections()/rebuild()/load()/save_to().
 */
import * as fs from "node:fs";
import { KEY_TO_HASH } from "./fnv1a32";
import { parse, type LpwSave } from "./parse";
import type { AssetKeyMap } from "./keymap";
import {
  decodeActionTracker,
  decodeSceneHistory,
  decodeGeneralData,
  decodeCertification,
  decodeMessageData,
  decodeVoiceData,
  decodeOxygenDrainData,
  decodeFoodChoiceData,
  decodeHabData,
  encodeActionTracker,
  encodeCertification,
  encodeGeneralData,
  encodeMessageData,
  encodeVoiceData,
  encodeOxygenDrainData,
  encodeFoodChoiceData,
  encodeHabData,
} from "./sections";

/** Populate the decoded convenience fields on an LpwSave in place. */
export function decodeKnownSections(save: LpwSave, keymap: AssetKeyMap): void {
  for (const sec of save.sections) {
    switch (sec.key) {
      case "ActionTrackerData":
        save.actionTracker = decodeActionTracker(sec.payload, keymap);
        break;
      case "RandomSceneHistory":
        save.randomSceneHistory = decodeSceneHistory(sec.payload, keymap);
        break;
      case "GeneralData":
        save.generalData = decodeGeneralData(sec.payload);
        break;
      case "CertificationTierData":
        save.certification = decodeCertification(sec.payload);
        break;
      case "MessageData":
        save.messageHistory = decodeMessageData(sec.payload, keymap);
        break;
      case "ProfileDifficultyData":
        // Not decoded into named fields (format beyond "4 raw bytes = one of 3
        // known constants" is unknown) -- exposed as a raw buffer for direct
        // byte-constant comparison/override, see difficultyMode.ts.
        save.difficultyModeBytes = Buffer.from(sec.payload);
        break;
      case "VoiceData":
        save.voiceData = decodeVoiceData(sec.payload);
        break;
      case "OxygenDrainData":
        save.oxygenDrainData = decodeOxygenDrainData(sec.payload);
        break;
      case "FoodChoiceData":
        save.foodChoiceData = decodeFoodChoiceData(sec.payload);
        break;
      case "HabData":
        save.habData = decodeHabData(sec.payload);
        break;
      default:
        break;
    }
  }
}

/**
 * Reassemble a full .lpw file from an LpwSave, using decoded fields where
 * present (actionTracker / certification / generalData / messageHistory /
 * difficultyModeBytes) and raw payload bytes otherwise.
 *
 * Call decodeKnownSections() first if you intend to edit any of those fields --
 * otherwise the edits won't be picked up, since this function only re-encodes
 * fields that were decoded (mirrors the Python tool's exact per-section
 * conditional logic -- do not change this to "always re-encode," since
 * RandomSceneHistory intentionally has no encoder and must always pass through
 * as raw bytes).
 */
export function rebuild(save: LpwSave, keymap: AssetKeyMap): Buffer {
  const parts: Buffer[] = [save.headerPrefix];
  for (const sec of save.sections) {
    let payload: Buffer;
    if (sec.key === "ActionTrackerData" && save.actionTracker !== null) {
      payload = encodeActionTracker(save.actionTracker, keymap);
    } else if (sec.key === "CertificationTierData" && save.certification !== null) {
      payload = encodeCertification(save.certification);
    } else if (sec.key === "GeneralData" && save.generalData !== null) {
      payload = encodeGeneralData(save.generalData);
    } else if (sec.key === "MessageData" && save.messageHistory !== null) {
      payload = encodeMessageData(save.messageHistory, keymap);
    } else if (sec.key === "ProfileDifficultyData" && save.difficultyModeBytes !== null) {
      payload = save.difficultyModeBytes;
    } else if (sec.key === "VoiceData" && save.voiceData !== null) {
      payload = encodeVoiceData(save.voiceData);
    } else if (sec.key === "OxygenDrainData" && save.oxygenDrainData !== null) {
      payload = encodeOxygenDrainData(save.oxygenDrainData);
    } else if (sec.key === "FoodChoiceData" && save.foodChoiceData !== null) {
      payload = encodeFoodChoiceData(save.foodChoiceData);
    } else if (sec.key === "HabData" && save.habData !== null) {
      payload = encodeHabData(save.habData);
    } else {
      payload = sec.payload;
    }
    const header = Buffer.alloc(8);
    const hash = KEY_TO_HASH.get(sec.key);
    if (hash === undefined) {
      throw new Error(`Unknown section key '${sec.key}' -- not in DATA_KEYS`);
    }
    header.writeUInt32LE(hash, 0);
    header.writeUInt32LE(sec.version, 4);
    parts.push(header, payload);
  }
  parts.push(save.trailer);
  return Buffer.concat(parts);
}

export function load(path: string, keymap: AssetKeyMap): LpwSave {
  const data = fs.readFileSync(path);
  const save = parse(data);
  decodeKnownSections(save, keymap);
  return save;
}

export function saveTo(save: LpwSave, keymap: AssetKeyMap, path: string): void {
  const data = rebuild(save, keymap);
  fs.writeFileSync(path, data);
}
