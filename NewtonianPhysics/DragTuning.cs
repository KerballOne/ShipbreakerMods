using System;
using System.Reflection;
using BBI;
using BBI.Unity.Game;
using BepInEx.Bootstrap;
using HarmonyLib;
using UnityEngine;

namespace NewtonianPhysics
{
    // Vanilla quietly damps momentum back toward zero over time in three separate places, all
    // independent of the on-demand air brake NoBrakes already disables - true Newtonian motion
    // (constant velocity until something actually acts on you) requires killing these too.
    internal static class DragTuning
    {
        // Rotation drag removal (PlayerRotationDrag) needs to back off whenever something is
        // actually holding/anchoring the player's orientation, or persisting spin would fight the
        // thing holding you instead of feeling natural: hand-grabbing something solid (same
        // condition vanilla itself already checks before applying free-float angularDrag), or
        // MagBoots being locked to a surface (a separate, optional mod - reflected into by GUID
        // so this compiles and runs fine whether or not MagBoots is installed at all).
        private static bool IsRotationAnchored(OrientationController controller)
        {
            var grabController = Traverse.Create(controller).Field("mGrabController").GetValue<GrabController>();
            if (grabController != null)
            {
                bool leftHolding = grabController.LeftHand.ConstraintsEnabled && grabController.LeftHand.GrabMassRatio > 0f;
                bool rightHolding = grabController.RightHand.ConstraintsEnabled && grabController.RightHand.GrabMassRatio > 0f;
                if (leftHolding || rightHolding)
                    return true;
            }

            return IsMagBootsLocked();
        }

        private static bool sMagBootsChecked;
        private static MethodInfo? sMagBootsIsAttachedGetter;
        private static object? sMagBootsControllerInstance;
        private static FieldInfo? sMagBootsControllerField;
        private static object? sMagBootsPluginInstance;

        // MagBoots is an entirely separate, optional mod - NewtonianPhysics has no compile-time
        // reference to it. Looked up once by GUID via BepInEx's plugin registry and cached; every
        // step (plugin found, controller field found, getter found) fails safe to "not locked" if
        // MagBoots isn't installed, is an incompatible version, or hasn't attached yet.
        private static bool IsMagBootsLocked()
        {
            if (!sMagBootsChecked)
            {
                sMagBootsChecked = true;
                try
                {
                    if (Chainloader.PluginInfos.TryGetValue("me.kerballone.MagBoots", out var pluginInfo) &&
                        pluginInfo.Instance != null)
                    {
                        sMagBootsPluginInstance = pluginInfo.Instance;
                        Type pluginType = sMagBootsPluginInstance.GetType();
                        sMagBootsControllerField = pluginType.GetField("_controller", BindingFlags.NonPublic | BindingFlags.Instance);
                        Type? controllerType = sMagBootsControllerField?.FieldType;
                        sMagBootsIsAttachedGetter = controllerType?.GetProperty("IsAttached", BindingFlags.Public | BindingFlags.Instance)?.GetGetMethod();
                    }
                }
                catch (Exception ex)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"NewtonianPhysics: MagBoots reflection lookup failed - {ex}");
                    sMagBootsPluginInstance = null;
                    sMagBootsControllerField = null;
                    sMagBootsIsAttachedGetter = null;
                }
            }

            if (sMagBootsPluginInstance == null || sMagBootsControllerField == null || sMagBootsIsAttachedGetter == null)
                return false;

            try
            {
                sMagBootsControllerInstance = sMagBootsControllerField.GetValue(sMagBootsPluginInstance);
                if (sMagBootsControllerInstance == null)
                    return false;

                return (bool)sMagBootsIsAttachedGetter.Invoke(sMagBootsControllerInstance, null);
            }
            catch (Exception ex)
            {
                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"NewtonianPhysics: MagBoots IsAttached read failed - {ex}");
                return false;
            }
        }

        // PlayerMotion.HandleAutoBrake runs every FixedUpdate and, whenever the player isn't
        // actively thrusting, hand-grabbing, or grappled (plus a short grace delay), applies a
        // constant counter-force opposite current velocity until speed decays to a small floor.
        // It's fully custom code (AddForce, not Rigidbody.drag), so a Prefix returning false to
        // skip it outright is the only way to disable it - nothing else depends on it having run.
        [HarmonyPatch(typeof(PlayerMotion), "HandleAutoBrake")]
        private static class PlayerMotion_HandleAutoBrake_Suppress
        {
            private static bool Prefix() => Plugin.ConfigPlayerLinearDrag.Value;
        }

        // OrientationController.HandleAxisRotation drives player look/tumble via a custom
        // "axis velocity" (mAxisVelocityX/Y/Z) applied through MoveRotation, NOT real physics
        // torque - so Rigidbody.angularDrag is irrelevant to it. The actual damping is this
        // velocity being divided down by mCurrentAxisDrag every call, unconditionally, regardless
        // of steering/grab state. mCurrentAxisDrag is only ever assigned from m_AxisDrag (Awake,
        // OnRespawned) or Vector3.zero (OnPlayerDeath) - forcing it to zero after every call keeps
        // each divide a no-op, so spin persists indefinitely just like linear drift does. Also
        // zero Rigidbody.angularDrag itself in case anything else ever reads/relies on it.
        [HarmonyPatch(typeof(OrientationController), "HandleAxisRotation")]
        private static class OrientationController_HandleAxisRotation_Suppress
        {
            private static void Postfix(OrientationController __instance)
            {
                var traverse = Traverse.Create(__instance);

                // mCurrentAxisDrag only ever gets reassigned to m_AxisDrag by vanilla in Awake/
                // OnRespawned - once we zero it out here, it STAYS zero on every later frame unless
                // we explicitly put it back, even after IsRotationAnchored starts returning true.
                // So this must actively restore m_AxisDrag when anchored, not just skip zeroing.
                if (Plugin.ConfigPlayerRotationDrag.Value || IsRotationAnchored(__instance))
                {
                    Vector3 configuredDrag = traverse.Field("m_AxisDrag").GetValue<Vector3>();
                    traverse.Field("mCurrentAxisDrag").SetValue(configuredDrag);
                    return;
                }

                traverse.Field("mCurrentAxisDrag").SetValue(Vector3.zero);

                Rigidbody? rigidbody = traverse.Field("mRigidbodyReference").GetValue<Rigidbody>();
                if (rigidbody != null)
                    rigidbody.angularDrag = 0f;
            }
        }

        // HierarchyParent.ApplyChildData is where vanilla copies each part's configured
        // Drag/AngularDrag (from its IRigidbodyAsset data, via an ECS DragComponent) onto the
        // part's real Rigidbody.drag/angularDrag - it runs continuously as the ship's structure
        // graph updates, not just once on spawn, so re-zeroing here after the original keeps
        // loose parts and debris drag-free the same way the player already is.
        //
        // Deliberately NOT auto-discovered by Harmony's PatchAll() (no [HarmonyPatch] attribute
        // here - see ApplyManually below) - HierarchyParent's static cctor builds a
        // Unity.Entities.ComponentTypes array from typeof(...) calls, which requires
        // Unity.Entities.TypeManager to already be initialized. Patching this method via
        // PatchAll() in Plugin.Awake() forces the CLR to JIT-compile it (MonoMod's
        // GetFunctionPointer/GetNativeStart), which in turn forces HierarchyParent's static
        // cctor to run - but Awake() fires during BepInEx's very early plugin-load pass, well
        // before the game's own TypeManager.Initialize() has run. That crashes the cctor with a
        // NullReferenceException, and .NET permanently marks HierarchyParent as faulted for the
        // rest of the session - every later legitimate use (ShipSpawnSystem.LoadingFixupHierarchies,
        // called every frame while a ship loads) then rethrows the same TypeInitializationException
        // forever. Confirmed via a user's crash log showing exactly this failure chain, immediately
        // under PatchAll() in Plugin.Awake(), only when this mod was installed.
        private static class HierarchyParent_ApplyChildData_Suppress
        {
            private static void Postfix(HierarchyParent __instance)
            {
                if (Plugin.ConfigObjectDrag.Value)
                    return;

                if (__instance.TryGetRidigbody(out Rigidbody rb))
                {
                    rb.drag = 0f;
                    rb.angularDrag = 0f;
                }
            }
        }

        private static bool sApplied;

        // Applied once, on the first real Gameplay transition (see Plugin.OnGameStateChanged) -
        // by then the game's own ECS systems are already running, so TypeManager is guaranteed
        // initialized and HierarchyParent's static cctor can run safely.
        public static void ApplyManually()
        {
            if (sApplied)
                return;
            sApplied = true;

            new Harmony(PluginInfo.PLUGIN_GUID).Patch(
                AccessTools.Method(typeof(HierarchyParent), nameof(HierarchyParent.ApplyChildData)),
                postfix: new HarmonyMethod(AccessTools.Method(typeof(HierarchyParent_ApplyChildData_Suppress), "Postfix")));
        }
    }
}
