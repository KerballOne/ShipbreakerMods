using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace GhostShipsInFreePlay
{
    // One career job-board entry (ShipClassAsset.GeneratableShips[i]) whose OverrideRef carries a
    // ghost property.
    internal sealed class GhostOverride
    {
        public string ShipRefGuid = "";
        public string ShipName = "";
        public AssetReferencePropertyContainerAsset OverrideRef = null!;
        public string OverrideName = "";
        public ModulePropertyAsset GhostProperty = null!;
        // Exclusive slot the ghost property lives in, or null when it's an affix.
        public PropertyContainer.Property? Slot;
    }

    // Career ghost ships aren't a separate ship: they're a normal ModuleConstructionAsset paired
    // with a PropertyContainerAsset override (ShipClassAsset.GeneratableShipOverridePair.OverrideRef)
    // that contains the ghost ModulePropertyAsset. JobBoardScreenController identifies them with a
    // serialized m_GhostShipModuleProperties list, but that controller only exists in the Hab, so
    // here the ghost property is recognised the same way ModuleSkinListAsset picks the ghost skin:
    // its addressable label is "GhostShip".
    internal static class GhostShipCatalog
    {
        private static readonly string[] kExclusiveSlots =
            { "Theme", "Condition", "Difficulty", "LightLevel", "ValueRange", "Atmosphere", "RoomType", "Size", "Company", "Role" };

        private static Task? sResolveTask;
        private static readonly List<GhostOverride> sOverrides = new List<GhostOverride>();
        // Loaded PropertyContainerAssets are held for the session so GhostProperty stays valid.
        private static readonly List<AsyncOperationHandle<PropertyContainerAsset>> sHandles = new List<AsyncOperationHandle<PropertyContainerAsset>>();

        public static bool IsResolved => sResolveTask != null && sResolveTask.IsCompleted;

        public static void EnsureResolving()
        {
            if (sResolveTask != null) return;
            sResolveTask = ResolveAsync();
            sResolveTask.ContinueWith(t => Plugin.Log.LogError($"Ghost override resolution failed: {t.Exception}"),
                TaskContinuationOptions.OnlyOnFaulted);
        }

        // True when career has a ghost entry for exactly this construction asset (as opposed to Pick's
        // fallback, which forces the ghost property onto a ship with no authored ghost content).
        public static bool HasVariant(string? shipRefGuid) =>
            IsResolved && !string.IsNullOrEmpty(shipRefGuid) && sOverrides.Exists(o => o.ShipRefGuid == shipRefGuid);

        // Prefers the ghost entry for the same construction asset the FreePlay level spawns, so the
        // override's size/role/company match the ship; otherwise falls back to the first ghost entry.
        public static GhostOverride? Pick(string? shipRefGuid)
        {
            if (sOverrides.Count == 0) return null;
            if (!string.IsNullOrEmpty(shipRefGuid))
            {
                var match = sOverrides.FirstOrDefault(o => o.ShipRefGuid == shipRefGuid);
                if (match != null) return match;
            }
            return sOverrides[0];
        }

        private static async Task ResolveAsync()
        {
            var shipClasses = Main.Instance?.MainSettings?.HabSettings?.JobBoardSettings?.ShipClasses;
            if (shipClasses == null)
            {
                Plugin.Log.LogWarning("JobBoardSettings.ShipClasses unavailable; ghost ships cannot be resolved.");
                return;
            }

            var cache = new Dictionary<string, PropertyContainerAsset?>();
            foreach (var shipClass in shipClasses)
            {
                if (shipClass == null) continue;
                foreach (var pair in shipClass.GeneratableShips)
                {
                    var guid = pair.OverrideRef?.AssetGUID;
                    if (string.IsNullOrEmpty(guid)) continue;

                    if (!cache.TryGetValue(guid!, out var container))
                    {
                        container = await LoadContainer(guid!);
                        cache[guid!] = container;
                        if (container != null)
                            Plugin.Debug($"[Catalog] override '{container.name}' ({guid}): {Describe(container)}");
                    }
                    if (container == null) continue;

                    if (!TryFindGhostProperty(container, out var ghost, out var slot)) continue;

                    var ghostOverride = new GhostOverride
                    {
                        ShipRefGuid = pair.ShipRef?.AssetGUID ?? "",
                        ShipName = pair.ShipArchetype != null ? pair.ShipArchetype.name : "?",
                        OverrideRef = new AssetReferencePropertyContainerAsset(guid),
                        OverrideName = container.name,
                        GhostProperty = ghost!,
                        Slot = slot,
                    };
                    sOverrides.Add(ghostOverride);
                    Plugin.Debug($"[Catalog] ghost entry: class={shipClass.ClassName} ship={ghostOverride.ShipName} " +
                        $"shipRef={ghostOverride.ShipRefGuid} override={container.name} ghostProperty={ghost!.name} " +
                        $"slot={(slot.HasValue ? slot.Value.ToString() : "Affix")}");
                }
            }
            Plugin.Log.LogInfo($"[Catalog] resolved {sOverrides.Count} ghost ship entries.");
        }

        private static async Task<PropertyContainerAsset?> LoadContainer(string guid)
        {
            // Load by key rather than through the AssetReference instance: the game's own
            // AssetReferences would refuse a second LoadAssetAsync on the same object.
            var handle = Addressables.LoadAssetAsync<PropertyContainerAsset>(guid);
            await handle.Task;
            if (handle.Status != AsyncOperationStatus.Succeeded || handle.Result == null)
            {
                Plugin.Log.LogWarning($"[Catalog] failed to load PropertyContainerAsset {guid}");
                if (handle.IsValid()) Addressables.Release(handle);
                return null;
            }
            sHandles.Add(handle);
            return handle.Result;
        }

        private static bool TryFindGhostProperty(PropertyContainerAsset container, out ModulePropertyAsset? ghost, out PropertyContainer.Property? slot)
        {
            foreach (var slotName in kExclusiveSlots)
            {
                var entries = Traverse.Create(container).Property(slotName).GetValue<ModuleProperty[]>();
                if (entries == null) continue;
                foreach (var entry in entries)
                {
                    if (!IsGhost(entry?.Asset)) continue;
                    ghost = entry!.Asset;
                    slot = (PropertyContainer.Property)Enum.Parse(typeof(PropertyContainer.Property), slotName);
                    return true;
                }
            }
            if (container.Affixes != null)
            {
                foreach (var affix in container.Affixes)
                {
                    if (!IsGhost(affix)) continue;
                    ghost = affix;
                    slot = null;
                    return true;
                }
            }
            ghost = null;
            slot = null;
            return false;
        }

        internal static bool IsGhost(ModulePropertyAsset? asset)
        {
            if (asset == null) return false;
            var label = asset.Data?.AssetLabelReference?.labelString;
            return string.Equals(label, ModuleSkinListAsset.kLabelGhostShip.labelString, StringComparison.OrdinalIgnoreCase);
        }

        private static string Describe(PropertyContainerAsset container)
        {
            var parts = new List<string>();
            foreach (var slotName in kExclusiveSlots)
            {
                var entries = Traverse.Create(container).Property(slotName).GetValue<ModuleProperty[]>();
                if (entries == null || entries.Length == 0) continue;
                parts.Add($"{slotName}=[{string.Join(",", entries.Select(e => Label(e?.Asset)))}]");
            }
            if (container.Affixes != null && container.Affixes.Length > 0)
                parts.Add($"Affixes(start={container.StartingChance:F2},dim={container.DiminishRate:F2})=[{string.Join(",", container.Affixes.Select(Label))}]");
            return string.Join(" ", parts);
        }

        private static string Label(ModulePropertyAsset? asset)
        {
            if (asset == null) return "null";
            var label = asset.Data?.AssetLabelReference?.labelString;
            return string.IsNullOrEmpty(label) ? asset.name : $"{asset.name}<{label}>";
        }
    }
}
