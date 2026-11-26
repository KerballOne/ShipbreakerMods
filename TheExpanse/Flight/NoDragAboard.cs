using System.Collections.Generic;
using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace TheExpanse
{
    // Aboard a ship under way, loose things have no drag (as NewtonianPhysics does everywhere): the physics' drag slows a
    // body against the bay, so with the ship at speed it dragged drifting pieces back toward the stern. Each free loose
    // thing has its drag set to zero while the ship is under way and given back when it stops. The game keeps copying
    // each part's drag onto its body (HierarchyParent.ApplyChildData), so that is zeroed again after it, for bodies
    // aboard while under way. (Copied from NewtonianPhysics' DragTuning; patched by hand after the game's ECS is up,
    // for the same reason as there: patching it early crashes HierarchyParent's static constructor.)
    internal static class NoDragAboard
    {
        static readonly Dictionary<Rigidbody, (float drag, float angular)> sSaved = new Dictionary<Rigidbody, (float, float)>();
        static bool sApplied;

        internal static void Zero(Rigidbody body)
        {
            if (body == null) return;
            if (!sSaved.ContainsKey(body)) sSaved[body] = (body.drag, body.angularDrag);
            body.drag = 0f;
            body.angularDrag = 0f;
        }

        internal static void RestoreAll()
        {
            foreach (var pair in sSaved)
                if (pair.Key != null) { pair.Key.drag = pair.Value.drag; pair.Key.angularDrag = pair.Value.angular; }
            sSaved.Clear();
        }

        // on the first real gameplay (the game's ECS running by then)
        internal static void OnGameStateChanged(GameStateChangedEvent ev)
        {
            if (sApplied || ev.GameState != GameSession.GameState.Gameplay) return;
            sApplied = true;
            try
            {
                new Harmony(PluginInfo.PLUGIN_GUID + ".nodrag").Patch(
                    AccessTools.Method(typeof(HierarchyParent), nameof(HierarchyParent.ApplyChildData)),
                    postfix: new HarmonyMethod(AccessTools.Method(typeof(NoDragAboard), nameof(Postfix))));
            }
            catch (System.Exception ex) { Plugin.Log.LogError($"[Flight] no-drag-aboard patch failed; loose things aboard keep their drag: {ex.GetBaseException().Message}"); }
        }

        static void Postfix(HierarchyParent __instance)
        {
            if (!FlightModel.UnderWay || FlightModel.Parked || !__instance.TryGetRidigbody(out Rigidbody rb) || rb == null || rb.isKinematic) return;
            if (!FlightModel.Aboard(rb.position)) return;
            Zero(rb);
        }
    }
}
