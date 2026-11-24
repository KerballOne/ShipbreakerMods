using BBI.Unity.Game;
using Unity.Entities;
using Unity.Mathematics;
using UnityEngine;

namespace TheExpanse
{
    // Fire and heat for the reactor: setting the player or a part on fire the game's own way, and heat damage to parts
    // that cannot burn.
    internal static class ReactorFire
    {
        static AssetTypeID<DamageTypeAsset> sHeat, sRadiation;
        static bool sLooked, sPlayerChecked;

        internal static void Reset() => sPlayerChecked = false;

        static void FindDamageTypes()
        {
            if (sLooked) return;
            sLooked = true;
            sHeat = AssetTypeID<DamageTypeAsset>.None;
            sRadiation = AssetTypeID<DamageTypeAsset>.None;
            foreach (var asset in Resources.FindObjectsOfTypeAll<DamageTypeAsset>())
            {
                if (asset == null) continue;
                string name = asset.name;
                if (sHeat == AssetTypeID<DamageTypeAsset>.None && name.IndexOf("heat", System.StringComparison.OrdinalIgnoreCase) >= 0) sHeat = asset.ID;
                if (sRadiation == AssetTypeID<DamageTypeAsset>.None && name.IndexOf("radiation", System.StringComparison.OrdinalIgnoreCase) >= 0) sRadiation = asset.ID;
            }
            Plugin.Log.LogInfo($"[Reactor] damage types: heat {(sHeat == AssetTypeID<DamageTypeAsset>.None ? "not found" : "found")}, " +
                               $"radiation {(sRadiation == AssetTypeID<DamageTypeAsset>.None ? "not found" : "found")}.");
        }

        static DynamicBuffer<T> Buffer<T>(EntityManager em, Entity e) where T : struct, IBufferElementData
            => em.HasComponent<T>(e) ? em.GetBuffer<T>(e) : em.AddBuffer<T>(e);

        // The game's own fire on the player: the burning effect, and damage until it goes out. Nothing happens while
        // they are already burning. If the player cannot burn, heat damage stands in for it.
        internal static void IgnitePlayer(EntityManager em, Entity player, float fallbackDamage)
        {
            FindDamageTypes();
            bool flammable = em.HasComponent<FlammableComponent>(player);
            if (!sPlayerChecked)
            {
                sPlayerChecked = true;
                Plugin.Log.LogInfo(flammable ? "[Reactor] the player can be set on fire the game's way."
                                             : "[Reactor] the player has no fire settings; the reactor deals heat damage instead.");
            }
            if (flammable)
            {
                if (em.HasComponent<BurningComponent>(player)) return;
                Buffer<ElementalModificationBuffer>(em, player).Add(new ElementalModificationBuffer
                {
                    ModType = ElementalType.Fire, ModAmount = 100f, Instigator = Entity.Null
                });
                Plugin.Log.LogInfo("[Reactor] the player is on fire.");
                return;
            }
            DamagePlayer(em, player, sHeat, fallbackDamage);
        }

        internal static void IrradiatePlayer(EntityManager em, Entity player, float amount)
        {
            FindDamageTypes();
            DamagePlayer(em, player, sRadiation, amount);
        }

        static void DamagePlayer(EntityManager em, Entity player, AssetTypeID<DamageTypeAsset> type, float amount)
        {
            if (amount <= 0f) return;
            Buffer<ModifyHealth>(em, player).Add(new ModifyHealth
            {
                DamageType = type, Instigator = Entity.Null, Amount = -amount,
                ModType = VitalityData.ModType.Relative, DamageDirection = default
            });
        }

        // Parts that can burn get the game's fire. Returns false if this part cannot burn.
        internal static bool TryIgnitePart(EntityManager em, Entity part)
        {
            if (!em.Exists(part) || !em.HasComponent<FlammableComponent>(part)) return false;
            if (em.HasComponent<BurningComponent>(part)) return true;
            Buffer<ElementalModificationBuffer>(em, part).Add(new ElementalModificationBuffer
            {
                ModType = ElementalType.Fire, ModAmount = 100f, Instigator = Entity.Null
            });
            return true;
        }

        // Heat damage to a part's integrity, the way the game's burning damages parts.
        internal static void HeatPart(StructurePart part, float amount)
        {
            FindDamageTypes();
            var vitality = part.VitalityController;
            if (vitality == null || vitality.EntityBlueprintComponent == null) return;
            VitalitySystem.TryModifyObjectIntegrity(vitality.EntityBlueprintComponent.Entity, new ModifyObjectIntegrity
            {
                DamageType = sHeat, Instigator = Entity.Null, Amount = -amount,
                ModType = VitalityData.ModType.Relative, DamageDirection = float3.zero
            });
        }

        // The game's own reactor meltdown, started the way the game starts it when a reactor is cut loose wrongly.
        // Returns how it was started, for the log.
        internal static string StartMeltdown(EntityManager em, Entity core)
        {
            if (!em.Exists(core)) return "the reactor core is gone";
            if (em.HasComponent<ReactorComponent>(core) && ElementalUtils.CanReceiveElectricityDamage(core, em))
            {
                Buffer<ElementalModificationBuffer>(em, core).Add(new ElementalModificationBuffer
                {
                    ModType = ElementalType.Electricity, ModAmount = float.MaxValue, Instigator = Entity.Null
                });
                return "the game's own reactor meltdown";
            }
            if (!em.HasComponent<ExplodingComponent>(core) && !em.HasComponent<ExplodedComponent>(core))
            {
                em.AddComponentData(core, new ExplodingComponent { RemainingTime = 5f });
                return "a direct explosion (the core takes no electricity)";
            }
            return "nothing: the core is already exploding";
        }
    }
}
