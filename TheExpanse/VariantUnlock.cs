using System;
using System.Collections.Generic;
using System.Globalization;
using BBI.Unity.Game;
using HarmonyLib;

namespace TheExpanse
{
    // Ship variants that only spawn while this mod is installed.
    //
    // A ship's module list picks one of its entries by weight (ModuleListAsset.GenerateRandomModuleEntry), and an entry
    // with weight 0 can never be picked. So an oversized variant is shipped with Weight 0 in its ModuleEntryDefinition and
    // this mod gives it a real weight in memory, just before the pick is made. Without the mod the variant stays at 0.
    //
    // Entries are recognised by the GUID of the module prefab they spawn (ModuleDefRef), which is unique to the variant.
    internal static class VariantUnlock
    {
        static readonly Dictionary<string, float> sWeights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        static readonly HashSet<int> sLogged = new HashSet<int>();

        internal static void Install(Harmony harmony)
        {
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

        // "guid=weight, guid=weight"
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
                    Plugin.Log.LogWarning($"[VariantUnlock] ignoring malformed entry '{item.Trim()}' (expected guid=weight).");
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
                    if (!sWeights.TryGetValue(def.ModuleDefRef.AssetGUID ?? "", out float weight)) continue;
                    if (def.Weight != weight)
                    {
                        float was = def.Weight;
                        def.Weight = weight;
                        if (sLogged.Add(def.GetInstanceID()))
                            Plugin.Log.LogInfo($"[VariantUnlock] '{def.name}' in '{__instance.name}': weight {was} -> {weight}.");
                    }
                }
            }
            catch (Exception ex) { Plugin.Log.LogError("[VariantUnlock] " + ex); }
        }
    }
}
