using System.Text;
using BBI.Unity.Game;
using Unity.Collections;
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
            foreach (var node in GhostNodes.Positions)
            {
                float d = Vector3.Distance(at, node);
                if (d >= radius) continue;
                dose += falloff <= 0f ? 1f : Mathf.Pow(1f - d / radius, falloff);
            }

            var health = em.GetComponentData<HealthComponent>(player);
            if (!sAnnounced)
            {
                sAnnounced = true;
                FindDamageType();
                Plugin.Log.LogInfo($"[Nodes] {GhostNodes.Positions.Count} node(s) radiate: {Plugin.NodeRadiationDamage.Value:0.##}% of full health per second " +
                                   $"at a node, out to {radius:0.#} m, falloff {falloff:0.##}. Player health {health.Current:0.#} of {health.Max:0.#}." +
                                   StockRadiationForReference(em));
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

        // TEMPORARY, for choosing defaults: what the game's own radiation sources on this ship are set to.
        static string StockRadiationForReference(EntityManager em)
        {
            var text = new StringBuilder();
            var query = em.CreateEntityQuery(ComponentType.ReadOnly<RadioactiveDecayStage>());
            using (var entities = query.ToEntityArray(Allocator.TempJob))
            {
                int shown = 0;
                foreach (var e in entities)
                {
                    var stages = em.GetBuffer<RadioactiveDecayStage>(e);
                    for (int i = 0; i < stages.Length && shown < 6; i++, shown++)
                        text.Append($" [stage at {stages[i].IntegrityPercent}% integrity: radius {stages[i].NormalEffectRadius:0.##} m, " +
                                    $"player {stages[i].NormalPlayerDamagePerTick:0.##} per {stages[i].NormalPlayerDamageTick:0.##} s]");
                    if (shown >= 6) break;
                }
                text.Insert(0, $" Game's own radiation sources on this ship: {entities.Length}.");
            }
            query.Dispose();
            return text.ToString();
        }
    }
}
