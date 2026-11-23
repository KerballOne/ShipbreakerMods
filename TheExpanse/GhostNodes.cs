using System.Collections.Generic;
using BBI.Unity.Game;
using Unity.Collections;
using Unity.Entities;
using Unity.Transforms;
using UnityEngine;

namespace TheExpanse
{
    // The nodes of a ghost ship (the Protomolecule nodes on the MCRN Corvette): where they are and how much of the
    // haunting is left.
    //
    // A node is any entity carrying the game's HauntingCauser, the same thing the game's own haunting counts. The game
    // adds up their haunting amounts and compares that with the total at spawn; this does the same, so "share left" here
    // matches the game's own figure. A node that has been destroyed or salvaged is gone from the list.
    internal static class GhostNodes
    {
        internal static readonly List<Vector3> Positions = new List<Vector3>();
        internal static float Total;            // haunting amount of the nodes still there
        internal static float Original;         // the most seen on this ship

        // Share of the haunting left, 1 at spawn falling to 0 with the last node. 0 on a ship with no nodes.
        internal static float ShareLeft => Original > 0f ? Mathf.Clamp01(Total / Original) : 0f;

        static EntityQuery sNodes;
        static World? sQueryWorld;

        internal static void Reset()
        {
            Positions.Clear();
            Total = 0f;
            Original = 0f;
        }

        internal static void Refresh()
        {
            Positions.Clear();
            Total = 0f;
            var world = World.DefaultGameObjectInjectionWorld;
            if (world == null || !world.IsCreated) return;
            var em = world.EntityManager;
            if (sQueryWorld != world)
            {
                sNodes = em.CreateEntityQuery(new EntityQueryDesc
                {
                    All = new[] { ComponentType.ReadOnly<HauntingCauser>() },
                    None = new[]
                    {
                        ComponentType.ReadOnly<PendingDestroySalvageable>(),
                        ComponentType.ReadOnly<PendingDestruction>(),
                        ComponentType.ReadOnly<PendingSalvageSalvageable>()
                    }
                });
                sQueryWorld = world;
            }
            using (var entities = sNodes.ToEntityArray(Allocator.TempJob))
            {
                foreach (var e in entities)
                {
                    Total += em.GetComponentData<HauntingCauser>(e).HauntingAmount;
                    if (TryGetPosition(em, e, out var position)) Positions.Add(position);
                }
            }
            if (Total > Original) Original = Total;
        }

        // The object's own transform first: it is right even while the ship is being turned.
        internal static bool TryGetPosition(EntityManager em, Entity e, out Vector3 position)
        {
            position = default;
            if (em.HasComponent<Transform>(e))
            {
                var transform = em.GetComponentObject<Transform>(e);
                if (transform != null) { position = transform.position; return true; }
            }
            if (em.HasComponent<LocalToWorld>(e)) { position = em.GetComponentData<LocalToWorld>(e).Position; return true; }
            return false;
        }
    }
}
