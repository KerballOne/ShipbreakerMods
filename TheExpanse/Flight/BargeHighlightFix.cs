using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // The whole ship stuck red (the barge's "bad" wireframe) after one of its pieces touched a barge.
    //
    // How the game does it: a piece entering the barge's trigger gets InBarge; BargeSystem then flags the piece's
    // hierarchy parent (the GameObject holding its whole structure: for a piece still in the ship's structure, the whole
    // ship) as "bad" and BargeSalvageHighlightController paints every part under it red. When a piece leaves the trigger,
    // BargeForceVolume.OnTriggerExit takes the highlight off (after the game's 3 s buffer), but only when the hierarchy
    // parent's Rigidbody is NOT kinematic. The ship's bodies are held kinematic (by the game for 50 t and up, and by the
    // flight model), so the ship stays red.
    //
    // Here: after that same exit, for a kinematic parent only, the game's own removal is done (the steps it skips):
    // out of BargeSystem's highlighted set (so a new touch highlights it again) and AddPendingRemoval. Nothing runs
    // except on a barge trigger exit.
    [HarmonyPatch(typeof(BargeForceVolume), "OnTriggerExit")]
    internal static class BargeHighlightFix
    {
        static readonly FieldInfo? fController = AccessTools.Field(typeof(BargeForceVolume), "m_HighlightController");

        static void Postfix(BargeForceVolume __instance, Collider other)
        {
            try
            {
                if (!Plugin.Enabled.Value || other == null) return;
                if (!EntityBlueprintComponent.TryGetEntityBlueprint(other.gameObject, out var ebc)) return;
                var em = ebc.EntityManager;
                if (!em.Exists(ebc.Entity) || !em.HasComponent<HierarchyMember>(ebc.Entity)) return;
                var body = em.GetComponentData<HierarchyMember>(ebc.Entity).ParentRigidbody;
                if (body == null || !body.isKinematic) return;          // not kinematic: the game has done it
                var controller = fController?.GetValue(__instance) as BargeSalvageHighlightController;
                var settings = Main.Instance?.MainSettings?.BargeSettings;
                if (controller == null || settings == null || !settings.DestroyHighlightOnExit) return;
                var go = body.gameObject;
                if (!controller.CheckIsBadObject(go)) return;
                World.DefaultGameObjectInjectionWorld?.GetExistingSystem<BargeSystem>()?.HighlightedInvalidObjects.Remove(go);
                controller.AddPendingRemoval(go);
                Plugin.Log.LogInfo($"[Barge] '{other.name}' left the barge: the red highlight on '{go.name}' (kinematic, which the game skips) comes off.");
            }
            catch (System.Exception ex) { Plugin.Log.LogError("[Barge] " + ex); }
        }
    }
}
