using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace FreePlayPlus
{
    internal enum SaveKind { Auto, Manual, Exit }

    internal sealed class ShipSave
    {
        public string Path = "";
        public DateTime Time;
        public SaveKind Kind;
        public float Percent;

        public string KindLabel => Kind switch { SaveKind.Auto => "Autosave", SaveKind.Manual => "Manual", _ => "Exit" };
    }

    // FreePlay ship saves, one folder per FreePlay ship, in the game's save-data folder but outside
    // Saves\ so nothing the game owns is touched:
    //   <SaveDataPath>\FreePlayPlus\ShipSaves\<Ship_Name>\<yyyyMMdd-HHmmss-fff>_<kind>_<percent>.ship
    // The folder is named after the ship as the FreePlay ship select shows it ("The Tachi" ->
    // The_Tachi); a ship-id.txt file inside holds the level's starting ship asset GUID, which is what
    // saves are matched by, so two ships sharing a display name can't mix. Folders from 1.1.0 test
    // builds (named by GUID, or with a ship.key file) are still recognised and migrated.
    // A .ship file is the same format career uses for its in-progress ship
    // (ShipPreview.WriteToFileAsync / CreateFromFileAsync), so salvage progress and every cut,
    // detached or processed part come back as they were. The name carries the list metadata, so
    // listing never opens a file.
    internal static class ShipSaveStore
    {
        internal const int MaxSavesPerShip = 12;
        private const string kTimeFormat = "yyyyMMdd-HHmmss-fff";

        private static readonly Dictionary<string, List<ShipSave>> sCache = new Dictionary<string, List<ShipSave>>();

        private static string Root => System.IO.Path.Combine(PlatformSettings.SaveDataPath, "FreePlayPlus", "ShipSaves");

        private const string kKeyFile = "ship-id.txt";
        private const string kLegacyKeyFile = "ship.key";

        // Ship names as the FreePlay ship select shows them, keyed by ship key; filled while the menu
        // is open (ShipListIndicators) and used to name save folders.
        internal static readonly Dictionary<string, string> DisplayNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static Dictionary<string, string>? sFolders; // ship key -> folder

        private static Dictionary<string, string> Folders()
        {
            if (sFolders != null) return sFolders;
            sFolders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(Root)) return sFolders;
            foreach (var dir in Directory.GetDirectories(Root))
            {
                var key = ReadKey(dir);
                if (string.IsNullOrEmpty(key) && IsGuid(System.IO.Path.GetFileName(dir))) key = System.IO.Path.GetFileName(dir); // legacy
                if (!string.IsNullOrEmpty(key) && !sFolders.ContainsKey(key!)) sFolders[key!] = dir;
            }
            return sFolders;
        }

        private static bool IsGuid(string name) => name.Length == 32 && name.All(Uri.IsHexDigit);

        private static string? ShipFolder(string shipKey) => Folders().TryGetValue(shipKey, out var dir) ? dir : null;

        private static string Sanitize(string name)
        {
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var chars = name.Trim().Select(c => char.IsWhiteSpace(c) ? '_' : invalid.Contains(c) ? '_' : c).ToArray();
            var result = new string(chars).Trim('_', '.');
            return string.IsNullOrEmpty(result) ? "Ship" : result;
        }

        // The folder for a ship's saves, created (or a legacy GUID folder renamed) on first use.
        private static string EnsureShipFolder(string shipKey, string shipName)
        {
            var wanted = Sanitize(shipName);
            var existing = ShipFolder(shipKey);
            if (existing != null && (wanted == "Ship" ||
                                     string.Equals(System.IO.Path.GetFileName(existing), wanted, StringComparison.OrdinalIgnoreCase)))
            {
                WriteKey(existing, shipKey);
                return existing;
            }

            var target = System.IO.Path.Combine(Root, wanted);
            for (var n = 2; Directory.Exists(target) && !string.Equals(ReadKey(target), shipKey, StringComparison.OrdinalIgnoreCase); n++)
                target = System.IO.Path.Combine(Root, $"{wanted}_{n}");

            Directory.CreateDirectory(Root);
            if (existing != null && !Directory.Exists(target))
            {
                try { Directory.Move(existing, target); }
                catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't rename save folder {existing}: {ex.Message}"); target = existing; }
            }
            Directory.CreateDirectory(target);
            WriteKey(target, shipKey);
            Folders()[shipKey] = target;
            return target;
        }

        private static string? ReadKey(string dir)
        {
            foreach (var file in new[] { kKeyFile, kLegacyKeyFile })
            {
                try
                {
                    var path = System.IO.Path.Combine(dir, file);
                    if (File.Exists(path)) return File.ReadAllText(path).Trim();
                }
                catch { }
            }
            return null;
        }

        private static void WriteKey(string dir, string shipKey)
        {
            try
            {
                File.WriteAllText(System.IO.Path.Combine(dir, kKeyFile), shipKey);
                var legacy = System.IO.Path.Combine(dir, kLegacyKeyFile);
                if (File.Exists(legacy)) File.Delete(legacy);
            }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't write ship id in {dir}: {ex.Message}"); }
        }

        // A ship folder with no saves left is removed (with its ship-id.txt).
        private static void RemoveIfEmpty(string shipKey)
        {
            var folder = ShipFolder(shipKey);
            if (folder == null || !Directory.Exists(folder) || Directory.GetFiles(folder, "*.ship").Length > 0) return;
            try { Directory.Delete(folder, recursive: true); Folders().Remove(shipKey); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't remove empty save folder {folder}: {ex.Message}"); }
        }

        internal static List<ShipSave> List(string? shipKey)
        {
            if (string.IsNullOrEmpty(shipKey)) return new List<ShipSave>();
            if (sCache.TryGetValue(shipKey!, out var cached)) return cached;
            var saves = new List<ShipSave>();
            var folder = ShipFolder(shipKey!);
            if (folder != null && Directory.Exists(folder))
            {
                foreach (var file in Directory.GetFiles(folder, "*.ship"))
                {
                    var parts = System.IO.Path.GetFileNameWithoutExtension(file).Split('_');
                    if (parts.Length < 3 ||
                        !DateTime.TryParseExact(parts[0], kTimeFormat, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ||
                        !Enum.TryParse(parts[1], true, out SaveKind kind) ||
                        !float.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var percent))
                        continue;
                    saves.Add(new ShipSave { Path = file, Time = time, Kind = kind, Percent = percent });
                }
            }
            saves.Sort((a, b) => b.Time.CompareTo(a.Time)); // newest first
            sCache[shipKey!] = saves;
            return saves;
        }

        internal static string NewPath(string shipKey, string shipName, SaveKind kind, float percent)
        {
            var folder = EnsureShipFolder(shipKey, shipName);
            var name = $"{DateTime.Now.ToString(kTimeFormat, CultureInfo.InvariantCulture)}_{kind.ToString().ToLowerInvariant()}_{percent.ToString("0.0", CultureInfo.InvariantCulture)}.ship";
            return System.IO.Path.Combine(folder, name);
        }

        internal static void Invalidate(string shipKey) => sCache.Remove(shipKey);

        // Saves are written during play; re-read the folders whenever the FreePlay menu opens.
        internal static void InvalidateAll() { sCache.Clear(); sFolders = null; }

        internal static void Delete(string shipKey, ShipSave save)
        {
            try { File.Delete(save.Path); }
            catch (Exception ex) { Plugin.Log.LogWarning($"Couldn't delete save {save.Path}: {ex.Message}"); }
            Invalidate(shipKey);
            RemoveIfEmpty(shipKey);
        }

        internal static void DeleteAll(string shipKey)
        {
            foreach (var save in List(shipKey).ToList()) Delete(shipKey, save);
        }

        // Autosaves beyond the Autosaves setting go first (oldest first), then anything beyond the
        // 12-per-ship cap, oldest of any kind.
        internal static void Prune(string shipKey)
        {
            Invalidate(shipKey);
            var saves = List(shipKey);
            var autos = saves.Where(s => s.Kind == SaveKind.Auto).ToList();
            foreach (var old in autos.Skip(Math.Max(0, Plugin.Autosaves.Value)))
                Delete(shipKey, old);
            saves = List(shipKey);
            foreach (var old in saves.Skip(MaxSavesPerShip).ToList())
                Delete(shipKey, old);
        }
    }

    internal static class ShipSaver
    {
        private static bool sSaving;
        private static ShipPreview? sTrackedPreview;
        private static int sLastAutosaveStep = -1;
        private static float sNextCheck;

        // The FreePlay ship being played, identified by the level's starting ship.
        internal static string? CurrentShipKey =>
            FreePlayDamage.IsFreePlay ? SceneLoader.Instance.CurrentLevelData.StartingShipRef?.AssetGUID : null;

        internal static string? ShipKey(LevelAsset.LevelData level) => level.StartingShipRef?.AssetGUID;

        // The name the FreePlay ship select showed for this ship; else the level's (English) name;
        // else the ship's model name.
        private static string ShipName(string shipKey, ShipPreview preview)
        {
            if (ShipSaveStore.DisplayNames.TryGetValue(shipKey, out var shown) && !string.IsNullOrWhiteSpace(shown)) return shown;
            var levelName = SceneLoader.Instance.CurrentLevelData.LevelDisplayName;
            if (!string.IsNullOrEmpty(levelName))
            {
                try
                {
                    var text = Main.Instance.LocalizationService.Localize(levelName, "en");
                    if (!string.IsNullOrWhiteSpace(text)) return text;
                }
                catch { }
                return levelName;
            }
            var id = preview.ConstructionAsset != null ? preview.ConstructionAsset.Data?.ShipModelName : null;
            if (string.IsNullOrEmpty(id) || id == "Undefined Ship Model") return preview.ShipName ?? "Ship";
            try
            {
                var text = Main.Instance.LocalizationService.Localize(id, "en");
                return string.IsNullOrEmpty(text) ? id! : text;
            }
            catch { return id!; }
        }

        internal static bool TryGetSalvagePercent(out float percent)
        {
            percent = 0f;
            var world = World.DefaultGameObjectInjectionWorld;
            var manifest = ModuleService.Instance != null ? ModuleService.Instance.CurrentManifest : Entity.Null;
            if (world == null || manifest == Entity.Null || !world.EntityManager.Exists(manifest) ||
                !world.EntityManager.HasComponent<ShipCurrentSalvageProgress>(manifest))
                return false;
            var progress = world.EntityManager.GetComponentData<ShipCurrentSalvageProgress>(manifest);
            if (progress.TotalCreditValue <= 0f) return true; // no value on this ship: report 0%
            percent = Mathf.Clamp(progress.CurrentSalvagedCreditValue / progress.TotalCreditValue * 100f, 0f, 100f);
            return true;
        }

        private static string SalvageDetail()
        {
            var world = World.DefaultGameObjectInjectionWorld;
            var manifest = ModuleService.Instance != null ? ModuleService.Instance.CurrentManifest : Entity.Null;
            if (world == null || manifest == Entity.Null || !world.EntityManager.HasComponent<ShipCurrentSalvageProgress>(manifest)) return "";
            var p = world.EntityManager.GetComponentData<ShipCurrentSalvageProgress>(manifest);
            return $": {p.CurrentSalvagedCreditValue:0} of {p.TotalCreditValue:0} credits";
        }

        internal static bool CanSave =>
            !sSaving && CurrentShipKey != null && ShipSpawnSystem.IsShipSpawned() &&
            ModuleService.Instance?.CurrentShipPreview != null;

        internal static async Task<bool> SaveAsync(SaveKind kind)
        {
            if (!CanSave) return false;
            var shipKey = CurrentShipKey!;
            var preview = ModuleService.Instance.CurrentShipPreview;
            TryGetSalvagePercent(out var percent);
            var path = ShipSaveStore.NewPath(shipKey, ShipName(shipKey, preview), kind, percent);
            sSaving = true;
            try
            {
                var ok = await preview.WriteToFileAsync(path);
                ShipSaveStore.Prune(shipKey);
                Plugin.Log.LogInfo(ok
                    ? $"Saved FreePlay ship ({kind}, {percent:0.0}% salvaged{SalvageDetail()})."
                    : $"FreePlay ship save failed ({kind}).");
                return ok;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"FreePlay ship save failed ({kind}): {ex.GetBaseException().Message}");
                return false;
            }
            finally { sSaving = false; }
        }

        internal static void SaveInBackground(SaveKind kind) => _ = SaveAsync(kind);

        // Called from Plugin.Update: an autosave each time salvage crosses another AutosavePoints %.
        // The step reached when a ship spawns (fresh or loaded from a save) is the baseline.
        internal static void Tick()
        {
            if (Time.unscaledTime < sNextCheck) return;
            sNextCheck = Time.unscaledTime + 1f;

            var step = Plugin.AutosavePoints.Value;
            if (step <= 0f || GameSession.CurrentGameState != GameSession.GameState.Gameplay || !CanSave) return;
            if (!TryGetSalvagePercent(out var percent)) return;

            var preview = ModuleService.Instance.CurrentShipPreview;
            var reached = Mathf.FloorToInt(percent / step);
            if (!ReferenceEquals(preview, sTrackedPreview))
            {
                sTrackedPreview = preview;
                sLastAutosaveStep = reached;
                return;
            }
            if (reached <= sLastAutosaveStep) return;
            sLastAutosaveStep = reached;
            SaveInBackground(SaveKind.Auto);
        }
    }

    // ---- Manual save: the master jack door's "return to menu" prompt -------------------------
    // In FreePlay ProcessMasterJackEntry.ProcessEntry queues GoToMainMenuTrigger, which shows a
    // pooled PromptPopup a frame or more later. The next prompt shown within a few seconds is that
    // one: its Decline button is relabelled "Save" and saves the ship (then closes the prompt, as
    // Decline does). Backing out (Back/Esc) still just closes it; Exit is unchanged.
    [HarmonyPatch(typeof(ProcessMasterJackEntry), nameof(ProcessMasterJackEntry.ProcessEntry))]
    internal static class ProcessMasterJackEntry_ProcessEntry_Patch
    {
        internal static float OpenedAt = -100f;

        private static void Prefix(ProcessMasterJackEntry __instance)
        {
            if (!FreePlayDamage.IsFreePlay || Traverse.Create(__instance).Field("mIsBlocked").GetValue<bool>()) return;
            OpenedAt = Time.unscaledTime;
        }
    }

    [HarmonyPatch(typeof(PromptPopup), "OnShow")]
    internal static class PromptPopup_OnShow_Patch
    {
        internal static PromptPopup? JackPrompt;

        private static void Postfix(PromptPopup __instance)
        {
            if (Time.unscaledTime - ProcessMasterJackEntry_ProcessEntry_Patch.OpenedAt > 5f) return;
            ProcessMasterJackEntry_ProcessEntry_Patch.OpenedAt = -100f;
            if (!ShipSaver.CanSave) return;
            JackPrompt = __instance;
            var label = Traverse.Create(__instance).Field("m_OnDeclineLabel").GetValue<LocalizedTextMeshProUGUI>();
            if (label != null)
            {
                label.ClearLocalizedID();
                label.TMProText.SetText("Save");
            }
        }
    }

    [HarmonyPatch(typeof(PromptPopup), nameof(PromptPopup.Decline))]
    internal static class PromptPopup_Decline_Patch
    {
        private static void Prefix(PromptPopup __instance)
        {
            if (!ReferenceEquals(__instance, PromptPopup_OnShow_Patch.JackPrompt)) return;
            PromptPopup_OnShow_Patch.JackPrompt = null;
            ShipSaver.SaveInBackground(SaveKind.Manual);
        }
    }

    [HarmonyPatch(typeof(PromptPopup), "OnDespawn")]
    internal static class PromptPopup_OnDespawn_Patch
    {
        private static void Prefix(PromptPopup __instance)
        {
            if (ReferenceEquals(__instance, PromptPopup_OnShow_Patch.JackPrompt)) PromptPopup_OnShow_Patch.JackPrompt = null;
        }
    }

    // ---- Exit save: confirming Exit on the master jack door prompt ----------------------------
    // Only that route: quitting from the pause menu or "Abandon shift" leaves no save, and neither
    // does a death. Confirm on the door prompt arms it; the menu teardown that follows waits for
    // the save to finish first.
    [HarmonyPatch(typeof(PromptPopup), nameof(PromptPopup.Confirm))]
    internal static class PromptPopup_Confirm_Patch
    {
        internal static float ExitArmedAt = -100f;

        private static void Prefix(PromptPopup __instance)
        {
            if (!ReferenceEquals(__instance, PromptPopup_OnShow_Patch.JackPrompt)) return;
            PromptPopup_OnShow_Patch.JackPrompt = null;
            ExitArmedAt = Time.unscaledTime;
        }
    }
    [HarmonyPatch(typeof(SceneLoader), nameof(SceneLoader.TearDownAndLoadFrontEndAsync))]
    internal static class SceneLoader_TearDownAndLoadFrontEndAsync_Patch
    {
        private static bool sBypass;

        private static bool Prefix(SceneLoader __instance, bool bDoQuitFade, ref Task __result)
        {
            var armed = Time.unscaledTime - PromptPopup_Confirm_Patch.ExitArmedAt < 15f;
            PromptPopup_Confirm_Patch.ExitArmedAt = -100f;
            if (sBypass || !armed || GameSession.CurrentGameState == GameSession.GameState.GameOver || !ShipSaver.CanSave) return true;
            __result = SaveThenExit(__instance, bDoQuitFade);
            return false;
        }

        private static async Task SaveThenExit(SceneLoader loader, bool bDoQuitFade)
        {
            await ShipSaver.SaveAsync(SaveKind.Exit);
            sBypass = true;
            try { await loader.TearDownAndLoadFrontEndAsync(bDoQuitFade); }
            finally { sBypass = false; }
        }
    }
}
