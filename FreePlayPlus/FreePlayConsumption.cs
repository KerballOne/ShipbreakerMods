using System.Collections;
using System.Collections.Generic;
using System.Reflection.Emit;
using BBI.Unity;
using BBI.Unity.Game;
using HarmonyLib;
using Unity.Entities;
using UnityEngine;

namespace FreePlayPlus
{
    // FreePlay makes fuel, demo charges and tethers unlimited with inline
    // "CurrentLevelData.SessionType == FreeMode" checks (and a tether count of 1000). The
    // transpilers below insert one stack-neutral call after each SessionType read in the specific
    // method that owns a resource, which reports SessionType.None instead of FreeMode while that
    // resource's Consume* setting is on. None takes the same branch career does (the limited one)
    // without satisfying any "== Career" check, so nothing career-only is switched on.
    internal enum Resource { Fuel, DemoCharges, Tethers }

    internal static class FreePlayConsumption
    {
        private static readonly AccessTools.FieldRef<DemoChargeController, bool> sUnlimitedDemoCharge =
            AccessTools.FieldRefAccess<DemoChargeController, bool>("mUnlimitedDemoCharge");

        internal static bool Consumes(Resource resource) =>
            FreePlayDamage.IsFreePlay && resource switch
            {
                Resource.Fuel => Plugin.ConsumeFuel.Value,
                Resource.DemoCharges => Plugin.ConsumeDemoCharges.Value,
                Resource.Tethers => Plugin.ConsumeTethers.Value,
                _ => false,
            };

        // Inserted after "ldfld LevelData::SessionType".
        public static GameSession.SessionType MaskSessionType(GameSession.SessionType sessionType, int resource) =>
            sessionType == GameSession.SessionType.FreeMode && Consumes((Resource)resource)
                ? GameSession.SessionType.None
                : sessionType;

        // Replaces calls to DemoChargeController.get_UnlimitedDemoCharges (FreeMode || event flag).
        // Replacing the call sites rather than patching the getter keeps it working even if the JIT
        // inlines the getter.
        public static bool UnlimitedDemoCharges(DemoChargeController controller) =>
            Consumes(Resource.DemoCharges) ? sUnlimitedDemoCharge(controller) : FreePlayDamage.IsFreePlay || sUnlimitedDemoCharge(controller);

        // Replaces calls to DemoChargeCapacityUIController.get_IsFreeMode, which shows "∞".
        public static bool DemoChargeUiIsFreeMode(DemoChargeCapacityUIController controller) =>
            FreePlayDamage.IsFreePlay && !Consumes(Resource.DemoCharges);

        internal static IEnumerable<CodeInstruction> MaskSessionTypeReads(IEnumerable<CodeInstruction> instructions, Resource resource, string where)
        {
            var sessionTypeField = AccessTools.Field(typeof(LevelAsset.LevelData), nameof(LevelAsset.LevelData.SessionType));
            var mask = AccessTools.Method(typeof(FreePlayConsumption), nameof(MaskSessionType));
            var count = 0;
            foreach (var instruction in instructions)
            {
                yield return instruction;
                if (instruction.LoadsField(sessionTypeField))
                {
                    yield return new CodeInstruction(OpCodes.Ldc_I4, (int)resource);
                    yield return new CodeInstruction(OpCodes.Call, mask);
                    count++;
                }
            }
            if (count == 0) Plugin.Log.LogWarning($"No SessionType read found in {where}; {resource} stays unlimited in FreePlay.");
        }

        internal static IEnumerable<CodeInstruction> ReplaceCall(IEnumerable<CodeInstruction> instructions, System.Reflection.MethodInfo target, System.Reflection.MethodInfo replacement)
        {
            foreach (var instruction in instructions)
            {
                if (instruction.Calls(target))
                    yield return new CodeInstruction(OpCodes.Call, replacement).MoveLabelsFrom(instruction).MoveBlocksFrom(instruction);
                else
                    yield return instruction;
            }
        }
    }

    [HarmonyPatch(typeof(ThrustController), "Update")]
    internal static class ThrustController_Update_Patch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            FreePlayConsumption.MaskSessionTypeReads(instructions, Resource.Fuel, "ThrustController.Update");
    }

    // Update resets the count to 1000 in FreePlay; TryCreateTether skips the decrement.
    [HarmonyPatch]
    internal static class TetherController_Update_Patch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            yield return AccessTools.Method(typeof(TetherController), "Update");
            yield return AccessTools.Method(typeof(TetherController), "TryCreateTether");
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, System.Reflection.MethodBase __originalMethod) =>
            FreePlayConsumption.MaskSessionTypeReads(instructions, Resource.Tethers, "TetherController." + __originalMethod.Name);
    }

    // Stock caps a purchase at 1000 outside career, so a FreePlay purchase at the career carry
    // limit was accepted (and charged) without adding anything. Use career's cap.
    [HarmonyPatch(typeof(TetherController), "OnTetherPurchased")]
    internal static class TetherController_OnTetherPurchased_Patch
    {
        private static bool Prefix(TetherController __instance, PurchaseTetherEvent eventReceived)
        {
            if (!FreePlayConsumption.Consumes(Resource.Tethers)) return true;
            var max = Traverse.Create(Traverse.Create(__instance).Field("mData").GetValue()).Property("MaxTethers").GetValue<int>();
            var canPurchase = __instance.NumAvailableTethers < max;
            if (canPurchase)
                Main.EventSystem.Post(TetherChangedEvent.GetEvent(TetherChangedEvent.TetherState.AddTethers, eventReceived.NumTethers));
            eventReceived.Callback?.Invoke(canPurchase);
            return false;
        }
    }

    // Outside career and race, Initialize hands out 1000 tethers; give FreePlay career's carry limit.
    [HarmonyPatch(typeof(TetherController), "Initialize")]
    internal static class TetherController_Initialize_Patch
    {
        private static void Postfix(TetherController __instance)
        {
            if (!FreePlayConsumption.Consumes(Resource.Tethers)) return;
            var t = Traverse.Create(__instance);
            var max = Traverse.Create(t.Field("mData").GetValue()).Property("MaxTethers").GetValue<int>();
            t.Property("NumAvailableTethers").SetValue(max);
            Plugin.Debug($"FreePlay tethers limited to {max}.");
        }
    }

    [HarmonyPatch]
    internal static class DemoChargeController_UnlimitedDemoCharges_Patch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var getter = AccessTools.PropertyGetter(typeof(DemoChargeController), "UnlimitedDemoCharges");
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(DemoChargeController)))
            {
                if (method == getter || method.IsAbstract || method.GetMethodBody() == null) continue;
                if (PatchProcessor.GetOriginalInstructions(method).Exists(i => i.Calls(getter))) yield return method;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            FreePlayConsumption.ReplaceCall(instructions,
                AccessTools.PropertyGetter(typeof(DemoChargeController), "UnlimitedDemoCharges"),
                AccessTools.Method(typeof(FreePlayConsumption), nameof(FreePlayConsumption.UnlimitedDemoCharges)));
    }

    [HarmonyPatch]
    internal static class DemoChargeCapacityUIController_IsFreeMode_Patch
    {
        private static IEnumerable<System.Reflection.MethodBase> TargetMethods()
        {
            var getter = AccessTools.PropertyGetter(typeof(DemoChargeCapacityUIController), "IsFreeMode");
            foreach (var method in AccessTools.GetDeclaredMethods(typeof(DemoChargeCapacityUIController)))
            {
                if (method == getter || method.IsAbstract || method.GetMethodBody() == null) continue;
                if (PatchProcessor.GetOriginalInstructions(method).Exists(i => i.Calls(getter))) yield return method;
            }
        }

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
            FreePlayConsumption.ReplaceCall(instructions,
                AccessTools.PropertyGetter(typeof(DemoChargeCapacityUIController), "IsFreeMode"),
                AccessTools.Method(typeof(FreePlayConsumption), nameof(FreePlayConsumption.DemoChargeUiIsFreeMode)));
    }

    // Kiosk refills are instant in stock. With RefillSeconds > 0, a FreePlay O2/fuel purchase is
    // charged immediately but added to a pending refill that fills at (amount / RefillSeconds) per
    // second of game time, and only while the kiosk screen is open: leaving pauses it, coming back
    // resumes it without buying again. Pending refills are dropped on death/respawn and new spawns.
    //
    // A kiosk purchase is VendingMachineItemUI.OnClick -> its prefab purchase action's TryPurchase.
    // Fuel arrives synchronously (PurchaseFuel's UpdateThrusterChargeEvent). O2 goes through a
    // TriggerableComponent.ForceTrigger(), which only *queues* the trigger for a later frame, so the
    // triggerables queued during OnClick are remembered and recognised when they actually fire.
    // Ship pickups use the same triggerables outside OnClick, so they stay instant.
    [HarmonyPatch(typeof(VendingMachineItemUI), nameof(VendingMachineItemUI.OnClick))]
    internal static class VendingMachineItemUI_OnClick_Patch
    {
        internal static bool InKioskPurchase;

        private static void Prefix(VendingMachineItemUI __instance)
        {
            InKioskPurchase = true;
            if (Plugin.DebugLogging.Value)
            {
                var action = Traverse.Create(__instance).Field("mPurchaseAction").GetValue<VendingMachinePurchaseBase>();
                Plugin.Debug($"Kiosk purchase via {(action != null ? action.GetType().Name : "inventory item")}.");
            }
        }

        private static void Finalizer() => InKioskPurchase = false;
    }

    [HarmonyPatch(typeof(TriggerableComponent), "QueueTrigger")]
    internal static class TriggerableComponent_QueueTrigger_Patch
    {
        internal static readonly HashSet<TriggerableComponent> KioskQueued = new HashSet<TriggerableComponent>();
        private static readonly HashSet<string> sLoggedTypes = new HashSet<string>();

        private static void Prefix(TriggerableComponent __instance)
        {
            if (!VendingMachineItemUI_OnClick_Patch.InKioskPurchase || !TimedRefill.Enabled) return;
            KioskQueued.Add(__instance);
            var type = __instance.GetType().Name;
            if (!(__instance is TriggerableVitalityChange) && !(__instance is TriggerableThrusterCharge) && !(__instance is TriggerableDamagePlayer) && sLoggedTypes.Add(type))
                Plugin.Log.LogInfo($"Kiosk purchase queued a {type}; that purchase applies instantly (no timed refill for it).");
        }

        internal static bool TakeKiosk(TriggerableComponent trigger) => KioskQueued.Remove(trigger);
    }

    internal static class TimedRefill
    {
        private static float sPendingFuel, sFuelRate, sPendingOxygen, sOxygenRate, sPendingHealth, sHealthRate;
        private static ThrustController? sThrust;
        private static VendingMachineUIController? sKiosk;
        private static System.Action<ThrustController, float>? sSetCharge;

        internal static bool Enabled => FreePlayDamage.IsFreePlay && Plugin.RefillSeconds.Value > 0f;

        internal static float PendingFuel => sPendingFuel;

        internal static float PendingOxygen => sPendingOxygen;

        internal static float PendingHealth => sPendingHealth;

        internal static void Clear(string why)
        {
            if (sPendingFuel > 0f || sPendingOxygen > 0f || sPendingHealth > 0f) Plugin.Debug($"Pending kiosk refills dropped ({why}).");
            sPendingFuel = sPendingOxygen = sPendingHealth = 0f;
            TriggerableComponent_QueueTrigger_Patch.KioskQueued.Clear();
        }

        internal static void AddFuel(ThrustController thrust, float amount)
        {
            sThrust = thrust;
            sPendingFuel += amount;
            sFuelRate = amount / Plugin.RefillSeconds.Value;
            Plugin.Debug($"Kiosk fuel refill queued: +{amount:F1} (pending {sPendingFuel:F1}).");
        }

        internal static void AddOxygen(float amount)
        {
            sPendingOxygen += amount;
            sOxygenRate = amount / Plugin.RefillSeconds.Value;
            Plugin.Debug($"Kiosk oxygen refill queued: +{amount:F1} (pending {sPendingOxygen:F1}).");
        }

        internal static void AddHealth(float amount)
        {
            sPendingHealth += amount;
            sHealthRate = amount / Plugin.RefillSeconds.Value;
            Plugin.Debug($"Kiosk health refill queued: +{amount:F1} (pending {sPendingHealth:F1}).");
        }

        internal static bool IsTimed(BuffableCategory category) =>
            category == BuffableCategory.Oxygen || category == BuffableCategory.Health;

        internal static void Add(BuffableCategory category, float amount)
        {
            if (category == BuffableCategory.Oxygen) AddOxygen(amount);
            else if (category == BuffableCategory.Health) AddHealth(amount);
        }

        internal static bool TryGetVitality(EntityManager entityManager, Entity player, BuffableCategory category, out float current, out float max)
        {
            current = max = 0f;
            if (!entityManager.Exists(player)) return false;
            if (category == BuffableCategory.Oxygen && entityManager.HasComponent<OxygenComponent>(player))
            {
                var oxygen = entityManager.GetComponentData<OxygenComponent>(player);
                current = oxygen.Current; max = oxygen.Max;
                return true;
            }
            if (category == BuffableCategory.Health && entityManager.HasComponent<HealthComponent>(player))
            {
                var health = entityManager.GetComponentData<HealthComponent>(player);
                current = health.Current; max = health.Max;
                return true;
            }
            return false;
        }

        private static bool KioskOpen()
        {
            if (sKiosk == null) sKiosk = Object.FindObjectOfType<VendingMachineUIController>();
            return sKiosk != null && sKiosk.IsVendingMachineUIControllerActive;
        }

        // Called from Plugin.Update.
        internal static void Tick()
        {
            if (sPendingFuel <= 0f && sPendingOxygen <= 0f && sPendingHealth <= 0f) return;
            if (!FreePlayDamage.IsFreePlay) { Clear("left FreePlay"); return; }
            if (!KioskOpen()) return;
            var dt = Time.deltaTime;

            if (sPendingFuel > 0f)
            {
                if (sThrust == null) sThrust = Object.FindObjectOfType<ThrustController>();
                if (sThrust != null)
                {
                    sSetCharge ??= AccessTools.MethodDelegate<System.Action<ThrustController, float>>(AccessTools.PropertySetter(typeof(ThrustController), "Charge"));
                    var step = Mathf.Min(sFuelRate * dt, sPendingFuel);
                    var room = sThrust.MaxCharge - sThrust.Charge;
                    if (room <= 0f) sPendingFuel = 0f;
                    else
                    {
                        sSetCharge(sThrust, sThrust.Charge + Mathf.Min(step, room));
                        sPendingFuel -= step;
                    }
                }
            }

            if (sPendingOxygen > 0f)
            {
                var world = World.DefaultGameObjectInjectionWorld;
                var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
                if (world != null && player != Entity.Null && world.EntityManager.Exists(player) && world.EntityManager.HasComponent<OxygenComponent>(player))
                {
                    var oxygen = world.EntityManager.GetComponentData<OxygenComponent>(player);
                    var step = Mathf.Min(sOxygenRate * dt, sPendingOxygen);
                    if (oxygen.Current >= oxygen.Max) sPendingOxygen = 0f;
                    else
                    {
                        oxygen.Current = Mathf.Min(oxygen.Current + step, oxygen.Max);
                        world.EntityManager.SetComponentData(player, oxygen);
                        sPendingOxygen -= step;
                    }
                }
            }

            if (sPendingHealth > 0f)
            {
                var world = World.DefaultGameObjectInjectionWorld;
                var player = PlayerProfileService.Instance?.Profile?.PlayerEntity ?? Entity.Null;
                if (world != null && player != Entity.Null && world.EntityManager.Exists(player) && world.EntityManager.HasComponent<HealthComponent>(player))
                {
                    var health = world.EntityManager.GetComponentData<HealthComponent>(player);
                    var step = Mathf.Min(sHealthRate * dt, sPendingHealth);
                    if (health.Current >= health.Max) sPendingHealth = 0f;
                    else
                    {
                        health.Current = Mathf.Min(health.Current + step, health.Max);
                        world.EntityManager.SetComponentData(player, health);
                        sPendingHealth -= step;
                    }
                }
            }
        }
    }

    // Fuel: PurchaseFuel posts UpdateThrusterChargeEvent synchronously inside OnClick; a kiosk
    // TriggerableThrusterCharge posts it when its queued trigger fires (flagged below).
    [HarmonyPatch(typeof(ThrustController), "OnThrustChargeUpdated")]
    internal static class ThrustController_OnThrustChargeUpdated_Patch
    {
        internal static bool InKioskThrusterTrigger;

        private static bool Prefix(ThrustController __instance, UpdateThrusterChargeEvent ev)
        {
            var kiosk = VendingMachineItemUI_OnClick_Patch.InKioskPurchase || InKioskThrusterTrigger;
            if (!kiosk || !TimedRefill.Enabled || ev.ChargeAmount <= 0f) return true;
            // Refuse (and don't charge) if what's already pending fills the tank anyway.
            var canPurchase = __instance.Charge + TimedRefill.PendingFuel < __instance.MaxCharge;
            ev.PurchaseCallback?.Invoke(canPurchase);
            if (canPurchase) TimedRefill.AddFuel(__instance, ev.ChargeAmount);
            return false;
        }
    }

    [HarmonyPatch(typeof(TriggerableThrusterCharge), "OnTrigger")]
    internal static class TriggerableThrusterCharge_OnTrigger_Patch
    {
        private static void Prefix(TriggerableThrusterCharge __instance) =>
            ThrustController_OnThrustChargeUpdated_Patch.InKioskThrusterTrigger = TriggerableComponent_QueueTrigger_Patch.TakeKiosk(__instance);

        private static void Finalizer() => ThrustController_OnThrustChargeUpdated_Patch.InKioskThrusterTrigger = false;
    }

    // Oxygen / health: PurchaseOxygen, PurchaseHealth or PurchaseTriggerable may queue a TriggerableVitalityChange.
    [HarmonyPatch(typeof(TriggerableVitalityChange), "OnTrigger")]
    internal static class TriggerableVitalityChange_OnTrigger_Patch
    {
        private static bool Prefix(TriggerableVitalityChange __instance)
        {
            if (!TriggerableComponent_QueueTrigger_Patch.TakeKiosk(__instance) || !TimedRefill.Enabled) return true;
            var t = Traverse.Create(__instance);
            var vitalityType = t.Field("m_VitalityType").GetValue<VitalityTypeAsset>();
            if (vitalityType == null || !TimedRefill.IsTimed(vitalityType.BuffableCategory)) return true;

            var player = PlayerProfileService.Instance.Profile.PlayerEntity;
            var entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!TimedRefill.TryGetVitality(entityManager, player, vitalityType.BuffableCategory, out var current, out var max)) return true;

            var modType = t.Field("m_ModificationType").GetValue<VitalityData.ModType>();
            var gain = Gain(current, max, t.Field("m_Amount").GetValue<float>(), modType);
            if (gain > 0f) TimedRefill.Add(vitalityType.BuffableCategory, gain);
            Main.EventSystem.Post(TriggerableVitalityEvent.GetEvent(modType, vitalityType));
            return false;
        }

        // How much a modification adds, per the vitality systems' ModType semantics.
        internal static float Gain(float current, float max, float amount, VitalityData.ModType modType) => modType switch
        {
            VitalityData.ModType.Relative => amount,
            VitalityData.ModType.RelativeFractional => amount * max,
            VitalityData.ModType.Fractional => amount * max - current,
            _ => amount - current,
        };
    }

    // Oxygen and health, as the FreePlay kiosk actually sells them: PurchaseOxygen / PurchaseHealth
    // queue a TriggerableDamagePlayer whose DamageAsset has an O2- or health-gaining
    // VitalityModification (VitalitySystem passes each one's Amount/ModType straight into
    // ModifyOxygen/ModifyHealth). Those gains become a pending refill; any other modifications in
    // the same asset are applied as stock.
    [HarmonyPatch(typeof(TriggerableDamagePlayer), "OnTrigger")]
    internal static class TriggerableDamagePlayer_OnTrigger_Patch
    {
        private static bool Prefix(TriggerableDamagePlayer __instance)
        {
            if (!TriggerableComponent_QueueTrigger_Patch.TakeKiosk(__instance) || !TimedRefill.Enabled) return true;
            var damage = Traverse.Create(__instance).Field("m_DamageAsset").GetValue<DamageAsset>()?.Data;
            if (damage?.VitalityModifications == null) return true;

            var player = PlayerProfileService.Instance.Profile.PlayerEntity;
            var entityManager = World.DefaultGameObjectInjectionWorld.EntityManager;
            if (!entityManager.Exists(player)) return true;

            // Resolve each modification's amount once (Range amounts are re-rolled per read). Only
            // O2/health gains are timed; everything else is applied as stock.
            var mods = new List<(DamageData.VitalityModification mod, float amount, bool timed)>();
            var gains = new Dictionary<BuffableCategory, float>();
            foreach (var mod in damage.VitalityModifications)
            {
                var amount = mod.Amount;
                var timed = false;
                var category = mod.VitalityType != null ? mod.VitalityType.BuffableCategory : BuffableCategory.None;
                if (TimedRefill.IsTimed(category) && TimedRefill.TryGetVitality(entityManager, player, category, out var current, out var max))
                {
                    var gain = TriggerableVitalityChange_OnTrigger_Patch.Gain(current, max, amount, mod.ModificationType);
                    if (gain > 0f)
                    {
                        timed = true;
                        gains[category] = (gains.TryGetValue(category, out var sum) ? sum : 0f) + gain;
                    }
                }
                mods.Add((mod, amount, timed));
            }
            if (gains.Count == 0) return true;

            foreach (var gain in gains) TimedRefill.Add(gain.Key, gain.Value);
            var damageType = damage.DamageType != null ? damage.DamageType.ID : AssetTypeID<DamageTypeAsset>.None;
            foreach (var (mod, amount, timed) in mods)
            {
                if (timed || mod.VitalityType == null) continue;
                VitalitySystem.TryModifyVitality(player, mod.VitalityType, damageType, Entity.Null, amount, mod.ModificationType);
            }
            return false;
        }
    }

    // Stock only refuses an O2 purchase when current O2 is near full; also count what's already
    // pending, so a repeat purchase that would fill nothing isn't charged.
    [HarmonyPatch(typeof(PurchaseOxygen), "CanModifyVitality")]
    internal static class PurchaseOxygen_CanModifyVitality_Patch
    {
        private static void Postfix(PurchaseOxygen __instance, OxygenComponent component, ref bool __result)
        {
            if (!__result || !TimedRefill.Enabled || TimedRefill.PendingOxygen <= 0f) return;
            var minMissing = Traverse.Create(__instance).Field("m_MinOxygenMissingToPurchase").GetValue<float>();
            __result = component.Current + TimedRefill.PendingOxygen < component.Max - minMissing;
        }
    }

    // Same for medkits: stock refuses one only at full health.
    [HarmonyPatch(typeof(PurchaseHealth), "CanModifyVitality")]
    internal static class PurchaseHealth_CanModifyVitality_Patch
    {
        private static void Postfix(HealthComponent component, ref bool __result)
        {
            if (!__result || !TimedRefill.Enabled || TimedRefill.PendingHealth <= 0f) return;
            __result = component.Current + TimedRefill.PendingHealth < component.Max;
        }
    }

    // FreePlay's throwaway profile uses the default difficulty, whose StartingDebtAmount
    // ($1,252,594,441.92) is what the kiosk shows (debt = StartingDebtAmount - Credits). Give the
    // FreePlay profile its own copy of that asset with no starting debt, so the kiosk starts at $0
    // and counts up with purchases. The shared asset career uses is never modified.
    [HarmonyPatch(typeof(PlayerProfileService), nameof(PlayerProfileService.SetActiveFreeModeProfile))]
    internal static class PlayerProfileService_SetActiveFreeModeProfile_Patch
    {
        private static DifficultyModeAsset? sZeroDebtMode;

        private static void Postfix(PlayerProfileService __instance, bool bValue)
        {
            if (!bValue) return;
            var profile = __instance.Profile;
            if (profile == null || profile.PlayerProfileType != PlayerProfileType.FreeMode) return;
            if (sZeroDebtMode == null)
            {
                sZeroDebtMode = Object.Instantiate(profile.DifficultyMode);
                sZeroDebtMode.name = profile.DifficultyMode.name + "_FreePlayZeroDebt";
                Traverse.Create(sZeroDebtMode).Field("m_StartingDebtAmount").SetValue(0.0);
            }
            Traverse.Create(profile).Field("mDifficultyMode").SetValue(sZeroDebtMode);
            Plugin.Debug("FreePlay profile starts at $0 debt.");
        }
    }

    // Demo charges unlock via UnlockAbilityEvent(DemoCharge) (from the level's starting upgrades).
    // Stock FreePlay never notices - unlimited charges skip the lock - but with ConsumeDemoCharges a
    // custom level without FreePlay upgrades leaves them locked (buyable, not selectable). FreePlay
    // always has demo charges, so unlock them if they're still locked once the controller starts.
    [HarmonyPatch(typeof(DemoChargeController), "Start")]
    internal static class DemoChargeController_Start_Patch
    {
        private static void Postfix(DemoChargeController __instance)
        {
            var unlocked = __instance.IsUnlocked;
            Plugin.Debug($"Demo charges at spawn: unlocked={unlocked}, freePlay={FreePlayDamage.IsFreePlay}, consume={Plugin.ConsumeDemoCharges.Value}.");
            if (unlocked || !FreePlayConsumption.Consumes(Resource.DemoCharges)) return;
            Traverse.Create(__instance).Method("OnDemoChargeUnlocked", UnlockAbilityEvent.GetEvent(UnlockAbilityID.DemoCharge)).GetValue();
            Plugin.Log.LogInfo("FreePlay: demo charges were locked at spawn; unlocked them.");
        }
    }
}
