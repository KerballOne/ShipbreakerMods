using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BBI.Unity.Game;
using BepInEx;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace FreePlayPlus
{
    // Prepares a saved custom ship's prefabs the way a fresh spawn does, before the ship loads.
    //
    // A custom ship's parts get their StructurePartAsset (and other ScriptableObject references) from
    // its ship mod (ModdedShipLoader) when the prefab is loaded through
    // Addressables.LoadAssetAsync<UnityEngine.Object>(key) - the call a fresh spawn goes through.
    // A save load reaches the prefab another way, so the references are still empty when the parts
    // are instantiated, and StructurePart.Awake destroys any StructurePart without an asset: the
    // loaded ship is solid but has no cuttable/scannable parts.
    //
    // So before loading a save, the saved ship's module prefabs that come from a mod bundle (under
    // BepInEx\plugins) - and, recursively, mod-bundle prefabs their loader stubs (AddressableLoader,
    // a game-assembly type) point to - are loaded through that same public call, each level in
    // parallel, and kept loaded until the ship has spawned. Stock prefabs are skipped: they carry no
    // ship-mod references. Nothing in the ship mod is patched.
    internal static class SavedShipPrimer
    {
        private static readonly Type? sLoaderType = AccessTools.TypeByName("BBI.Unity.Game.AddressableLoader");
        private static readonly List<AsyncOperationHandle<UnityEngine.Object>> sHeld = new List<AsyncOperationHandle<UnityEngine.Object>>();
        private static readonly string sPluginPath = Paths.PluginPath.Replace('/', '\\').TrimEnd('\\');

        internal static async Task PrimeAsync(ShipPreview preview)
        {
            ReleaseHeld();
            if (preview?.ModuleGroup?.AllModules == null) return;
            var started = Time.realtimeSinceStartup;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var level = new List<string>();
            int skippedStock = 0;
            foreach (var module in preview.ModuleGroup.AllModules)
            {
                var key = module?.ModulePrefabRef?.RuntimeKey as string;
                if (string.IsNullOrEmpty(key) || !seen.Add(key!)) continue;
                if (FromModBundle(key!)) level.Add(key!); else skippedStock++;
            }

            while (level.Count > 0)
            {
                var handles = new List<(string key, AsyncOperationHandle<UnityEngine.Object> handle)>();
                foreach (var key in level)
                {
                    try { handles.Add((key, Addressables.LoadAssetAsync<UnityEngine.Object>(key))); }
                    catch (Exception ex) { Plugin.Log.LogWarning($"Priming {key} failed: {ex.Message}"); }
                }
                foreach (var (_, handle) in handles)
                    try { await handle.Task; } catch { }

                var next = new List<string>();
                foreach (var (key, handle) in handles)
                {
                    if (handle.Status != AsyncOperationStatus.Succeeded || !(handle.Result is GameObject prefab))
                    {
                        Plugin.Debug($"Priming {key}: {handle.Status} ({(handle.IsValid() ? handle.Result?.GetType().Name ?? "null" : "invalid")}).");
                        if (handle.IsValid()) Addressables.Release(handle);
                        continue;
                    }
                    sHeld.Add(handle);
                    Plugin.Debug($"Primed prefab '{prefab.name}' ({key}).");
                    if (sLoaderType == null) continue;
                    foreach (var loader in prefab.GetComponentsInChildren(sLoaderType, true))
                    {
                        var guid = Traverse.Create(loader).Field("assetGUID").GetValue<string>();
                        if (string.IsNullOrEmpty(guid) || !seen.Add(guid)) continue;
                        if (FromModBundle(guid)) next.Add(guid); else skippedStock++;
                    }
                }
                level = next;
            }
            Plugin.Log.LogInfo($"Primed {sHeld.Count} custom-ship prefab(s) for the saved ship in {Time.realtimeSinceStartup - started:0.0}s ({skippedStock} stock prefab(s) skipped).");
        }

        // Called once the saved ship has finished spawning (and before any next load).
        internal static void ReleaseHeld()
        {
            foreach (var handle in sHeld)
                if (handle.IsValid()) Addressables.Release(handle);
            sHeld.Clear();
        }

        // Whether the asset behind this key is loaded from a bundle file under BepInEx\plugins.
        private static bool FromModBundle(string key)
        {
            foreach (var locator in Addressables.ResourceLocators)
            {
                if (!locator.Locate(key, typeof(UnityEngine.Object), out var locations) || locations == null) continue;
                foreach (var location in locations)
                    if (DependsOnModBundle(location, 0)) return true;
            }
            return false;
        }

        private static bool DependsOnModBundle(IResourceLocation location, int depth)
        {
            var id = location.InternalId;
            if (!string.IsNullOrEmpty(id) && id.EndsWith(".bundle", StringComparison.OrdinalIgnoreCase) &&
                id.Replace('/', '\\').StartsWith(sPluginPath, StringComparison.OrdinalIgnoreCase))
                return true;
            if (depth > 4 || !location.HasDependencies) return false;
            foreach (var dependency in location.Dependencies)
                if (DependsOnModBundle(dependency, depth + 1)) return true;
            return false;
        }

    }
}
