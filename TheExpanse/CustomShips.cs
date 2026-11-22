using System;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;

namespace TheExpanse
{
    // Tells whether the ship now spawning is a custom one, for PartSeparation. A custom ship is one whose modules come
    // out of a mod's bundle, i.e. a bundle file under BepInEx\plugins. The check is made as the ship starts to spawn;
    // nothing about the spawn is changed.
    internal static class CustomShips
    {
        internal static void Install(Harmony harmony)
        {
            var target = AccessTools.Method(typeof(ShipRandomizationHelper), nameof(ShipRandomizationHelper.SpawnShipAsync));
            if (target == null)
            {
                Plugin.Log.LogError("[Parts] ShipRandomizationHelper.SpawnShipAsync not found; gentle separation stays off.");
                return;
            }
            try { harmony.Patch(target, prefix: new HarmonyMethod(typeof(CustomShips), nameof(Prefix))); }
            catch (Exception ex) { Plugin.Log.LogError("[Parts] could not hook ship spawning; gentle separation stays off: " + ex.Message); }
        }

        static void Prefix(ShipPreview shipPreview)
        {
            if (shipPreview?.ModuleGroup == null) return;
            try { PartSeparation.CustomShip = IsCustom(shipPreview); }
            catch (Exception ex) { PartSeparation.CustomShip = false; Plugin.Log.LogWarning("[Parts] could not tell whether this ship is a custom one: " + ex.Message); }
        }

        static bool IsCustom(ShipPreview shipPreview)
        {
            string plugins = BepInEx.Paths.PluginPath.Replace('/', '\\').TrimEnd('\\');
            foreach (var module in shipPreview.ModuleGroup.AllModules)
            {
                string guid = module?.ModulePrefabRef?.AssetGUID ?? "";
                if (guid.Length == 0) continue;
                foreach (var locator in Addressables.ResourceLocators)
                {
                    // Real catalogs only: other mods' own locators can be slow to answer (see FreePlayPlus's).
                    if (!(locator is ResourceLocationMap) || !locator.Locate(guid, null, out var locations) || locations == null) continue;
                    foreach (var location in locations)
                    {
                        if (!location.HasDependencies) continue;
                        foreach (var bundle in location.Dependencies)
                            if (!string.IsNullOrEmpty(bundle.InternalId) &&
                                bundle.InternalId.Replace('/', '\\').StartsWith(plugins, StringComparison.OrdinalIgnoreCase)) return true;
                    }
                }
            }
            return false;
        }
    }
}
