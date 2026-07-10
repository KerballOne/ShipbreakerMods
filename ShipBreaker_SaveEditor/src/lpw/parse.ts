/**
 * Section discovery (byte-scan, not positional) and the LpwSave in-memory model.
 * Ported from SaveEditor/lpw_tool.py's Section/LpwSave dataclasses, parse(),
 * and _scan_section_offsets().
 */
import { HASH_TO_KEY } from "./fnv1a32";

export interface Section {
  key: string;
  version: number;
  /** Offset of the section header (hash+version) in the original file bytes. */
  offset: number;
  /** Raw payload bytes -- decoded sections also keep this for round-trip safety. */
  payload: Buffer;
}

export interface GeneralData {
  profileName: string;
  tutorialCompleted: boolean;
  currentTutorialObjectiveHash: number;
  debtPaidOff: boolean;
  hasPendingShiftExpenses: boolean;
  previousShiftEarnings: number;
  resetOnlineStatsRequired: boolean;
  shiftsUntilPolaris: number;
}

export interface CertificationTierData {
  rank: number;
  tiers: Map<number, number>;
  xp: number;
}

/** MessageData's two count-prefixed (hash, timestamp) lists -- see sections.ts
 * decodeMessageData for the full format writeup. */
export interface MessageDataSections {
  primary: Map<string, bigint>;
  pending: Map<string, bigint>;
}

/** One CurrencyData entry -- see sections.ts decodeCurrencyData. Defined here
 * (not sections.ts) to avoid a circular import, same reasoning as MessageDataSections. */
export interface CurrencyEntry {
  amount: number;
  spentAmount: number;
}

/** DurabilityData's header + per-tool records -- see sections.ts decodeDurabilityData. */
export interface DurabilityHeader {
  thrusterCharge: number;
  field2: number;
  field3: number;
  /** 7 still-unexplained bytes, preserved byte-for-byte. */
  opaque: Buffer;
}
export interface DurabilityRecord {
  toolType: number;
  previous: number;
  current: number;
  max: number;
}
export interface DurabilityData {
  header: DurabilityHeader;
  records: DurabilityRecord[];
}

export interface LpwSave {
  /** Raw bytes from file start up to (not including) the first section. */
  headerPrefix: Buffer;
  sections: Section[];
  /** Anything after the last recognized section (should normally be empty). */
  trailer: Buffer;

  // Decoded views (populated by decodeKnownSections)
  actionTracker: Map<string, number> | null;
  randomSceneHistory: string[] | null;
  generalData: GeneralData | null;
  certification: CertificationTierData | null;
  /** NarrativeMessageAsset name -> Unix epoch seconds. `primary` is the main
   * "first delivered/read" list; `pending` is a second list of unconfirmed
   * semantics found 2026-07-08 (see sections.ts decodeMessageData docstring). */
  messageHistory: MessageDataSections | null;
  /** ProfileDifficultyData's raw 4-byte payload, exposed for direct byte-constant
   * comparison/override (see difficultyMode.ts) -- not decoded into named fields
   * since the format beyond "4 raw bytes = one of 3 known constants" is unknown. */
  difficultyModeBytes: Buffer | null;
  /** PlayerProfile.VoiceIndex -- confirmed 2026-07-10 via live reflection dump. */
  voiceData: number | null;
  /** PlayerProfile.IgnoreOxygenDrain (boolean, stored as one byte) -- confirmed 2026-07-10. */
  oxygenDrainData: number | null;
  /** PlayerProfile.FoodChoice (LynxFoodOption enum ordinal) -- confirmed 2026-07-10; 2=PlasticFree observed. */
  foodChoiceData: number | null;
  /** PlayerProfile.HabSavedPosters -- confirmed 2026-07-10 (exact byte-for-byte match), poster ID per hab wall slot. */
  habData: number[] | null;
  /** PlayerProfile.CurrencyController.Currencies -- confirmed 2026-07-10. See sections.ts decodeCurrencyData. */
  currencyData: Map<string, CurrencyEntry> | null;
  /** PlayerProfile.StoredDurabilityMap + ThrusterCharge -- confirmed 2026-07-10 (records), header partially confirmed. See sections.ts decodeDurabilityData. */
  durabilityData: DurabilityData | null;
}

/**
 * Find every (offset, key, version) by scanning for known section hashes.
 *
 * This is a brute-force scan rather than a positional header parse, because the
 * exact header layout preceding the first section was never fully reverse-
 * engineered. Scanning for the hash marker is reliable because FNV-1a32
 * collisions among our 22 known keys are not observed in practice. Do NOT
 * "clean this up" into a positional parser -- this is the confirmed-working
 * strategy from the Python tool.
 */
function scanSectionOffsets(data: Buffer): Array<{ offset: number; key: string; version: number }> {
  const found: Array<{ offset: number; key: string; version: number }> = [];
  const n = data.length;
  for (let i = 0; i < n - 8; i++) {
    const val = data.readUInt32LE(i);
    const key = HASH_TO_KEY.get(val);
    if (key !== undefined) {
      const ver = data.readUInt32LE(i + 4);
      found.push({ offset: i, key, version: ver });
    }
  }
  return found;
}

export function parse(data: Buffer): LpwSave {
  const offsets = scanSectionOffsets(data);
  if (offsets.length === 0) {
    throw new Error("No recognized sections found -- not a valid .lpw file?");
  }
  // Not every one of the 23 known DataKeys is written in every file -- observed
  // saves consistently included 19 of them and omitted HasPlayedBefore,
  // PastProfilesData, CompletedCertificationsData, CurrentCertificationProgessData.
  // Only warn if the count is surprisingly low, suggesting real truncation/corruption.
  if (offsets.length < 15) {
    // eslint-disable-next-line no-console
    console.warn(
      `warning: only found ${offsets.length} sections (expected ~19-23) -- file may be truncated or corrupted`,
    );
  }

  const headerPrefix = data.subarray(0, offsets[0].offset);

  const sections: Section[] = [];
  for (let idx = 0; idx < offsets.length; idx++) {
    const { offset, key, version } = offsets[idx];
    const payloadStart = offset + 8;
    const payloadEnd = idx + 1 < offsets.length ? offsets[idx + 1].offset : data.length;
    const payload = data.subarray(payloadStart, payloadEnd);
    sections.push({ key, version, offset, payload });
  }

  return {
    headerPrefix,
    sections,
    trailer: Buffer.alloc(0),
    actionTracker: null,
    randomSceneHistory: null,
    generalData: null,
    certification: null,
    messageHistory: null,
    difficultyModeBytes: null,
    voiceData: null,
    oxygenDrainData: null,
    foodChoiceData: null,
    habData: null,
    currencyData: null,
    durabilityData: null,
  };
}
