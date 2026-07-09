/**
 * FNV-1a 32-bit hash + the registered save-data section keys
 * (BBI.Unity.Game PlayerProfileSaveLoadManager.cs). Ported from
 * SaveEditor/lpw_tool.py.
 */

export const FNV_OFFSET_BASIS = 2166136261;
export const FNV_PRIME = 16777619;

/** 32-bit FNV-1a over the UTF-8 bytes of `s`. Result is forced unsigned via
 * `>>> 0` since JS bitwise/Math multiply intermediate values are 32-bit signed. */
export function fnv1a32(s: string, seed: number = FNV_OFFSET_BASIS): number {
  let h = seed >>> 0;
  const bytes = Buffer.from(s, "utf8");
  for (const b of bytes) {
    h ^= b;
    // (h * FNV_PRIME) & 0xFFFFFFFF -- JS Math.imul gives correct 32-bit
    // wraparound multiplication without precision loss from float multiply.
    h = Math.imul(h, FNV_PRIME) >>> 0;
  }
  return h >>> 0;
}

/** The 22 registered PlayerProfile save-data section names. Order matches the
 * Python source's DATA_KEYS list (not meaningful for lookups, kept for parity). */
export const DATA_KEYS: readonly string[] = [
  "AbilitiesData", "ActionTrackerData", "AvailableShipsData", "CertificationTierData",
  "CurrencyData", "DurabilityData", "GeneralData", "NarrativeData", "UpgradeData",
  "ProfileDifficultyData", "CurrentCertificationProgessData", "PastProfilesData",
  "CompletedCertificationsData", "HasPlayedBefore", "VoiceData", "OxygenDrainData",
  "MessageData", "SpaceTruckState", "RandomSceneHistory", "StickerCollectionData",
  "StickerPlacementData", "FoodChoiceData", "HabData",
];

export const HASH_TO_KEY: ReadonlyMap<number, string> = new Map(
  DATA_KEYS.map((k) => [fnv1a32(k), k]),
);
export const KEY_TO_HASH: ReadonlyMap<string, number> = new Map(
  DATA_KEYS.map((k) => [k, fnv1a32(k)]),
);

/** Sections with a structured decode/encode (raw payload bytes are also always
 * retained for these, per the round-trip discipline in rebuild.ts). */
export const DECODED_KEYS: ReadonlySet<string> = new Set([
  "ActionTrackerData",
  "RandomSceneHistory",
  "GeneralData",
  "CertificationTierData",
  "MessageData",
  "ProfileDifficultyData",
]);
