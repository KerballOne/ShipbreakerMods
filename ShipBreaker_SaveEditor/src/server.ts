import * as path from "node:path";
import * as fs from "node:fs";
import express, { type Request, type Response } from "express";
import { AssetKeyMap } from "./lpw/keymap";
import { load, saveTo, rebuild } from "./lpw/rebuild";
import {
  loadMilestoneData,
  loadCertificationLevels,
  gotoMilestone,
  fillToMilestone,
  completeMissingMilestone,
  snapshot,
  diffSnapshots,
  buildMilestoneChecklist,
} from "./lpw/milestones";
import { loadDifficultyModes, resolveDifficultyMode, setDifficultyMode } from "./lpw/difficultyMode";
import { scanProfiles, summarizeProfile, validateBrowsedPath } from "./services/profileDiscovery";
import { createBackup, restoreLatestBackup, restoreSpecificBackup, listBackups } from "./services/backup";
import { toAdvancedJson, fromAdvancedJson } from "./lpw/advancedJson";
import type { LpwSave } from "./lpw/parse";

const APP_ROOT = path.join(__dirname, "..");
const DATA_DIR = path.join(APP_ROOT, "data");
const PORT = Number(process.env.PORT ?? 4173);

const keymap = AssetKeyMap.load(path.join(DATA_DIR, "asset_save_keys.json"));
const milestoneData = loadMilestoneData(path.join(DATA_DIR, "milestones.json"));
const certLevels = loadCertificationLevels(path.join(DATA_DIR, "certification_levels.json"));
const difficultyModes = loadDifficultyModes(path.join(DATA_DIR, "difficulty_modes.json"));
const REFERENCE_SAVE_PATH = path.join(DATA_DIR, "reference_completed_save.lpw");

const app = express();
app.use(express.json());
app.use(express.static(path.join(APP_ROOT, "public")));

// The :id used across /api/save/:id routes is the save's absolute file path,
// base64url-encoded so it's safe in a URL path segment.
function encodeId(filePath: string): string {
  return Buffer.from(filePath, "utf8").toString("base64url");
}
function decodeId(id: string): string {
  return Buffer.from(id, "base64url").toString("utf8");
}

function loadSaveById(id: string): { save: LpwSave; filePath: string } {
  const filePath = decodeId(id);
  if (!fs.existsSync(filePath)) {
    throw new Error(`Save file not found: ${filePath}`);
  }
  return { save: load(filePath, keymap), filePath };
}

app.get("/api/profiles", (_req: Request, res: Response) => {
  const profiles = scanProfiles(keymap, difficultyModes).map((p) => ({ ...p, id: encodeId(p.path) }));
  res.json(profiles);
});

app.post("/api/profiles/browse", (req: Request, res: Response) => {
  const { path: filePath } = req.body as { path?: string };
  if (!filePath) {
    res.status(400).json({ error: "Missing 'path' in request body" });
    return;
  }
  try {
    const summary = validateBrowsedPath(filePath, keymap, difficultyModes);
    res.json({ ...summary, id: encodeId(summary.path) });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.get("/api/milestones", (_req: Request, res: Response) => {
  res.json(milestoneData.milestoneChain);
});

app.get("/api/difficulty-modes", (_req: Request, res: Response) => {
  res.json(difficultyModes);
});

app.get("/api/save/:id", (req: Request, res: Response) => {
  try {
    const { save, filePath } = loadSaveById(req.params.id);
    const checklist = buildMilestoneChecklist(save, milestoneData);
    res.json({
      fileName: path.basename(filePath),
      profileName: save.generalData?.profileName ?? null,
      rank: save.certification?.rank ?? null,
      xp: save.certification?.xp ?? null,
      difficultyMode: resolveDifficultyMode(save, difficultyModes),
      milestones: checklist,
    });
  } catch (err) {
    res.status(404).json({ error: (err as Error).message });
  }
});

app.get("/api/save/:id/backups", (req: Request, res: Response) => {
  try {
    const { filePath } = loadSaveById(req.params.id);
    res.json(listBackups(APP_ROOT, filePath));
  } catch (err) {
    res.status(404).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/goto-milestone", (req: Request, res: Response) => {
  const { milestone } = req.body as { milestone?: string };
  if (!milestone) {
    res.status(400).json({ error: "Missing 'milestone' in request body" });
    return;
  }
  try {
    const { save, filePath } = loadSaveById(req.params.id);
    const before = snapshot(save);

    createBackup(APP_ROOT, filePath, `goto-milestone: ${milestone}`);

    const result = gotoMilestone(save, milestone, milestoneData, certLevels);
    const after = snapshot(save);
    const diff = diffSnapshots(before, after);

    saveTo(save, keymap, filePath);

    res.json({ result, diff });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/fill-milestone", (req: Request, res: Response) => {
  const { milestone } = req.body as { milestone?: string };
  if (!milestone) {
    res.status(400).json({ error: "Missing 'milestone' in request body" });
    return;
  }
  try {
    const { save, filePath } = loadSaveById(req.params.id);
    const before = snapshot(save);
    const reference = load(REFERENCE_SAVE_PATH, keymap);

    createBackup(APP_ROOT, filePath, `fill-milestone: ${milestone}`);

    const result = fillToMilestone(save, reference, milestone, milestoneData, certLevels);
    const after = snapshot(save);
    const diff = diffSnapshots(before, after);

    saveTo(save, keymap, filePath);

    res.json({ result, diff });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/complete-missing", (req: Request, res: Response) => {
  const { milestone } = req.body as { milestone?: string };
  if (!milestone) {
    res.status(400).json({ error: "Missing 'milestone' in request body" });
    return;
  }
  try {
    const { save, filePath } = loadSaveById(req.params.id);
    const before = snapshot(save);
    const reference = load(REFERENCE_SAVE_PATH, keymap);

    createBackup(APP_ROOT, filePath, `complete-missing: ${milestone}`);

    const result = completeMissingMilestone(save, reference, milestone, milestoneData);
    const after = snapshot(save);
    const diff = diffSnapshots(before, after);

    saveTo(save, keymap, filePath);

    res.json({ result, diff });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/set-difficulty-mode", (req: Request, res: Response) => {
  const { mode } = req.body as { mode?: string };
  if (!mode) {
    res.status(400).json({ error: "Missing 'mode' in request body" });
    return;
  }
  try {
    const { save, filePath } = loadSaveById(req.params.id);

    // Best-effort check: does another known profile already use this mode?
    // Per the game's "one save per difficulty mode" constraint -- informational
    // only, does not block the write.
    const otherProfiles = scanProfiles(keymap, difficultyModes).filter((p) => p.path !== filePath);
    const conflict = otherProfiles.find((p) => p.difficultyMode === mode);

    createBackup(APP_ROOT, filePath, `set-difficulty-mode: ${mode}`);

    setDifficultyMode(save, mode, difficultyModes);
    saveTo(save, keymap, filePath);

    res.json({
      mode,
      warning: conflict
        ? `Profile '${conflict.profileName}' (${conflict.fileName}) is already set to ${mode} mode -- the game only allows one active save per difficulty mode at a time.`
        : null,
    });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.get("/api/save/:id/advanced", (req: Request, res: Response) => {
  try {
    const { save } = loadSaveById(req.params.id);
    res.json(toAdvancedJson(save, difficultyModes));
  } catch (err) {
    res.status(404).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/advanced", (req: Request, res: Response) => {
  const { data } = req.body as { data?: unknown };
  if (data === undefined) {
    res.status(400).json({ error: "Missing 'data' in request body" });
    return;
  }
  try {
    const { save, filePath } = loadSaveById(req.params.id);

    // Validate fully before touching anything on disk -- fromAdvancedJson
    // throws (and mutates nothing) on any structural problem or unknown
    // asset name, so an invalid edit never reaches the backup/write step.
    fromAdvancedJson(save, keymap, difficultyModes, data);

    // rebuild() itself is also exercised here (via saveTo) as a second,
    // format-level check -- if re-encoding the edited fields somehow
    // produces something rebuild() can't handle (e.g. an unknown section
    // key), it throws before any backup is taken or file is written.
    const rebuilt = rebuild(save, keymap);

    createBackup(APP_ROOT, filePath, "advanced-edit");
    fs.writeFileSync(filePath, rebuilt);

    res.json({ ok: true });
  } catch (err) {
    res.status(400).json({ error: (err as Error).message });
  }
});

app.post("/api/save/:id/restore-backup", (req: Request, res: Response) => {
  const { backupPath } = req.body as { backupPath?: string };
  try {
    const { filePath } = loadSaveById(req.params.id);
    if (backupPath) {
      res.json(restoreSpecificBackup(APP_ROOT, filePath, backupPath));
      return;
    }
    const restored = restoreLatestBackup(APP_ROOT, filePath);
    if (restored === null) {
      res.status(404).json({ error: "No backup found for this profile" });
      return;
    }
    res.json(restored);
  } catch (err) {
    res.status(404).json({ error: (err as Error).message });
  }
});

// Bind explicitly to 127.0.0.1, not the default 0.0.0.0 -- this is a
// single-player local tool, it has no business listening on external network
// interfaces. Doesn't suppress the Windows Firewall "allow this app" prompt
// (Windows can't tell from outside that the bind is loopback-only), but it's
// still the correct/safe thing to do regardless of that prompt.
app.listen(PORT, "127.0.0.1", () => {
  // eslint-disable-next-line no-console
  console.log(`ShipBreaker_SaveEditor running at http://localhost:${PORT}`);
});
