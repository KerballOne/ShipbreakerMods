using System.Collections.Generic;
using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;
using UnityEngine.AddressableAssets;
using Object = UnityEngine.Object;

namespace GhostShipsInFreePlay
{
    // Launch flow for a FreePlay ship (LevelSelectButton.LoadNormalLevelAsync):
    //   SceneLoader.TearDownAndLoadLevel[Ref]Async(levelData, OptionalCreateParams{PropertyOverridesRef = random FreePlay container})
    //     -> SceneLoader.CreateShipPreviewFromLevelData(levelData, optionalParams, ...)   [patched: swap in career ghost override]
    //       -> ShipPreview.CreateAsync(seed, shipRef, shipSpawnParams, optionalParams)    [patched: career module rules]
    //         -> ShipPreview.GenerateProperties(random)                                    [patched: guarantee ghost property]
    // Everything ghost-specific downstream (GhostShip skin label, post-process volume profile,
    // property-gated modules/narrative entries) reads ShipPreview.Properties, so getting the ghost
    // property into that container is the whole job.
    internal static class PendingGhost
    {
        // Set for exactly one FreePlay preview generation and cleared as soon as its properties
        // are generated, so career job-board previews that share the same override are untouched.
        public static GhostOverride? Current;

        // The preview the checkbox forced into a ghost ship; runtime hooks compare it against
        // ModuleService.CurrentShipPreview so they only act on that ship.
        public static ShipPreview? ActivePreview;

        public static bool IsActiveShip() =>
            ActivePreview != null && ModuleService.Instance != null && ModuleService.Instance.CurrentShipPreview == ActivePreview;

        public static bool Matches(AssetReferencePropertyContainerAsset? overrideRef) =>
            Current != null && overrideRef != null && overrideRef.AssetGUID == Current.OverrideRef.AssetGUID;
    }

    [HarmonyPatch(typeof(SceneLoader), "CreateShipPreviewFromLevelData")]
    internal static class SceneLoader_CreateShipPreviewFromLevelData_Patch
    {
        private static void Prefix(LevelAsset.LevelData levelData, ref ShipPreview.OptionalCreateParams optionalParams, AssetReferenceModuleConstructionAsset constructionAssetRefOverride)
        {
            PendingGhost.Current = null;
            PendingGhost.ActivePreview = null;
            if (levelData.SessionType != GameSession.SessionType.FreeMode || !Plugin.ForceGhostShip.Value) return;

            if (!GhostShipCatalog.IsResolved)
            {
                Plugin.Log.LogWarning("Ghost Ship is enabled but ghost overrides haven't finished resolving; loading the stock FreePlay ship.");
                return;
            }

            var shipRefGuid = (constructionAssetRefOverride ?? levelData.StartingShipRef)?.AssetGUID;
            var ghost = GhostShipCatalog.Pick(shipRefGuid);
            if (ghost == null)
            {
                Plugin.Log.LogWarning("Ghost Ship is enabled but no career ghost override was found; loading the stock FreePlay ship.");
                return;
            }

            optionalParams ??= new ShipPreview.OptionalCreateParams();
            var replaced = optionalParams.PropertyOverridesRef?.AssetGUID ?? "none";
            optionalParams.PropertyOverridesRef = ghost.OverrideRef;
            PendingGhost.Current = ghost;
            Plugin.Log.LogInfo($"FreePlay ghost ship: level ship {shipRefGuid} -> override '{ghost.OverrideName}' " +
                $"(from career {ghost.ShipName}{(ghost.ShipRefGuid == shipRefGuid ? "" : ", fallback: no entry for this ship")}); replaced FreePlay override {replaced}");
        }
    }

    [HarmonyPatch(typeof(ShipPreview), nameof(ShipPreview.CreateAsync))]
    internal static class ShipPreview_CreateAsync_Patch
    {
        // ShipSpawnParams.SessionType is only consulted for the DisableInFreeMode module/hardpoint
        // gates in ShipRandomizationHelper; the generated ModuleGroup is reused when the ship spawns.
        private static void Prefix(ref ShipSpawnParams shipSpawnParams, ShipPreview.OptionalCreateParams optionalParams)
        {
            if (!PendingGhost.Matches(optionalParams?.PropertyOverridesRef) || !Plugin.IncludeCareerOnlyModules.Value) return;
            shipSpawnParams.SessionType = GameSession.SessionType.Career;
            Plugin.Debug("Generating ghost ship modules with career rules (DisableInFreeMode parts kept).");
        }
    }

    [HarmonyPatch(typeof(ShipPreview), nameof(ShipPreview.GenerateProperties))]
    internal static class ShipPreview_GenerateProperties_Patch
    {
        // Affixes are rolled (PropertyContainerAsset.StartingChance/DiminishRate, 15% for the ghost
        // affix), so the ghost override alone doesn't make a ghost ship. The checkbox always forces
        // it: the ghost property is added whenever the roll didn't already include it.
        private static void Postfix(ShipPreview __instance)
        {
            var ghost = PendingGhost.Current;
            if (ghost == null || !PendingGhost.Matches(__instance.PropertyOverridesRef)) return;
            PendingGhost.Current = null;
            PendingGhost.ActivePreview = __instance;

            var properties = __instance.Properties;
            if (properties == null)
            {
                Plugin.Log.LogWarning("Ghost ship preview has no PropertyContainer; cannot apply ghost property.");
                return;
            }
            if (properties.Contains(ghost.GhostProperty))
            {
                Plugin.Debug($"Ghost property '{ghost.GhostProperty.name}' already present (roll landed). Properties: {RefList(properties)}");
                return;
            }

            var guid = ghost.GhostProperty.AssetGUID;
            if (ghost.Slot.HasValue)
            {
                properties.AssignPropertyReference(ghost.Slot.Value, new AssetReferenceT<ModulePropertyAsset>(guid));
            }
            else
            {
                var affixes = Traverse.Create(properties).Field("mAffixesRef");
                var list = affixes.GetValue<List<AssetReferenceT<ModulePropertyAsset>>>();
                if (list == null)
                {
                    list = new List<AssetReferenceT<ModulePropertyAsset>>();
                    affixes.SetValue(list);
                }
                list.Add(new AssetReferenceT<ModulePropertyAsset>(guid));
            }
            Plugin.Debug($"Ghost property '{ghost.GhostProperty.name}' forced into " +
                $"{(ghost.Slot.HasValue ? ghost.Slot.Value.ToString() : "affixes")}. Properties: {RefList(properties)}");
        }

        private static string RefList(PropertyContainer properties)
        {
            var guids = new List<string>();
            var refs = Traverse.Create(properties).Property("AllReferences").GetValue<IEnumerable<AssetReferenceT<ModulePropertyAsset>>>();
            if (refs == null) return "?";
            foreach (var r in refs)
                guids.Add(r?.RuntimeKey as string ?? "null");
            return string.Join(",", guids);
        }
    }

    // SpawnObjectActionBase<T>.TriggerSpawn checks the *live* session type and does nothing when
    // m_DisableInFreeMode is set -- a runtime gate the career-rules generation swap can't reach.
    // Ghost-ship content spawned by property-driven scripting (PropertyQueryTrigger -> UnityEvent ->
    // Spawn*Action) goes through here, so for the forced ghost ship the flag is cleared on the
    // instance before the check. TriggerSpawn() (no args) forwards to this overload.
    [HarmonyPatch]
    internal static class SpawnObjectActionBase_TriggerSpawn_Patch
    {
        private static readonly HashSet<int> sLogged = new HashSet<int>();

        // The closed SpawnObjectActionBase<T> types, taken from the concrete actions (one of the T's,
        // FXElement, lives in an assembly this project doesn't reference).
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var action in new[] { typeof(SpawnObjectAction), typeof(SpawnModuleAction), typeof(SpawnPooledObjectAction) })
            {
                var closed = action.BaseType!;
                // Matched by arity: GetNestedType on a constructed generic yields the open delegate type.
                var method = AccessTools.GetDeclaredMethods(closed).Find(m => m.Name == "TriggerSpawn" && m.GetParameters().Length == 2);
                if (method != null) yield return method;
                else Plugin.Log.LogWarning($"TriggerSpawn(2) not found on {closed}; FreePlay-disabled spawns won't be bypassed for it.");
            }
        }

        private static void Prefix(Component __instance)
        {
            if (!Plugin.IncludeCareerOnlyModules.Value || !PendingGhost.IsActiveShip()) return;
            var flag = Traverse.Create(__instance).Field("m_DisableInFreeMode");
            if (!flag.GetValue<bool>()) return;
            flag.SetValue(false);
            if (Plugin.DebugLogging.Value && sLogged.Add(__instance.GetInstanceID()))
            {
                var prefab = Traverse.Create(__instance).Field("m_SpawnPrefab").GetValue<Object>();
                Plugin.Log.LogInfo($"Allowed FreePlay-disabled spawn on ghost ship: {__instance.GetType().Name} '{Path(__instance.transform)}' prefab={(prefab != null ? prefab.name : "null")}");
            }
        }

        internal static string Path(Transform t)
        {
            var path = t.name;
            for (var p = t.parent; p != null && path.Length < 200; p = p.parent) path = p.name + "/" + path;
            return path;
        }
    }

    // Diagnostic: property-driven scripting on the ghost ship (what the ghost affix actually turns on).
    [HarmonyPatch(typeof(PropertyQueryTrigger), "OnShipSpawnComplete")]
    internal static class PropertyQueryTrigger_OnShipSpawnComplete_Patch
    {
        private static void Postfix(PropertyQueryTrigger __instance)
        {
            if (!Plugin.DebugLogging.Value || !PendingGhost.IsActiveShip()) return;
            var t = Traverse.Create(__instance);
            var query = t.Field("m_PropertyQuery").GetValue<List<ModulePropertyAsset>>();
            var names = query == null ? "null" : string.Join(",", query.ConvertAll(q => q != null ? q.name : "null"));
            var matched = t.Method("MatchesPropertyQuery").GetValue<bool>();
            Plugin.Log.LogInfo($"[PropertyQueryTrigger] '{SpawnObjectActionBase_TriggerSpawn_Patch.Path(__instance.transform)}' " +
                $"type={t.Field("m_QueryType").GetValue()} query=[{names}] matched={matched}");
        }
    }

    [HarmonyPatch(typeof(LevelSelectController), nameof(LevelSelectController.SetupButtons))]
    internal static class LevelSelectController_SetupButtons_Patch
    {
        private static void Postfix(LevelSelectController __instance)
        {
            PendingGhost.Current = null;
            GhostShipCatalog.EnsureResolving();
            FreePlayToggleUI.Attach(__instance);
        }
    }
}
