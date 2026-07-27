using BBI.Unity.Game;
using HarmonyLib;
using UnityEngine;

namespace MagBoots
{
    // Scales the Cutter's existing recoil, and adds a single, target-transparent player recoil to the
    // grapple's Push/Throw action - independent of MagBoots itself, bundled into this plugin at the
    // user's request rather than a separate mod.
    internal static class RecoilTuning
    {
        // BrakeBreak: whenever any recoil above actually applies a force, briefly disable the air brake
        // so the kick isn't instantly cancelled out by the player reflexively braking (or already holding
        // the brake) the moment they get hit. sBrakeBreakUntil is compared against Time.time rather than
        // using a countdown/coroutine, so it self-expires with no separate update tick needed.
        private static float sBrakeBreakUntil = -1f;

        private static void TriggerBrakeBreak()
        {
            float duration = Plugin.ConfigBrakeBreak.Value;
            if (duration <= 0f)
                return;

            sBrakeBreakUntil = Time.time + duration;
        }

        private static bool IsBrakeBroken => Plugin.ConfigNoBrakes.Value || Time.time < sBrakeBreakUntil;

        // Vanilla reads the brake input directly via this extension method every FixedUpdate
        // (OrientationController has no cached/settable "is braking" field to flip instead) - Prefix
        // forces it to report "not pressed" for the two brake actions while either NoBrakes or
        // BrakeBreak is active, and lets every other action (and the brake itself otherwise) pass
        // through untouched.
        [HarmonyPatch(typeof(LynxControlExtensions), nameof(LynxControlExtensions.GetInputIsPressed))]
        private static class LynxControlExtensions_GetInputIsPressed_BrakeBreak
        {
            private static bool Prefix(LynxControls.ActionSetAndId actionSetAndId, ref bool __result)
            {
                if (!IsBrakeBroken)
                    return true;

                if (!actionSetAndId.Equals((LynxControls.ActionSetAndId)GameplayActions.GameplayActionSet.ThrustBrakeLeft) &&
                    !actionSetAndId.Equals((LynxControls.ActionSetAndId)GameplayActions.GameplayActionSet.ThrustBrakeRight))
                    return true;

                __result = false;
                return false;
            }
        }

        // Vanilla applies saw Cutter mode's recoil from TryPerformCut -> ApplyRecoilForce, which only
        // runs once a cut line's configured Delay has elapsed (HandleCutting checks each
        // BuffableCutLine's Delay every FixedUpdate). Note this ONLY covers saw/Cutter mode -
        // Scalpel/single-laser mode is a completely separate class (ScalpelController) with no
        // RecoilForce mechanism at all; see ScalpelController_OnStateChanged_Recoil below for that.
        //
        // Fixed by moving recoil entirely to StartCutting, the hook point Cutter mode fires through the
        // instant CutterFire is pressed and cutting begins (via the CuttingState.Cutting transition), and
        // suppressing the original delayed ApplyRecoilForce call so recoil happens exactly once per
        // trigger pull, immediately, regardless of how long the cut itself takes to resolve.
        [HarmonyPatch(typeof(CuttingController), "ApplyRecoilForce")]
        private static class CuttingController_ApplyRecoilForce_Suppress
        {
            // Harmony Prefix: returning false skips the original method. Skip vanilla's delayed
            // ApplyRecoilForce whenever our own recoil tuning is enabled (StartCutting's Postfix
            // handles it instead); let it run normally when recoil tuning is disabled.
            private static bool Prefix() => !Plugin.ConfigRecoilEnabled.Value;
        }

        [HarmonyPatch(typeof(CuttingController), "StartCutting")]
        private static class CuttingController_StartCutting_Recoil
        {
            private static void Postfix(CuttingController __instance, ICutExecutionData data)
            {
                if (!Plugin.ConfigRecoilEnabled.Value)
                    return;

                float recoil = Plugin.ConfigSawCutterRecoil.Value;
                if (recoil <= 0f || data.BuffableCutLines.Count == 0)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: StartCutting recoil skipped - recoil={recoil}, cutLines={data.BuffableCutLines.Count}");
                    return;
                }

                var traverse = Traverse.Create(__instance);
                Rigidbody? playerRigidbody = traverse.Field("m_PlayerRigidbody").GetValue<Rigidbody>();
                if (playerRigidbody == null)
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo("MagBoots: StartCutting recoil skipped - m_PlayerRigidbody was null.");
                    return;
                }

                // Multiple cut lines can fire in one trigger pull (e.g. cross-shaped cuts) - scale by
                // line count so a multi-line fire kicks harder than a single line, same principle as
                // vanilla applying one ApplyRecoilForce call per line as each resolved individually.
                //
                // Applied as ForceMode.VelocityChange rather than whatever mode the asset configures
                // (usually Acceleration, mass-independent but only integrated over a single FixedUpdate
                // tick): a value integrated over one tick produces a velocity delta too small to feel as
                // a "kick." VelocityChange applies the full value as an instant velocity change
                // regardless of physics step size, which is what a felt recoil actually needs.
                float totalRecoil = recoil * data.BuffableCutLines.Count;

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: StartCutting recoil - totalRecoil={totalRecoil}, applied={-playerRigidbody.transform.forward * totalRecoil}");

                Vector3 force = -playerRigidbody.transform.forward * totalRecoil;
                playerRigidbody.AddForce(force, ForceMode.VelocityChange);
                TriggerBrakeBreak();
            }
        }

        // ScalpelController.HandleCuttingState runs every Update() (not FixedUpdate) for as long as
        // CutterFire is held - it early-returns to CuttingState.Ready the instant the trigger is
        // released, so simply being called at all each frame already means the trigger is actively
        // held, regardless of whether the beam happens to be on a valid/cuttable target. Recoil fires
        // from firing the beam itself, not from successfully vaporizing something - same principle as
        // the saw Cutter recoiling on press regardless of whether the cut actually lands. Unlike the
        // saw Cutter's discrete per-pull cuts, the Scalpel is meant to be held down continuously, so a
        // one-shot kick isn't the right shape here; vanilla has no recoil mechanism at all for this mode.
        // Since AddForce needs to be applied every FixedUpdate to integrate correctly with physics (and
        // this method runs on the variable-rate Update instead), the Postfix here only sets a flag for
        // "trigger held this frame" - the actual constant ForceMode.Acceleration is applied every physics
        // tick from ScalpelFixedUpdate(), called from Plugin.FixedUpdate(). The flag naturally goes stale
        // within one frame once HandleCuttingState stops being called (trigger released, tool switched).
        private static bool sScalpelActivelyCutting;
        private static float sScalpelActivelyCuttingUpdateTime = -1f;

        [HarmonyPatch(typeof(ScalpelController), "HandleCuttingState")]
        private static class ScalpelController_HandleCuttingState_Recoil
        {
            private static void Postfix()
            {
                sScalpelActivelyCutting = true;
                sScalpelActivelyCuttingUpdateTime = Time.time;
            }
        }

        // Called every FixedUpdate from Plugin - applies the constant Scalpel recoil for as long as the
        // flag set above is still fresh (set within the last Update tick; if HandleCuttingState stops
        // running - trigger released, target lost, tool unequipped - the flag goes stale within one frame
        // and recoil stops on its own without needing a separate "stopped cutting" hook).
        public static void ScalpelFixedUpdate()
        {
            if (!Plugin.ConfigRecoilEnabled.Value || !sScalpelActivelyCutting)
                return;

            if (Time.time - sScalpelActivelyCuttingUpdateTime > 0.5f)
            {
                sScalpelActivelyCutting = false;
                return;
            }

            float acceleration = Plugin.ConfigScalpelCutterRecoil.Value;
            if (acceleration <= 0f)
                return;

            var grabController = Object.FindObjectOfType<GrabController>();
            Rigidbody? playerRigidbody = grabController == null
                ? null
                : Traverse.Create(grabController).Field("m_PlayerRigidbodyReference").GetValue<Rigidbody>();
            if (playerRigidbody == null)
                return;

            if (Plugin.ConfigDebugPrint.Value)
                Plugin.Log.LogInfo($"MagBoots: Scalpel recoil - acceleration={acceleration}");

            Vector3 force = -playerRigidbody.transform.forward * acceleration;
            playerRigidbody.AddForce(force, ForceMode.Acceleration);
            TriggerBrakeBreak();
        }

        // Shared by both GrapplePush (raycast, nothing grappled) and Throw (grappled + light enough to
        // throw): inverse-square-falloff recoil reflected back at the player based on how far away the
        // pushed/thrown object actually is, only within GrappleReflectionDistance, and scaled by a
        // Newton's-third-law mass share: the player receives objectMass / (ConfigAssumedPlayerMassKg +
        // objectMass) of the push force - heavier/more-immovable objects reflect MORE force back (like
        // pushing off a wall), while lighter objects mostly just fly away with little reaction on the
        // player. Real player Rigidbody.mass isn't a realistic reference for this (ship structure runs
        // into the tens/hundreds of thousands of kg, a totally different scale than a person), so
        // ConfigAssumedPlayerMassKg is used instead. objectMass == null means a static/immovable
        // structure (no rigidbody) - treated as effectively infinite mass, so it also reflects the full
        // force back, consistent with the same formula as mass approaches infinity. Normalized so a hit
        // at 1m gets the full baseForce, not asymptotically infinite as distance approaches 0.
        private static float ReflectionForce(float baseForce, float distance, float? objectMass)
        {
            float scalar = Plugin.ConfigGrappleReflectionRecoil.Value;
            if (scalar <= 0f || distance > Plugin.ConfigGrappleReflectionDistance.Value)
                return 0f;

            float clampedDistance = Mathf.Max(distance, 1f);
            float falloff = 1f / (clampedDistance * clampedDistance);
            float assumedPlayerMass = Plugin.ConfigAssumedPlayerMassKg.Value;
            float massShare = objectMass.HasValue
                ? Mathf.Max(objectMass.Value, 0f) / (assumedPlayerMass + Mathf.Max(objectMass.Value, 0f))
                : 1f;
            return baseForce * falloff * massShare * scalar;
        }

        // GrapplingHook.OnRangedThrowPressed ("Grapple Push" WITHOUT anything grappled): vanilla's own
        // multi-ray DOTS scoring system (GrapplePushRaycastSystem) is a screen-space grid tuned for
        // scoring/selecting push targets, not for a simple "how far away is what I'm looking at" distance
        // check - it frequently reports zero hits even when clearly aiming at something, and doesn't
        // suit a straightforward distance-based falloff. Instead this fires its own centered
        // Physics.Raycast from the camera, independent of vanilla's targeting, and reflects recoil off
        // the hit distance via ReflectionForce above. Runs as a Prefix so CalculateRangedThrowForce()
        // (which scales with charge time) is read before vanilla's CancelPushCharge() resets it - reading
        // it afterward would silently produce near-zero recoil regardless of how charged the push was.
        [HarmonyPatch(typeof(GrapplingHook), "OnRangedThrowPressed")]
        private static class GrapplingHook_OnRangedThrowPressed_Recoil
        {
            private static void Prefix(GrapplingHook __instance)
            {
                if (!Plugin.ConfigRecoilEnabled.Value)
                    return;

                var traverse = Traverse.Create(__instance);
                Rigidbody? playerRigidbody = traverse.Field("m_PlayerRigidbody").GetValue<Rigidbody>();
                var mData = traverse.Field("mData").GetValue<IGrapplingHookData>();
                if (playerRigidbody == null || mData == null || LynxCameraController.MainCameraTransform == null)
                    return;

                Vector3 origin = LynxCameraController.MainCameraTransform.position;
                Vector3 forward = LynxCameraController.MainCameraTransform.forward;
                float maxDistance = Plugin.ConfigGrappleReflectionDistance.Value;

                LayerMask mask = Main.Instance.MainSettings.RaycastSettings.BaseLayerMask;
                if (!Physics.Raycast(origin, forward, out RaycastHit hit, maxDistance, mask))
                {
                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: GrapplePush reflection recoil - no hit within {maxDistance}m, no recoil.");
                    return;
                }

                float baseForce = traverse.Method("CalculateRangedThrowForce").GetValue<float>();
                // No rigidbody (a static/immovable structure) reflects the full push force - see
                // ReflectionForce's null-objectMass handling.
                float? objectMass = hit.rigidbody != null ? hit.rigidbody.mass : (float?)null;
                float appliedForce = ReflectionForce(baseForce, hit.distance, objectMass);

                if (Plugin.ConfigDebugPrint.Value)
                    Plugin.Log.LogInfo($"MagBoots: GrapplePush reflection recoil - distance={hit.distance}, baseForce={baseForce}, playerMass={playerRigidbody.mass}, objectMass={objectMass}, appliedForce={appliedForce}");

                if (appliedForce <= 0f)
                    return;

                // Applied as Impulse (divides by player mass, same as vanilla's own PlayerPushbackScalar
                // mechanism in OnThrowPressed) rather than VelocityChange (which added the raw value
                // directly with no mass division at all) - VelocityChange was producing results 40-75x
                // larger than the grappled path's real Impulse-based recoil at the same baseForce, since
                // nothing there ever divided by the player's ~50kg mass.
                Vector3 recoil = -forward * appliedForce;
                playerRigidbody.AddForce(recoil, ForceMode.Impulse);
                TriggerBrakeBreak();
            }
        }

        // GrapplingHook.OnThrowPressed (Push/Throw WHILE grappled): two distinct player-recoil sources.
        // (1) Object too heavy to throw: vanilla already redirects the throw force into pushing the
        // PLAYER back directly (via PlayerPushbackScalar) - GrappleThrowRecoilMultiplier just multiplies
        // that existing push. (2) Object light enough to actually throw: vanilla applies force only to
        // the object, none to the player - GrappleReflectionRecoil adds a distance-based reflection here
        // too, using the SAME ReflectionForce formula as the raycast-push case above, so throwing a light
        // grappled object close to you kicks you back the same way pushing a nearby raycasted object
        // would. RemoveHookPoint (called first in the original method) clears GrappledRigidbody, so both
        // branch decisions must be captured in a Prefix, before vanilla runs and before that field clears.
        [HarmonyPatch(typeof(GrapplingHook), "OnThrowPressed")]
        private static class GrapplingHook_OnThrowPressed_Recoil
        {
            private static void Prefix(GrapplingHook __instance)
            {
                if (!Plugin.ConfigRecoilEnabled.Value)
                    return;

                var traverse = Traverse.Create(__instance);
                var mData = traverse.Field("mData").GetValue<IGrapplingHookData>();
                Rigidbody? playerRigidbody = traverse.Field("m_PlayerRigidbody").GetValue<Rigidbody>();
                if (mData == null || playerRigidbody == null || LynxCameraController.MainCameraTransform == null)
                    return;

                Rigidbody grappledRigidbody = __instance.GrappledRigidbody;
                bool tooHeavyToThrow = grappledRigidbody == null || mData.GrappledMassRange.AboveRange(grappledRigidbody.mass);
                Vector3 forward = LynxCameraController.MainCameraTransform.forward;

                if (tooHeavyToThrow)
                {
                    float multiplier = Plugin.ConfigGrappleThrowRecoilMultiplier.Value;
                    float throwForceForLog = traverse.Method("CalculateThrowForce").GetValue<float>();
                    // Vanilla's own player push-back for this branch (what happens even at multiplier=1,
                    // i.e. unmodified): -throwForce * PlayerPushbackScalar, applied via mData.ThrowForceMode
                    // elsewhere in OnThrowPressed itself (not here - this method only adds the EXTRA delta
                    // on top when multiplier != 1).
                    float vanillaPushbackForLog = -throwForceForLog * mData.PlayerPushbackScalar;

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: Throw (heavy) recoil - throwForce={throwForceForLog}, playerMass={playerRigidbody.mass}, objectMass={grappledRigidbody?.mass}, vanillaPushback={vanillaPushbackForLog}, multiplier={multiplier}");

                    // Vanilla's own pushback fires here regardless of multiplier, so brake-break should
                    // too - only the extra scaled delta below is gated on multiplier != 1.
                    TriggerBrakeBreak();

                    if (multiplier == 1f)
                        return;

                    Vector3 extraRecoil = forward * (-throwForceForLog * mData.PlayerPushbackScalar) * (multiplier - 1f);
                    playerRigidbody.AddForce(extraRecoil, mData.ThrowForceMode);
                }
                else
                {
                    // Light enough to actually throw - vanilla pushes the object, not the player. Reflect
                    // recoil back based on the grappled object's current distance from the player.
                    // grappledRigidbody is guaranteed non-null here: tooHeavyToThrow is only false when
                    // grappledRigidbody != null (see its definition above).
                    float distance = Vector3.Distance(playerRigidbody.position, grappledRigidbody!.position);
                    float throwForce = traverse.Method("CalculateThrowForce").GetValue<float>();
                    float appliedForce = ReflectionForce(throwForce, distance, grappledRigidbody.mass);

                    if (Plugin.ConfigDebugPrint.Value)
                        Plugin.Log.LogInfo($"MagBoots: Throw reflection recoil - distance={distance}, throwForce={throwForce}, playerMass={playerRigidbody.mass}, objectMass={grappledRigidbody.mass}, appliedForce={appliedForce}");

                    if (appliedForce <= 0f)
                        return;

                    // Impulse (divides by player mass), matching the GrapplePush reflection case above
                    // and vanilla's own PlayerPushbackScalar mechanism - not VelocityChange, which added
                    // the raw value with no mass division and produced far oversized kicks.
                    playerRigidbody.AddForce(-forward * appliedForce, ForceMode.Impulse);
                    TriggerBrakeBreak();
                }
            }
        }
    }
}
