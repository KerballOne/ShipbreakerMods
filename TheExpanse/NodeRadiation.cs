using BBI.Unity.Game;
using Unity.Entities;
using UnityEngine;

namespace TheExpanse
{
    // Being near a ghost ship's nodes hurts, as radiation.
    //
    // The game's own radiation has no falloff: a leaking part makes a cloud of a set radius, and anything inside takes
    // the same damage every tick while anything outside takes none. Here each node has a radius too, and the settings
    // choose the shape inside it: Falloff 0 is the game's flat dose, 1 falls in a straight line to nothing at the edge,
    // higher keeps the dose close to the node. Doses from several nodes add up.
    //
    // The dose is handed to the game as a health change of its radiation damage type, so the suit's radiation
    // resistance and invulnerability count as they do for the game's own radiation.
    internal static class NodeRadiation
    {
        static AssetTypeID<DamageTypeAsset> sDamageType;
        static bool sDamageTypeLooked;
        static bool sAnnounced;

        internal static void Reset()
        {
            sAnnounced = false;
        }

        // Called every 'seconds' of game time, after GhostNodes.Refresh.
        internal static void Apply(float seconds)
        {
            if (!Plugin.NodeRadiation.Value || GhostNodes.Positions.Count == 0) return;
            var world = World.DefaultGameObjectInjectionWorld;
            var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
            if (world == null || !world.IsCreated || player == Entity.Null) return;
            var em = world.EntityManager;
            if (!em.Exists(player) || !em.HasComponent<HealthComponent>(player)) return;
            if (!GhostNodes.TryGetPosition(em, player, out var at)) return;

            float radius = Mathf.Max(0.1f, Plugin.NodeRadiationRadius.Value);
            float falloff = Mathf.Max(0f, Plugin.NodeRadiationFalloff.Value);
            float dose = 0f;                                    // in "nodes' worth": 1 = touching one node
            for (int i = 0; i < GhostNodes.Positions.Count; i++)
            {
                float reach = radius * GhostNodes.Reach[i];             // a larger node reaches further
                float d = Vector3.Distance(at, GhostNodes.Positions[i]);
                if (d >= reach) continue;
                dose += falloff <= 0f ? 1f : Mathf.Pow(1f - d / reach, falloff);
            }

            var health = em.GetComponentData<HealthComponent>(player);
            if (!sAnnounced)
            {
                sAnnounced = true;
                FindDamageType();
                Plugin.Log.LogInfo($"[Nodes] {GhostNodes.Positions.Count} node(s) radiate: {Plugin.NodeRadiationDamage.Value:0.##}% of full health per second " +
                                   $"at a node, out to {radius:0.#} m ({LargeNodes()}), falloff {falloff:0.##}. Player health {health.Current:0.#} of {health.Max:0.#}.");
            }
            if (dose <= 0f) return;

            float amount = dose * Mathf.Max(0f, Plugin.NodeRadiationDamage.Value) / 100f * health.Max * seconds;
            if (amount <= 0f) return;
            var changes = em.HasComponent<ModifyHealth>(player) ? em.GetBuffer<ModifyHealth>(player) : em.AddBuffer<ModifyHealth>(player);
            changes.Add(new ModifyHealth
            {
                DamageType = sDamageType,
                Instigator = Entity.Null,
                Amount = -amount,
                ModType = VitalityData.ModType.Relative,
                DamageDirection = default
            });
        }

        static string LargeNodes()
        {
            int large = 0; float most = 1f;
            foreach (float r in GhostNodes.Reach) { if (r > 1.01f) large++; most = Mathf.Max(most, r); }
            return large == 0 ? "all nodes normal size" : $"{large} larger node(s), reaching up to {most:0.#}x as far";
        }

        static void FindDamageType()
        {
            if (sDamageTypeLooked) return;
            sDamageTypeLooked = true;
            sDamageType = AssetTypeID<DamageTypeAsset>.None;
            foreach (var asset in Resources.FindObjectsOfTypeAll<DamageTypeAsset>())
            {
                if (asset == null || asset.name.IndexOf("radiation", System.StringComparison.OrdinalIgnoreCase) < 0) continue;
                sDamageType = asset.ID;
                Plugin.Log.LogInfo($"[Nodes] node damage uses the game's damage type '{asset.name}'.");
                return;
            }
            Plugin.Log.LogWarning("[Nodes] the game's radiation damage type was not found; node damage is dealt as untyped damage, so radiation resistance will not count.");
        }
    }
}
