using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine.AddressableAssets;
using UnityEngine.AddressableAssets.ResourceLocators;
using UnityEngine.ResourceManagement.ResourceLocations;

namespace TheExpanse
{
    // Ship variants that only spawn while this mod is installed.
    //
    // A ship's module list picks one of its entries by weight (ModuleListAsset.GenerateRandomModuleEntry), and an entry
    // with weight 0 can never be picked. So an oversized variant is shipped with Weight 0 in its ModuleEntryDefinition and
    // this mod gives it a real weight in memory, just before the pick is made. Without the mod the variant stays at 0.
    //
    // Nothing in a ship's data says how big it is, so the variants have to be named in the settings. An entry is
    // recognised by NAME: the file name of the module prefab it spawns (looked up from the ship's addressables catalog,
    // e.g. "Tachi_Gas_Mount"), or the entry asset's own name. Names survive a ship being rebuilt or reorganised, which
    // the prefab's GUID does not reliably do; a GUID is still accepted for anyone who prefers it.
    internal static class VariantUnlock
    {
        internal const string DefaultWeights = "Tachi_Gas_Mount=1";
        // The first release named the Tachi's variant by this GUID, which stopped matching the variant's entry when the
        // ship was reorganised. A settings file still holding it has it swapped for the name, keeping its weight.
        const string RetiredGuid = "d51a0f78d6974d7881b6d7214326fed2", RetiredGuidName = "Tachi_Gas_Mount";

        static readonly Dictionary<string, float> sWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly Dictionary<string, string> sPrefabNames = new Dictionary<string, string>();     // guid -> prefab file name ("" = unknown)
        static readonly HashSet<int> sLogged = new HashSet<int>();

        internal static void Install(Harmony harmony)
        {
            if (Plugin.VariantWeights.Value.IndexOf(RetiredGuid, StringComparison.OrdinalIgnoreCase) >= 0)
                Plugin.VariantWeights.Value = System.Text.RegularExpressions.Regex.Replace(
                    Plugin.VariantWeights.Value, RetiredGuid, RetiredGuidName, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            Parse(Plugin.VariantWeights.Value);
            Plugin.VariantWeights.SettingChanged += (_, __) => Parse(Plugin.VariantWeights.Value);

            var target = AccessTools.Method(typeof(ModuleListAsset), "GenerateRandomModuleEntry");
            if (target == null)
            {
                Plugin.Log.LogError("[VariantUnlock] ModuleListAsset.GenerateRandomModuleEntry not found; variants stay locked.");
                return;
            }
            harmony.Patch(target, prefix: new HarmonyMethod(typeof(VariantUnlock), nameof(Prefix)));
            Plugin.Log.LogInfo($"[VariantUnlock] hooked; {sWeights.Count} variant(s) configured.");
        }

        // "name=weight, name=weight"
        static void Parse(string text)
        {
            sWeights.Clear();
            sLogged.Clear();
            foreach (var item in (text ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = item.Split('=');
                if (kv.Length == 2 && kv[0].Trim().Length > 0 &&
                    float.TryParse(kv[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float w) && w >= 0f)
                    sWeights[kv[0].Trim()] = w;
                else
                    Plugin.Log.LogWarning($"[VariantUnlock] ignoring malformed entry '{item.Trim()}' (expected name=weight).");
            }
        }

        static void Prefix(ModuleListAsset __instance)
        {
            try
            {
                if (!Plugin.Enabled.Value || sWeights.Count == 0) return;
                var container = __instance.Data?.ModuleEntryContainer;
                if (container == null) return;
                for (int i = 0; i < container.Count; i++)
                {
                    if (!(container[i] is ModuleEntryDefinition def) || def.ModuleDefRef == null) continue;
                    string guid = def.ModuleDefRef.AssetGUID ?? "";
                    string prefab = PrefabName(guid);
                    string matched;
                    float weight;
                    if (prefab.Length > 0 && sWeights.TryGetValue(prefab, out weight)) matched = "prefab " + prefab;
                    else if (sWeights.TryGetValue(def.name, out weight)) matched = "entry name";
                    else if (guid.Length > 0 && sWeights.TryGetValue(guid, out weight)) matched = "GUID";
                    else continue;

                    if (def.Weight != weight)
                    {
                        float was = def.Weight;
                        def.Weight = weight;
                        if (sLogged.Add(def.GetInstanceID()))
                            Plugin.Log.LogInfo($"[VariantUnlock] '{def.name}' in '{__instance.name}' (matched by {matched}): weight {was} -> {weight}.");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[VariantUnlock] " + ex); }
        }

        // File name, without extension, of the prefab a GUID points at, from the loaded addressables catalogs (their
        // entries carry the asset's path). "" when no catalog knows the GUID.
        static string PrefabName(string guid)
        {
            if (guid.Length == 0) return "";
            if (sPrefabNames.TryGetValue(guid, out var known)) return known;
            string name = "";
            try
            {
                foreach (var locator in Addressables.ResourceLocators)
                {
                    // Real catalogs only. Other mods add locators of their own, and asking those about a GUID nobody
                    // knows can be slow (FreePlayPlus's searches every loaded catalog on a miss); a catalog's own
                    // lookup is a dictionary hit.
                    if (!(locator is ResourceLocationMap)) continue;
                    if (!locator.Locate(guid, null, out IList<IResourceLocation> locations) || locations == null) continue;
                    foreach (var loc in locations)
                    {
                        string path = !string.IsNullOrEmpty(loc.InternalId) ? loc.InternalId : loc.PrimaryKey;
                        if (string.IsNullOrEmpty(path)) continue;
                        name = Path.GetFileNameWithoutExtension(path);
                        if (name.Length > 0) break;
                    }
                    if (name.Length > 0) break;
                }
            }
            catch (Exception) { name = ""; }
            sPrefabNames[guid] = name;
            return name;
        }
    }
}
