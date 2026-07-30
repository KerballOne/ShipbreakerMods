using BBI;
using BBI.Unity.Game;
using HarmonyLib;
using InControl;
using UnityEngine;

namespace MagBoots
{
    // While attached, replaces the vanilla zero-g "accumulate velocity, then decay it" mouselook model
    // (OrientationController.HandleAxisRotation) with instant, drift-free yaw/pitch - each frame's look
    // input applies directly to rotation, with no persisted spin that keeps turning the player after
    // input stops. Floating and rotating with lingering momentum makes sense while thrusting around in
    // zero-g, but once your boots are locked to a surface, you should be able to look around with the
    // same speed and precision as standing on solid ground - the vanilla drift model actively fights
    // that. Roll is intentionally left alone here (and not read at all) - it doesn't mean anything while
    // attached, since MagBootsController already re-orients roll/yaw to match the surface normal on its
    // own. Adapted from the standalone ViewNoLag mod's replacement math (which also handles roll, since it
    // has no attach-state concept and applies everywhere), gated here to only run while MagBoots is
    // attached.
    internal static class PreciseRotation
    {
        private static bool sActive;

        // Toggled from MagBoots' own attach/detach lifecycle (BeginSnap/Detach/OnLeaveGameplay, the last
        // of which already routes through Detach) - mirrors ThrustSuppression's pattern.
        public static void SetActive(bool active) => sActive = active;

        [HarmonyPatch(typeof(OrientationController), "HandleAxisRotation")]
        private static class OrientationController_HandleAxisRotation_Instant
        {
            private static bool Prefix(OrientationController __instance)
            {
                if (!sActive || !Plugin.ConfigPreciseRotation.Value)
                    return true;

                var traverse = Traverse.Create(__instance);
                var rigidbody = traverse.Field("mRigidbodyReference").GetValue<Rigidbody>();
                if (rigidbody == null)
                    return true;

                // Only yaw/pitch (look direction) get the instant treatment - roll doesn't mean anything
                // while attached, since MagBootsController already drives roll/yaw-to-surface separately
                // (re-orienting "down" to match the surface normal, elsewhere in UpdateAttached) and roll
                // input has no independent role once grounded, unlike free-floating in zero-g.
                Vector2 rawAxis;
                Vector3 sensitivity;
                AnimationCurve sensitivityCurve;

                BindingSourceType lastInputType = LynxControls.Instance.LastInputType;
                bool isMouseOrKeyboard = lastInputType == BindingSourceType.MouseBindingSource || lastInputType == BindingSourceType.KeyBindingSource;

                if (isMouseOrKeyboard)
                {
                    sensitivity = traverse.Property("MouseSensitivity").GetValue<Vector3>();
                    sensitivityCurve = traverse.Field("m_MouseSensitivityCurve").GetValue<AnimationCurve>();
                    rawAxis = LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite);
                }
                else
                {
                    sensitivity = traverse.Property("ControllerSensitivity").GetValue<Vector3>();
                    sensitivityCurve = traverse.Field("m_ControllerSensitivityCurve").GetValue<AnimationCurve>();

                    var deadzone = traverse.Field("m_OrientationDeadzone").GetValue<LynxControls.DeadZoneDefinition>();
                    rawAxis = LynxControls.GetDeadzoneVector(LynxControls.Instance.GetTwoAxisInputControlVector(GameplayActions.GameplayActionSet.RotateBodyComposite), deadzone);
                }

                Vector2 input = rawAxis;

                if (input.sqrMagnitude <= 0f)
                    return false;

                input *= sensitivityCurve.Evaluate(input.magnitude);

                float userSensitivity = Plugin.ConfigPreciseRotationSensitivity.Value;
                input.x *= sensitivity.x * userSensitivity;
                input.y *= sensitivity.y * userSensitivity;

                float deltaTime = Time.deltaTime;
                Quaternion rotation = rigidbody.rotation;
                rotation *= Quaternion.Euler(Vector3.up * input.x * deltaTime);
                rotation *= Quaternion.Euler(Vector3.right * -input.y * deltaTime);
                rigidbody.MoveRotation(rotation);
                rigidbody.angularDrag = 0f;

                return false;
            }
        }
    }
}
