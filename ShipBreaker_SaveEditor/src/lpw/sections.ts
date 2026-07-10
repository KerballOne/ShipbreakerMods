// Decode/encode for the structured sections: ActionTrackerData, RandomSceneHistory,
// GeneralData, CertificationTierData, MessageData. Ported from
// SaveEditor/lpw_tool.py's _decode_*/_encode_* functions.
//
// MessageData timestamps use bigint (Int64) since JS number cannot safely
// represent all int64 values. bigint is used throughout for correctness.
import type { AssetKeyMap } from "./keymap";
import type { GeneralData, CertificationTierData, MessageDataSections } from "./parse";
import { read7BitLen, write7BitLen } from "./binary";

export function decodeActionTracker(payload: Buffer, keymap: AssetKeyMap): Map<string, number> {
  let p = 0;
  const count = payload.readInt32LE(p);
  p += 4;
  const result = new Map<string, number>();
  for (let i = 0; i < count; i++) {
    const h = payload.readUInt32LE(p);
    p += 4;
    const v = payload.readInt32LE(p);
    p += 4;
    result.set(keymap.resolve(h), v);
  }
  return result;
}

export function encodeActionTracker(entries: Map<string, number>, keymap: AssetKeyMap): Buffer {
  const out = Buffer.alloc(4 + entries.size * 8);
  out.writeInt32LE(entries.size, 0);
  let p = 4;
  for (const [name, value] of entries) {
    out.writeUInt32LE(keymap.hashOf(name), p);
    p += 4;
    out.writeInt32LE(value, p);
    p += 4;
  }
  return out;
}

export function decodeSceneHistory(payload: Buffer, keymap: AssetKeyMap): string[] {
  let p = 0;
  const count = payload.readInt32LE(p);
  p += 4;
  const result: string[] = [];
  for (let i = 0; i < count; i++) {
    const h = payload.readUInt32LE(p);
    p += 4;
    result.push(keymap.resolve(h));
  }
  return result;
}

/**
 * MessageData: count(int32) + count * (hash:uint32, unixEpochSeconds:int64).
 *
 * Per-NarrativeMessageAsset "first delivered/read" timestamp table, entirely
 * separate from ActionTrackerData's PAT completion flags. Critically, this
 * section is untouched by setRank()/gotoMilestone() unless explicitly told to
 * strip it -- a message's delivery record can survive a full ActionTrackerData
 * wipe and its read-PAT re-posting can bypass the intended linear story gate.
 *
 * SECOND LIST (format confirmed 2026-07-08 during the TS port, superseding the
 * Python tool's docstring which only speculated "likely an empty second
 * count-prefixed list ... preserved as trailer but not itself decoded pending a
 * save with a nonzero value there"): a NEVARMOAR fixture turned up exactly that
 * nonzero case -- a second count(int32)-prefixed list of the SAME
 * (hash:uint32, unixEpochSeconds:int64) entry shape, all 3 entries sharing one
 * timestamp matching when that save was captured. Working theory (unconfirmed
 * against game code): a "delivered this session" / pending-notification queue,
 * separate from the main list's presumed "first read" semantics. Decoded here
 * as a second Map so it round-trips correctly; not yet exposed through
 * gotoMilestone's message-stripping logic since its semantics relative to
 * MESSAGE_MAP are unconfirmed -- treat it as opaque pass-through data for any
 * write path until that's understood.
 */

function decodeTimestampList(payload: Buffer, pos: number, keymap: AssetKeyMap): { list: Map<string, bigint>; newPos: number } {
  let p = pos;
  const count = payload.readInt32LE(p);
  p += 4;
  const result = new Map<string, bigint>();
  for (let i = 0; i < count; i++) {
    const h = payload.readUInt32LE(p);
    p += 4;
    const ts = payload.readBigInt64LE(p);
    p += 8;
    result.set(keymap.resolve(h), ts);
  }
  return { list: result, newPos: p };
}

function encodeTimestampList(entries: Map<string, bigint>, keymap: AssetKeyMap): Buffer {
  const out = Buffer.alloc(4 + entries.size * 12);
  out.writeInt32LE(entries.size, 0);
  let p = 4;
  for (const [name, ts] of entries) {
    out.writeUInt32LE(keymap.hashOf(name), p);
    p += 4;
    out.writeBigInt64LE(ts, p);
    p += 8;
  }
  return out;
}

export function decodeMessageData(payload: Buffer, keymap: AssetKeyMap): MessageDataSections {
  const { list: primary, newPos } = decodeTimestampList(payload, 0, keymap);
  // Some saves end exactly after the primary list with no second list at all
  // (not even a 4-byte zero count) -- guard against reading past the payload.
  if (newPos >= payload.length) {
    return { primary, pending: new Map() };
  }
  const { list: pending } = decodeTimestampList(payload, newPos, keymap);
  return { primary, pending };
}

export function encodeMessageData(sections: MessageDataSections, keymap: AssetKeyMap): Buffer {
  return Buffer.concat([
    encodeTimestampList(sections.primary, keymap),
    encodeTimestampList(sections.pending, keymap),
  ]);
}

export function decodeGeneralData(payload: Buffer): GeneralData {
  let p = 0;
  const { value: strlen, newPos } = read7BitLen(payload, p);
  p = newPos;
  const profileName = payload.subarray(p, p + strlen).toString("utf8");
  p += strlen;
  const tutorialCompleted = payload[p] !== 0;
  p += 1;
  const currentTutorialObjectiveHash = payload.readUInt32LE(p);
  p += 4;
  const debtPaidOff = payload[p] !== 0;
  p += 1;
  const hasPendingShiftExpenses = payload[p] !== 0;
  p += 1;
  const previousShiftEarnings = payload.readFloatLE(p);
  p += 4;
  const resetOnlineStatsRequired = payload[p] !== 0;
  p += 1;
  const shiftsUntilPolaris = payload.readInt32LE(p);
  p += 4;
  return {
    profileName,
    tutorialCompleted,
    currentTutorialObjectiveHash,
    debtPaidOff,
    hasPendingShiftExpenses,
    previousShiftEarnings,
    resetOnlineStatsRequired,
    shiftsUntilPolaris,
  };
}

export function encodeGeneralData(gd: GeneralData): Buffer {
  const nameBytes = Buffer.from(gd.profileName, "utf8");
  const lenPrefix = write7BitLen(nameBytes.length);
  const out = Buffer.alloc(lenPrefix.length + nameBytes.length + 1 + 4 + 1 + 1 + 4 + 1 + 4);
  let p = 0;
  lenPrefix.copy(out, p);
  p += lenPrefix.length;
  nameBytes.copy(out, p);
  p += nameBytes.length;
  out.writeUInt8(gd.tutorialCompleted ? 1 : 0, p);
  p += 1;
  out.writeUInt32LE(gd.currentTutorialObjectiveHash, p);
  p += 4;
  out.writeUInt8(gd.debtPaidOff ? 1 : 0, p);
  p += 1;
  out.writeUInt8(gd.hasPendingShiftExpenses ? 1 : 0, p);
  p += 1;
  out.writeFloatLE(gd.previousShiftEarnings, p);
  p += 4;
  out.writeUInt8(gd.resetOnlineStatsRequired ? 1 : 0, p);
  p += 1;
  out.writeInt32LE(gd.shiftsUntilPolaris, p);
  return out;
}

export function decodeCertification(payload: Buffer): CertificationTierData {
  let p = 0;
  const rank = payload.readInt32LE(p);
  p += 4;
  const count = payload.readInt32LE(p);
  p += 4;
  const tiers = new Map<number, number>();
  for (let i = 0; i < count; i++) {
    const ctype = payload.readInt32LE(p);
    p += 4;
    const val = payload.readInt32LE(p);
    p += 4;
    tiers.set(ctype, val);
  }
  const xp = payload.readFloatLE(p);
  return { rank, tiers, xp };
}

export function encodeCertification(cert: CertificationTierData): Buffer {
  const out = Buffer.alloc(4 + 4 + cert.tiers.size * 8 + 4);
  out.writeInt32LE(cert.rank, 0);
  out.writeInt32LE(cert.tiers.size, 4);
  let p = 8;
  for (const [ctype, val] of cert.tiers) {
    out.writeInt32LE(ctype, p);
    p += 4;
    out.writeInt32LE(val, p);
    p += 4;
  }
  out.writeFloatLE(cert.xp, p);
  return out;
}

/**
 * VoiceData: a single int32 (4 bytes total). Observed values 0 and 2 across
 * 5 real saves -- consistent with a small enum (voice-option index), exact
 * meaning of each value not confirmed against in-game labels.
 */
export function decodeVoiceData(payload: Buffer): number {
  return payload.readInt32LE(0);
}
export function encodeVoiceData(value: number): Buffer {
  const out = Buffer.alloc(4);
  out.writeInt32LE(value, 0);
  return out;
}

/**
 * OxygenDrainData: a single byte (1 byte total). Always observed as 0 across
 * 5 real saves -- no variance seen yet to confirm whether it's a boolean
 * flag or something else; decoded as a raw byte value (0-255) rather than
 * assumed-boolean, since the true meaning is unconfirmed.
 */
export function decodeOxygenDrainData(payload: Buffer): number {
  return payload.readUInt8(0);
}
export function encodeOxygenDrainData(value: number): Buffer {
  const out = Buffer.alloc(1);
  out.writeUInt8(value, 0);
  return out;
}

/**
 * FoodChoiceData: a single int32 (4 bytes total). Observed values 1 and 2
 * across 5 real saves -- consistent with a small enum (starting food
 * choice), exact meaning of each value not confirmed against in-game labels.
 */
export function decodeFoodChoiceData(payload: Buffer): number {
  return payload.readInt32LE(0);
}
export function encodeFoodChoiceData(value: number): Buffer {
  const out = Buffer.alloc(4);
  out.writeInt32LE(value, 0);
  return out;
}

/**
 * HabData: count(int32) + count * int32 -- same "count-prefixed int32 list"
 * shape as several other sections. Always 7 entries across 5 real saves;
 * values differ meaningfully between saves (e.g. one fixture: 18,20,14,15,
 * 13,16,18; three others sharing: 1,2,3,5,2,3,4) -- likely per-slot indices
 * for hab decoration/customization, exact per-slot meaning unconfirmed.
 * Decoded as a plain number[] (not a Map) since there's no evidence the
 * values are keyed by anything -- position IS the key.
 */
export function decodeHabData(payload: Buffer): number[] {
  let p = 0;
  const count = payload.readInt32LE(p);
  p += 4;
  const result: number[] = [];
  for (let i = 0; i < count; i++) {
    result.push(payload.readInt32LE(p));
    p += 4;
  }
  return result;
}
export function encodeHabData(values: number[]): Buffer {
  const out = Buffer.alloc(4 + values.length * 4);
  out.writeInt32LE(values.length, 0);
  let p = 4;
  for (const v of values) {
    out.writeInt32LE(v, p);
    p += 4;
  }
  return out;
}
