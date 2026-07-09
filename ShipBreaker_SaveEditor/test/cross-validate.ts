/**
 * Stage 3 verification (see plan): cross-validate the JS port against the
 * proven-correct Python tool. For each fixture + target milestone, runs
 * `lpw_tool.py goto-milestone` and this port's gotoMilestone(), then byte-diffs
 * the two output files. Must be byte-identical.
 *
 * Run with: npx tsx test/cross-validate.ts
 */
import * as fs from "node:fs";
import * as path from "node:path";
import { execFileSync } from "node:child_process";
import { load, rebuild } from "../src/lpw/rebuild";
import { AssetKeyMap } from "../src/lpw/keymap";
import { loadMilestoneData, loadCertificationLevels, gotoMilestone } from "../src/lpw/milestones";

const REPO_ROOT = path.join(__dirname, "..", "..");
const LPW_TOOL_PY = path.join(REPO_ROOT, "SaveEditor", "lpw_tool.py");
const KEYMAP_PATH = path.join(__dirname, "..", "data", "asset_save_keys.json");
const MILESTONES_PATH = path.join(__dirname, "..", "data", "milestones.json");
const CERT_LEVELS_PATH = path.join(__dirname, "..", "data", "certification_levels.json");
const FIXTURES_DIR = path.join(__dirname, "fixtures");
const TMP_DIR = path.join(__dirname, "_cross_validate_tmp");

// Representative targets covering the cases the plan calls out: an early-chain
// milestone, a same-rank-cluster case (previously buggy per the Python
// docstring), and a late-chain case that exercises the Act-3 sweep.
const TARGETS = [
  "PAT_CMP_02_00_HABIntro_Night_Complete",
  "PAT_CMP_17_03_LouUpsetAboutKaito_Night_Complete",
  "PAT_CMP_17_X2_LYNXUnionClampDown_Complete",
];

const FIXTURES = ["beltalowda_full.lpw", "heidi_original.lpw"];

function findPython(): string {
  for (const candidate of ["python", "python3"]) {
    try {
      execFileSync(candidate, ["--version"], { stdio: "ignore" });
      return candidate;
    } catch {
      // try next
    }
  }
  throw new Error("No Python interpreter found on PATH (tried 'python', 'python3')");
}

function main(): void {
  fs.mkdirSync(TMP_DIR, { recursive: true });
  const python = findPython();
  const keymap = AssetKeyMap.load(KEYMAP_PATH);
  const milestoneData = loadMilestoneData(MILESTONES_PATH);
  const certLevels = loadCertificationLevels(CERT_LEVELS_PATH);

  let failures = 0;
  let ran = 0;

  for (const fixtureFile of FIXTURES) {
    const fixturePath = path.join(FIXTURES_DIR, fixtureFile);
    for (const target of TARGETS) {
      ran++;
      const label = `${fixtureFile} -> ${target}`;
      const pythonOut = path.join(TMP_DIR, `python_${fixtureFile}_${target}.lpw`);
      const jsOut = path.join(TMP_DIR, `js_${fixtureFile}_${target}.lpw`);

      try {
        execFileSync(
          python,
          [
            LPW_TOOL_PY,
            "--keymap",
            KEYMAP_PATH,
            "goto-milestone",
            fixturePath,
            target,
            "--out",
            pythonOut,
            "--no-diff",
          ],
          { stdio: "pipe" },
        );
      } catch (err) {
        console.error(`FAIL ${label}: Python tool errored`);
        console.error((err as Error).message);
        failures++;
        continue;
      }

      const save = load(fixturePath, keymap);
      gotoMilestone(save, target, milestoneData, certLevels);
      const jsBuffer = rebuild(save, keymap);
      fs.writeFileSync(jsOut, jsBuffer);

      const pythonBuffer = fs.readFileSync(pythonOut);
      if (pythonBuffer.equals(jsBuffer)) {
        console.log(`OK   ${label} (${jsBuffer.length} bytes, byte-identical)`);
      } else {
        console.error(
          `FAIL ${label}: byte mismatch (python ${pythonBuffer.length} bytes, js ${jsBuffer.length} bytes)`,
        );
        const minLen = Math.min(pythonBuffer.length, jsBuffer.length);
        for (let i = 0; i < minLen; i++) {
          if (pythonBuffer[i] !== jsBuffer[i]) {
            console.error(`  first differing byte at offset ${i}`);
            console.error(`  python: ...${pythonBuffer.subarray(Math.max(0, i - 8), i + 16).toString("hex")}...`);
            console.error(`  js:     ...${jsBuffer.subarray(Math.max(0, i - 8), i + 16).toString("hex")}...`);
            break;
          }
        }
        failures++;
      }
    }
  }

  console.log(`\n${ran - failures}/${ran} cross-validation checks passed.`);
  if (failures > 0) {
    process.exit(1);
  }
}

main();
