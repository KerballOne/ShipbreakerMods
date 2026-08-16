using System.Reflection;
using BBI.Unity.Game;
using HarmonyLib;

namespace PartInfoLogger
{
    // One-off diagnostic tracing for the baked-ThrusterFuel-doesn't-grant-fuel investigation. Static
    // component/field dumps (PickupInspector) now match exactly between the working addressable and the
    // broken baked copy, so the remaining difference must be in the ACTUAL RUNTIME CALL PATH (method
    // entry, early returns, object-identity checks) that a serialized-field dump can never show. Rather
    // than guessing which single method matters, this traces every method in the chain from "player
    // presses interact" down to "OnTrigger() actually fires" so a full call sequence is visible for
    // both objects side by side. Gated behind EnrichmentEnabled like the rest of PIL; remove once the
    // root cause is found — this is not meant to be permanent tooling.
    static class TT
    {
        public static void Log(string msg)
        {
            if (!Plugin.EnrichmentEnabled.Value) return;
            Plugin.Log.LogInfo($"[TriggerableTrace] {msg}");
        }

        public static string GoInfo(UnityEngine.GameObject go) =>
            go == null ? "null" : $"'{go.name}'(id={go.GetInstanceID()})";
    }

    // ── InteractionController: the top of the chain — did the player's interact input even reach here? ──
    [HarmonyPatch(typeof(InteractionController), "HandleInteractionProgress")]
    static class Patch_InteractionController_HandleInteractionProgress
    {
        static void Prefix(InteractionController __instance)
        {
            var mCurrentInteractable = AccessTools.Field(typeof(InteractionController), "mCurrentInteractable")?.GetValue(__instance) as InteractableObject;
            if (mCurrentInteractable == null) return; // don't spam every frame when nothing is targeted
            TT.Log($"InteractionController.HandleInteractionProgress: mCurrentInteractable={TT.GoInfo(mCurrentInteractable.gameObject)} " +
                $"IsComplete={mCurrentInteractable.IsComplete} JustCompleted={mCurrentInteractable.JustCompleted} " +
                $"IsInteractable={mCurrentInteractable.IsInteractable} IsPressed={mCurrentInteractable.IsPressed}");
        }
    }

    // ── InteractableObject: state machine for the interaction itself ──
    [HarmonyPatch(typeof(InteractableObject), "Complete")]
    static class Patch_InteractableObject_Complete
    {
        static void Prefix(InteractableObject __instance) =>
            TT.Log($"InteractableObject.Complete() ENTER on {TT.GoInfo(__instance.gameObject)}");
        static void Postfix(InteractableObject __instance) =>
            TT.Log($"InteractableObject.Complete() EXIT (no exception) on {TT.GoInfo(__instance.gameObject)}");
        static System.Exception Finalizer(InteractableObject __instance, System.Exception __exception)
        {
            if (__exception != null)
                TT.Log($"InteractableObject.Complete() THREW on {TT.GoInfo(__instance.gameObject)}: {__exception}");
            return __exception;
        }
    }

    [HarmonyPatch(typeof(InteractableObject), "CheckConditions")]
    static class Patch_InteractableObject_CheckConditions
    {
        static void Postfix(InteractableObject __instance, bool __result) =>
            TT.Log($"InteractableObject.CheckConditions() on {TT.GoInfo(__instance.gameObject)} => {__result}");
    }

    // ── TriggerableComponent: the base class every Triggerable* (including TriggerableThrusterCharge) shares ──
    [HarmonyPatch(typeof(TriggerableComponent), "OnInteractionEvent")]
    static class Patch_TriggerableComponent_OnInteractionEvent
    {
        static void Prefix(TriggerableComponent __instance, InteractionEvent evt)
        {
            var evtGo = evt?.InteractableObject?.gameObject;
            var selfGo = __instance.gameObject;
            TT.Log($"{__instance.GetType().Name}.OnInteractionEvent on {TT.GoInfo(selfGo)}: evt.Interaction={evt?.Interaction} " +
                $"evt.InteractableObject.gameObject={TT.GoInfo(evtGo)} IDENTITY_MATCH={evtGo == selfGo}");
        }
    }

    [HarmonyPatch(typeof(TriggerableComponent), "ShouldTrigger")]
    static class Patch_TriggerableComponent_ShouldTrigger
    {
        static void Postfix(TriggerableComponent __instance, object typeCheck, bool __result) =>
            TT.Log($"{__instance.GetType().Name}.ShouldTrigger({typeCheck}) on {TT.GoInfo(__instance.gameObject)} => {__result}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "QueueTrigger")]
    static class Patch_TriggerableComponent_QueueTrigger
    {
        static void Prefix(TriggerableComponent __instance) =>
            TT.Log($"{__instance.GetType().Name}.QueueTrigger() CALLED on {TT.GoInfo(__instance.gameObject)}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "Trigger")]
    static class Patch_TriggerableComponent_Trigger
    {
        static void Prefix(TriggerableComponent __instance) =>
            TT.Log($"{__instance.GetType().Name}.Trigger() CALLED on {TT.GoInfo(__instance.gameObject)}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "OnInteractStart")]
    static class Patch_TriggerableComponent_OnInteractStart
    {
        static void Prefix(TriggerableComponent __instance) =>
            TT.Log($"{__instance.GetType().Name}.OnInteractStart() on {TT.GoInfo(__instance.gameObject)}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "OnInteractComplete")]
    static class Patch_TriggerableComponent_OnInteractComplete
    {
        static void Prefix(TriggerableComponent __instance) =>
            TT.Log($"{__instance.GetType().Name}.OnInteractComplete() on {TT.GoInfo(__instance.gameObject)}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "OnInteractEnd")]
    static class Patch_TriggerableComponent_OnInteractEnd
    {
        static void Prefix(TriggerableComponent __instance) =>
            TT.Log($"{__instance.GetType().Name}.OnInteractEnd() on {TT.GoInfo(__instance.gameObject)}");
    }

    [HarmonyPatch(typeof(TriggerableComponent), "OnEnable")]
    static class Patch_TriggerableComponent_OnEnable
    {
        static void Postfix(TriggerableComponent __instance)
        {
            var triggerOn = AccessTools.Field(typeof(TriggerableComponent), "m_TriggerOn")?.GetValue(__instance);
            TT.Log($"{__instance.GetType().Name}.OnEnable() on {TT.GoInfo(__instance.gameObject)} m_TriggerOn={triggerOn}");
        }
    }

    // ── The actual payload — did it fire, and with what charge value? ──
    [HarmonyPatch(typeof(TriggerableThrusterCharge), "OnTrigger")]
    static class Patch_TriggerableThrusterCharge_OnTrigger
    {
        static void Prefix(TriggerableThrusterCharge __instance)
        {
            var numCharges = AccessTools.Field(typeof(TriggerableThrusterCharge), "m_NumCharges")?.GetValue(__instance);
            TT.Log($"TriggerableThrusterCharge.OnTrigger() *** FIRED *** on {TT.GoInfo(__instance.gameObject)} m_NumCharges={numCharges}");
        }
    }

    // ── The RECEIVING end — OnTrigger() only proves the event was POSTED, not that anything actually
    // consumed it and changed Charge. Trace ThrustController.OnThrustChargeUpdated directly (private,
    // patched by name) to get real before/after Charge values for every UpdateThrusterChargeEvent,
    // matching each up against which GameObject's OnTrigger() caused it. ──
    [HarmonyPatch(typeof(ThrustController), "OnThrustChargeUpdated")]
    static class Patch_ThrustController_OnThrustChargeUpdated
    {
        static void Prefix(ThrustController __instance, UpdateThrusterChargeEvent ev, out float __state)
        {
            __state = __instance.Charge;
        }

        static void Postfix(ThrustController __instance, UpdateThrusterChargeEvent ev, float __state)
        {
            TT.Log($"ThrustController.OnThrustChargeUpdated: ChargeAmount={ev.ChargeAmount} Charge {__state:F1} -> {__instance.Charge:F1} (max={__instance.MaxCharge:F1})");
        }
    }

    // ── The REAL popup source (found after CurrencyChangedEvent turned out to be a dead end — neither
    // pickup posted one this session). InventoryNotificationController listens to
    // Inventory.OnInventoryChanged, fired from Inventory.AddItemToInventory whenever an
    // InventoryItemAsset is added — this looks like the actual "item collected" popup trigger for
    // consumables like ThrusterFuel/PatchKit, independent of currency/credits entirely. ──
    [HarmonyPatch(typeof(Inventory), "AddItemToInventory")]
    static class Patch_Inventory_AddItemToInventory
    {
        static void Prefix(InventoryItemAsset itemAsset, bool equip)
        {
            TT.Log($"Inventory.AddItemToInventory: itemAsset={(itemAsset != null ? itemAsset.name : "null")} " +
                $"Type={itemAsset?.Type?.name} equip={equip}");
        }
        static void Postfix(InventoryItemAsset itemAsset, bool __result)
        {
            TT.Log($"Inventory.AddItemToInventory RESULT: itemAsset={(itemAsset != null ? itemAsset.name : "null")} added={__result}");
        }
    }

    // ── The ACTUAL VISIBLE GAUGE — ThrustUIController.OnThrustChargeChanged is what sets the fill bar
    // (m_BarFillImage.fillAmount) and the numeric label (m_FuelValueLabel.text). Trace it directly so we
    // see exactly what the player's HUD is being told to display, not just the underlying data value. ──
    [HarmonyPatch(typeof(ThrustUIController), "OnThrustChargeChanged")]
    static class Patch_ThrustUIController_OnThrustChargeChanged
    {
        static void Prefix(ThrustUIController __instance, ThrustChargeChangedEvent ev)
        {
            TT.Log($"ThrustUIController.OnThrustChargeChanged on {TT.GoInfo(__instance.gameObject)}: " +
                $"NewCharge={ev.NewCharge:F1} MaxCharge={ev.MaxCharge:F1} ChargeState={ev.ChargeState} fillAmount(will be)={ev.NewCharge / ev.MaxCharge:F2}");
        }
    }

    // ── Working backwards from the HUD popup: what actually shows "ITEM COLLECTED"? Found via
    // decompile that RepairItemCollectedNotificationController listens for CurrencyChangedEvent (Add
    // operation on a tracked currency) — this may be a COMPLETELY SEPARATE system from
    // TriggerableThrusterCharge/UpdateThrusterChargeEvent (which we've already confirmed fires
    // identically for both baked and addressable). If so, the popup difference is unrelated to whether
    // fuel is actually granted — worth confirming directly rather than assuming. ──
    [HarmonyPatch(typeof(RepairItemCollectedNotificationController), "OnCurrencyChanged")]
    static class Patch_RepairItemCollectedNotificationController_OnCurrencyChanged
    {
        static void Prefix(CurrencyChangedEvent ev)
        {
            TT.Log($"RepairItemCollectedNotificationController.OnCurrencyChanged: Operation={ev.Operation} CurrencyID={ev.CurrencyID} Amount={ev.Amount}");
        }
    }

    [HarmonyPatch(typeof(RepairItemCollectedNotificationController), "SetNotificationData")]
    static class Patch_RepairItemCollectedNotificationController_SetNotificationData
    {
        static void Prefix(RepairItemCollectedNotificationData data, bool isQueuedNotification)
        {
            TT.Log($"RepairItemCollectedNotificationController.SetNotificationData: isQueuedNotification={isQueuedNotification} " +
                $"CurrencyAssetID={data?.RepairItemCurrencyAsset?.ID} Amount={data?.Amount}");
        }
    }

    // Any CurrencyChangedEvent at all, regardless of whether RepairItemCollectedNotificationController
    // cares about that specific currency — settles whether ThrusterFuel posts a currency event at all.
    [HarmonyPatch(typeof(CurrencyChangedEvent), "GetEvent")]
    static class Patch_CurrencyChangedEvent_GetEvent
    {
        static void Postfix(CurrencyChangedEvent __result)
        {
            if (__result != null)
                TT.Log($"CurrencyChangedEvent.GetEvent: Operation={__result.Operation} CurrencyID={__result.CurrencyID} Amount={__result.Amount}");
        }
    }

    // ── Fourth hypothesis, found via decompile: SalvageNotificationUIController listens for
    // SalvageableChangedEvent (NOT currency, NOT inventory) and calls AddToQueue(new SalvageUIData(
    // ev.ObjectName, num, ev.Mass)) where num defaults to 0 whenever the item has no configured credit
    // reward — this is EXACTLY the "Item Name, 0 Credits" popup pattern described, and is gated on
    // ev.Mass >= MinProcessTriggerMass. SalvageableChangedEvent is posted by SalvageableUtility.
    // MarkAsSalvaged/MarkAsDestroyed, both called from TriggerableSalvage.OnTrigger — a base class both
    // the baked and addressable pickups share, unlike Currency/Inventory which we already ruled out. ──
    [HarmonyPatch(typeof(SalvageableChangedEvent), "ProcessObject")]
    static class Patch_SalvageableChangedEvent_ProcessObject
    {
        static void Postfix(SalvageableChangedEvent __result)
        {
            if (__result != null)
                TT.Log($"SalvageableChangedEvent.ProcessObject: ObjectName={__result.ObjectName} Mass={__result.Mass:F2} State={__result.State}");
        }
    }

    [HarmonyPatch(typeof(SalvageableChangedEvent), "DestroyObject")]
    static class Patch_SalvageableChangedEvent_DestroyObject
    {
        static void Postfix(SalvageableChangedEvent __result)
        {
            if (__result != null)
                TT.Log($"SalvageableChangedEvent.DestroyObject: ObjectName={__result.ObjectName} Mass={__result.Mass:F2} State={__result.State} Scrapped={__result.Scrapped}");
        }
    }

    [HarmonyPatch(typeof(SalvageNotificationUIController), "OnSalvageableChanged", MethodType.Normal)]
    static class Patch_SalvageNotificationUIController_OnSalvageableChanged
    {
        static void Prefix(SalvageableChangedEvent ev)
        {
            TT.Log($"SalvageNotificationUIController.OnSalvageableChanged: ObjectName={ev.ObjectName} Mass={ev.Mass:F2} State={ev.State}");
        }
    }
}
