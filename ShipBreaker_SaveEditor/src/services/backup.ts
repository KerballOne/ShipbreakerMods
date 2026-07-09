/**
 * Auto-backup + restore-last-backup. Every write to a live save is preceded by
 * an automatic, non-optional backup (timestamped, app-local backups/ folder --
 * not inside the game's save folder). "Restore last backup" finds the most
 * recent backup for a profile and copies it back over the live file without
 * deleting the backup (repeat-safe).
 */
import * as fs from "node:fs";
import * as path from "node:path";

function backupsRootDir(appRoot: string): string {
  return path.join(appRoot, "backups");
}

function profileBackupDir(appRoot: string, liveFilePath: string): string {
  const stem = path.basename(liveFilePath, path.extname(liveFilePath));
  return path.join(backupsRootDir(appRoot), stem);
}

function timestampSuffix(date: Date): string {
  const pad = (n: number, len = 2) => String(n).padStart(len, "0");
  return (
    `${date.getFullYear()}${pad(date.getMonth() + 1)}${pad(date.getDate())}` +
    `_${pad(date.getHours())}${pad(date.getMinutes())}${pad(date.getSeconds())}`
  );
}

export interface BackupLogEntry {
  timestamp: string;
  backupFile: string;
  reason: string;
}

function logPath(dir: string): string {
  return path.join(dir, "log.json");
}

function readLog(dir: string): BackupLogEntry[] {
  const p = logPath(dir);
  if (!fs.existsSync(p)) return [];
  try {
    return JSON.parse(fs.readFileSync(p, "utf8"));
  } catch {
    return [];
  }
}

function appendLog(dir: string, entry: BackupLogEntry): void {
  const entries = readLog(dir);
  entries.push(entry);
  fs.writeFileSync(logPath(dir), JSON.stringify(entries, null, 2));
}

/** Back up `liveFilePath` before it's about to be overwritten. Returns the
 * backup's absolute path. Non-optional -- always call this before any write
 * to a live Saves\Profiles file. */
export function createBackup(appRoot: string, liveFilePath: string, reason: string): string {
  const dir = profileBackupDir(appRoot, liveFilePath);
  fs.mkdirSync(dir, { recursive: true });

  const ext = path.extname(liveFilePath);
  const stem = path.basename(liveFilePath, ext);
  const suffix = timestampSuffix(new Date());
  const backupPath = path.join(dir, `${stem}_${suffix}${ext}`);

  fs.copyFileSync(liveFilePath, backupPath);
  appendLog(dir, { timestamp: suffix, backupFile: path.basename(backupPath), reason });

  return backupPath;
}

export interface BackupInfo {
  path: string;
  timestamp: string;
  reason: string;
}

/** List all backups for a profile, newest first. */
export function listBackups(appRoot: string, liveFilePath: string): BackupInfo[] {
  const dir = profileBackupDir(appRoot, liveFilePath);
  const entries = readLog(dir);
  return entries
    .slice()
    .reverse()
    .map((e) => ({ path: path.join(dir, e.backupFile), timestamp: e.timestamp, reason: e.reason }));
}

/** Restore the most recent backup for a profile back over the live file.
 * Does NOT delete the backup (repeat-safe). Returns info about what was
 * restored, or null if no backup exists. */
export function restoreLatestBackup(appRoot: string, liveFilePath: string): BackupInfo | null {
  const backups = listBackups(appRoot, liveFilePath);
  if (backups.length === 0) return null;
  return restoreSpecificBackup(appRoot, liveFilePath, backups[0].path);
}

/** Restore one specific backup (by its absolute path, as returned from
 * listBackups) back over the live file. Does NOT delete the backup
 * (repeat-safe). Throws if the given path isn't a known backup for this
 * profile -- guards against restoring an unrelated file via a crafted path. */
export function restoreSpecificBackup(appRoot: string, liveFilePath: string, backupPath: string): BackupInfo {
  const backups = listBackups(appRoot, liveFilePath);
  const match = backups.find((b) => b.path === backupPath);
  if (match === undefined) {
    throw new Error(`'${backupPath}' is not a known backup for this profile`);
  }
  fs.copyFileSync(match.path, liveFilePath);
  return match;
}
