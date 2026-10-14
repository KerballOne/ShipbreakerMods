using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace FreePlayPlus
{
    // Fixes saved custom ships loading "dead" (solid, but nothing scannable, cuttable or openable).
    //
    // When a ship loads from a save, each part's GameObject is re-attached to its saved entity:
    // ShipManifestSystem.RecursivelyAddAndInitializeChildren -> EntityBlueprintComponent.Initialize
    // (saved entity) -> ResetComponentObjectsOnGameObject, which calls AddComponentObject for every
    // component on the GameObject. A component whose script is missing (custom ships ship some, e.g.
    // ModdedShipLoader ships' InvisibleJoint objects reference an InvisibleJointMarker script that
    // doesn't exist at runtime) comes back from GetComponents<Component>() as null, and
    // AddComponentObject(entity, null) throws. That aborts the registration walk for the whole
    // module, so the queued entity registrations are never played back and almost every part stays
    // unregistered. Fresh spawns don't take this path, so they work.
    //
    // Same logic as the original, minus the null components: real components are attached exactly as
    // before, so stock ships behave identically.
    [HarmonyPatch(typeof(EntityBlueprintComponent), nameof(EntityBlueprintComponent.ResetComponentObjectsOnGameObject))]
    internal static class MissingScriptFix
    {
        private static int sSkipped;
        private static int sLogged;

        private static bool Prefix(Entity entity, EntityManager entityManager, GameObject gameObject)
        {
            if (!entityManager.Exists(entity) || gameObject == null) return false;
            foreach (var component in gameObject.GetComponents<Component>())
            {
                if (component == null)
                {
                    sSkipped++;
                    if (Plugin.DebugLogging.Value && sLogged < 5)
                    {
                        sLogged++;
                        Plugin.Log.LogInfo($"Skipped a missing-script component on '{gameObject.name}' (#{sSkipped}).");
                    }
                    continue;
                }
                entityManager.AddComponentObject(entity, component);
            }
            return false;
        }
    }
}
