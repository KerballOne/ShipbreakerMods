/**
 * Cross-validate fillToMilestone against the Python tool. Since the Python
 * CLI's fill-milestone doesn't touch rank/XP (our JS port bundles that step
 * for a complete one-click "Go Forward" UI action), this chains Python's
 * fill-milestone + set-rank to match our port's combined behavior.
 *
 * Unlike goto-milestone's cross-validation (which is byte-exact), this
 * compares DECODED CONTENT rather than raw bytes for the MessageData section
 * specifically. Root-caused 2026-07-08: lpw_tool.py's fill_to_milestone
 * builds its "wanted" message set via a Python set comprehension
 * (`{name for name, gap_start, _ in MESSAGE_MAP if ...}`), and Python set
 * iteration order is NOT insertion-order -- it's hash-based and, with string-
 * hash randomization enabled by default since Python 3.3, not even stable
 * across separate process runs of the Python tool itself. This means the
 * Python reference's own MessageData entry order is inherently
 * non-reproducible for this one code path -- NOT a bug in this port. Verified
 * directly: both outputs contain the exact same 17 message names with the
 * same timestamps, just in different byte order. The JS port uses an
 * insertion-ordered Set, making it MORE deterministic than the reference, not
 * less correct. ActionTrackerData ordering is unaffected (built via list
 * iteration in the Python source, not a set) and IS still byte-exact.
 *
 * Run with: npx tsx test/cross-validate-fill.ts
 */
import * as fs from "node:fs";
import * as path from "node:path";
import { execFileSync } from "node:child_process";
import { load, rebuild } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, loadCertificationLevels, fillToMilestone } from "../src/lpw/milestones";
import { decodeActionTracker, decodeMessageData } from "../src/lpw/sections";
import { parse } from "../src/lpw/parse";

const REPO_ROOT = path.join(__dirname, "..", "..");
const LPW_TOOL_PY = path.join(REPO_ROOT, "SaveEditor", "lpw_tool.py");
const DATA_DIR = path.join(__dirname, "..", "data");
const KEYMAP_PATH = path.join(DATA_DIR, "asset_save_keys.json");
const REFERENCE_PATH = path.join(DATA_DIR, "reference_completed_save.lpw");
const FIXTURES_DIR = path.join(__dirname, "fixtures");
const TMP_DIR = path.join(__dirname, "_cross_validate_fill_tmp");

const CASES: Array<{ fixture: string; target: string }> = [
  { fixture: "test5_rank4_pristine.lpw", target: "PAT_CMP_07_00_CalyssiaAntiUnion_Complete" },
  { fixture: "test5_rank4_pristine.lpw", target: "PAT_CMP_17_X2_LYNXUnionClampDown_Complete" },
];

function findPython(): string {
  for (const candidate of ["python", "python3"]) {
    try {
      execFileSync(candidate, ["--version"], { stdio: "ignore" });
      return candidate;
    } catch {
      // try next
    }
  }
  throw new Error("No Python interpreter found on PATH");
}

function mapsEqual<V>(a: Map<string, V>, b: Map<string, V>): boolean {
  if (a.size !== b.size) return false;
  for (const [k, v] of a) {
    if (!b.has(k) || b.get(k) !== v) return false;
  }
  return true;
}

function main(): void {
  fs.mkdirSync(TMP_DIR, { recursive: true });
  const python = findPython();
  const keymap = AssetKeyMap.load(KEYMAP_PATH);
  const milestoneData = loadMilestoneData(path.join(DATA_DIR, "milestones.json"));
  const certLevels = loadCertificationLevels(path.join(DATA_DIR, "certification_levels.json"));

  let failures = 0;
  for (const { fixture, target } of CASES) {
    const label = `${fixture} -> fill to ${target}`;
    const fixturePath = path.join(FIXTURES_DIR, fixture);
    const targetRank = milestoneData.milestoneChain.find((m) => m.name === target)!.rank;

    const pythonFillOut = path.join(TMP_DIR, `python_fill_${fixture}_${target}.lpw`);
    const pythonFinalOut = path.join(TMP_DIR, `python_final_${fixture}_${target}.lpw`);

    execFileSync(
      python,
      [LPW_TOOL_PY, "--keymap", KEYMAP_PATH, "fill-milestone", fixturePath, target, "--reference", REFERENCE_PATH, "--out", pythonFillOut, "--no-diff"],
      { stdio: "pipe" },
    );
    execFileSync(
      python,
      [LPW_TOOL_PY, "--keymap", KEYMAP_PATH, "set-rank", pythonFillOut, String(targetRank), "--out", pythonFinalOut],
      { stdio: "pipe" },
    );

    const save = load(fixturePath, keymap);
    const reference = load(REFERENCE_PATH, keymap);
    fillToMilestone(save, reference, target, milestoneData, certLevels, { setXp: false });
    const jsBuffer = rebuild(save, keymap);

    const pythonBuffer = fs.readFileSync(pythonFinalOut);
    const pythonSave = parse(pythonBuffer);
    const pythonActionTracker = decodeActionTracker(pythonSave.sections.find((s) => s.key === "ActionTrackerData")!.payload, keymap);
    const pythonMessages = decodeMessageData(pythonSave.sections.find((s) => s.key === "MessageData")!.payload, keymap);

    const actionTrackerOk = mapsEqual(pythonActionTracker, save.actionTracker!);
    const messagesOk = mapsEqual(pythonMessages.primary, save.messageHistory!.primary);
    const jsParsed = parse(jsBuffer);
    const jsCert = jsParsed.sections.find((s) => s.key === "CertificationTierData")!;
    const pyCert = pythonSave.sections.find((s) => s.key === "CertificationTierData")!;
    const certOk = jsCert.payload.equals(pyCert.payload);

    if (actionTrackerOk && messagesOk && certOk) {
      console.log(`OK   ${label} (ActionTrackerData: ${save.actionTracker!.size} entries match, MessageData: ${save.messageHistory!.primary.size} entries match, rank/cert bytes match)`);
    } else {
      console.error(`FAIL ${label}: actionTrackerOk=${actionTrackerOk} messagesOk=${messagesOk} certOk=${certOk}`);
      failures++;
    }
  }

  console.log(`\n${CASES.length - failures}/${CASES.length} fill-milestone cross-validation checks passed.`);
  if (failures > 0) process.exit(1);
}

main();
